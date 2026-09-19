using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EnhancedFin.Configuration
{
    /// <summary>
    /// Réglages du plugin, dans
    /// `<config>/plugins/configurations/Jellyfin.Plugin.EnhancedFin.xml`.
    ///
    /// Un seul réglage, et il est lu : `MediaCacheHours` et `LegacyDbPath` ont été
    /// retirés parce qu'aucune ligne de code ne les consultait — la documentation
    /// promettait un comportement que rien n'implémentait.
    ///
    /// - `MediaCacheHours` décrivait un TTL de rafraîchissement. `RefreshTask` ne
    ///   raisonne pas en durée mais en complétude (`backdrop_url IS NULL OR year IS
    ///   NULL`) : le câbler aurait été ajouter une fonctionnalité, pas corriger un bug.
    /// - `LegacyDbPath` servait à attacher l'ancienne base pour ses alias source externe.
    ///   C'est du ressort de un autre plugin, et la table `media_alias` qui l'accompagnait a
    ///   été retirée du schéma.
    ///
    /// Les rétablir un jour ne coûte rien ; les laisser mentir coûtait à chaque
    /// lecture.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Clé API TMDB, utilisée pour alimenter le référentiel `media` et le calendrier.</summary>
        public string TmdbApiKey { get; set; } = "";
    }
}
