# Stage 5 — Gnutella integration research

Status: research/proof only. Do not merge to `main` until build + live-network smoke tests pass.

## Fixed BlackLink baseline

- BlackLink upstream: `zipper9/blacklink`
- Pinned upstream commit: `1a72cfddca154da9070caca1b5a02df56d5498ab`
- Stage 5 branch starts from clean Stage 4/report commit: `23c670d1471eed92f019d337a57eb8f32bc31ac8`
- Native DC/NMDC/ADC/DHT code is out of scope for Stage 5.
- Existing External Search providers remain unchanged: Soulseek/slskd, Torznab, qBittorrent download backend, aMule/eD2k.

## Stage 5 goal

Add one genuinely new search network without changing the working DC path. The selected target is Gnutella (G1). Gnutella2 is a separate protocol and is not to be mislabeled as part of this Stage unless separately proven.

The existing Stage 4 result model already reserves `NETWORK_GNUTELLA` and contains generic fields for network, backend, name/path, size, source, hash type/hash, download URI, search id, source counts and availability metadata. No replacement result model is required just to add the provider.

## What is explicitly rejected

### Toy native Gnutella 0.6 client

Not acceptable for a rare-file-search client. A useful implementation is more than the initial text handshake and QUERY/QUERY HIT. Production coverage needs mature ultrapeer/leaf behavior, QRP, GGEP, dynamic querying, SHA1/URN handling, HTTP transfer/resume and PUSH for firewalled sources.

### Shareaza as the default Stage 5 dependency

Not selected. The project goal is a unified BlackLink client; adding another full GUI client as a mandatory bridge is not the preferred architecture.

## Candidate A: GnutellaBun — primary Stage 5 candidate

Pinned research source:

- repository: `RickCarlino/gnutella-bun-client`
- commit: `98adf6e9244a5499bff4718069f698f876d745e7`
- package version at that commit: `1.2.0`
- Bun version declared by the project: `1.3.11`

Why selected for proof:

- actively developed in 2026;
- source exposes a public library API rather than requiring UI scraping;
- official build script produces a Windows x64 single executable with Bun;
- source contains QRP, GGEP, compression, TLS, pong caching, BYE and PUSH support;
- query results expose query ID, hop count, remote IP/port, file name, size, SHA1 URN/URN list, vendor, busy/PUSH flags and stable result number;
- downloads support direct HTTP, `/uri-res`, resume, SHA1 verification and PUSH/GIV fallback;
- download queue supports retry/backoff, pause/resume/remove and persisted incomplete files.

Verified public API from `src/protocol.ts`, `src/types.ts`, `src/protocol/node_transfer.ts`:

- `GnutellaServent.start()` / `stop()`
- `sendQuery()`
- `getResults()`
- `getPeers()` / `getStatus()`
- `downloadResult()`
- `getDownloadJobs()`
- event stream with `QUERY_SENT`, `QUERY_RESULT`, peer lifecycle and download lifecycle events.

Search correlation is safe to implement without polling the global result list: `QUERY_SENT` emits the descriptor ID and every `QUERY_RESULT` includes `hit.queryIdHex`. The Stage 5 bridge maps each BlackLink search session to that descriptor ID and only returns hits for the matching session.

Gnutella queries are one-shot routed descriptors. A sent query cannot be recalled from the network; BlackLink cancel therefore means stop collecting results for that local search session, not pretend that a network-wide retraction exists.

### Bootstrap caveat

The source contains a built-in GWebCache seed list, but several seed URLs are historical. Therefore source quality and successful compilation do not prove current public-network reachability. Stage 5 requires a live bootstrap + peer connection + real search hit before merge.

### Stage 5 localhost bridge

A proof bridge is maintained in `stage5/gnutella_bridge.ts`.

It imports only the public `gnutella` package API and exposes a machine interface bound to loopback:

- `GET /v1/health`
- `POST /v1/search`
- `GET /v1/search/{id}`
- `GET /v1/search/{id}/results`
- `DELETE /v1/search/{id}` — local result collection cancellation
- `POST /v1/download`
- `GET /v1/downloads`
- `POST /v1/shutdown`

The bridge rejects a non-loopback API bind. The Gnutella P2P listener remains a separate listener because incoming peer/PUSH connectivity is part of the protocol.

Returned result fields are deliberately close to `ExternalSearch::Result`: name, size, source, SHA1 URN/URNs, vendor, speed, hop count, busy/PUSH and backend result number.

Proof workflow: `.github/workflows/stage5-gnutellabun-proof.yml`

The proof gate performs:

1. checkout of the exact pinned GnutellaBun commit;
2. upstream typecheck + unit tests + integration tests;
3. compile the bridge on Linux;
4. attempt real public Gnutella bootstrap;
5. require at least one public peer;
6. issue exactly one controlled public search and require at least one result;
7. verify local search cancellation semantics;
8. compile a Windows x64 single executable;
9. launch that Windows executable and verify the loopback health API.

The Windows single-EXE compile/startup proof has already passed on the Stage 5 branch. Public-network proof is a separate gate and must pass before BlackLink integration is treated as done.

## Candidate B: MLDonkey core — fallback only

Pinned research source:

- repository: `ygrek/mldonkey`
- commit: `0d4463568fa6374fec964d139a769c5966bb7599`

MLDonkey has an excellent machine-control protocol:

- GUI protocol default port `4001`, localhost-only access by default;
- protocol negotiation through `GuiProtocol` / `CoreProtocol`;
- authentication through `Password`;
- `Search_query` can target a discovered network ID;
- `Search_result` carries a global result number;
- `Download_query` starts a download by global result number;
- `CloseSearch(false)` stops a search without destroying the globally indexed result.

However, its Gnutella/Gnutella2 modules are a poor primary choice: upstream discussion explicitly describes these networks as unmaintained since 2007, while recent work mainly fixes compilation. Its bundled GWebCache configuration is also historical. Therefore MLDonkey is retained only as a fallback/control-protocol reference unless live-network proof is unexpectedly strong.

## Candidate C: gtk-gnutella — mature engine fallback

Research source: `gtk-gnutella/gtk-gnutella`, current `devel` source during Stage 5 research.

Advantages:

- actively maintained Gnutella/G2 implementation;
- current G1 bootstrap contains a Global Host Cache fallback;
- current G2 code has dedicated GWC handling;
- source supports headless/topless compilation;
- mature routing/network implementation.

Control-plane problem:

- stock `--topless` mode does not provide the complete search-result/download machine API needed by BlackLink;
- in current source, the GUI bridge makes `gcu_search_gui_new_search()` return false when `USE_TOPLESS` is active.

Therefore gtk-gnutella is the robust engine fallback if GnutellaBun fails live-network proof. The acceptable fallback is a small source-reviewed localhost machine interface in gtk-gnutella, not GUI scraping.

## Merge gate for Stage 5

Stage 5 is allowed onto `main` only after all of the following are true:

1. Windows backend/helper build is reproducible from pinned sources.
2. Live public Gnutella bootstrap and search have been demonstrated, not assumed.
3. A controlled search returns at least one real public hit with decoded name/size/source and, when available, SHA1.
4. BlackLink provider implements health, search, result polling, download request, local cancel, timeout and failure handling.
5. Search IDs are isolated so an old search cannot leak hits into a new BlackLink search tab.
6. A result remains downloadable after its BlackLink search session is locally cancelled.
7. No DC/NMDC/ADC/DHT source changes are part of the Stage 5 patch.
8. Existing Soulseek/Torznab/qBittorrent/aMule providers still compile and their configuration remains backward compatible.
9. API credentials, if enabled, are not written to logs or `REPORT_FOR_CHATGPT` output.
10. The machine-control API is loopback-only by default.
11. The packaged helper exits cleanly and a dead helper cannot hang BlackLink.
12. The final CI run produces one ready x64 ZIP; exploratory proof runs stay on the Stage 5 branch.

## Decision rule

- Primary: GnutellaBun if the pinned source passes live public peer + search proof.
- Fallback: gtk-gnutella with a minimal machine interface if GnutellaBun cannot reliably reach the current network.
- MLDonkey is not the preferred Gnutella engine due the upstream maintenance status of its G1/G2 modules.
- Do not ship a provider merely because it compiles or displays a `Gnutella` checkbox.
