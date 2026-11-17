# Trailhead API

| | |
|---|---|
| Plugin id | `e88e0913-0a07-4904-bcdc-80f925458cac` |
| Base path | `/trailhead/` |
| Jellyfin | 12.0 and later |

The routes below are the ones Trailhead's setup page calls. They change in any release until Trailhead's first stable
release. JSON is PascalCase, as Jellyfin's is, and is read in either casing.

## All routes

Every route needs an administrator (`admin`: a signed-in administrator, or an API key from Dashboard, API Keys).

| Method | Path | Level | Purpose |
|---|---|---|---|
| `GET` | `/trailhead/setup` | admin | The state the setup page draws itself from. Never holds the Dynu key. |
| `POST` | `/trailhead/dynu-key` | admin | Checks a Dynu API key with Dynu and, if Dynu accepts it, saves it sealed. |
| `POST` | `/trailhead/name/check` | admin | Looks a name up on the Dynu account. Saves nothing. |
| `POST` | `/trailhead/name/claim` | admin | Creates the name on the Dynu account (or finds it) and saves it as the web address. |
| `GET` | `/trailhead/strings/{lang}` | admin | The page's text in one language, with fallbacks merged in. |

## Anonymous routes

None.

## Changed Jellyfin routes

| Route | What changes | When | Registered type | Opt out | Do-no-harm test |
|---|---|---|---|---|---|
| `POST /Plugins/e88e0913-0a07-4904-bcdc-80f925458cac/Configuration` | The posted `Hostname`, `DynuKey`, `PendingName` and `NameCreatedByTrailhead` are ignored; the saved ones stay. Everything else is saved as Jellyfin does. | Always, for Trailhead's own configuration | `Jellyfin.Plugin.Trailhead.Plugin` (`UpdateConfiguration`) | None: a plain-text key posted here would be saved unsealed. | `ConfigGateTests.The_generic_save_keeps_the_owned_settings` |

## Authentication

Jellyfin's own: the `Authorization: MediaBrowser Token="..."` header (the older `X-Emby-Token` header only works if the server has legacy authorization turned on). A missing token gives 401, a user who isn't an
administrator gives 403. The Dynu key goes in a request body only, never the query.

## Request and response shapes

`GET /trailhead/setup` answers `{ DynuKey, Hostname, Parents, Renewal }`.

- `DynuKey` is `None`, `Saved` or `Reenter` (a key is saved but can't be opened on this server).
- `Hostname` is empty until a name is saved.
- `Parents` is `[{ Name, IsDefault }]`, best first.
- `Renewal` is `{ State, Outcome, At }`. `State` is `NotRunYet`, `NotSetUp`, `Ran` or `BuildFailed`. `Outcome` is
  `Issued`, `UpToDate`, `Blocked`, `Failed` or null. `At` is when the last check finished, or null.

`POST /trailhead/dynu-key` takes `{ Key }` and answers `{ NameCleared, Setup }`. `NameCleared` is true when the new
key's account doesn't have the saved name, so the name was cleared.

`POST /trailhead/name/check` takes `{ Label, Parent }` and answers `{ Kind, Name, Problem, Message }`. `Kind` is `Ours`,
`Unchecked` or `Invalid`.

`POST /trailhead/name/claim` takes `{ Label, Parent }` and answers
`{ Kind, Name, Problem, Message, Alternatives, Setup }`. `Kind` is `Claimed`, `Ours`, `Taken`, `AccountFull`,
`DynuRefused` or `Invalid`. These are answers about the name, not failures. `Alternatives` has up to three other
endings for a taken name. `Problem` is null, or the rule an invalid name broke. `Message` is the words, translated.

`GET /trailhead/strings/{lang}` answers one flat object of key to text. `lang` is a language tag such as `en` or
`pt-br`. One that Trailhead has no table for answers in the closest language it has, or English.

## Errors

The body is plain text for the user, translated by `Accept-Language`, and its wording can change in any release.
`X-Campground-Error` carries the code, which doesn't. A caller treats a code it doesn't know by its status. 429 and 503
come with `Retry-After`.

| Code | Status | Meaning |
|---|---|---|
| `dynu_key_invalid` | 400 | The key is empty, over 256 characters, or has spaces, control characters or non-ASCII characters. Nothing was sent to Dynu. |
| `dynu_key_rejected` | 400 or 409 | Dynu refused the key. 400 from the key route, 409 from a name route (the saved key no longer works). |
| `dynu_key_missing` | 409 | No key is saved, or the saved one can't be opened on this server. |
| `dynu_unavailable` | 503 | Dynu didn't answer in time, or answered with an error. Nothing was saved. |
| `rate_limited` | 429 | Too many tries. |
| `bad_request` | 400 | The body isn't the JSON the route takes, or `lang` isn't a language tag. |
| `request_too_large` | 400 | The body is over 4 KB. |
| `save_failed` | 500 | A setting was written and didn't read back the same. |
| `internal` | 500 | A bug. Jellyfin's log has the type of the error. |

## Rate limits

Counted before the work is done, for the whole server.

| Routes | Attempts | Window |
|---|---|---|
| `POST /trailhead/dynu-key` | 10 | 10 minutes |
| `POST /trailhead/name/check` | 60 | 1 minute |
| `POST /trailhead/name/claim` | 10 | 1 minute |

## Caching

`GET /trailhead/strings/{lang}` sends an `ETag` and answers `If-None-Match` with 304. Other routes send nothing to
cache.

## Stability

Fields keep their type and meaning within a major version. Callers ignore fields they don't know.

## For other plugins

Nothing is offered in-process yet.
