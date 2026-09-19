using System;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    /// <summary>
    /// Garantit qu'un média existe dans le référentiel avant qu'on y rattache des
    /// données utilisateur.
    ///
    /// Sans ce service, noter un film inconnu échouerait sur la contrainte de clé
    /// étrangère — on ne pourrait noter que ce que la migration a importé.
    ///
    /// Le peuplement est **paresseux** : un média n'entre dans le référentiel que
    /// lorsqu'on le note, l'ajoute en watchlist, le suit ou le regarde. Peupler
    /// depuis les résultats de recherche accumulerait des milliers de fiches jamais
    /// touchées, pour rien.
    /// </summary>
    public class MediaCatalog
    {
        private readonly Db _db;
        private readonly TmdbClient _tmdb;
        private readonly ILogger<MediaCatalog> _logger;

        public MediaCatalog(Db db, TmdbClient tmdb, ILogger<MediaCatalog> logger)
        {
            _db = db;
            _tmdb = tmdb;
            _logger = logger;
        }

        /// <summary>
        /// Découpe une clé média en ses deux composantes.
        ///
        /// Parametres :
        /// - media_key (string) : clé au format '{type}:{tmdb_id}'
        ///
        /// Output :
        /// - parts ((string Type, int TmdbId)?) : composantes, null si la clé est mal formée
        /// </summary>
        /// <remarks>
        /// Point unique de validation d'une clé média : `is_valid_media_key` du
        /// controller de base y délègue, pour qu'une règle ajoutée ici vaille partout.
        ///
        /// Pas de plafond sur l'identifiant au-delà de celui d'`int` : il ne réduirait
        /// l'espace de clés que d'un ordre de grandeur, sans rien empêcher, et
        /// transformerait le 404 « introuvable sur TMDB » en 400 pour des valeurs
        /// parfaitement bien formées. Ce qui borne réellement les appels sortants est
        /// la mise en cache des absences dans `TmdbClient`.
        /// </remarks>
        public static (string Type, int TmdbId)? split(string media_key)
        {
            var parts = (media_key ?? "").Split(':', 2);
            if (parts.Length != 2) return null;
            if (parts[0] != "movie" && parts[0] != "tv") return null;
            if (!int.TryParse(parts[1], out var id) || id <= 0) return null;

            return (parts[0], id);
        }

        /// <summary>
        /// S'assure qu'un média est présent dans le référentiel, en le récupérant
        /// depuis TMDB si nécessaire.
        ///
        /// Parametres :
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - exists (bool) : vrai si le média est disponible en base à l'issue de l'appel
        /// </summary>
        public async Task<bool> ensure_exists(string media_key, bool force = false)
        {
            var parts = split(media_key);
            if (parts is null) return false;

            using var con = _db.open();

            // `force` saute ce raccourci pour atteindre l'UPSERT et compléter une
            // fiche déjà présente. Sans lui, un média entré une fois en base n'était
            // plus jamais enrichi — la boucle d'entretien de RefreshTask ne faisait
            // rien du tout, malgré son commentaire.
            if (!force && exists(con, media_key)) return true;

            var item = await _tmdb.get_item(parts.Value.Type, parts.Value.TmdbId);
            if (item is null)
            {
                _logger.LogWarning("[EnhancedFin] {Key} introuvable sur TMDB", media_key);
                return false;
            }

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var transaction = con.BeginTransaction();

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO media (media_key, media_type, tmdb_id, title, year,
                                       poster_url, backdrop_url, logo_url, refreshed_at)
                    VALUES ($k, $t, $id, $title, $year, $poster, $backdrop, $logo, $now)
                    ON CONFLICT(media_key) DO UPDATE SET
                        title        = excluded.title,
                        year         = COALESCE(excluded.year, media.year),
                        poster_url   = COALESCE(excluded.poster_url, media.poster_url),
                        backdrop_url = COALESCE(excluded.backdrop_url, media.backdrop_url),
                        -- COALESCE et non écrasement : un logo venu de la migration
                        -- ne doit pas être perdu si TMDB n'en renvoie pas.
                        logo_url     = COALESCE(excluded.logo_url, media.logo_url),
                        refreshed_at = excluded.refreshed_at";
                cmd.Parameters.AddWithValue("$k", media_key);
                cmd.Parameters.AddWithValue("$t", parts.Value.Type);
                cmd.Parameters.AddWithValue("$id", parts.Value.TmdbId);
                cmd.Parameters.AddWithValue("$title", item.DisplayTitle);
                cmd.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$poster", (object?)TmdbClient.image_url(item.PosterPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$backdrop", (object?)TmdbClient.image_url(item.BackdropPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$logo", (object?)TmdbClient.image_url(item.LogoPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            if (!string.IsNullOrWhiteSpace(item.Overview))
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO media_detail (media_key, overview) VALUES ($k, $o)
                    ON CONFLICT(media_key) DO UPDATE SET overview = excluded.overview";
                cmd.Parameters.AddWithValue("$k", media_key);
                cmd.Parameters.AddWithValue("$o", item.Overview);
                cmd.ExecuteNonQuery();
            }

            // `genres` sur une fiche détaillée, `genre_ids` sur un résultat de recherche.
            var genres = item.Genres?.ConvertAll(g => g.Id) ?? item.GenreIds;
            if (genres is not null)
            {
                foreach (var genre_id in genres)
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO media_genre (media_key, genre_id) VALUES ($k, $g)
                        ON CONFLICT DO NOTHING";
                    cmd.Parameters.AddWithValue("$k", media_key);
                    cmd.Parameters.AddWithValue("$g", genre_id);
                    cmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return true;
        }

        /// <summary>
        /// Met à jour les dates de diffusion d'une série dans `release`.
        ///
        /// Parametres :
        /// - media_key (string) : clé d'une série ('tv:...')
        ///
        /// Output :
        /// - count (int) : nombre d'épisodes enregistrés
        /// </summary>
        public async Task<int> refresh_releases(string media_key)
        {
            var parts = split(media_key);
            if (parts is null || parts.Value.Type != "tv") return 0;

            var episodes = await _tmdb.get_episodes(parts.Value.TmdbId);
            if (episodes.Count == 0) return 0;

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var con = _db.open();
            using var transaction = con.BeginTransaction();

            foreach (var ep in episodes)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO release (media_key, season, episode, episode_name,
                                         air_date, refreshed_at)
                    VALUES ($k, $s, $e, $name, $air, $now)
                    ON CONFLICT(media_key, season, episode) DO UPDATE SET
                        episode_name = COALESCE(excluded.episode_name, release.episode_name),
                        air_date     = COALESCE(excluded.air_date, release.air_date),
                        refreshed_at = excluded.refreshed_at";
                cmd.Parameters.AddWithValue("$k", media_key);
                cmd.Parameters.AddWithValue("$s", ep.SeasonNumber);
                cmd.Parameters.AddWithValue("$e", ep.EpisodeNumber);
                cmd.Parameters.AddWithValue("$name", (object?)ep.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$air", (object?)ep.AirDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return episodes.Count;
        }

        private static bool exists(SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM media WHERE media_key = $k";
            cmd.Parameters.AddWithValue("$k", media_key);

            return cmd.ExecuteScalar() is not null;
        }
    }
}
