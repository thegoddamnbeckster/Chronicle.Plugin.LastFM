# Chronicle.Plugin.LastFM

[Last.fm](https://www.last.fm/) metadata provider plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Adds artist biographies, album and track write-ups, community tags, listener and play counts, similar
artists, album cover art and album track listings to Chronicle's music library. It supplements
MusicBrainz (which keeps titles and identity); it does not replace it.

## Setup

1. Create a free API key at <https://www.last.fm/api/account/create>.
2. Install the plugin in Chronicle (**Settings → Plugins**).
3. Open the plugin's settings and paste the key into **Last.fm API Key** (stored encrypted).
4. Run a metadata refresh on any music entry.

## Supported Media Types

`music` — Artist → Album → Track.

| Level | Fields |
|-------|--------|
| Artist | overview (biography), tags; extended: stats, similar artists, full biography, MBID |
| Album | overview, poster_url, tags; extended: stats, track listing, full write-up |
| Track | overview, runtime_minutes, poster_url, tags; extended: stats, album, MBIDs |

Everything Last.fm returns that has no first-class field is kept in the item's extended data.

Notes:

- Last.fm no longer serves real artist pictures (every artist image is a grey-star placeholder), so
  artists get no poster from this plugin. The placeholder is detected and discarded.
- Tags are Last.fm's community folksonomy ("seen live", "female vocalists" …), so they are stored as
  **tags**, not genres.

## How items are matched

1. A MusicBrainz id Chronicle already holds (artist → artist id, track → recording id).
2. An exact lookup by name, using the parent names Chronicle supplies (album → artist, track → artist + album).
3. Last.fm's own search, scored by title and artist agreement. A same-titled album or track by a
   different artist is penalised rather than treated as a weak match.

Albums are never looked up by MusicBrainz id: Last.fm's album id is a *release* id while Chronicle
stores release-*group* ids.

## External ID format

Last.fm has no ids of its own, so ids are percent-encoded names:

- `artist:Radiohead`
- `album:Radiohead/OK%20Computer`
- `track:Radiohead/Karma%20Police` or `track:Radiohead/OK%20Computer/Karma%20Police`

A track id includes the album it was matched under when known, so two same-named tracks on different
albums never share an id. Fix Match also accepts last.fm URLs
(`https://www.last.fm/music/Radiohead`, `…/Radiohead/OK+Computer`, `…/Radiohead/_/Karma+Police`).

## Development

```bash
dotnet build
dotnet test tests
```

Rate limiting: requests are paced at ~4/s (Last.fm allows ~5/s); rate-limit and transient errors are
retried with backoff, and an invalid or suspended key surfaces as a plugin authentication failure.
