using System;
using System.IO;
using System.Text.RegularExpressions;
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
        /// <summary>
        /// Forme d'un identifiant SQL acceptable : lettre ou souligné, puis lettres,
        /// chiffres et soulignés. Assez large pour tout nom de table, de colonne ou
        /// de type SQLite utilisé ici, assez étroit pour qu'aucun fragment de
        /// requête ne s'y glisse.
        /// </summary>
        private static readonly Regex SqlIdentifier =
            new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private string ConnectionString { get; }

        public Db(IApplicationPaths app_paths)
        {
            var dir = Path.Combine(app_paths.PluginConfigurationsPath, "EnhancedFin");
            Directory.CreateDirectory(dir);
            ConnectionString = $"Data Source={Path.Combine(dir, "EnhancedFin.db")}";

            ensure_schema();
            ensure_columns();
            fix_svg_logos();
        }

        /// <summary>
        /// Remplace les logos SVG déjà en base par leur version PNG.
        ///
        /// `TmdbClient.image_url` ne produit plus de SVG, mais les fiches entrées avant
        /// en gardent un, et `ensure_exists` ne les relit pas. Idempotent : sans ligne
        /// en `.svg`, l'`UPDATE` ne touche rien.
        /// </summary>
        private void fix_svg_logos()
        {
            using var con = new SqliteConnection(ConnectionString);
            con.Open();

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                UPDATE media
                SET logo_url = substr(logo_url, 1, length(logo_url) - 4) || '.png'
                WHERE logo_url LIKE '%.svg'";
            cmd.ExecuteNonQuery();
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
        /// Ajoute les colonnes apparues après la création du schéma.
        ///
        /// `CREATE TABLE IF NOT EXISTS` ne touche pas une table déjà créée : sans
        /// cette étape, une base existante n'obtiendrait jamais les colonnes
        /// ajoutées par la suite. Chaque ajout est vérifié par `PRAGMA table_info`,
        /// ce qui rend la méthode idempotente — SQLite n'a pas d'`ADD COLUMN IF NOT
        /// EXISTS`.
        ///
        /// Rien d'autre que des `ALTER` ici : une table se déclare dans
        /// `ensure_schema`, qui tourne au même démarrage et dont le
        /// `CREATE TABLE IF NOT EXISTS` est déjà idempotent. Décrire le schéma à
        /// deux endroits, c'est en avoir deux versions.
        /// </summary>
        private void ensure_columns()
        {
            using var con = new SqliteConnection(ConnectionString);
            con.Open();

            add_column(con, "media", "vote_average", "REAL");
            add_column(con, "media", "vote_count", "INTEGER");
            // Bloc « infos » de la fiche. `directors` en texte joint (« A, B »).
            add_column(con, "media_detail", "release_date", "TEXT");
            add_column(con, "media_detail", "directors", "TEXT");
            // Dernière relecture TMDB du bloc. NULL = fiche jamais relue depuis
            // l'ajout du bloc : `GET /media` la relit une fois, voir
            // `MediaCatalog.needs_facts`.
            add_column(con, "media_detail", "facts_refreshed_at", "TEXT");
        }

        /// <summary>
        /// Ajoute une colonne si elle manque.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - table (string) : nom de la table
        /// - column (string) : nom de la colonne
        /// - type (string) : type SQLite
        /// </summary>
        /// <remarks>
        /// Les trois identifiants sont **interpolés**, et il n'y a pas d'alternative :
        /// SQLite n'accepte pas de paramètre lié à la place d'un nom de table ou de
        /// colonne. Aujourd'hui tous les appels sont des littéraux écrits ici, donc
        /// rien n'est exploitable — mais c'est un invariant tacite, que le premier
        /// appel construit depuis une liste externe romprait sans bruit. Le garde
        /// ci-dessous le rend explicite et bruyant.
        /// </remarks>
        private static void add_column(SqliteConnection con, string table, string column, string type)
        {
            if (!SqlIdentifier.IsMatch(table) || !SqlIdentifier.IsMatch(column) || !SqlIdentifier.IsMatch(type))
                throw new ArgumentException($"Identifiant SQL invalide : {table}.{column} {type}");

            using var check = con.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table})";

            using (var rd = check.ExecuteReader())
            {
                while (rd.Read())
                {
                    if (rd.GetString(1) == column) return;
                }
            }

            using var alter = con.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();
        }

        /// <summary>
        /// Crée le schéma s'il n'existe pas. Idempotent : sûr à chaque démarrage.
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
                    -- Inutilisées (ni écrites ni lues) ; gardées pour ne pas migrer les
                    -- bases existantes.
                    screenwriters TEXT,
                    studios       TEXT
                );

                -- Remplace le CSV genre_ids de l'ancien schéma : filtrable en SQL.
                CREATE TABLE IF NOT EXISTS media_genre (
                    media_key TEXT    NOT NULL REFERENCES media(media_key) ON DELETE CASCADE,
                    genre_id  INTEGER NOT NULL,
                    PRIMARY KEY (media_key, genre_id)
                );

                -- Référentiel des noms de genres TMDB : `media_genre` ne stocke que
                -- des identifiants, et un client ne peut pas afficher « 18, 80 ».
                CREATE TABLE IF NOT EXISTS genre (
                    genre_id INTEGER PRIMARY KEY,
                    name     TEXT NOT NULL
                );

                -- Notes Rotten Tomatoes venues de MDBList, en cache (voir
                -- `MdblistClient`). Un cache, pas une donnée du référentiel : pas de
                -- clé étrangère, il se reconstruit seul. Une ligne aux notes nulles est
                -- un résultat, pas un manque : MDBList ne les a pas, et on ne le
                -- redemande qu'à expiration.
                CREATE TABLE IF NOT EXISTS media_score (
                    media_key   TEXT PRIMARY KEY,
                    rt_critics  INTEGER,
                    rt_audience INTEGER,
                    fetched_at  TEXT NOT NULL
                );

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
                -- lisent la même ligne. Alimenté par TMDB uniquement.
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
