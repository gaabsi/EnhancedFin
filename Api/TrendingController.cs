using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Une page de tendances.
    ///
    /// Volontairement **sans `total`**, contrairement aux routes de liste : le
    /// classement TMDB n'a pas de cardinalité connue, et un total qui vaudrait
    /// toujours `items.Count` ne dirait rien — c'est le défaut qu'avait `/person`.
    /// C'est `nextCursor` qui porte l'information utile : absent, il n'y a plus rien
    /// à charger.
    ///
    /// Propriétés en camelCase à dessein — voir <see cref="ListResponse{T}"/>.
    /// </summary>
    public record TrendingResponse(IReadOnlyList<SearchItem> items, int? nextCursor);

    /// <summary>
    /// Tendances de la semaine, filtrables, pour le rail de découverte.
    ///
    /// Reprend le comportement du rail `trending.js` du front web, mais **côté
    /// serveur**. La raison tient à un fait mesurable : une page TMDB de vingt items
    /// ne contient que une à trois séries d'animation. Filtrer après coup laisserait
    /// donc un rail « Animés » quasi vide, ce qui a obligé le front web à empiler les
    /// pages lui-même (`_ensure_minimum_visible`). Cette logique vit ici, à un seul
    /// endroit, plutôt que d'être réécrite dans chaque client.
    ///
    /// Aucune écriture : c'est un `GET`, il ne peuple pas le référentiel. Un média
    /// n'y entre que si l'utilisateur le note, l'ajoute ou le suit.
    /// </summary>
    public class TrendingController : EnhancedFinController
    {
        /// <summary>
        /// Nombre d'items visé avant de rendre la main.
        ///
        /// Un objectif, pas un plafond : la dernière page TMDB lue est rendue
        /// entière. La couper laisserait ses derniers items dans un angle mort —
        /// ni rendus maintenant, ni atteignables ensuite, puisque le curseur
        /// désigne une page et non une position dans une page.
        /// </summary>
        private const int TargetItems = 20;

        /// <summary>
        /// Pages TMDB consultées au maximum par appel.
        ///
        /// Borne le coût d'une requête : sans elle, un filtre qui ne rencontrerait
        /// aucun résultat ferait défiler tout le classement en un seul appel HTTP.
        ///
        /// ⚠️ Cinq pages ne **suffisent pas** pour « Animés » : mesuré, cent items du
        /// classement n'en contiennent qu'une dizaine. L'appel rend donc moins que
        /// <see cref="TargetItems"/> et laisse le curseur avancer de cinq d'un coup.
        /// C'est assumé — le rail se complète au défilement, et la borne protège le
        /// Pi d'une rafale d'appels sortants.
        /// </summary>
        private const int MaxPagesPerCall = 5;

        /// <summary>
        /// Dernière page du classement hebdomadaire que l'on accepte de demander.
        /// Reprise du plafond de l'ancien plugin : au-delà, ce ne sont plus des
        /// tendances.
        /// </summary>
        private const int LastPage = 30;

        /// <summary>Identifiant TMDB du genre « Animation ».</summary>
        private const int AnimationGenre = 16;

        private readonly Db _db;
        private readonly TmdbClient _tmdb;
        private readonly JellyfinLibrary _library;

        public TrendingController(Db db, TmdbClient tmdb, JellyfinLibrary library)
        {
            _db = db;
            _tmdb = tmdb;
            _library = library;
        }

        // GET /api/EnhancedFin/v1/trending?filter=anime&cursor=3
        [HttpGet("trending")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        // `filter` omis vaut « tout », le défaut du front web : le classement mixte
        // est la vue la plus intéressante, films et séries étant ordonnés entre eux.
        public async Task<ActionResult<TrendingResponse>> get(
            [FromQuery] string? filter = null,
            [FromQuery] int? cursor = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();

            if (!is_valid_filter(filter))
                return problem(400, "Filtre invalide",
                               "filter doit valoir 'all', 'movie', 'tv', 'anime', ou être omis.");

            var page = cursor ?? 1;
            if (page is < 1 or > LastPage)
                return problem(400, "Curseur invalide",
                               $"cursor doit être compris entre 1 et {LastPage}.");

            var (hits, next) = await collect(filter ?? "all", page);

            return Ok(new TrendingResponse(enrich(user.Value, hits), next));
        }

        private static bool is_valid_filter(string? filter) =>
            filter is null or "all" or "movie" or "tv" or "anime";

        /// <summary>
        /// Empile les pages TMDB jusqu'à réunir assez d'items retenus.
        ///
        /// Parametres :
        /// - filter (string) : 'all', 'movie', 'tv' ou 'anime'
        /// - from_page (int) : première page TMDB à lire
        ///
        /// Output :
        /// - hits (List&lt;TmdbItem&gt;) : items retenus, dans l'ordre du classement
        /// - next (int?) : page à demander pour la suite, null s'il n'y a plus rien
        /// </summary>
        private async Task<(List<TmdbItem> Hits, int? Next)> collect(string filter, int from_page)
        {
            var hits = new List<TmdbItem>();

            // TMDB rejoue le même item sur des pages voisines : son classement
            // évolue entre les appels, et `/trending/all/week` présente de vrais
            // recouvrements (mesuré : 11 répétitions sur 80 items, soit ~14 %).
            //
            // ⚠️ Cette dédup ne vaut **que pour l'appel en cours**. Le serveur est
            // sans état : il ignore ce que le client a déjà affiché, et le curseur
            // ne désigne qu'une page. **C'est donc au client d'écarter les clés
            // déjà accumulées** — ce que fait le front web avec son `seen`
            // persistant. Sans ça, le rail répète des affiches en se prolongeant.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var page = from_page;
            var pages_read = 0;

            while (hits.Count < TargetItems && pages_read < MaxPagesPerCall && page <= LastPage)
            {
                var results = await _tmdb.trending(page);
                pages_read++;
                page++;

                // Page vide : TMDB n'a plus rien, ou l'appel a échoué. Dans les deux
                // cas, insister sur les pages suivantes ne rapporterait rien.
                if (results.Count == 0) return (hits, null);

                foreach (var item in results)
                {
                    if (!is_eligible(item, filter)) continue;
                    if (!seen.Add(media_key_of(item))) continue;

                    hits.Add(item);
                }
            }

            return (hits, page > LastPage ? null : page);
        }

        /// <summary>
        /// Décide si un item du classement a sa place dans le rail.
        ///
        /// Parametres :
        /// - item (TmdbItem) : item renvoyé par TMDB
        /// - filter (string) : filtre demandé
        ///
        /// Output :
        /// - keep (bool) : faux pour une personne, un item sans affiche, un titre
        ///   illisible, ou un type que le filtre écarte
        /// </summary>
        private static bool is_eligible(TmdbItem item, string filter)
        {
            // `/trending/all/week` rend aussi des personnes, qui n'ont pas de fiche
            // média et dont la clé n'aurait aucun sens.
            if (item.MediaType is not ("movie" or "tv")) return false;

            // Une affiche absente laisserait une tuile grise dans un rail dont c'est
            // tout l'intérêt visuel. Le front web applique le même écart.
            if (string.IsNullOrEmpty(item.PosterPath)) return false;

            // Un titre en japonais ou en coréen est illisible pour l'utilisateur
            // visé. `get_item` sait se replier sur l'anglais, mais pas ici : ce
            // serait un aller-retour par item.
            if (TmdbClient.is_non_latin(item.DisplayTitle)) return false;

            return filter switch
            {
                "movie" => item.MediaType == "movie",
                "tv" => item.MediaType == "tv",
                // Un anime est une **série** d'animation. Un film d'animation — Pixar,
                // Disney — reste dans « Films » : c'est la règle du front web, et elle
                // correspond à ce que les gens entendent par « animé ».
                "anime" => item.MediaType == "tv"
                           && item.GenreIds?.Contains(AnimationGenre) == true,
                _ => true,
            };
        }

        private static string media_key_of(TmdbItem item) => $"{item.MediaType}:{item.Id}";

        /// <summary>
        /// Habille les items TMDB de ce que le serveur sait déjà d'eux.
        ///
        /// Deux ajouts, chacun en une seule requête pour tout le lot : ce que
        /// l'utilisateur en a fait (note, watchlist, suivi) et ce que la bibliothèque
        /// en porte. Sans le second, le client ne peut pas proposer « Lire » sur un
        /// média qu'il possède déjà.
        ///
        /// Parametres :
        /// - user (Guid) : appelant, pour ses droits bibliothèque
        /// - hits (List&lt;TmdbItem&gt;) : items retenus
        ///
        /// Output :
        /// - items (List&lt;SearchItem&gt;) : items prêts à être sérialisés
        /// </summary>
        /// <remarks>
        /// La forme est celle de <see cref="SearchItem"/>, et ce n'est pas un
        /// raccourci : une tendance et un résultat de recherche sont le même objet —
        /// un média découvert, avec mon état dessus et sa disponibilité locale.
        /// En fabriquer un jumeau imposerait de le faire évoluer deux fois, et au
        /// client d'en décoder deux.
        /// </remarks>
        private List<SearchItem> enrich(Guid user, List<TmdbItem> hits)
        {
            if (hits.Count == 0) return new List<SearchItem>();

            var keys = hits.ConvertAll(media_key_of);

            using var con = _db.open();
            var states = read_my_state(con, user.ToString("D"), keys);

            // L'index porte l'utilisateur : il ne doit pas révéler les bibliothèques
            // auxquelles il n'a pas accès.
            var in_library = _library.index(user);

            return hits.ConvertAll(item =>
            {
                var media_key = media_key_of(item);
                var state = states.GetValueOrDefault(media_key);
                in_library.TryGetValue(media_key, out var match);

                return new SearchItem(
                    mediaKey: media_key,
                    mediaType: item.MediaType!,
                    tmdbId: item.Id,
                    title: item.DisplayTitle,
                    originalTitle: item.OriginalTitle ?? item.OriginalName,
                    year: item.Year,
                    releaseDate: item.Date,
                    overview: item.Overview,
                    posterUrl: TmdbClient.image_url(item.PosterPath),
                    backdropUrl: TmdbClient.image_url(item.BackdropPath),
                    genreIds: item.GenreIds,
                    known: state?.Known ?? false,
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D"),
                    me: new SearchItemMe(
                        rating: state?.Rating,
                        inWatchlist: state?.InWatchlist ?? false,
                        following: state?.Following ?? false));
            });
        }
    }
}
