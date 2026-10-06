<div align="center">
  <h1>EnhancedFin</h1>
  <img src="https://img.shields.io/badge/Jellyfin-12-9962be"/>
  <img src="https://img.shields.io/badge/.NET-10-512bd4"/>
  <img src="https://img.shields.io/badge/license-GPL--3.0-blue"/>
</div>

<p align="center">
  <b>EnhancedFin</b> is a <a href="https://github.com/jellyfin/jellyfin">Jellyfin</a> server plugin that adds a personal layer on top of your library: ratings, watchlist, continue watching, followed shows and a release calendar. Everything is keyed on TMDB, so it also works for titles you <b>don't</b> have on the server. It powers the discovery features of <a href="https://github.com/gaabsi/SweetFin"><b>SweetFin</b></a>, the iOS client, and exposes a REST API any client can use.
</p>

## ✨ Features

- **Ratings**: rate movies and shows, and get a "to rate" list built from what you
  actually watched (a movie, a finished show, or at least 5 episodes).
- **Watchlist**: with genres, so clients can split it into movies, shows and anime.
- **Continue watching**: one entry per title, with hide and restore.
- **Follows and calendar**: follow a show, get its upcoming episodes grouped by day.
- **Search**: merges your own data, TMDB and the Jellyfin library (each result tells
  whether it's playable on the server).
- **Trending**: TMDB weekly trends, filterable by movie, show or anime.
- **Rich media pages**: metadata, cast, directors, seasons and episodes with watched
  state, Rotten Tomatoes scores (via MDBList) and Seerr availability.
- **Seerr requests**: request a movie or seasons on behalf of the calling user.
- **SyncPlay**: invite a user into your group, and automatically stop groups that
  everyone left mid-playback.
- **ASS → SRT** (opt-in): converts embedded ASS subtitles to external `.srt` files,
  for players that cannot render ASS.

Each user only ever sees their own data, and only items from the libraries they can
access on the server.

## 🧩 Compatibility

**Jellyfin 12.x** (`net10.0`). Jellyfin 12 broke plugin binary compatibility: this
plugin does not load on 10.11 or earlier.

> **Note** — `Microsoft.Data.Sqlite` must match the exact version bundled with your
> Jellyfin server (10.0.11 for Jellyfin 12.1). Jellyfin loads its own copy; a higher
> version in the plugin makes it fail at startup with a `FileNotFoundException`.

## 🛠️ Build

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
# → bin/Release/net10.0/Jellyfin.Plugin.EnhancedFin.dll
```

Warnings are treated as errors, and builds are reproducible (no local paths in the
binary).

## 📦 Installation

**From the plugin repository** (recommended, with automatic updates):

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/gaabsi/EnhancedFin/main/manifest.json
   ```
2. Open **Catalog**, install **EnhancedFin** and restart Jellyfin.
3. Fill in the settings page (below).

**Manually**: download the zip of the latest [release](https://github.com/gaabsi/EnhancedFin/releases),
extract the DLL into `<jellyfin-config>/plugins/EnhancedFin/` and restart Jellyfin.

The plugin stores its data in `<jellyfin-config>/plugins/configurations/EnhancedFin/`
(SQLite, in WAL mode: back up the whole folder, not just `EnhancedFin.db`).

> If the plugin crashed once, Jellyfin marks it `Malfunctioned` in its `meta.json`
> and stops loading it, even after a fix. Set `"status"` back to `"Active"` and restart.

## ⚙️ Configuration

Open **Dashboard → Plugins → EnhancedFin** (administrators only). Each key has a
**Test** button that checks what you typed before saving, and changes apply
immediately, without a restart.

| Setting | Required | Purpose | Where to get it |
|---|---|---|---|
| TMDB API key | **yes** | metadata, search, trending, release dates | free account on [themoviedb.org](https://www.themoviedb.org/settings/api) → Settings → API → "API Key" (v3) |
| MDBList API key | no | Rotten Tomatoes scores, omitted if empty | free account on [mdblist.com](https://mdblist.com/preferences/) → Preferences → API key (1,000 requests a day; scores are cached 7 days) |
| Seerr URL + API key | no | availability and requests, omitted if empty | in Jellyseerr / Overseerr: Settings → General → API Key; the URL is how the Jellyfin server reaches Seerr |
| Convert ASS subtitles | no (off) | **rewrites your files**: after each library scan, converts embedded ASS subtitles of `.mkv` files to external `.srt` and removes the converted tracks (stream copy, no re-encoding). For players that cannot render ASS; ASS styling is lost. | |
| Keep ASS backup | no (on) | keeps each original as a hidden `.<file>.ass-backup` (e.g. `.Movie.mkv.ass-backup`) next to it. Doubles disk usage until you delete the backups. | |

The settings are stored in `<jellyfin-config>/plugins/configurations/Jellyfin.Plugin.EnhancedFin.xml`,
readable by administrators only. Never share or commit this file: it holds your API keys.

## 🔌 API

Base path: `/api/EnhancedFin/v1`. Every route requires a Jellyfin user token
(`Authorization: MediaBrowser Token="…"` header; Jellyfin 12 rejects the legacy `X-Emby-Token`).

**The caller's identity always comes from the token** — no route takes a `userId`,
and everything personal lives under `/me`. API keys are rejected, since they don't
represent a user.

Media are identified by a **media key**, `"{type}:{tmdbId}"`, e.g. `movie:550` or
`tv:1396`. The prefix is mandatory: TMDB movie and tv IDs are separate namespaces.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/me` | identity, admin flag, collection counters |
| `GET` | `/media/{key}` | full media page + my data (`?detail=true` for cast, directors, scores, IMDb id, original language) |
| `GET` | `/media/{key}/seasons` | seasons, with my watched count |
| `GET` | `/media/{key}/seasons/{n}` | episodes, with my watched state |
| `GET` | `/media/{key}/playable` | `{ playable, itemId? }` — playable on this server for me (`?season=&episode=`) |
| `GET` | `/media/{key}/seasons/{n}/playable` | playable episodes of a season |
| `GET` | `/me/ratings` | my ratings (`?type=`, `?score=`) |
| `GET` | `/me/ratings/pending` | watched but not rated yet |
| `PUT` `DELETE` | `/me/ratings/{key}` | rate (`{ "score": 2 }`) / unrate |
| `GET` | `/me/watchlist` | my watchlist (`?type=`, `?genre=`) |
| `PUT` `DELETE` | `/me/watchlist/{key}` | add / remove |
| `GET` | `/me/continue-watching` | resume list (`?limit=`): in-progress items, and the next episode of a show whose last episode is watched (`episodeName`) |
| `GET` | `/me/next-up/{key}` | next episode to watch of a show (`{ season, episode }`, 404 when up to date) |
| `GET` `PUT` | `/me/progress/{key}` | playback progress |
| `PUT` `DELETE` | `/me/watched/{key}` | mark / unmark watched (`{ season, episodes }`) |
| `GET` | `/me/hidden` | hidden items |
| `PUT` `DELETE` | `/me/hidden/{key}` | hide / unhide |
| `GET` | `/me/follows` | followed shows, with next air date |
| `PUT` `DELETE` | `/me/follows/{key}` | follow / unfollow |
| `GET` | `/me/calendar` | releases grouped by day (`?from=`, `?to=`) |
| `GET` | `/search` | search by title (`?q=`, `?type=`) |
| `GET` | `/trending` | weekly trends (`?filter=`, `?cursor=`) |
| `GET` | `/person/{tmdbId}` | person page and filmography |
| `GET` | `/seerr/{key}` | Seerr status and seasons |
| `POST` | `/me/requests/{key}` | request on Seerr (`{ seasons: [..] }` for a show) |
| `POST` | `/syncplay/invite` | invite a user into my SyncPlay group (`{ groupId, userId }`) |
| `POST` | `/admin/check/{tmdb\|mdblist\|seerr}` | **admins only**: test a key before saving it (`{ key, url? }`), returns `{ ok, message }` |

Conventions:

- **JSON properties are camelCase**; null fields are omitted rather than sent as `null`.
- **List routes** accept `limit` / `offset` (max 500) and return the real `total`.
- **Writes are lazy**: a `PUT` on a media unknown to the database fetches it from
  TMDB first. `GET` and `DELETE` never create data.
- **Errors** follow RFC 7807 (`ProblemDetails`).
- **Rate limits**, per user: 300 requests per minute overall, 60 per minute on routes
  that call TMDB, MDBList or Seerr (media pages, seasons, search, trending, person,
  writes), 5 SyncPlay invitations per minute. Over the limit, the route answers `429`.

## ⏱️ Scheduled tasks

Both appear in the dashboard under the **EnhancedFin** category.

| Task | Trigger | What it does |
|---|---|---|
| Media refresh | daily, 4 AM | completes incomplete media records and refreshes release dates of followed shows, in batches of 50 |
| ASS → SRT | after each library scan + manual, **only if `ConvertAssSubtitles` is on** | converts embedded ASS tracks to `<name>.<lang>[.sdh][.forced].srt`, then remuxes the file without them |

> **Warning** — when enabled, the ASS task **rewrites your media files**. Only tracks
> whose `.srt` was just written are removed (a second track in the same language, or
> one whose `.srt` already exists, is kept). Each removed track is checked to be ASS
> in the file itself, the remux must keep exactly the other tracks and the duration,
> and the original is replaced in a single atomic rename, kept as a hidden backup if
> `KeepAssBackup` is on. On a read-only library it only logs what it would have done.

## 🗂️ Project layout

```
Api/            controllers — all inherit EnhancedFinController (auth, identity, rate limits, errors)
Services/       TMDB, MDBList, Seerr clients, Jellyfin library index, SyncPlay guard
Data/Db.cs      SQLite schema and connection
Tasks/          scheduled tasks
Configuration/  plugin settings and their dashboard page
```

## 🔒 Privacy

All data stays on **your** Jellyfin server, in the plugin's SQLite database. Only the
server talks to third parties: TMDB (metadata), and MDBList and Seerr if you configure
them. Clients never see your API keys.

## 🙏 Acknowledgements

EnhancedFin is built on [Jellyfin](https://jellyfin.org) and uses data from
[TMDB](https://www.themoviedb.org) and [MDBList](https://mdblist.com). Thanks to their
contributors. This product uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.

EnhancedFin is an independent project, not affiliated with or endorsed by Jellyfin.

## ☕ Support

If EnhancedFin is useful to you, you can [buy me a coffee](https://buymeacoffee.com/gaabsi).

## 📄 License

EnhancedFin is distributed under the **GNU General Public License v3.0**, see
[LICENSE](LICENSE), like Jellyfin itself.
