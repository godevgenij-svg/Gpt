# Stage 5 — Gnutella/Gnutella2 integration research

Status: research/proof only. Do not merge to `main` until build + live-network smoke tests pass.

## Fixed BlackLink baseline

- BlackLink upstream: `zipper9/blacklink`
- Pinned upstream commit: `1a72cfddca154da9070caca1b5a02df56d5498ab`
- Stage 5 branch starts from clean Stage 4/report commit: `23c670d1471eed92f019d337a57eb8f32bc31ac8`
- Native DC/NMDC/ADC/DHT code is out of scope for Stage 5.
- Existing External Search providers remain unchanged: Soulseek/slskd, Torznab, qBittorrent download backend, aMule/eD2k.

## Stage 5 goal

Add a genuinely new search network without changing the working DC path. Candidate target is Gnutella, with Gnutella2 kept distinct when the backend exposes it separately.

The existing Stage 4 result model already reserves `NETWORK_GNUTELLA` and contains generic fields for network, backend, name/path, size, source, hash type/hash, download URI, search id, source counts and availability metadata. No replacement result model is required just to add the provider.

## What is explicitly rejected

### Toy native Gnutella 0.6 client

Not acceptable for a rare-file-search client. A useful modern implementation is more than the initial text handshake and QUERY/QUERY HIT. Production coverage requires the mature ultrapeer/leaf ecosystem and its routing/bootstrap/download behavior. Gnutella2 is a separate protocol.

### Shareaza as the default Stage 5 dependency

Not selected. The project goal is a unified BlackLink client; adding another full GUI client as a mandatory bridge is not the preferred architecture.

## Candidate A: MLDonkey core

Pinned research source:

- repository: `ygrek/mldonkey`
- commit: `0d4463568fa6374fec964d139a769c5966bb7599`

### Why it is attractive

MLDonkey exposes a binary Core/GUI protocol instead of requiring HTML scraping. The same core contains Gnutella and Gnutella2 network modules.

Verified from current source:

- GUI protocol default port: `4001` (`src/daemon/common/commonOptions.ml`)
- telnet default port: `4000`
- HTTP default port: `4080`
- `allowed_ips` defaults to localhost only.
- GUI protocol best version in this source: `41` (`src/daemon/common/guiProto.ml`).
- Initial client message: `GuiProtocol(version)`.
- Core replies with `CoreProtocol(...)`; the official MLDonkey GUI then sends `Password(login,password)`.
- Default account is `admin` with an empty password on a new core (`src/daemon/common/commonUserDb.ml`). Stage 5 packaging must not expose the GUI port remotely and should create/use a dedicated local credential rather than rely on that default.

### Search lifecycle verified from core source

`src/daemon/driver/driverInterface.ml`:

1. `Search_query` creates a search.
2. A result callback emits `Search_result(search_num, result_num, ...)`.
3. If `search_network != 0`, the search is sent only to the matching MLDonkey network ID.
4. `Download_query(filenames, result_num, force)` resolves the global result and starts a download.
5. `CloseSearch(search_num, false)` calls `search_close`.
6. `CloseSearch(search_num, true)` calls `search_forget`.

`src/daemon/common/commonSearch.ml` verifies that `search_close` only asks network modules to stop searching; it does not delete the global indexed result. This is important: unlike the aMule problem already discovered in Stage 4, stopping a search does not inherently destroy the result required by a later `Download_query`.

### Binary framing/encoding verified from source

`src/daemon/common/guiEncoding.ml`:

- frame starts with 4-byte little-endian payload length;
- message opcode follows as a little-endian 16-bit value;
- strings are UTF-8 and normally use a 16-bit length prefix;
- for modern protocol versions, `Search_query` uses opcode `42`;
- search payload contains search number, encoded query, max hits, search type, and (protocol >= 16) network ID;
- a simple keyword query is represented by `Q_KEYWORDS("", text)` by the official GUI;
- result information contains result number, source network, names, UID list, size, format/type/tags/comment/done/time;
- protocol >= 27 uses a list of UID strings rather than the obsolete MD4-only result field.

The Stage 5 BlackLink provider must implement only the verified subset and must negotiate the protocol version from `CoreProtocol`; it must not hard-code version 41 as an assumption about every core.

### Network identity

Do not hard-code Gnutella/Gnutella2 numeric network IDs. Read `Network_info` after authentication, match by network name, then use the returned network ID in `Search_query`.

### Result identity

The MLDonkey Gnutella code stores results with UID-based identity and can expose SHA1/bitprint-related UIDs. BlackLink must parse UID strings and map the strongest supported hash to `ExternalSearch::Result.hashType/hash`. Unknown UID schemes are preserved as metadata rather than silently treated as SHA1.

### Critical problem: stale bootstrap

Current MLDonkey Gnutella source still ships a legacy list of GWebCache URLs in `src/networks/gnutella/gnutellaOptions.ml`, including historical BearShare-era services. Gnutella2 inherits the same option template through `src/networks/gnutella2/g2Options.mlt`.

The MLDonkey redirector code expects old GWebCache-style `?hostfile=1&client=MLDK&version=...` responses. Therefore a successful build is NOT sufficient evidence that this backend can reach the current Gnutella/G2 networks.

No MLDonkey backend may be merged until a live-network test proves:

- bootstrap obtains real peers;
- the core establishes Gnutella/G2 connections;
- a controlled search receives at least one real result on a query known to be present;
- result UID/name/size are correctly decoded;
- a selected result can enter the download path;
- stopping/cancelling the BlackLink search does not destroy a downloadable result;
- core reconnect/restart does not make BlackLink hang.

## Candidate B: gtk-gnutella

Research source: `gtk-gnutella/gtk-gnutella`, branch `devel` (current source during Stage 5 research).

Advantages:

- maintained Gnutella/G2 implementation;
- current G1 bootstrap source includes a Global Host Cache fallback (`src/core/ghc.c`);
- current G2 bootstrap has dedicated GWC handling (`src/core/g2/gwc.c`).

Control-plane drawback:

- stock local shell supports `search add`, but does not expose a complete machine-readable search-result/download API (`src/shell/search.c`).

Therefore gtk-gnutella is the fallback network engine if MLDonkey's Gnutella modules fail live-network proof. The correct fallback is to add a small localhost-only machine interface/result stream to gtk-gnutella, not to scrape its GUI and not to reimplement Gnutella in BlackLink.

## Build proof

Workflow: `.github/workflows/stage5-gnutella-proof.yml`

It pins the exact MLDonkey commit and attempts a Windows build with only Gnutella/Gnutella2 enabled while other MLDonkey P2P networks are disabled. The workflow is a backend proof only and must not modify the BlackLink Stage 4 source.

## Merge gate for Stage 5

Stage 5 is allowed onto `main` only after all of the following are true:

1. Windows backend build is reproducible and pinned.
2. Live G1/G2 bootstrap/search has been demonstrated, not assumed.
3. BlackLink provider implements authenticated localhost connection, protocol negotiation, network discovery, search, result decoding, download request, cancel/close, timeout and reconnect.
4. Gnutella and Gnutella2 are represented separately in UI/result metadata if both pass tests.
5. No DC/NMDC/ADC/DHT source changes are part of the Stage 5 patch.
6. Existing Soulseek/Torznab/qBittorrent/aMule providers still compile and their configuration remains backward compatible.
7. Secrets/credentials are not written to logs or `REPORT_FOR_CHATGPT` output.
8. Packaged helper is bound/allowed to localhost by default.
9. The final CI run builds one ready x64 ZIP; exploratory runs remain on the Stage 5 branch.

## Decision rule

- If MLDonkey passes build + live G1/G2 search/download proof, use its binary Core/GUI protocol as the Stage 5 backend.
- If its current Gnutella modules cannot achieve reliable live-network connectivity even after a minimal, source-reviewed bootstrap update, reject it and use gtk-gnutella with a small local machine interface.
- Do not ship a provider merely because it compiles or displays a "Gnutella" checkbox.
