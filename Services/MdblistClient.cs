using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    /// <summary>Notes Rotten Tomatoes d'un média, en pourcentage. Nulles si MDBList ne les a pas.</summary>
    public record MdblistScores(int? RtCritics, int? RtAudience);

    public record MdblistRating(
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("value")] double? Value);

    public record MdblistItem(
        [property: JsonPropertyName("ratings")] List<MdblistRating>? Ratings);

    /// <summary>
    /// Notes Rotten Tomatoes via MDBList, pour le bloc « infos » d'une fiche.
    ///
    /// Format vérifié sur une vraie réponse (2026-09-23) :
    /// `GET https://api.mdblist.com/tmdb/{movie|show}/{tmdbId}?apikey=…` rend un
    /// tableau `ratings` de `{ source, value }`. `tomatoes` = critiques (Tomatometer),
    /// `popcorn` = public (Popcornmeter), en pourcentage. Une série se dit `show`
    /// (`tv` → 400), un id inconnu → 404.
    ///
    /// Mis en cache dans `media_score` : l'offre gratuite plafonne à 1 000 requêtes
    /// par jour, et des notes établies ne bougent presque plus.
    /// </summary>
    public class MdblistClient
    {
        private const string BaseUrl = "https://api.mdblist.com";

        /// <summary>Durée de vie d'une note en cache.</summary>
        private static readonly TimeSpan _ttl = TimeSpan.FromDays(7);

        /// <summary>
        /// Court à dessein : l'appel se fait pendant l'ouverture d'une fiche. Au-delà,
        /// mieux vaut une fiche sans notes qu'une fiche qui attend.
        /// </summary>
        private static readonly HttpClient _client = new(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        private readonly Db _db;
        private readonly ILogger<MdblistClient> _logger;

        public MdblistClient(Db db, ILogger<MdblistClient> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Notes RT d'un média, depuis le cache ou MDBList.
        ///
        /// Parametres :
        /// - media_key (string) : clé du média, `movie:{id}` ou `tv:{id}`
        ///
        /// Output :
        /// - scores (MdblistScores | null) : notes, null si inconnues (sans clé, en
        ///   panne, ou jamais obtenues)
        /// </summary>
        public async Task<MdblistScores?> get_scores(string media_key)
        {
            var cached = read_cache(media_key, out var fetched_at);
            if (cached is not null && DateTime.UtcNow - fetched_at < _ttl) return cached;

            var parts = MediaCatalog.split(media_key);
            var key = Plugin.Instance?.Configuration?.MdblistApiKey;
            if (parts is null || string.IsNullOrWhiteSpace(key)) return cached;

            var fetched = await fetch(parts.Value.Type == "tv" ? "show" : "movie", parts.Value.TmdbId, key);

            // Panne : on garde l'ancienne note plutôt que rien, et on n'écrit pas —
            // une erreur réseau n'est pas « MDBList n'a pas de note ».
            if (fetched is null) return cached;

            write_cache(media_key, fetched);
            return fetched;
        }

        /// <summary>
        /// Appelle MDBList.
        ///
        /// ⚠️ Ne jamais journaliser l'URL : la clé y figure en clair. Seul le chemin
        /// part dans les logs.
        ///
        /// Parametres :
        /// - type (string) : `movie` ou `show`
        /// - tmdb_id (int) : identifiant TMDB
        /// - key (string) : clé API
        ///
        /// Output :
        /// - scores (MdblistScores | null) : notes (nulles si le média est inconnu),
        ///   null en cas d'échec
        /// </summary>
        private async Task<MdblistScores?> fetch(string type, int tmdb_id, string key)
        {
            var path = $"/tmdb/{type}/{tmdb_id}";
            try
            {
                using var response = await _client.GetAsync($"{BaseUrl}{path}?apikey={Uri.EscapeDataString(key)}");

                // Inconnu de MDBList : c'est une réponse, qu'on met en cache comme telle.
                if (response.StatusCode == HttpStatusCode.NotFound) return new MdblistScores(null, null);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[EnhancedFin] MDBList {Path} → HTTP {Code}", path, (int)response.StatusCode);
                    return null;
                }

                var item = JsonSerializer.Deserialize<MdblistItem>(await response.Content.ReadAsStringAsync());
                return new MdblistScores(score(item, "tomatoes"), score(item, "popcorn"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // `ex.Message` et non `ex` : la trace d'une HttpRequestException peut
                // recopier l'URL demandée, donc la clé.
                _logger.LogWarning("[EnhancedFin] MDBList {Path} a échoué : {Message}", path, ex.Message);
                return null;
            }
        }

        private static int? score(MdblistItem? item, string source) =>
            item?.Ratings?.FirstOrDefault(r => r.Source == source)?.Value is double v
                ? (int)Math.Round(v)
                : null;

        private MdblistScores? read_cache(string media_key, out DateTime fetched_at)
        {
            fetched_at = DateTime.MinValue;

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT rt_critics, rt_audience, fetched_at FROM media_score WHERE media_key = $k";
            cmd.Parameters.AddWithValue("$k", media_key);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;

            fetched_at = DateTime.Parse(rd.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return new MdblistScores(
                rd.IsDBNull(0) ? null : rd.GetInt32(0),
                rd.IsDBNull(1) ? null : rd.GetInt32(1));
        }

        private void write_cache(string media_key, MdblistScores scores)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO media_score (media_key, rt_critics, rt_audience, fetched_at)
                VALUES ($k, $c, $a, $now)
                ON CONFLICT(media_key) DO UPDATE SET
                    rt_critics  = excluded.rt_critics,
                    rt_audience = excluded.rt_audience,
                    fetched_at  = excluded.fetched_at";
            cmd.Parameters.AddWithValue("$k", media_key);
            cmd.Parameters.AddWithValue("$c", (object?)scores.RtCritics ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", (object?)scores.RtAudience ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }
}
