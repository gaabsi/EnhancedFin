using System;
using System.Collections.Generic;
using Jellyfin.Plugin.EnhancedFin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EnhancedFin
{
    /// <summary>
    /// Point d'entrée du plugin. Ne fait QUE de l'enregistrement : toute la logique
    /// vit dans Api/ et Data/.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "EnhancedFin";

        // Identifiant unique du plugin : deux plugins au même GUID ne peuvent pas être
        // chargés ensemble. Ne jamais le changer, sinon Jellyfin y verrait un autre
        // plugin et sa configuration serait perdue.
        public override Guid Id => Guid.Parse("2dd4485f-d630-456e-9ed4-6bb35c46ebf9");

        public override string Description =>
            "Ratings, watchlist, resume, follows and release calendar for Jellyfin users.";

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths app_paths, IXmlSerializer xml_serializer)
            : base(app_paths, xml_serializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Page de réglages du tableau de bord (clés, test des clés, sous-titres ASS),
        /// intégrée au DLL : réservée aux administrateurs par Jellyfin.
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        };
    }
}
