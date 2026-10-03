using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EnhancedFin
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        {
            // Singleton : le schéma n'est initialisé qu'une fois, au premier accès.
            services.AddSingleton<Db>();

            // Clients sortants : sans état, leur HttpClient est statique (`OutboundHttp`).
            services.AddSingleton<TmdbClient>();

            // Même raisonnement : HttpClient statique, le cache vit en base.
            services.AddSingleton<MdblistClient>();
            services.AddSingleton<SeerrClient>();

            // MediaCatalog peuple le référentiel à la demande, pour qu'une note sur un
            // média inconnu n'échoue pas sur la contrainte de clé étrangère.
            services.AddSingleton<MediaCatalog>();

            // Singleton : ses dépendances Jellyfin le sont aussi, et ses caches (index de
            // la bibliothèque, médias vus) doivent survivre d'une requête à l'autre.
            services.AddSingleton<JellyfinLibrary>();

            // Service de fond : écoute les arrêts de lecture pour figer les groupes
            // SyncPlay abandonnés.
            services.AddHostedService<SyncPlayGroupGuard>();
        }
    }
}
