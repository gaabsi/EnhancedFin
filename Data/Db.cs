using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.EnhancedFin.Data
{
    /// <summary>
    /// Source de vérité de la connexion SQLite et du schéma.
    /// Singleton DI : le schéma est initialisé une fois, avant toute requête.
    /// </summary>
    public class Db
    {
        public string ConnectionString { get; }

        public Db(IApplicationPaths app_paths)
        {
            var dir = Path.Combine(app_paths.PluginConfigurationsPath, "EnhancedFin");
            Directory.CreateDirectory(dir);
            ConnectionString = $"Data Source={Path.Combine(dir, "EnhancedFin.db")}";

            ensure_schema();
        }

        /// <summary>Ouvre une connexion prête à l'emploi (clés étrangères activées).</summary>
        public SqliteConnection open()
        {
            var con = new SqliteConnection(ConnectionString);
            con.Open();

            // Non activées par défaut en SQLite : sans ça, les REFERENCES du schéma
            // seraient purement décoratives et les ON DELETE CASCADE inopérants.
            using var pragma = con.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();

            return con;
        }

        /// <summary>
        /// Crée le schéma s'il n'existe pas. Idempotent : sûr à chaque démarrage.
        /// Le modèle complet et ses justifications sont dans DESIGN.md.
        /// </summary>
        private void ensure_schema()
        {
            using var con = new SqliteConnection(ConnectionString);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                -- ========== RÉFÉRENTIEL MÉDIAS ==========
                -- media_key = '{type}:{tmdb_id}'. Le préfixe est obligatoire :
                -- TMDB a des espaces d'ID séparés (movie/550 = Fight Club,
                -- tv/550 = Till Death Us Do Part).
                CREATE TABLE IF NOT EXISTS media (
                    media_key    TEXT PRIMARY KEY,
                    media_type   TEXT    NOT NULL CHECK (media_type IN ('movie','tv')),
                    tmdb_id      INTEGER NOT NULL,
                    title        TEXT    NOT NULL,
                    year         INTEGER,
                    poster_url   TEXT,
                    backdrop_url TEXT,
                    logo_url     TEXT,
                    refreshed_at TEXT    NOT NULL,
                    UNIQUE (media_type, tmdb_id)
                );

                -- Champs lourds isolés : cast_json pèse 2-8 Ko, et SQLite lit la ligne
                -- entière. Afficher une liste de 50 posters ne doit pas charger 50 castings.
                CREATE TABLE IF NOT EXISTS media_detail (
                    media_key     TEXT PRIMARY KEY REFERENCES media(media_key) ON DELETE CASCADE,
                    overview      TEXT,
                    cast_json     TEXT,
                    screenwriters TEXT,
                    studios       TEXT
                );

                -- Remplace le CSV genre_ids de l'ancien schéma : filtrable en SQL.
                CREATE TABLE IF NOT EXISTS media_genre (
                    media_key TEXT    NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    genre_id  INTEGER NOT NULL,
                    PRIMARY KEY (media_key, genre_id)
                );

                -- (`media_alias` a été retirée : aucune lecture ni écriture nulle part.
                --  Les alias de sources externes — slug source externe, etc. — relèvent de
                --  un autre plugin, qui définira son propre schéma. `CREATE TABLE IF NOT EXISTS`
                --  ne supprime rien : une base existante garde sa table vide.)

                -- ========== DONNÉES UTILISATEUR ==========
                -- La PK (user_id, media_key) rend les doublons impossibles. L'ancien
                -- schéma utilisait un AUTOINCREMENT sans contrainte d'unicité.
                CREATE TABLE IF NOT EXISTS rating (
                    user_id   TEXT    NOT NULL,
                    media_key TEXT    NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    score     INTEGER NOT NULL CHECK (score IN (-1, 1, 2)),
                    rated_at  TEXT    NOT NULL,
                    PRIMARY KEY (user_id, media_key)
                );
                CREATE INDEX IF NOT EXISTS idx_rating_media ON rating(media_key);

                CREATE TABLE IF NOT EXISTS watchlist (
                    user_id   TEXT NOT NULL,
                    media_key TEXT NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    added_at  TEXT NOT NULL,
                    PRIMARY KEY (user_id, media_key)
                );
                CREATE INDEX IF NOT EXISTS idx_watchlist_user ON watchlist(user_id, added_at DESC);

                -- Remplace external_watched ET anime_watched_v2, qui avaient la même
                -- structure à l'identifiant près. Un film : season=0, episode=0.
                CREATE TABLE IF NOT EXISTS playback (
                    user_id        TEXT    NOT NULL,
                    media_key      TEXT    NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    season         INTEGER NOT NULL DEFAULT 0,
                    episode        INTEGER NOT NULL DEFAULT 0,
                    position_ticks INTEGER NOT NULL DEFAULT 0,
                    duration_ticks INTEGER NOT NULL DEFAULT 0,
                    lang           TEXT,
                    watched_at     TEXT,
                    updated_at     TEXT    NOT NULL,
                    PRIMARY KEY (user_id, media_key, season, episode)
                );
                CREATE INDEX IF NOT EXISTS idx_playback_recent ON playback(user_id, updated_at DESC);

                -- Masquage unifié (items Jellyfin natifs ET externes).
                -- Sémantique reprise de l'existant : masqué tant que
                -- hidden_at >= playback.updated_at, donc reprendre la lecture
                -- fait réapparaître l'item automatiquement.
                CREATE TABLE IF NOT EXISTS hidden_item (
                    user_id   TEXT NOT NULL,
                    media_key TEXT NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    hidden_at TEXT NOT NULL,
                    PRIMARY KEY (user_id, media_key)
                );

                -- ========== SUIVIS & CALENDRIER ==========
                CREATE TABLE IF NOT EXISTS follow (
                    user_id   TEXT NOT NULL,
                    media_key TEXT NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    added_at  TEXT NOT NULL,
                    PRIMARY KEY (user_id, media_key)
                );

                -- Référentiel partagé : deux utilisateurs suivant la même série
                -- lisent la même ligne. Alimenté par TMDB uniquement — la
                -- disponibilité sur les sources externes relève de un autre plugin.
                CREATE TABLE IF NOT EXISTS release (
                    media_key    TEXT    NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    season       INTEGER NOT NULL,
                    episode      INTEGER NOT NULL,
                    episode_name TEXT,
                    air_date     TEXT,
                    refreshed_at TEXT    NOT NULL,
                    PRIMARY KEY (media_key, season, episode)
                );
                CREATE INDEX IF NOT EXISTS idx_release_date ON release(air_date);
            ";
            cmd.ExecuteNonQuery();
        }
    }
}
