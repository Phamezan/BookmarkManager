import { createServer } from "node:http";

const PORT = process.env.MOCK_PORT ? Number(process.env.MOCK_PORT) : 8080;
const VERBOSE = process.env.MOCK_VERBOSE === "1";

function log(msg) {
  process.stdout.write(`${msg}\n`);
}

const trackedRoots = [
  {
    trackedRootId: "0864a0b6-6096-4f99-949f-a2cda748df56",
    browserNodeId: "42",
    displayName: "Manga",
    defaultCategory: "Manga",
  },
];

const seenEventIds = new Set();
const completedOperations = new Set();
let configVersion = 4;
let snapshotRequest = null;
let commands = [];

function sendJson(res, status, body) {
  const json = body !== undefined ? JSON.stringify(body) : "";
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Headers": "Content-Type",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  });
  res.end(json);
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = "";
    req.on("data", (chunk) => (data += chunk));
    req.on("end", () => {
      try {
        resolve(data ? JSON.parse(data) : {});
      } catch {
        resolve({});
      }
    });
  });
}

function ts() {
  return new Date().toLocaleTimeString();
}

const server = createServer(async (req, res) => {
  if (req.method === "OPTIONS") {
    sendJson(res, 204);
    return;
  }

  const url = new URL(req.url, `http://localhost:${PORT}`);
  const path = url.pathname;
  const body = await readBody(req);

  log("");
  log(`[${ts()}] ${req.method} ${path}`);
  if (VERBOSE) {
    log(`[${ts()}] Body: ${JSON.stringify(body, null, 2)}`);
  }

  if (path === "/api/extension/heartbeat" && req.method === "POST") {
    log(`[${ts()}] → heartbeat: extVersion=${body.extensionVersion}, configVersion=${body.localConfigVersion}, pending=${body.pendingEventCount}`);
    sendJson(res, 200, {
      extensionClientId: "4d477a68-13cb-4fad-b5af-0aad9d747de1",
      serverTime: new Date().toISOString(),
      configVersion,
      pollIntervalSeconds: 30,
      trackedRootCount: trackedRoots.length,
    });
    return;
  }

  if (path === "/api/extension/folders" && req.method === "POST") {
    log(`[${ts()}] → catalog: ${body.folders?.length || 0} folders, catalogId=${body.catalogId?.slice(0, 8)}...`);
    sendJson(res, 202, {
      catalogId: body.catalogId,
      acceptedAt: new Date().toISOString(),
    });
    return;
  }

  if (path === "/api/extension/config" && req.method === "GET") {
    log(`[${ts()}] → returning config: version=${configVersion}, roots=${trackedRoots.length}, snapshot=${snapshotRequest ? "yes" : "no"}`);
    sendJson(res, 200, {
      configVersion,
      pollIntervalSeconds: 30,
      trackedRoots,
      snapshotRequest,
    });
    return;
  }

  if (path === "/api/extension/snapshot" && req.method === "POST") {
    const rootCount = body.roots?.length || 0;
    const totalChildren = (body.roots || []).reduce((sum, r) => sum + (r.root?.children?.length || 0), 0);
    log(`[${ts()}] → snapshot: ${rootCount} roots, ${totalChildren} total children, requestId=${body.requestId?.slice(0, 8)}...`);
    for (const r of body.roots || []) {
      log(`[${ts()}]   root: "${r.root?.title}" (${r.root?.children?.length || 0} children)`);
    }
    const mappings = (body.roots || []).flatMap((r) =>
      (r.root?.children || []).map((child) => ({
        bookmarkId: `bm-${child.browserNodeId}`,
        browserNodeId: child.browserNodeId,
      })),
    );
    snapshotRequest = null;
    sendJson(res, 202, {
      requestId: body.requestId,
      acceptedAt: new Date().toISOString(),
      mappings,
    });
    return;
  }

  if (path === "/api/extension/events" && req.method === "POST") {
    const accepted = [];
    const duplicates = [];
    for (const event of body.events || []) {
      if (seenEventIds.has(event.eventId)) {
        duplicates.push(event.eventId);
      } else {
        seenEventIds.add(event.eventId);
        accepted.push(event.eventId);
        log(`[${ts()}]   event: ${event.eventType} node=${event.browserNodeId} root=${event.trackedRootBrowserNodeId ?? "null"}`);
      }
    }
    log(`[${ts()}] → events: ${accepted.length} accepted, ${duplicates.length} duplicates`);
    sendJson(res, 200, {
      batchId: body.batchId,
      acceptedEventIds: accepted,
      duplicateEventIds: duplicates,
      configVersion,
    });
    return;
  }

  if (path === "/api/extension/commands/claim" && req.method === "POST") {
    log(`[${ts()}] → claim: returning ${commands.length} commands`);
    sendJson(res, 200, { commands });
    return;
  }

  if (path.startsWith("/api/extension/commands/") && path.endsWith("/complete") && req.method === "POST") {
    const operationId = path.split("/")[4];
    completedOperations.add(operationId);
    log(`[${ts()}] → complete: op=${operationId?.slice(0, 8)}... status=${body.status} nodeId=${body.browserNodeId ?? "null"}`);
    sendJson(res, 204);
    return;
  }

  log(`[${ts()}] → 404 NOT FOUND: ${path}`);
  sendJson(res, 404, { code: "NOT_FOUND", detail: `Unknown route: ${path}` });
});

server.listen(PORT, () => {
  log("");
  log("========================================");
  log(" Bookmark Manager Mock API Server");
  log("========================================");
  log(`Listening on http://localhost:${PORT}`);
  log(`Verbose mode: ${VERBOSE ? "ON" : "OFF"}`);
  log("");
  log("Tracked roots:");
  trackedRoots.forEach((r) => log(`  ${r.displayName} (browserNodeId: ${r.browserNodeId}, id: ${r.trackedRootId})`));
  log("");
  log("In the extension popup:");
  log(`  API Base URL: http://localhost:${PORT}`);
  log("");
  log("Waiting for extension connections...");
  log("");
});
