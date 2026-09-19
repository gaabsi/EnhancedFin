using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    /// <summary>Correspondance entre un média du référentiel et l'item Jellyfin qui le porte.</summary>
    public record LibraryMatch(string MediaKey, Guid JellyfinId, string Name);

    /// <summary>
    /// Pont vers la bibliothèque Jellyfin.
    ///
    /// Permet à la recherche de signaler ce que l'utilisateur possède déjà : un média
    /// présent en bibliothèque se lit directement, un média absent ne peut être
    /// qu'ajouté à la watchlist ou demandé. Le front a besoin de cette distinction
    /// pour choisir l'action proposée sur la tuile.
    ///
    /// Le pivot est le `media_key` : l'item Jellyfin est rapproché par son `ProviderId`
    /// TMDB, pas par son titre — ce qui évite les faux positifs sur les titres proches.
    ///
    /// ⚠️ **Tout passe par un utilisateur.** Les requêtes portent un `User`, et le cache
    /// est indexé par utilisateur. Sans ça, `InternalItemsQuery` ignore les droits :
    /// un compte restreint à une bibliothèque voyait les titres et les `jellyfinId` de
    /// toutes les autres, via `/search`, `/me/ratings` et `/me/watchlist`.
    /// </summary>
    public class JellyfinLibrary
    {
        /// Durée de validité de l'index bibliothèque. Assez court pour qu'un ajout
        /// se voie dans la session, assez long pour ne pas reparcourir la
        /// bibliothèque à chaque requête d'une liste.
        private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);

        /// <summary>Index déjà construit, par utilisateur.</summary>
        private record CachedIndex(Dictionary<string, LibraryMatch> Entries, DateTime BuiltAt);

        // Statiques, et non champs d'instance : le service est enregistré en `Scoped`,
        // donc recréé à chaque requête HTTP — un cache d'instance ne survivrait pas.
        // Même raisonnement que le HttpClient statique de TmdbClient.
        private static readonly ConcurrentDictionary<Guid, CachedIndex> _index = new();

        // Un verrou **par utilisateur** : la construction parcourt toute la
        // bibliothèque, et l'app iOS déclenche trois appels de liste en parallèle au
        // chargement de l'Explorer. Sans lui, les trois reconstruisaient le même index
        // en même temps. Par utilisateur et non global : deux profils distincts n'ont
        // aucune raison de s'attendre.
        private static readonly ConcurrentDictionary<Guid, object> _locks = new();

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly ILogger<JellyfinLibrary> _logger;

        public JellyfinLibrary(ILibraryManager library, IUserManager users, ILogger<JellyfinLibrary> logger)
        {
            _library = library;
            _users = users;
            _logger = logger;
        }

        /// <summary>
        /// Cherche dans la bibliothèque **visible par cet utilisateur** les items
        /// correspondant à un titre.
        ///
        /// Parametres :
        /// - user_id (Guid) : utilisateur dont on applique les droits
        /// - query (string) : texte saisi
        /// - media_type (string) : 'movie' ou 'tv'
        /// - limit (int) : nombre maximum d'items
        ///
        /// Output :
        /// - matches (Dictionary&lt;string, LibraryMatch&gt;) : media_key -> item Jellyfin,
        ///   vide si la recherche échoue ou si aucun item n'a d'identifiant TMDB
        /// </summary>
        public Dictionary<string, LibraryMatch> search(Guid user_id, string query, string media_type, int limit = 20)
        {
            var matches = new Dictionary<string, LibraryMatch>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(query)) return matches;

            var user = _users.GetUserById(user_id);
            if (user is null) return matches;

            try
            {
                var kind = media_type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series;
                var items = _library.GetItemList(new InternalItemsQuery(user)
                {
                    SearchTerm = query.Trim(),
                    IncludeItemTypes = new[] { kind },
                    Recursive = true,
                    Limit = limit,
                });

                collect(items, media_type, matches);
            }
            catch (Exception ex)
            {
                // La recherche doit rester utilisable même si la bibliothèque répond mal :
                // on perd l'indication « en bibliothèque », pas les résultats.
                _logger.LogWarning(ex, "[EnhancedFin] Recherche bibliothèque en échec pour « {Query} »", query);
            }

            return matches;
        }

        /// <summary>
        /// Index complet `media_key` -> item Jellyfin, pour la bibliothèque **visible
        /// par cet utilisateur**.
        ///
        /// Sert aux listes (notes, watchlist, « à noter ») qui doivent savoir lesquels
        /// de leurs items sont lisibles. Une requête bibliothèque par ligne en ferait
        /// 234 pour les notes migrées, sur un Pi.
        ///
        /// Le résultat est mis en cache quelques minutes : construire l'index parcourt
        /// toute la bibliothèque, ce qui est bon marché une fois mais pas à chaque
        /// requête HTTP. Un média fraîchement ajouté apparaît donc avec ce délai, ce
        /// qui est sans conséquence : il reste affiché, seul le bouton « Lire » tarde.
        ///
        /// Parametres :
        /// - user_id (Guid) : utilisateur dont on applique les droits
        ///
        /// Output :
        /// - index (IReadOnlyDictionary&lt;string, LibraryMatch&gt;) : vide si la
        ///   bibliothèque répond mal — on perd l'indication, jamais les données
        /// </summary>
        public IReadOnlyDictionary<string, LibraryMatch> index(Guid user_id)
        {
            if (fresh(user_id) is { } cached) return cached;

            // Verrou pris **autour** de la construction, et non après : sans ça, N
            // requêtes parallèles lançaient N parcours complets de la bibliothèque
            // avant que la première ne publie son résultat.
            lock (_locks.GetOrAdd(user_id, _ => new object()))
            {
                // Seconde vérification : un appelant concurrent a pu remplir le cache
                // pendant qu'on attendait le verrou.
                if (fresh(user_id) is { } filled) return filled;

                var built = build_index(user_id);
                _index[user_id] = new CachedIndex(built, DateTime.UtcNow);

                return built;
            }
        }

        /// <summary>Index en cache s'il est encore valide, null sinon.</summary>
        private static Dictionary<string, LibraryMatch>? fresh(Guid user_id) =>
            _index.TryGetValue(user_id, out var cached)
                && DateTime.UtcNow - cached.BuiltAt < IndexTtl
                    ? cached.Entries
                    : null;

        private Dictionary<string, LibraryMatch> build_index(Guid user_id)
        {
            var index = new Dictionary<string, LibraryMatch>(StringComparer.Ordinal);

            var user = _users.GetUserById(user_id);
            if (user is null) return index;

            foreach (var (media_type, kind) in new[]
            {
                ("movie", BaseItemKind.Movie),
                ("tv", BaseItemKind.Series),
            })
            {
                try
                {
                    var items = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[] { kind },
                        Recursive = true,
                    });

                    collect(items, media_type, index);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EnhancedFin] Index bibliothèque en échec pour {Kind}", kind);
                }
            }

            return index;
        }

        /// <summary>
        /// Range des items Jellyfin sous leur clé média.
        ///
        /// Sans identifiant TMDB, un item ne peut pas être rattaché au référentiel :
        /// on l'ignore plutôt que de deviner sur le titre, qui produirait des faux
        /// positifs entre un film et son remake.
        ///
        /// Parametres :
        /// - items (IEnumerable&lt;BaseItem&gt;) : items renvoyés par la bibliothèque
        /// - media_type (string) : 'movie' ou 'tv'
        /// - into (Dictionary&lt;string, LibraryMatch&gt;) : dictionnaire à remplir
        /// </summary>
        private static void collect(
            IEnumerable<BaseItem> items, string media_type, Dictionary<string, LibraryMatch> into)
        {
            foreach (var item in items)
            {
                var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
                if (string.IsNullOrWhiteSpace(tmdb) || !int.TryParse(tmdb, out var tmdb_id))
                    continue;

                var media_key = $"{media_type}:{tmdb_id}";
                into.TryAdd(media_key, new LibraryMatch(media_key, item.Id, item.Name));
            }
        }
    }
}
