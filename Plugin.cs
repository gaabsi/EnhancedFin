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

        // GUID neuf : doit impérativement différer de celui de Media Rating
        // (a1b2c3d4-e5f6-7890-abcd-ef1234567890), sinon Jellyfin refuse de
        // charger les deux plugins simultanément.
        public override Guid Id => Guid.Parse("2dd4485f-d630-456e-9ed4-6bb35c46ebf9");

        public override string Description =>
            "Données utilisateur enrichies : notes, watchlist, reprise de lecture, suivis et calendrier.";

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths app_paths, IXmlSerializer xml_serializer)
            : base(app_paths, xml_serializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages() => Array.Empty<PluginPageInfo>();
    }
}
