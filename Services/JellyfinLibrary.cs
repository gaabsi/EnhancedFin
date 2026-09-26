using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    /// <summary>Correspondance entre un média du référentiel et l'item Jellyfin qui le porte.</summary>
    public record LibraryMatch(string MediaKey, Guid JellyfinId, string Name);

    /// <summary>
    /// Un média que **Jellyfin** considère comme vu par cet utilisateur.
    ///
    /// Distinct de ce que porte la table `playback` du plugin : celle-ci ne connaît
    /// que ce qu'un client lui a explicitement envoyé, alors que Jellyfin sait ce qui
    /// a réellement été lu sur le serveur. Les deux sources se complètent — voir
    /// `RatingsController.pending`.
    ///
    /// `Year` et `Name` viennent de l'item Jellyfin : un média vu mais jamais noté
    /// n'est pas au référentiel, et n'a donc ni titre ni affiche de notre côté.
    /// </summary>
    public record WatchedMedia(
        string MediaKey,
        Guid JellyfinId,
        string Name,
        int? Year,
        int WatchedEpisodes,
        DateTime LastPlayedAt,
        /// Vrai quand il ne reste rien à voir : tous les épisodes pour une série,
        /// le film lui-même pour un film. Sans cette information, une œuvre plus
        /// courte que le seuil de notation ne pourrait **jamais** être proposée.
        bool IsComplete);

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
        private readonly IUserDataManager _user_data;
        private readonly ILogger<JellyfinLibrary> _logger;

        public JellyfinLibrary(
            ILibraryManager library,
            IUserManager users,
            IUserDataManager user_data,
            ILogger<JellyfinLibrary> logger)
        {
            _library = library;
            _users = users;
            _user_data = user_data;
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

        /// <summary>
        /// Item Jellyfin **à lire** pour un média, s'il est lisible sur ce serveur par
        /// cet utilisateur.
        ///
        /// Les droits (bibliothèques, contrôle parental) sont déjà appliqués par
        /// `index`, qui passe par `InternalItemsQuery(user)`. Reste le cas qu'un index
        /// ne voit pas : les items **virtuels** — un épisode manquant a une page mais
        /// aucun fichier. Le droit de lecture du compte n'est pas revérifié : Jellyfin
        /// le fait déjà au moment de servir le flux.
        ///
        /// Parametres :
        /// - user_id (Guid) : utilisateur dont on applique les droits
        /// - media_key (string) : clé du film ou de la série
        /// - season (int?) : saison de l'épisode visé, null pour l'œuvre entière
        /// - episode (int?) : numéro de l'épisode visé, null pour l'œuvre entière
        ///
        /// Output :
        /// - item_id (Guid?) : l'épisode demandé, sinon le film ou la série ; null si
        ///   rien n'est lisible ou si la bibliothèque répond mal — on grise un bouton,
        ///   on ne lance jamais une lecture vouée à l'échec
        /// </summary>
        public Guid? find_playable(Guid user_id, string media_key, int? season, int? episode)
        {
            var user = _users.GetUserById(user_id);
            if (user is null) return null;
            if (!index(user_id).TryGetValue(media_key, out var match)) return null;

            try
            {
                var item = _library.GetItemById(match.JellyfinId);
                if (item is null || item.IsVirtualItem) return null;
                if (season is null || episode is null) return item.Id;

                // L'épisode lui-même, et non la série : c'est lui que le lecteur lance.
                return _library.GetItemList(new InternalItemsQuery(user)
                {
                    AncestorIds = new[] { item.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    ParentIndexNumber = season,
                    IndexNumber = episode,
                    Recursive = true,
                }).FirstOrDefault(e => !e.IsVirtualItem)?.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EnhancedFin] Lisibilité en échec pour {Key}", media_key);

                return null;
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
        /// Ce que **Jellyfin** sait avoir été vu par cet utilisateur.
        ///
        /// Le plugin ne recevait jusqu'ici aucun signal de lecture : sa table
        /// `playback` n'est alimentée que par `PUT /me/progress`, qu'aucun client
        /// n'appelle. Un film regardé sur le serveur n'entrait donc jamais dans
        /// « à noter ». C'est cette source-là que le front web interroge en premier.
        ///
        /// Parametres :
        /// - user_id (Guid) : utilisateur dont on applique les droits
        ///
        /// Output :
        /// - watched (Dictionary&lt;string, WatchedMedia&gt;) : media_key -&gt; média vu,
        ///   vide si la bibliothèque répond mal
        /// </summary>
        public Dictionary<string, WatchedMedia> watched(Guid user_id)
        {
            var watched = new Dictionary<string, WatchedMedia>(StringComparer.Ordinal);

            var user = _users.GetUserById(user_id);
            if (user is null) return watched;

            collect_watched_movies(user, watched);
            collect_watched_series(user, watched);

            return watched;
        }

        /// <summary>
        /// Ajoute les films marqués vus. Un film n'a pas d'épisode : son compte vaut 1,
        /// ce qui le fait franchir n'importe quel seuil.
        /// </summary>
        private void collect_watched_movies(User user, Dictionary<string, WatchedMedia> into)
        {
            try
            {
                var items = _library.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    IsPlayed = true,
                    Recursive = true,
                });

                foreach (var item in items)
                {
                    if (media_key_of(item, "movie") is not { } media_key) continue;

                    into.TryAdd(media_key, new WatchedMedia(
                        MediaKey: media_key,
                        JellyfinId: item.Id,
                        Name: item.Name,
                        Year: item.ProductionYear,
                        WatchedEpisodes: 1,
                        LastPlayedAt: last_played(user, item),
                        // Un film vu est vu : il n'y a rien de plus à en voir.
                        IsComplete: true));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EnhancedFin] Films vus : lecture bibliothèque en échec");
            }
        }

        /// <summary>
        /// Ajoute les séries, avec leur nombre d'épisodes vus.
        /// </summary>
        /// <remarks>
        /// ⚠️ On interroge les **épisodes**, pas les séries. `IsPlayed = true` sur une
        /// `Series` exige que **tous** ses épisodes soient vus : une série suivie à 5
        /// épisodes sur 8 n'en sort pas, alors que c'est précisément le cas qui nous
        /// intéresse (seuil de notation à 5). Mesuré sur le serveur de test :
        /// *Lessons in Chemistry*, 5 épisodes vus sur 8, ressort à `Played = false`.
        ///
        /// La série est ensuite relue par son identifiant pour son `ProviderId` TMDB :
        /// l'épisode porte le sien, qui désigne l'épisode et non l'œuvre.
        /// </remarks>
        private void collect_watched_series(User user, Dictionary<string, WatchedMedia> into)
        {
            try
            {
                var episodes = _library.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsPlayed = true,
                    Recursive = true,
                });

                foreach (var group in episodes.OfType<Episode>().GroupBy(e => e.SeriesId))
                {
                    var series = _library.GetItemById(group.Key);
                    if (series is null) continue;
                    if (media_key_of(series, "tv") is not { } media_key) continue;

                    var watched_count = group.Count();

                    into.TryAdd(media_key, new WatchedMedia(
                        MediaKey: media_key,
                        JellyfinId: series.Id,
                        Name: series.Name,
                        Year: series.ProductionYear,
                        WatchedEpisodes: watched_count,
                        LastPlayedAt: group.Max(e => last_played(user, e)),
                        IsComplete: watched_count >= episode_count(user, series)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EnhancedFin] Séries vues : lecture bibliothèque en échec");
            }
        }

        /// <summary>
        /// Nombre total d'épisodes d'une série, vus ou non.
        ///
        /// Sert à reconnaître une œuvre **terminée**, ce qu'on ne peut pas déduire
        /// autrement.
        /// </summary>
        /// <remarks>
        /// ⚠️ **Ne pas passer par `GetUserData(user, series).Played`.** Le
        /// `UserItemData` d'une série est celui qui a été **stocké**, c'est-à-dire
        /// renseigné seulement si la série a été explicitement marquée vue. L'API REST
        /// de Jellyfin, elle, *calcule* `Played` et `UnplayedItemCount` en agrégeant
        /// les épisodes — d'où un `Played = true` visible dans `/Users/{id}/Items` et
        /// un `false` côté plugin, pour la même série. Mesuré sur *Dans leur regard*.
        ///
        /// `GetCount` plutôt que `GetItemList().Count` : on veut un nombre, pas
        /// quelques centaines d'entités instanciées pour être aussitôt jetées.
        ///
        /// Une requête par série **commencée**, donc proportionnelle à ce que
        /// l'utilisateur regarde et non à la taille de la bibliothèque.
        ///
        /// Parametres :
        /// - user (User) : utilisateur dont on applique les droits
        /// - series (BaseItem) : la série
        ///
        /// Output :
        /// - count (int) : nombre d'épisodes, `int.MaxValue` si le compte échoue —
        ///   une série n'est alors jamais dite terminée, ce qui est le repli sûr
        /// </remarks>
        private int episode_count(User user, BaseItem series)
        {
            try
            {
                return _library.GetCount(new InternalItemsQuery(user)
                {
                    AncestorIds = new[] { series.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EnhancedFin] Comptage des épisodes en échec pour {Series}", series.Name);

                return int.MaxValue;
            }
        }

        /// <summary>
        /// Date de dernière lecture, ou une date plancher quand Jellyfin n'en a pas.
        ///
        /// Vérifié sur le serveur de test : un marquage manuel (sans lecture) pose bien
        /// `LastPlayedDate`. Le repli ne sert donc qu'aux cas limites, où il relègue
        /// l'item en fin de liste plutôt que de le faire disparaître.
        /// </summary>
        private DateTime last_played(User user, BaseItem item) =>
            _user_data.GetUserData(user, item)?.LastPlayedDate ?? DateTime.MinValue;

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
                if (media_key_of(item, media_type) is not { } media_key) continue;

                into.TryAdd(media_key, new LibraryMatch(media_key, item.Id, item.Name));
            }
        }

        /// <summary>
        /// Clé média d'un item Jellyfin, dérivée de son `ProviderId` TMDB.
        ///
        /// Point unique du rapprochement : l'index, la recherche et les médias vus
        /// le faisaient chacun de leur côté, et un écart entre eux n'aurait produit
        /// aucune erreur — juste des listes qui ne se recoupent pas.
        ///
        /// Parametres :
        /// - item (BaseItem) : item de la bibliothèque
        /// - media_type (string) : 'movie' ou 'tv'
        ///
        /// Output :
        /// - media_key (string | null) : null si l'item n'a pas d'identifiant TMDB
        /// </summary>
        private static string? media_key_of(BaseItem item, string media_type)
        {
            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);

            return string.IsNullOrWhiteSpace(tmdb) || !int.TryParse(tmdb, out var tmdb_id)
                ? null
                : $"{media_type}:{tmdb_id}";
        }
    }
}
