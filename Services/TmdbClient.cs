using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    public record TmdbGenre(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name = null);

    /// <summary>Un membre du casting, dans l'ordre d'affiche de TMDB.</summary>
    public record TmdbCastMember(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("character")] string? Character,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        [property: JsonPropertyName("order")] int Order);

    public record TmdbCredits(
        [property: JsonPropertyName("cast")] List<TmdbCastMember>? Cast,
        // L'équipe technique : n'y sert que le poste `Director`, voir `TmdbItem.Directors`.
        [property: JsonPropertyName("crew")] List<TmdbCredit>? Crew = null);

    /// <summary>Un créateur de série, tel que TMDB le liste dans `created_by`.</summary>
    public record TmdbCreator(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name);

    /// <summary>Un rôle tenu au fil d'une série.</summary>
    public record TmdbAggregateRole(
        [property: JsonPropertyName("character")] string? Character);

    /// <summary>
    /// Membre du casting d'une série.
    ///
    /// Forme distincte du casting de film : un acteur peut tenir plusieurs rôles
    /// au fil des saisons, d'où `roles` au lieu d'un `character` unique.
    /// </summary>
    public record TmdbAggregateCastMember(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("roles")] List<TmdbAggregateRole>? Roles);

    public record TmdbAggregateCredits(
        [property: JsonPropertyName("cast")] List<TmdbAggregateCastMember>? Cast);

    public record TmdbItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("genres")] List<TmdbGenre>? Genres,
        [property: JsonPropertyName("genre_ids")] List<int>? GenreIds,
        [property: JsonPropertyName("images")] TmdbImages? Images = null,
        // Sert à classer films et séries ensemble : chaque liste TMDB est triée
        // par popularité, mais rien ne les ordonne entre elles.
        [property: JsonPropertyName("popularity")] double? Popularity = null,
        [property: JsonPropertyName("vote_average")] double? VoteAverage = null,
        [property: JsonPropertyName("credits")] TmdbCredits? Credits = null,
        // Les séries n'ont presque jamais de `credits` exploitable : leur casting
        // vit dans `aggregate_credits`, agrégé sur toutes les saisons.
        [property: JsonPropertyName("aggregate_credits")] TmdbAggregateCredits? AggregateCredits = null,
        [property: JsonPropertyName("vote_count")] int? VoteCount = null,
        // Renseigné par les seules listes mixtes — `/trending/all/week` rend films,
        // séries **et** personnes dans le même tableau. Nul partout ailleurs, le type
        // étant alors connu de l'appelant.
        [property: JsonPropertyName("media_type")] string? MediaType = null,
        [property: JsonPropertyName("created_by")] List<TmdbCreator>? CreatedBy = null)
    {
        /// <summary>
        /// Logo à retenir, par ordre de préférence : français, anglais, puis sans
        /// langue. Un logo anglais vaut mieux que pas de logo — la fiche retomberait
        /// sinon sur le titre en texte.
        /// </summary>
        public string? LogoPath
        {
            get
            {
                var logos = Images?.Logos;
                if (logos is null || logos.Count == 0) return null;

                return logos.Find(l => l.Language == "fr")?.FilePath
                    ?? logos.Find(l => l.Language == "en")?.FilePath
                    ?? logos.Find(l => l.Language is null)?.FilePath
                    ?? logos[0].FilePath;
            }
        }

        /// <summary>Titre affichable : TMDB le nomme `title` pour un film, `name` pour une série.</summary>
        public string DisplayTitle => Title ?? Name ?? "Sans titre";

        /// <summary>Date de sortie, quel que soit le type.</summary>
        public string? Date => ReleaseDate ?? FirstAirDate;

        public int? Year =>
            Date is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

        /// <summary>
        /// Qui signe l'œuvre : la réalisation d'un film, les créateurs d'une série.
        ///
        /// Une série n'a pas de réalisateur mais un par épisode ; `created_by` est ce
        /// que TMDB met en avant à la place. Les deux ne peuvent pas coexister : un
        /// film n'a pas de `created_by`, une série n'a pas de `credits` (voir
        /// `get_item`, qui demande `aggregate_credits` pour elle).
        /// </summary>
        public List<string> Directors =>
            (CreatedBy?.Select(c => c.Name)
                ?? Credits?.Crew?.Where(c => c.Job == "Director").Select(c => c.Name)
                ?? Enumerable.Empty<string?>())
            .OfType<string>()
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Une image TMDB. `iso_639_1` vaut null pour une image sans texte, donc
    /// utilisable quelle que soit la langue.
    /// </summary>
    public record TmdbImage(
        [property: JsonPropertyName("file_path")] string? FilePath,
        [property: JsonPropertyName("iso_639_1")] string? Language);

    public record TmdbImages(
        [property: JsonPropertyName("logos")] List<TmdbImage>? Logos);

    /// <summary>
    /// Un crédit de filmographie, acteur ou équipe technique confondus.
    ///
    /// TMDB sert les deux dans la même forme, à un champ près : `character` pour un
    /// rôle joué, `job` pour un poste occupé.
    /// </summary>
    public record TmdbCredit(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("media_type")] string? MediaType,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("character")] string? Character,
        [property: JsonPropertyName("job")] string? Job,
        [property: JsonPropertyName("popularity")] double? Popularity)
    {
        public string DisplayTitle => Title ?? Name ?? "Sans titre";

        public string? Date => ReleaseDate ?? FirstAirDate;

        public int? Year =>
            Date is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

        /// <summary>Ce qu'on affiche sous l'affiche : le rôle joué, ou le poste.</summary>
        public string? Role => string.IsNullOrWhiteSpace(Character) ? Job : Character;
    }

    public record TmdbCombinedCredits(
        [property: JsonPropertyName("cast")] List<TmdbCredit>? Cast,
        [property: JsonPropertyName("crew")] List<TmdbCredit>? Crew);

    public record TmdbPerson(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("biography")] string? Biography,
        [property: JsonPropertyName("birthday")] string? Birthday,
        [property: JsonPropertyName("deathday")] string? Deathday,
        [property: JsonPropertyName("place_of_birth")] string? PlaceOfBirth,
        [property: JsonPropertyName("profile_path")] string? ProfilePath,
        [property: JsonPropertyName("combined_credits")] TmdbCombinedCredits? CombinedCredits);

    public record TmdbSearchResponse(
        [property: JsonPropertyName("results")] List<TmdbItem>? Results);

    public record TmdbEpisode(
        [property: JsonPropertyName("episode_number")] int EpisodeNumber,
        [property: JsonPropertyName("season_number")] int SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("air_date")] string? AirDate);

    public record TmdbSeason(
        [property: JsonPropertyName("episodes")] List<TmdbEpisode>? Episodes);

    public record TmdbSeasonRef(
        [property: JsonPropertyName("season_number")] int SeasonNumber);

    public record TmdbTvDetail(
        [property: JsonPropertyName("seasons")] List<TmdbSeasonRef>? Seasons);

    /// <summary>
    /// Accès à l'API TMDB.
    ///
    /// Reprend deux leçons du plugin actuel :
    /// - un HttpClient **statique partagé** : en créer un par appel laissait des sockets
    ///   en TIME_WAIT (~2 min sous Linux) et finissait en « Resource temporarily
    ///   unavailable » dès que les tâches de fond sollicitaient TMDB ;
    /// - un **repli sur l'anglais** quand la fiche française renvoie un titre non latin
    ///   (fréquent sur les animes, dont le titre FR retombe sur le japonais).
    ///
    /// Différence avec l'existant : il n'y a plus de table `tmdb_enrich_cache`.
    /// La table `media` **est** le cache persistant, avec sa colonne `refreshed_at`.
    /// Le cache mémoire ci-dessous ne sert qu'aux recherches, qui ne correspondent
    /// à aucun média unique et n'ont donc pas leur place en base.
    /// </summary>
    public class TmdbClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";
        private const string ImageBase = "https://image.tmdb.org/t/p/w500";

        /// <summary>
        /// Plafond de lecture d'une réponse TMDB.
        ///
        /// La plus grosse réponse légitime est une filmographie complète, de l'ordre
        /// de quelques centaines de kilo-octets. Sans plafond, `GetAsync` bufférise
        /// tout ce qui arrive : une réponse anormale suffirait à faire enfler la
        /// mémoire du Pi, où la limite du compose est de toute façon ignorée.
        /// Au-delà, la lecture lève — et `fetch` renvoie `null`, comme pour toute
        /// autre panne réseau.
        /// </summary>
        private const long MaxResponseBytes = 8L * 1024 * 1024;

        private static readonly HttpClient _client = new(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

        // Recherches uniquement, TTL court : le catalogue TMDB bouge peu mais les
        // requêtes sont nombreuses et éphémères.
        //
        // `MemoryCache` et non `ConcurrentDictionary` : la clé dérive du texte saisi,
        // donc d'une entrée utilisateur. Un dictionnaire qui ne purge jamais grossit
        // d'une entrée par recherche distincte et n'a aucune borne — sur un Pi, c'est
        // la mémoire du serveur qui finit par payer. `SizeLimit` fait évincer les
        // entrées les plus anciennes au-delà du plafond.
        private const int CacheEntries = 500;

        private static readonly MemoryCache _cache =
            new(new MemoryCacheOptions { SizeLimit = CacheEntries });

        private static readonly TimeSpan _search_ttl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Durée pendant laquelle un média introuvable sur TMDB le reste.
        ///
        /// Courte : une fiche peut apparaître au catalogue. Assez longue pour qu'une
        /// boucle de `PUT` sur une clé inexistante n'émette pas un appel sortant par
        /// tentative.
        /// </summary>
        private static readonly TimeSpan _miss_ttl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Durée de vie d'une fiche personne au cache.
        ///
        /// Longue à dessein : une filmographie ne bouge pas dans la journée, et
        /// c'est la seule route du plugin qui expose directement un identifiant
        /// choisi par l'appelant à un appel sortant. Sans cache, une boucle sur les
        /// identifiants se traduisait en autant d'appels à TMDB — et un 429 sur la
        /// clé ne casse pas que `/person` : `fetch` renvoie alors `null` pour la
        /// recherche, l'enrichissement et la tâche d'entretien, pour tout le monde.
        /// </summary>
        private static readonly TimeSpan _person_ttl = TimeSpan.FromHours(12);

        /// <summary>
        /// Durée de vie d'une page de tendances.
        ///
        /// Le classement TMDB est hebdomadaire : une heure est largement en deçà de sa
        /// fréquence de changement. Surtout, cette liste est **la même pour tous les
        /// utilisateurs** — c'est le brut TMDB qui est mis en cache, l'état personnel
        /// (note, watchlist, bibliothèque) étant ajouté après, par appelant.
        /// </summary>
        private static readonly TimeSpan _trending_ttl = TimeSpan.FromHours(1);

        private readonly ILogger<TmdbClient> _logger;

        public TmdbClient(ILogger<TmdbClient> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// URL complète d'une image TMDB.
        ///
        /// ⚠️ Un logo peut être un **SVG**, que les clients affichent mal (Swiftfin :
        /// logo vide dans l'en-tête, vécu sur « Demain tout commence »). TMDB sert la
        /// même image rastérisée si l'on demande `.png` à la place (vérifié : 200,
        /// `image/png`). Voir aussi `Db.fix_svg_logos` pour les fiches déjà en base.
        ///
        /// Parametres :
        /// - path (string | null) : chemin TMDB, `/abc.jpg`
        ///
        /// Output :
        /// - url (string | null) : URL complète, null sans chemin
        /// </summary>
        public static string? image_url(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var raster = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? path[..^4] + ".png"
                : path;
            return ImageBase + raster;
        }

        /// <summary>
        /// Recherche des médias par titre.
        ///
        /// Parametres :
        /// - query (string) : texte saisi
        /// - media_type (string) : 'movie' ou 'tv'
        ///
        /// Output :
        /// - results (List&lt;TmdbItem&gt;) : candidats, liste vide si aucun ou en cas d'échec
        /// </summary>
        public async Task<List<TmdbItem>> search(string query, string media_type)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<TmdbItem>();

            var cache_key = $"search|{media_type}|{query.Trim().ToLowerInvariant()}";
            if (_cache.TryGetValue(cache_key, out List<TmdbItem>? hit) && hit is not null) return hit;

            var encoded = WebUtility.UrlEncode(query.Trim());
            var res = await fetch<TmdbSearchResponse>(
                $"/search/{media_type}", $"&query={encoded}&include_adult=false&page=1");

            var results = res?.Results ?? new List<TmdbItem>();

            // Seuls les résultats non vides sont retenus : mettre en cache les échecs
            // et les recherches sans réponse remplirait le cache de bruit fabriqué
            // depuis l'extérieur, en évinçant les entrées utiles.
            if (results.Count > 0) put(cache_key, results);

            return results;
        }

        /// <summary>
        /// Une page des tendances de la semaine, films et séries mélangés.
        ///
        /// `/trending/all/week` plutôt que `/trending/movie` puis `/trending/tv` : un
        /// seul aller-retour, et surtout un classement **commun** aux deux types. Deux
        /// listes séparées ne s'ordonnent pas entre elles — c'est le piège déjà
        /// rencontré sur `/search`, où les séries se retrouvaient derrière tous les
        /// films. TMDB y joint `genre_ids`, ce qui rend le filtre « animés »
        /// calculable sans requête de plus.
        ///
        /// Les personnes, que cette route rend aussi, sont écartées par l'appelant :
        /// ici on ne fait que rapporter ce que TMDB renvoie.
        ///
        /// Parametres :
        /// - page (int) : numéro de page TMDB, à partir de 1
        ///
        /// Output :
        /// - results (List&lt;TmdbItem&gt;) : items de la page, liste vide en cas d'échec
        /// </summary>
        public async Task<List<TmdbItem>> trending(int page)
        {
            var cache_key = $"trending|week|{page}";
            if (_cache.TryGetValue(cache_key, out List<TmdbItem>? hit) && hit is not null) return hit;

            var res = await fetch<TmdbSearchResponse>("/trending/all/week", $"&page={page}");
            var results = res?.Results ?? new List<TmdbItem>();

            // Comme pour la recherche : une page vide n'est pas mise en cache, sans quoi
            // un incident réseau figerait une liste vide pendant une heure.
            if (results.Count > 0) put(cache_key, results, _trending_ttl);

            return results;
        }

        /// <summary>
        /// Fiche détaillée d'un média, avec repli sur l'anglais si le titre français
        /// revient dans un alphabet non latin.
        ///
        /// Parametres :
        /// - media_type (string) : 'movie' ou 'tv'
        /// - tmdb_id (int) : identifiant TMDB
        ///
        /// Output :
        /// - item (TmdbItem | null) : fiche, null si introuvable
        /// </summary>
        public async Task<TmdbItem?> get_item(string media_type, int tmdb_id)
        {
            // `append_to_response` évite un second aller-retour : les logos arrivent
            // avec la fiche. `include_image_language` est indispensable — sans lui
            // TMDB ne renvoie que les images de la langue demandée, et les logos
            // sans texte (iso_639_1 = null) seraient exclus.
            // Un seul aller-retour rapporte logo, casting et note.
            // Le bloc de casting diffère selon le type : `credits` pour un film,
            // `aggregate_credits` pour une série, dont le `credits` est presque
            // toujours vide.
            var credits_block = media_type == "tv" ? "aggregate_credits" : "credits";
            var with_images =
                $"&append_to_response=images,{credits_block}&include_image_language=fr,en,null";

            // Une absence est mise en cache elle aussi : sans ça, chaque `PUT` sur une
            // clé inexistante repartait vers TMDB.
            var miss_key = $"miss|{media_type}|{tmdb_id}";
            if (_cache.TryGetValue(miss_key, out _)) return null;

            var item = await fetch<TmdbItem>($"/{media_type}/{tmdb_id}", with_images);
            if (item is null)
            {
                put(miss_key, "", _miss_ttl);
                return null;
            }

            if (is_non_latin(item.DisplayTitle))
            {
                var en = await fetch<TmdbItem>($"/{media_type}/{tmdb_id}", with_images, lang: "en-US");
                if (en is not null && !is_non_latin(en.DisplayTitle)) return en;
            }

            return item;
        }

        /// <summary>
        /// Dates de diffusion de tous les épisodes d'une série.
        ///
        /// Alimente la table `release`, qui sert le calendrier des sorties.
        ///
        /// Parametres :
        /// - tmdb_id (int) : identifiant TMDB de la série
        ///
        /// Output :
        /// - episodes (List&lt;TmdbEpisode&gt;) : épisodes de toutes les saisons, hors saison 0 (specials)
        /// </summary>
        public async Task<List<TmdbEpisode>> get_episodes(int tmdb_id)
        {
            var episodes = new List<TmdbEpisode>();

            var detail = await fetch<TmdbTvDetail>($"/tv/{tmdb_id}");
            if (detail?.Seasons is null) return episodes;

            foreach (var season in detail.Seasons)
            {
                // Saison 0 = épisodes spéciaux, hors calendrier des sorties régulières.
                if (season.SeasonNumber == 0) continue;

                var data = await fetch<TmdbSeason>($"/tv/{tmdb_id}/season/{season.SeasonNumber}");
                if (data?.Episodes is not null) episodes.AddRange(data.Episodes);
            }

            return episodes;
        }

        /// <summary>
        /// Fiche d'une personne, filmographie comprise.
        ///
        /// `combined_credits` rapporte films et séries en un seul appel, acteur et
        /// équipe confondus — d'où l'absence de seconde requête.
        ///
        /// Mise en cache, symétriquement à `get_item` : réponse **et** absence.
        /// C'était la seule méthode qui partait vers TMDB à chaque appel, alors
        /// qu'elle est justement celle dont l'identifiant vient de l'extérieur —
        /// une amplification de 1 pour 1 jusqu'au 429.
        ///
        /// Parametres :
        /// - tmdb_id (int) : identifiant TMDB de la personne
        ///
        /// Output :
        /// - person (TmdbPerson | null) : null si TMDB ne la connaît pas
        /// </summary>
        public async Task<TmdbPerson?> get_person(int tmdb_id)
        {
            var cache_key = $"person|{tmdb_id}";
            if (_cache.TryGetValue(cache_key, out TmdbPerson? hit) && hit is not null) return hit;

            var miss_key = $"miss|person|{tmdb_id}";
            if (_cache.TryGetValue(miss_key, out _)) return null;

            var person = await fetch<TmdbPerson>($"/person/{tmdb_id}", "&append_to_response=combined_credits");
            if (person is null)
            {
                put(miss_key, "", _miss_ttl);
                return null;
            }

            put(cache_key, person, _person_ttl);

            return person;
        }

        /// <summary>
        /// Range une valeur au cache, en lui donnant une taille pour que `SizeLimit`
        /// puisse compter.
        ///
        /// Parametres :
        /// - key (string) : clé de cache
        /// - value (object) : valeur à retenir
        /// - ttl (TimeSpan?) : durée de vie, celle des recherches par défaut
        /// </summary>
        private static void put(string key, object value, TimeSpan? ttl = null) =>
            _cache.Set(key, value, new MemoryCacheEntryOptions
            {
                // Taille 1 par entrée : le plafond se lit alors en nombre d'entrées,
                // ce qui est la grandeur qu'on veut borner ici.
                Size = 1,
                AbsoluteExpirationRelativeToNow = ttl ?? _search_ttl,
            });

        /// <summary>
        /// Vrai si le texte contient des caractères hors alphabet latin.
        /// Sert à détecter les titres « français » qui sont en réalité le titre
        /// original japonais, chinois ou coréen.
        /// </summary>
        /// <remarks>
        /// Public parce que les listes de découverte s'en servent aussi : `get_item`
        /// peut se replier sur l'anglais, mais une liste de vingt items ne le peut
        /// pas — ce serait un aller-retour par ligne. Elle écarte donc l'item, comme
        /// le fait le front web.
        /// </remarks>
        public static bool is_non_latin(string text)
        {
            foreach (var c in text)
                if (c > 0x2FFF) return true;

            return false;
        }

        private async Task<T?> fetch<T>(string path, string extra = "", string lang = "fr-FR")
            where T : class
        {
            var key = Plugin.Instance?.Configuration?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogWarning("[EnhancedFin] Clé API TMDB non configurée");
                return null;
            }

            var url = $"{BaseUrl}{path}?api_key={key}&language={lang}{extra}";
            try
            {
                using var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 = média inexistant, cas normal lors d'un sondage movie puis tv.
                    if (response.StatusCode != HttpStatusCode.NotFound)
                        _logger.LogWarning("[EnhancedFin] TMDB {Path} → HTTP {Code}",
                                           path, (int)response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogError(ex, "[EnhancedFin] TMDB {Path} a échoué", path);
                return null;
            }
        }
    }
}
