using System;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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
        /// Sérialise le casting au format attendu par le client.
        ///
        /// Limité aux 20 premiers, qui sont ceux de l'affiche : TMDB en renvoie
        /// parfois plusieurs centaines, figurants compris, et les stocker gonflerait
        /// la base pour une liste que personne ne fait défiler jusqu'au bout.
        ///
        /// Les films le portent dans `credits`, les séries dans `aggregate_credits`
        /// où un acteur peut tenir plusieurs rôles — on retient le premier, qui est
        /// le principal.
        ///
        /// Parametres :
        /// - item (TmdbItem) : fiche TMDB, films et séries confondus
        ///
        /// Output :
        /// - json (string | null) : tableau JSON, null si aucun casting exploitable
        /// </summary>
        private static string? serialize_cast(TmdbItem item)
        {
            var people = (item.Credits?.Cast ?? new List<TmdbCastMember>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .OrderBy(c => c.Order)
                .Select(c => (c.Id, c.Name, Character: c.Character, c.ProfilePath))
                .ToList();

            if (people.Count == 0)
            {
                people = (item.AggregateCredits?.Cast ?? new List<TmdbAggregateCastMember>())
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .OrderBy(c => c.Order)
                    .Select(c => (c.Id, c.Name, Character: c.Roles?.Count > 0 ? c.Roles[0].Character : null, c.ProfilePath))
                    .ToList();
            }

            if (people.Count == 0) return null;

            var serializable = people
                .Take(20)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    character = c.Character,
                    profileUrl = TmdbClient.image_url(c.ProfilePath),
                })
                .ToList();

            return JsonSerializer.Serialize(serializable);
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
            // Forme canonique seulement : « 550 », jamais « +550 », « 0550 » ni « 550 ».
            // La clé brute est stockée telle quelle ; une variante passerait la contrainte
            // `UNIQUE(media_type, tmdb_id)` mais pas `ON CONFLICT(media_key)`, et ferait
            // échouer toutes les écritures sur le vrai média, pour tous les utilisateurs.
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0
                || parts[1] != id.ToString(CultureInfo.InvariantCulture)) return null;

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
        public async Task<bool> ensure_exists(string media_key, bool force = false, CancellationToken cancellation = default)
        {
            var parts = split(media_key);
            if (parts is null) return false;

            // `force` saute ce raccourci pour atteindre l'UPSERT et compléter une
            // fiche déjà présente. Sans lui, un média entré une fois en base n'était
            // plus jamais enrichi — la boucle d'entretien de RefreshTask ne faisait
            // rien du tout, malgré son commentaire.
            if (!force)
            {
                using var check = _db.open();
                if (exists(check, media_key)) return true;
            }

            // Connexion ouverte **après** l'appel TMDB, qui peut durer plusieurs
            // secondes : la garder ouverte pendant ce temps bloquerait d'autres écritures.
            var item = await _tmdb.get_item(parts.Value.Type, parts.Value.TmdbId, cancellation);
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            using var con = _db.open();

            if (item is null)
            {
                _logger.LogWarning("[EnhancedFin] {Key} introuvable sur TMDB", media_key);

                // La tentative est notée quand même : `RefreshTask` trie par
                // `refreshed_at`, et une fiche que TMDB ne complète pas restait sinon en
                // tête du lot à chaque passage, en prenant la place des autres.
                using var touch = con.CreateCommand();
                touch.CommandText = "UPDATE media SET refreshed_at = $now WHERE media_key = $k";
                touch.Parameters.AddWithValue("$now", now);
                touch.Parameters.AddWithValue("$k", media_key);
                touch.ExecuteNonQuery();
                return false;
            }

            using var transaction = con.BeginTransaction();

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO media (media_key, media_type, tmdb_id, title, year,
                                       poster_url, backdrop_url, logo_url,
                                       vote_average, vote_count, refreshed_at)
                    VALUES ($k, $t, $id, $title, $year, $poster, $backdrop, $logo,
                            $vote_avg, $vote_count, $now)
                    ON CONFLICT(media_key) DO UPDATE SET
                        title        = excluded.title,
                        year         = COALESCE(excluded.year, media.year),
                        poster_url   = COALESCE(excluded.poster_url, media.poster_url),
                        backdrop_url = COALESCE(excluded.backdrop_url, media.backdrop_url),
                        -- COALESCE et non écrasement : un logo venu de la migration
                        -- ne doit pas être perdu si TMDB n'en renvoie pas.
                        logo_url     = COALESCE(excluded.logo_url, media.logo_url),
                        vote_average = COALESCE(excluded.vote_average, media.vote_average),
                        vote_count   = COALESCE(excluded.vote_count, media.vote_count),
                        refreshed_at = excluded.refreshed_at";
                cmd.Parameters.AddWithValue("$k", media_key);
                cmd.Parameters.AddWithValue("$t", parts.Value.Type);
                cmd.Parameters.AddWithValue("$id", parts.Value.TmdbId);
                cmd.Parameters.AddWithValue("$title", item.DisplayTitle);
                cmd.Parameters.AddWithValue("$year", (object?)item.Year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$poster", (object?)TmdbClient.image_url(item.PosterPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$backdrop", (object?)TmdbClient.image_url(item.BackdropPath) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$logo", (object?)TmdbClient.image_url(item.LogoPath) ?? DBNull.Value);

                // Une note à 0 signifie « personne n'a voté », pas « mauvais film ».
                // La stocker afficherait 0/10 sur les sorties récentes.
                //
                // `VoteAverage is not null` en plus du compte : l'opérateur `!` fait
                // taire le compilateur, il ne déballe pas un `Nullable<T>`.
                // `item.VoteAverage!` reste un `double?`, donc une fiche sans note
                // envoyait un `null` boxé là où on croyait passer DBNull.
                var has_votes = item.VoteCount is > 0 && item.VoteAverage is not null;
                cmd.Parameters.AddWithValue("$vote_avg", has_votes ? item.VoteAverage!.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$vote_count", has_votes ? item.VoteCount!.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            var cast_json = serialize_cast(item);

            // TMDB renvoie très souvent `""` — et non `null` — pour un synopsis
            // absent en fr-FR. Le COALESCE de l'UPSERT ne protège pas de ça : une
            // chaîne vide n'est pas NULL, elle écrase donc le synopsis venu de la
            // migration. Chaque passage de RefreshTask vidait ainsi une fiche.
            var overview = string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview;
            // Même piège que le synopsis : TMDB rend `""` pour une date inconnue.
            var release_date = string.IsNullOrWhiteSpace(item.Date) ? null : item.Date;
            var directors = item.Directors.Count > 0 ? string.Join(", ", item.Directors) : null;
            var imdb_id = string.IsNullOrWhiteSpace(item.ExternalIds?.ImdbId) ? null : item.ExternalIds.ImdbId;
            var original_language = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? null : item.OriginalLanguage;

            // Écrite même quand TMDB n'a rien renvoyé : `facts_refreshed_at` doit être
            // posé, sinon la fiche serait relue à chaque ouverture (`needs_facts`).
            using (var cmd = con.CreateCommand())
            {
                // COALESCE partout : un rafraîchissement où TMDB ne renvoie pas le
                // casting ne doit pas effacer celui de la migration.
                cmd.CommandText = @"
                    INSERT INTO media_detail (media_key, overview, cast_json, release_date,
                                              directors, imdb_id, original_language,
                                              facts_refreshed_at)
                    VALUES ($k, $o, $cast, $date, $directors, $imdb, $lang, $now)
                    ON CONFLICT(media_key) DO UPDATE SET
                        overview           = COALESCE(excluded.overview, media_detail.overview),
                        cast_json          = COALESCE(excluded.cast_json, media_detail.cast_json),
                        release_date       = COALESCE(excluded.release_date, media_detail.release_date),
                        directors          = COALESCE(excluded.directors, media_detail.directors),
                        imdb_id            = COALESCE(excluded.imdb_id, media_detail.imdb_id),
                        original_language  = COALESCE(excluded.original_language, media_detail.original_language),
                        facts_refreshed_at = excluded.facts_refreshed_at";
                cmd.Parameters.AddWithValue("$k", media_key);
                cmd.Parameters.AddWithValue("$o", (object?)overview ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cast", (object?)cast_json ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$date", (object?)release_date ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$directors", (object?)directors ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$imdb", (object?)imdb_id ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lang", (object?)original_language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }

            // Le nom n'arrive que sur une fiche détaillée. On le mémorise dans le
            // référentiel `genre` : sans lui, le client ne peut afficher que « 18, 80 ».
            foreach (var genre in item.Genres ?? new List<TmdbGenre>())
            {
                if (string.IsNullOrWhiteSpace(genre.Name)) continue;

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO genre (genre_id, name) VALUES ($id, $n)
                    ON CONFLICT(genre_id) DO UPDATE SET name = excluded.name";
                cmd.Parameters.AddWithValue("$id", genre.Id);
                cmd.Parameters.AddWithValue("$n", genre.Name);
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
        public async Task<int> refresh_releases(string media_key, CancellationToken cancellation = default)
        {
            var parts = split(media_key);
            if (parts is null) return 0;

            // Un film n'a qu'une sortie, la sienne. Elle est enregistrée comme une
            // ligne de `release` en saison 0 / épisode 0 : le calendrier n'a pas à
            // savoir qu'il s'agit d'un film, il affiche une date et un titre.
            if (parts.Value.Type == "movie")
                return await refresh_movie_release(media_key, parts.Value.TmdbId, cancellation);

            var episodes = await _tmdb.get_episodes(parts.Value.TmdbId, cancellation);
            if (episodes.Count == 0) return 0;

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var con = _db.open();
            using var transaction = con.BeginTransaction();

            foreach (var ep in episodes)
                upsert_release(con, media_key, ep.SeasonNumber, ep.EpisodeNumber, ep.Name, ep.AirDate, now);

            transaction.Commit();
            return episodes.Count;
        }

        /// <summary>
        /// Enregistre la date de sortie d'un film comme une entrée de calendrier.
        ///
        /// Suivre un film n'avait aucun effet jusque-là : la ligne de `follow` était
        /// écrite, mais rien ne peuplait `release` pour lui, donc il n'apparaissait
        /// jamais. Une sortie unique se représente en saison 0 / épisode 0, ce qui
        /// laisse la clé primaire intacte sans cas particulier côté lecture.
        ///
        /// Parametres :
        /// - media_key (string) : clé du film
        /// - tmdb_id (int) : identifiant TMDB
        ///
        /// Output :
        /// - count (int) : 1 si une ligne a été écrite, 0 si TMDB ne connaît pas le film
        /// </summary>
        private async Task<int> refresh_movie_release(string media_key, int tmdb_id, CancellationToken cancellation)
        {
            var movie = await _tmdb.get_item("movie", tmdb_id, cancellation);
            if (movie is null) return 0;

            // La ligne est écrite **même sans date de sortie** (`air_date` est
            // NULL-able). C'est `refreshed_at` qui sort le film du lot au passage
            // suivant : sans ligne, MAX(refreshed_at) reste NULL et RefreshTask le
            // resélectionne à chaque exécution, à vie, pour un appel TMDB complet.
            var date = movie.Date is { Length: >= 10 } d ? d : null;

            using var con = _db.open();
            upsert_release(con, media_key, 0, 0, movie.DisplayTitle, date,
                           DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

            return 1;
        }

        /// <summary>
        /// Écrit ou met à jour une sortie. Un nom ou une date absents ne remplacent
        /// jamais une valeur déjà connue.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        /// - season (int) : saison (0 pour un film)
        /// - episode (int) : épisode (0 pour un film)
        /// - name (string | null) : nom de l'épisode, ou titre du film
        /// - air_date (string | null) : date de sortie `AAAA-MM-JJ`
        /// - now (string) : horodatage du rafraîchissement
        /// </summary>
        private static void upsert_release(
            SqliteConnection con, string media_key, int season, int episode,
            string? name, string? air_date, string now)
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
            cmd.Parameters.AddWithValue("$s", season);
            cmd.Parameters.AddWithValue("$e", episode);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$air", (object?)air_date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Vrai si le média est en base mais que son bloc « infos » n'a jamais été lu.
        ///
        /// Rattrapage paresseux : les fiches entrées avant le bloc n'auraient jamais
        /// leur date ni leur réalisation, puisque `ensure_exists`
        /// s'arrête dès que le média existe. `GET /media` force alors une relecture
        /// TMDB, **une seule fois** : `facts_refreshed_at` est posé même quand TMDB
        /// n'a rien. Tester les champs eux-mêmes relirait à chaque ouverture un média
        /// que TMDB ne date pas.
        ///
        /// Parametres :
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - needs (bool) : vrai si la ligne existe et que le bloc n'a jamais été lu
        /// </summary>
        public bool needs_facts(string media_key)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT 1 FROM media m
                LEFT JOIN media_detail d ON d.media_key = m.media_key
                WHERE m.media_key = $k AND d.facts_refreshed_at IS NULL";
            cmd.Parameters.AddWithValue("$k", media_key);

            return cmd.ExecuteScalar() is not null;
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
