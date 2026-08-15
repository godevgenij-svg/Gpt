# Stage 4 — eD2k/Kad verified research

Base: `modern-clients-stage3-baseline` / `4405d3d496dffaf29a51bad887d48c4bae23a25f`.

This branch is intentionally based on the last confirmed Stage 3 checkpoint from the “Современные клиенты dc++” line. Later Stage 4/5 branches are reference material only and are not inherited.

## Stage 3 contract we must preserve

- Native BlackLink DC/NMDC/ADC/DHT code remains unchanged.
- External networks enter through `ExternalSearchManager` and `ExternalSearch::Result`.
- Existing providers: Soulseek/slskd and BitTorrent/Torznab; qBittorrent handles BitTorrent downloads.
- Search UI has one master External Search switch; individual providers are enabled in External Search settings.
- External results are inserted into the existing search result window.
- eD2k/Kad already has a placeholder network enum but no implementation in Stage 3.

## aMule source/version facts

Official source: `amule-org/amule`.

The published `3.0.1` release predates the REST `amuleapi` work. Do not claim that stock 3.0.1 contains `amuleapi`.

Verified post-release Windows build used during research:

- aMule commit: `68eb98885dfcdaed407c9b0ace4dacd5fb8065ea`
- official GitHub Actions run: `31881861606`
- artifact: `aMule-3.0.1-557-g68eb98885-Windows-x64.zip`
- artifact digest: `sha256:cbafaf2aa1484886f368e27165db6bd5ea06ae223712af6b2502372dbfb134b7`

The portable Windows package contains `amuled.exe`, `amuleapi.exe`, `amule.exe`, `amulecmd.exe` and their runtime DLLs.

## Native amuleapi lifecycle

Current aMule can auto-start `amuleapi` itself. Relevant preferences:

- `/AmuleApi/Enabled`
- `/AmuleApi/HttpPort` (default 4713)
- `/AmuleApi/BindAddress` (default `127.0.0.1`)
- `/AmuleApi/Path` (default `amuleapi`)

When enabled, the aMule core launches `amuleapi --config-dir=<same config dir> --bind=<address> --http-port=<port>`.

The core generates an ephemeral EC token in the common config directory, writes it with restrictive permissions, starts `amuleapi`, and removes any unread token after a short TTL. `amuleapi` consumes/deletes the token immediately and keeps it only in memory. This is preferable to storing a separate clear-text EC password in `amuleapi.conf`.

This token authenticates only the internal **aMule/amuled ↔ amuleapi EC connection**. It does not authenticate BlackLink to the HTTP API. Stage 4 still needs an amuleapi REST admin credential for search/download mutations, but it does not need to create or store a separate EC password.

Therefore Stage 4 should not invent an EC client and should use the native aMule sidecar/token lifecycle for the backend connection.

## amuleapi HTTP/auth contract

Default REST endpoint: `http://127.0.0.1:4713/api/v0/`.

For remote deployments, plaintext HTTP must not be accepted by the BlackLink provider. Loopback HTTP is acceptable; remote should require HTTPS through a reverse proxy.

Admin login:

- `POST /api/v0/auth/login`
- JSON body: `{ "password": "..." }`
- request `Accept: application/jwt` (or `?type=bearer`) to receive JWT in JSON
- cache the bearer token until rejected

Important current API rule: a polling client must stop using a token on the first `401`, re-authenticate once, and only then resume. Continuing to poll with a stale token can trip the generic 401 limiter and lock the local IP for five minutes.

Provision the REST admin password using the official one-shot CLI:

`amuleapi --config-dir=<dir> --set-admin-pass=<plain>`

Do not write or reverse-engineer `amuleapi-passwords` ourselves. For a one-click package, generate/provision this REST credential once and make the same credential available to the BlackLink provider. The secret must be redacted from diagnostic reports.

## Search contract

Start:

`POST /api/v0/search`

Body:

```json
{
  "query": "required string",
  "type": "local|global|kad"
}
```

Optional supported filters include `file_type`, `extension`, `min_size`, `max_size`, `min_avail`.

Response is `202` with `search_id`.

Poll:

`GET /api/v0/search/results?search_id=N`

The response contains `results`, pagination fields, and a `progress` object with `state` (`running|finished|idle`), `kind`, and `percent`.

A global/local eD2k search and a Kad search are independent and may coexist. Stage 4 should start only the configured mode for a normal search; it must not automatically blast all modes.

Stop/free:

`POST /api/v0/search/stop`

Body example: `{ "search_id": 42, "close": true }`.

`close:true` frees the daemon-side search slot and makes its cached result set unavailable. Keep a search alive only while a result may still need the search-result download endpoint; close it deterministically when the owning BlackLink search is cancelled/replaced/closed.

## Result identity and grouped names

Top-level search results include:

- 32-character eD2k MD4 `hash`
- `name`
- `size`
- `sources.total`
- `sources.complete`
- `already_have`
- `rating`
- `status`
- `type`
- `children[]`

Strong file identity for this provider is the eD2k MD4 hash, with size as an additional consistency check.

Current aMule groups identical content (same hash + size) advertised under different filenames. Alternative names appear in `children[]`; each child has its own `ecid`. Downloading a specific child name requires that `ecid`.

Stage 4 should preserve `ecid` in the external result model so a user-selected grouped filename can be downloaded correctly. The late donor Stage 4 did not preserve these child rows and therefore loses this capability.

## Download contract

`POST /api/v0/search/results/{hash}/download`

Optional body:

```json
{ "category": 0, "ecid": 621 }
```

`ecid` selects a grouped child filename; omit it for the parent/highest-source name.

## What may be reused from the late Stage 4 donor

The late donor branch is useful only as reviewed reference. Reusable ideas:

- `AmuleConfig` in `ExternalSearchManager`
- loopback-HTTP / remote-HTTPS URL safety check
- bearer login
- bounded polling
- search IDs
- eD2k MD4 mapping
- source/complete-source counts
- explicit stop/cancel
- search mode selector `local|global|kad`

Do not transplant it wholesale.

Known donor problems versus current API:

1. It retries/re-authenticates `401` only for search start and download, not for `REQ_AMULE_RESULTS` polling. Current API explicitly requires polling to stop on first `401` and re-authenticate.
2. Its `Password` field is the REST admin password (not an EC password), which is legitimate, but it leaves provisioning/secret handling entirely manual and does not package the current native aMule-managed `amuleapi` sidecar lifecycle.
3. It parses only parent search rows and ignores `children[].ecid`, losing alternate-filename selection.
4. It predates current multi-search/list behavior and should not be treated as authoritative for lifecycle details.

## Stage 4 implementation boundary

Minimum BlackLink-side changes should stay in the external-provider layer:

- `client/ExternalSearchManager.h/.cpp`
- `client/ExternalSearchResult.h` only for provider-neutral fields needed by eD2k (source counts / backend item id)
- `compiled/Settings/ExternalSearch.xml`
- `windows/ExternalSearchSettingsDlg.h/.cpp`
- `windows/SearchFrm.cpp` only for displaying eD2k source counts and forwarding the selected backend item id on download
- resource/localization additions strictly as required

Do not modify native DC protocol/search/queue code for Stage 4.

## Required proof before Stage 4 is considered complete

1. Reconstruct exact Stage 3 and apply only the new Stage 4 delta.
2. Build BlackLink x64 successfully.
3. Use an exact pinned official post-3.0.1 aMule source/runtime revision.
4. Start aMule/amuled + its native `amuleapi` sidecar on loopback.
5. Confirm REST health/auth.
6. Run one controlled eD2k global search through BlackLink provider.
7. Poll growing results and finish state without stale-token loops.
8. Verify MD4/size/source counts and at least one grouped result path if available.
9. Hand one selected result to aMule download endpoint.
10. Cancel/replace a search and verify its daemon slot is closed.
11. Package the required backend/runtime or provide a deterministic one-click launcher; no manual EC configuration should be required.
