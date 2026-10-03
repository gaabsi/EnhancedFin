# EnhancedFin

A Jellyfin plugin that adds a per-user data layer on top of your library: ratings,
watchlist, continue watching, followed shows and a release calendar — all keyed on
TMDB, so they work for titles you **don't** have on the server too.

It exposes a REST API under `/api/EnhancedFin/v1`, meant to be consumed by a custom
client (it ships no web UI of its own).

## Features

- **Ratings** — rate movies and shows, and get a "to rate" list built from what you
  actually watched (a movie, a finished show, or at least 5 episodes).
- **Watchlist** — with genres, so clients can split it into movies / shows / anime.
- **Continue watching** — one entry per title, with a hide/restore mechanism.
- **Follows & calendar** — follow a show, get its upcoming episodes grouped by day.
- **Search** — merges three sources: your own data, TMDB, and the Jellyfin library
  (each result tells whether it's playable on the server).
- **Trending** — TMDB weekly trends, filterable by movie / tv / anime.
- **Rich media pages** — metadata, cast, directors, seasons and episodes with
  watched state, Rotten Tomatoes scores (via MDBList) and Seerr availability.
- **Seerr requests** — request a movie or seasons on behalf of the calling user.
- **SyncPlay** — invite a user into your group, and automatically stop groups that
  everyone left mid-playback.
- **ASS → SRT** — converts embedded ASS subtitle tracks to external `.srt` files
  (see [Scheduled tasks](#scheduled-tasks)).

## Compatibility

| Jellyfin | .NET | Branch |
|---|---|---|
| **12.x** | net10.0 | `main` |
| 10.11 | net9.0 | `jf-10.11` |

Jellyfin 12 broke plugin binary compatibility, so one DLL cannot serve both.

> **Note** — `Microsoft.Data.Sqlite` must match the exact version bundled with your
> Jellyfin server (10.0.11 for Jellyfin 12.1). Jellyfin loads its own copy; a higher
> version in the plugin makes it fail at startup with a `FileNotFoundException`.

## Build

Requires the .NET SDK matching your branch (10 for `main`, 9 for `jf-10.11`).

```bash
dotnet build
# → bin/Debug/net10.0/Jellyfin.Plugin.EnhancedFin.dll
```

Warnings are treated as errors.

## Installation

1. Create `<jellyfin-config>/plugins/EnhancedFin_1.0.0.0/` and copy the DLL into it.
2. Restart Jellyfin. The plugin creates its configuration file and its database:
   - `<jellyfin-config>/plugins/configurations/Jellyfin.Plugin.EnhancedFin.xml`
   - `<jellyfin-config>/plugins/configurations/EnhancedFin/EnhancedFin.db` (SQLite)
3. Fill in the configuration (below) and restart again.

> If the plugin crashed once, Jellyfin marks it `Malfunctioned` in its `meta.json`
> and stops loading it, even after a fix. Set `"status"` back to `"Active"` and restart.

## Configuration

There is no settings page: edit the XML file directly.

| Key | Required | Purpose |
|---|---|---|
| `TmdbApiKey` | **yes** | metadata, search, trending, release dates |
| `MdblistApiKey` | no | Rotten Tomatoes scores — omitted if empty |
| `SeerrUrl` / `SeerrApiKey` | no | availability and requests — omitted if empty |
| `ConvertAssSubtitles` | no (`false`) | **rewrites your files**: after each library scan, converts embedded ASS subtitles of `.mkv` files to external `.srt` and removes the converted tracks (stream copy, no re-encoding). Useful for players that cannot render ASS. ASS styling is lost. |
| `KeepAssBackup` | no (`true`) | keeps each original as a hidden `.<name>.mkv.ass-backup` next to the film. Doubles disk usage until you delete the backups. |

These keys are secrets: the XML file is git-ignored, keep it that way.

## API

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
| `GET` | `/media/{key}` | full media page + my data (`?detail=true` for cast, directors, scores) |
| `GET` | `/media/{key}/seasons` | seasons, with my watched count |
| `GET` | `/media/{key}/seasons/{n}` | episodes, with my watched state |
| `GET` | `/media/{key}/playable` | `{ playable, itemId? }` — playable on this server for me (`?season=&episode=`) |
| `GET` | `/media/{key}/seasons/{n}/playable` | playable episodes of a season |
| `GET` | `/me/ratings` | my ratings (`?type=`, `?score=`) |
| `GET` | `/me/ratings/pending` | watched but not rated yet |
| `PUT` `DELETE` | `/me/ratings/{key}` | rate (`{ "score": 2 }`) / unrate |
| `GET` | `/me/watchlist` | my watchlist (`?type=`, `?genre=`) |
| `PUT` `DELETE` | `/me/watchlist/{key}` | add / remove |
| `GET` | `/me/continue-watching` | resume list (`?limit=`) |
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

Conventions:

- **JSON properties are camelCase**; null fields are omitted rather than sent as `null`.
- **List routes** accept `limit` / `offset` (max 500) and return the real `total`.
- **Writes are lazy**: a `PUT` on a media unknown to the database fetches it from
  TMDB first. `GET` and `DELETE` never create data.
- **Errors** follow RFC 7807 (`ProblemDetails`).

## Scheduled tasks

Both appear in the dashboard under the **EnhancedFin** category.

| Task | Trigger | What it does |
|---|---|---|
| Media refresh | daily, 4 AM | completes incomplete media records and refreshes release dates of followed shows, in batches of 50 |
| ASS → SRT | after each library scan + manual | converts embedded ASS tracks to `<name>.<lang>[.sdh][.forced].srt`, then remuxes the file without them |

> **Warning** — the ASS task **rewrites your media files**. The remux is verified
> (track count, duration) before an atomic rename, and the original is kept as a
> hidden `.<name>.ass-backup` next to it. On a read-only library it only logs what
> it would have done.

## Project layout

```
Api/            controllers — all inherit EnhancedFinController (auth, identity, errors)
Services/       TMDB, MDBList, Seerr clients, Jellyfin library index, SyncPlay guard
Data/Db.cs      SQLite schema and connection
Tasks/          scheduled tasks
Configuration/  plugin settings
```
