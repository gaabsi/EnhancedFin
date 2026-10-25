using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EnhancedFin.Configuration
{
    /// <summary>
    /// Réglages du plugin, dans
    /// `<config>/plugins/configurations/Jellyfin.Plugin.EnhancedFin.xml`.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Clé API TMDB, utilisée pour alimenter le référentiel `media` et le calendrier.</summary>
        public string TmdbApiKey { get; set; } = "";

        /// <summary>
        /// Clé API MDBList, pour les notes Rotten Tomatoes (critiques et public) du bloc
        /// « infos » d'une fiche. Vide : les notes RT sont simplement absentes.
        /// </summary>
        public string MdblistApiKey { get; set; } = "";

        /// <summary>
        /// Seerr, pour savoir si une série du serveur est complète (statut 5) — la
        /// fiche propose alors ses saisons manquantes. Vides : la carte n'apparaît pas.
        /// </summary>
        public string SeerrUrl { get; set; } = "";

        public string SeerrApiKey { get; set; } = "";

        /// <summary>
        /// Affiche la rangée des tendances TMDB de la semaine dans l'onglet Explorer des
        /// clients. **Désactivé par défaut** : l'admin choisit de montrer une vitrine de
        /// titres commerciaux. Désactivé, `GET trending` répond `enabled: false` sans
        /// appeler TMDB, et le client retire la rangée.
        /// </summary>
        public bool ShowTrending { get; set; }
    }
}
