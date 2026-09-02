import { timingSafeEqual } from "node:crypto";
import { readFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { focusListStore } from "../src/store.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const PAGE_PATH = path.join(ROOT, "web", "index.html");
const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  args.set(process.argv[index], process.argv[index + 1]);
}

const token = String(args.get("--token") ?? "");
const requestedPort = Number(args.get("--port") ?? 0);
const parentPid = Number(args.get("--parent-pid") ?? 0);
if (token.length < 24) throw new Error("悬浮窗服务缺少有效的本机访问令牌。");

function sendJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  });
  response.end(body);
}

function authorized(request) {
  const supplied = String(request.headers["x-focus-list-token"] ?? "");
  const expected = Buffer.from(token);
  const actual = Buffer.from(supplied);
  return expected.length === actual.length && timingSafeEqual(expected, actual);
}

async function readJson(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > 64 * 1024) throw new Error("请求内容过大。");
    chunks.push(chunk);
  }
  if (chunks.length === 0) return {};
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

function taskIdFrom(url) {
  const match = url.pathname.match(/^\/api\/tasks\/([a-f0-9-]+)$/i);
  return match ? match[1] : null;
}

const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    if (request.method === "GET" && url.pathname === "/") {
      if (url.searchParams.get("token") !== token) {
        response.writeHead(403).end("Forbidden");
        return;
      }
      const page = await readFile(PAGE_PATH);
      response.writeHead(200, {
        "Content-Type": "text/html; charset=utf-8",
        "Content-Length": page.length,
        "Cache-Control": "no-store",
        "Content-Security-Policy": "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
        "X-Content-Type-Options": "nosniff",
        "Referrer-Policy": "no-referrer",
      });
      response.end(page);
      return;
    }

    if (!url.pathname.startsWith("/api/") || !authorized(request)) {
      response.writeHead(403).end("Forbidden");
      return;
    }

    if (request.method === "GET" && url.pathname === "/api/snapshot") {
      sendJson(response, 200, await focusListStore.snapshot());
      return;
    }

    if (request.method === "POST" && url.pathname === "/api/tasks") {
      const task = await focusListStore.create(await readJson(request));
      sendJson(response, 201, { task });
      return;
    }

    const id = taskIdFrom(url);
    if (request.method === "PATCH" && id) {
      const task = await focusListStore.update(id, await readJson(request));
      sendJson(response, 200, { task });
      return;
    }

    if (request.method === "DELETE" && id) {
      sendJson(response, 200, await focusListStore.remove(id));
      return;
    }

    sendJson(response, 404, { error: "Not found" });
  } catch (error) {
    sendJson(response, 400, { error: error instanceof Error ? error.message : String(error) });
  }
});

server.listen(Number.isFinite(requestedPort) ? requestedPort : 0, "127.0.0.1", () => {
  const address = server.address();
  process.stdout.write(`${JSON.stringify({ port: address.port })}\n`);
});

if (Number.isInteger(parentPid) && parentPid > 0 && process.platform !== "win32") {
  const parentTimer = setInterval(() => {
    try {
      process.kill(parentPid, 0);
    } catch {
      clearInterval(parentTimer);
      server.close(() => process.exit(0));
    }
  }, 2000);
  parentTimer.unref();
}

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
