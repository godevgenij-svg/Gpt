import crypto from "node:crypto";
import path from "node:path";
import process from "node:process";

import {
  GnutellaServent,
  loadDoc,
  type GnutellaEvent,
  type SearchHit,
} from "gnutella";

type SearchSession = {
  id: string;
  query: string;
  queryIdHex?: string;
  createdAt: string;
  active: boolean;
  results: BridgeResult[];
  resultNos: Set<number>;
  error?: string;
};

type BridgeResult = {
  resultNo: number;
  queryIdHex: string;
  queryHops: number;
  name: string;
  size: number;
  source: string;
  speedKBps: number;
  fileIndex: number;
  sha1Urn?: string;
  urns: string[];
  vendor?: string;
  needsPush: boolean;
  busy: boolean;
  serventIdHex: string;
};

function argValue(name: string, fallback: string): string {
  const prefix = `--${name}=`;
  const arg = process.argv.find((value) => value.startsWith(prefix));
  return arg ? arg.slice(prefix.length) : fallback;
}

function positivePort(value: string, fallback: number): number {
  const port = Number(value);
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : fallback;
}

const configPath = path.resolve(
  argValue("config", process.env.GNUTELLA_CONFIG || "gnutella-bridge.json"),
);
const apiHost = argValue("api-host", process.env.GNUTELLA_API_HOST || "127.0.0.1");
const apiPort = positivePort(
  argValue("api-port", process.env.GNUTELLA_API_PORT || "47831"),
  47831,
);
const apiToken = argValue("api-token", process.env.GNUTELLA_API_TOKEN || "");

if (apiHost !== "127.0.0.1" && apiHost !== "::1" && apiHost !== "localhost") {
  throw new Error("Stage 5 bridge API must bind to loopback");
}

const sessions = new Map<string, SearchSession>();
const queryToSession = new Map<string, string>();
let armingSession: string | undefined;
let shuttingDown = false;

function resultFromHit(hit: SearchHit): BridgeResult {
  return {
    resultNo: hit.resultNo,
    queryIdHex: hit.queryIdHex,
    queryHops: hit.queryHops,
    name: hit.fileName,
    size: hit.fileSize,
    source: `${hit.remoteHost}:${hit.remotePort}`,
    speedKBps: hit.speedKBps,
    fileIndex: hit.fileIndex,
    sha1Urn: hit.sha1Urn,
    urns: [...(hit.urns || [])],
    vendor: hit.vendorCode,
    needsPush: hit.needsPush === true,
    busy: hit.busy === true,
    serventIdHex: hit.serventIdHex,
  };
}

function onEvent(event: GnutellaEvent): void {
  if (event.type === "QUERY_SENT" && armingSession) {
    const session = sessions.get(armingSession);
    if (session) {
      session.queryIdHex = event.descriptorIdHex;
      queryToSession.set(event.descriptorIdHex, session.id);
    }
    return;
  }
  if (event.type === "QUERY_SKIPPED" && armingSession) {
    const session = sessions.get(armingSession);
    if (session) session.error = event.reason;
    return;
  }
  if (event.type !== "QUERY_RESULT") return;
  const sessionId = queryToSession.get(event.hit.queryIdHex);
  if (!sessionId) return;
  const session = sessions.get(sessionId);
  if (!session || !session.active) return;
  if (session.resultNos.has(event.hit.resultNo)) return;
  session.resultNos.add(event.hit.resultNo);
  session.results.push(resultFromHit(event.hit));
}

const doc = await loadDoc(configPath);
const node = new GnutellaServent(configPath, doc, { onEvent });
await node.start();

function json(data: unknown, status = 200): Response {
  return Response.json(data, { status });
}

function unauthorized(): Response {
  return json({ error: "unauthorized" }, 401);
}

function authorized(request: Request): boolean {
  if (!apiToken) return true;
  return request.headers.get("authorization") === `Bearer ${apiToken}`;
}

async function readJson(request: Request): Promise<Record<string, unknown>> {
  try {
    const value = await request.json();
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      throw new Error("JSON object required");
    }
    return value as Record<string, unknown>;
  } catch {
    throw new Error("invalid JSON body");
  }
}

function sessionView(session: SearchSession) {
  return {
    id: session.id,
    query: session.query,
    queryIdHex: session.queryIdHex,
    createdAt: session.createdAt,
    active: session.active,
    error: session.error,
    resultCount: session.results.length,
  };
}

function matchSearchPath(pathname: string): { id: string; results: boolean } | undefined {
  const match = /^\/v1\/search\/([^/]+)(\/results)?$/.exec(pathname);
  if (!match) return undefined;
  return { id: decodeURIComponent(match[1]), results: !!match[2] };
}

async function handle(request: Request): Promise<Response> {
  if (!authorized(request)) return unauthorized();
  const url = new URL(request.url);

  if (request.method === "GET" && url.pathname === "/v1/health") {
    return json({
      ok: true,
      bridge: "blacklink-stage5-gnutella",
      status: node.getStatus(),
      peers: node.getPeers(),
      activeSearches: [...sessions.values()].filter((s) => s.active).length,
    });
  }

  if (request.method === "POST" && url.pathname === "/v1/search") {
    const body = await readJson(request);
    const query = typeof body.query === "string" ? body.query.trim() : "";
    if (!query) return json({ error: "query is required" }, 400);
    const session: SearchSession = {
      id: crypto.randomUUID(),
      query,
      createdAt: new Date().toISOString(),
      active: true,
      results: [],
      resultNos: new Set<number>(),
    };
    sessions.set(session.id, session);
    armingSession = session.id;
    try {
      node.sendQuery(query);
    } finally {
      armingSession = undefined;
    }
    return json(sessionView(session), session.error ? 503 : 202);
  }

  const searchPath = matchSearchPath(url.pathname);
  if (searchPath) {
    const session = sessions.get(searchPath.id);
    if (!session) return json({ error: "search not found" }, 404);
    if (request.method === "GET" && searchPath.results) {
      return json({ ...sessionView(session), results: session.results });
    }
    if (request.method === "GET" && !searchPath.results) {
      return json(sessionView(session));
    }
    if (request.method === "DELETE" && !searchPath.results) {
      session.active = false;
      return json(sessionView(session));
    }
  }

  if (request.method === "POST" && url.pathname === "/v1/download") {
    const body = await readJson(request);
    const resultNo = Number(body.resultNo);
    if (!Number.isInteger(resultNo) || resultNo <= 0) {
      return json({ error: "positive resultNo is required" }, 400);
    }
    const destPath = typeof body.destPath === "string" && body.destPath.trim()
      ? body.destPath.trim()
      : undefined;
    const job = await node.downloadResult(resultNo, destPath);
    return json({ job }, 202);
  }

  if (request.method === "GET" && url.pathname === "/v1/downloads") {
    return json({ downloads: node.getDownloadJobs() });
  }

  if (request.method === "POST" && url.pathname === "/v1/shutdown") {
    if (!shuttingDown) {
      shuttingDown = true;
      queueMicrotask(async () => {
        try {
          await node.stop();
          server.stop(true);
          process.exitCode = 0;
        } catch (error) {
          console.error(error);
          process.exitCode = 1;
        }
      });
    }
    return json({ ok: true });
  }

  return json({ error: "not found" }, 404);
}

const server = Bun.serve({
  hostname: apiHost,
  port: apiPort,
  async fetch(request) {
    try {
      return await handle(request);
    } catch (error) {
      return json(
        { error: error instanceof Error ? error.message : String(error) },
        500,
      );
    }
  },
});

console.log(
  JSON.stringify({
    event: "bridge-ready",
    api: `http://${apiHost}:${apiPort}`,
    config: configPath,
    p2p: node.getStatus(),
  }),
);

async function shutdown(signal: string) {
  if (shuttingDown) return;
  shuttingDown = true;
  console.log(JSON.stringify({ event: "shutdown", signal }));
  await node.stop();
  server.stop(true);
}

for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.once(signal, () => {
    void shutdown(signal).finally(() => {
      process.exitCode = 0;
    });
  });
}
