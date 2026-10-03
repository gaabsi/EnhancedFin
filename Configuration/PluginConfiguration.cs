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
        /// Convertit les sous-titres ASS intégrés en `.srt` externes et les retire des mkv
        /// (`AssSubtitlesTask`). **Désactivé par défaut** : la tâche réécrit les fichiers
        /// de la bibliothèque, ce qu'un admin doit choisir en connaissance de cause.
        /// </summary>
        public bool ConvertAssSubtitles { get; set; }

        /// <summary>
        /// Garde l'original de chaque mkv converti, en fichier caché à côté du film
        /// (`.nom.mkv.ass-backup`, ignoré par Jellyfin). À désactiver une fois le résultat
        /// vérifié : chaque sauvegarde double la place du film.
        /// </summary>
        public bool KeepAssBackup { get; set; } = true;
    }
}
