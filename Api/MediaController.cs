using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Fiche média : métadonnées + données de l'utilisateur courant, en un seul appel.
    ///
    /// Remplace la cascade du front actuel, qui enchaîne 6 à 8 requêtes pour afficher
    /// une fiche (ProviderIds, MyRating, Watchlist, Monitoring, SeerrDetails, TmdbImages,
    /// Anime/Match…) puis assemble le tout en JavaScript.
    ///
    /// Cet endpoint n'était pas réalisable sur l'ancien schéma : les quatre systèmes
    /// d'identifiants concurrents empêchaient toute jointure entre note, watchlist et
    /// progression (cf. DESIGN.md).
    /// </summary>
    public class MediaController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly MediaCatalog _catalog;

        public MediaController(Db db, MediaCatalog catalog)
        {
            _db = db;
            _catalog = catalog;
        }

        // GET /api/EnhancedFin/v1/media/{mediaKey}?detail=true&enrich=true
        //
        // `enrich` fait entrer le média dans le référentiel s'il n'y est pas, en le
        // récupérant depuis TMDB. C'est une **demande explicite** de l'appelant, pas
        // un effet de bord : la règle « un GET ne crée pas de données » tient, ce
        // qu'un peuplement automatique aurait rompu en accumulant des milliers de
        // fiches jamais touchées (cf. DESIGN.md).
        //
        // Sert aux fiches de découverte, qui ont besoin du logo et du synopsis pour
        // s'afficher correctement alors que l'utilisateur n'a encore rien fait du média.
        [HttpGet("media/{mediaKey}")]
        public async Task<ActionResult> get(
            string mediaKey,
            [FromQuery] bool detail = false,
            [FromQuery] bool enrich = false)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);

            if (enrich && !await _catalog.ensure_exists(mediaKey))
                return problem(404, "Média inconnu", $"'{mediaKey}' est introuvable sur TMDB.");

            using var con = _db.open();

            // Une seule requête pour le média et toutes les données de l'utilisateur.
            // Les sous-requêtes corrélées évitent la multiplication de lignes qu'aurait
            // produite une cascade de LEFT JOIN sur des tables à cardinalités différentes.
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT m.media_key, m.media_type, m.tmdb_id, m.title, m.year,
                       m.poster_url, m.backdrop_url, m.logo_url, m.refreshed_at,
                       (SELECT score FROM rating      WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT 1     FROM watchlist   WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT 1     FROM follow      WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT hidden_at FROM hidden_item WHERE user_id=$u AND media_key=m.media_key)
                FROM media m
                WHERE m.media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read())
                return problem(404, "Média inconnu", $"'{mediaKey}' n'est pas dans le référentiel.");

            // ⚠️ `Dictionary` et non un record, à dessein — et c'est un compromis, pas
            // une préférence.
            //
            // Un record donnerait un contrat typé, mais changerait le JSON : System.Text
            // .Json applique `DefaultIgnoreCondition = WhenWritingNull` aux **propriétés**,
            // pas aux entrées de dictionnaire. Mesuré sur le serveur de test : cette route
            // émet aujourd'hui `"hiddenAt": null` et `"progress": null`, là où tout le
            // reste de l'API omet ses nuls. Passer aux records les ferait disparaître.
            //
            // Les rétablir demanderait un [JsonIgnore(Condition = Never)] sur chaque
            // propriété nullable — soit un type qui décrit une anomalie au lieu du
            // modèle. À arbitrer : soit on accepte la rupture (aucun client connu ne lit
            // ces deux champs), soit on garde le dictionnaire.
            var response = new Dictionary<string, object?>
            {
                ["mediaKey"] = rd.GetString(0),
                ["mediaType"] = rd.GetString(1),
                ["tmdbId"] = rd.GetInt32(2),
                ["title"] = rd.GetString(3),
                ["year"] = rd.IsDBNull(4) ? null : rd.GetInt32(4),
                ["posterUrl"] = rd.IsDBNull(5) ? null : rd.GetString(5),
                ["backdropUrl"] = rd.IsDBNull(6) ? null : rd.GetString(6),
                ["logoUrl"] = rd.IsDBNull(7) ? null : rd.GetString(7),
                ["refreshedAt"] = rd.GetString(8),
            };

            // Index alignés sur l'ordre du SELECT :
            // 9 = score, 10 = watchlist, 11 = follow, 12 = hidden_at.
            var me = new Dictionary<string, object?>
            {
                ["rating"] = rd.IsDBNull(9) ? null : rd.GetInt32(9),
                ["inWatchlist"] = !rd.IsDBNull(10),
                ["following"] = !rd.IsDBNull(11),
                ["hidden"] = !rd.IsDBNull(12),
                ["hiddenAt"] = rd.IsDBNull(12) ? null : rd.GetString(12),
            };
            rd.Close();

            me["progress"] = read_last_progress(con, user_id, mediaKey);
            response["genres"] = read_genres(con, mediaKey);
            response["me"] = me;

            if (detail)
                response["detail"] = read_detail(con, mediaKey);

            return Ok(response);
        }

        /// <summary>
        /// Dernière position de lecture connue pour ce média.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - user_id (string) : identifiant normalisé de l'appelant
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - progress (object | null) : saison, épisode, position, durée ; null si jamais lu
        /// </summary>
        private static object? read_last_progress(
            Microsoft.Data.Sqlite.SqliteConnection con, string user_id, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT season, episode, position_ticks, duration_ticks, lang, watched_at, updated_at
                FROM playback
                WHERE user_id = $u AND media_key = $k
                ORDER BY updated_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;

            return new
            {
                season = rd.GetInt32(0),
                episode = rd.GetInt32(1),
                positionTicks = rd.GetInt64(2),
                durationTicks = rd.GetInt64(3),
                lang = rd.IsDBNull(4) ? null : rd.GetString(4),
                watchedAt = rd.IsDBNull(5) ? null : rd.GetString(5),
                updatedAt = rd.GetString(6),
            };
        }

        /// <summary>
        /// Identifiants de genres TMDB du média.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - genres (List&lt;int&gt;) : identifiants de genres, liste vide si aucun
        /// </summary>
        private static List<int> read_genres(
            Microsoft.Data.Sqlite.SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT genre_id FROM media_genre WHERE media_key = $k ORDER BY genre_id";
            cmd.Parameters.AddWithValue("$k", media_key);

            var genres = new List<int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) genres.Add(rd.GetInt32(0));

            return genres;
        }

        /// <summary>
        /// Métadonnées lourdes (synopsis, casting), chargées uniquement sur demande.
        ///
        /// Elles vivent dans une table séparée : cast_json pèse 2 à 8 Ko et SQLite lit
        /// la ligne entière. Afficher une liste de 50 posters ne doit pas charger
        /// 50 castings.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - detail (object | null) : synopsis, casting, studios, scénaristes
        /// </summary>
        private static object? read_detail(
            Microsoft.Data.Sqlite.SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT overview, cast_json, screenwriters, studios
                FROM media_detail WHERE media_key = $k";
            cmd.Parameters.AddWithValue("$k", media_key);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;

            // cast_json est stocké tel que l'ancien front l'avait sérialisé : on le
            // ré-expose en JSON plutôt qu'en chaîne échappée, pour que le client
            // n'ait pas à le désérialiser une seconde fois.
            object? cast = null;
            if (!rd.IsDBNull(1))
            {
                try
                {
                    cast = JsonSerializer.Deserialize<JsonElement>(rd.GetString(1));
                }
                catch (JsonException)
                {
                    cast = rd.GetString(1);
                }
            }

            return new
            {
                overview = rd.IsDBNull(0) ? null : rd.GetString(0),
                cast,
                screenwriters = rd.IsDBNull(2) ? null : rd.GetString(2),
                studios = rd.IsDBNull(3) ? null : rd.GetString(3),
            };
        }
    }
}
