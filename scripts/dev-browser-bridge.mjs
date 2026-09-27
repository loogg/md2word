import { timingSafeEqual } from "node:crypto";
import { createServer } from "node:http";

const MAX_BODY_BYTES = 64 * 1024;

function sameSecret(provided, expected) {
  if (typeof provided !== "string" || typeof expected !== "string") return false;
  const left = Buffer.from(provided);
  const right = Buffer.from(expected);
  return left.length === right.length && timingSafeEqual(left, right);
}

function sendJson(response, status, value) {
  response.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  });
  response.end(JSON.stringify(value));
}

async function readJson(request) {
  let size = 0;
  const chunks = [];
  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_BODY_BYTES) throw new Error("Bridge request is too large.");
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

/** Development-only HTTP adapter for the same validated Main command handlers used by Electron IPC. */
export async function startBrowserReviewBridge({ secret, invoke, subscribe, toPublicError, port = 4174 }) {
  if (!/^[0-9a-f]{64}$/.test(secret)) throw new Error("Browser Review requires a random 32-byte secret.");
  const origin = "http://127.0.0.1:4173";
  const clients = new Set();
  const server = createServer(async (request, response) => {
    if (request.headers.host !== `127.0.0.1:${port}`
      || !sameSecret(request.headers["x-md2word-review-token"], secret)) {
      sendJson(response, 403, { error: "Browser Review request rejected." });
      return;
    }

    const route = new URL(request.url ?? "/", `http://127.0.0.1:${port}`).pathname;
    if (request.method === "GET" && route === "/health") {
      sendJson(response, 200, { ready: true, backend: "electron-worker" });
      return;
    }
    if (request.method === "GET" && route === "/events") {
      if (request.headers.origin && request.headers.origin !== origin) {
        sendJson(response, 403, { error: "Browser Review origin rejected." });
        return;
      }
      response.writeHead(200, {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-store",
        Connection: "keep-alive",
        "X-Content-Type-Options": "nosniff",
      });
      response.write(": connected\n\n");
      const unsubscribe = subscribe((event) => {
        if (!response.destroyed) response.write(`data: ${JSON.stringify(event)}\n\n`);
      });
      clients.add(response);
      const keepAlive = setInterval(() => {
        if (!response.destroyed) response.write(": keep-alive\n\n");
      }, 15_000);
      request.on("close", () => {
        clearInterval(keepAlive);
        clients.delete(response);
        unsubscribe();
      });
      return;
    }
    if (request.method !== "POST" || route !== "/invoke") {
      sendJson(response, 404, { error: "Browser Review route not found." });
      return;
    }
    if (request.headers.origin !== origin
      || !/^application\/json(?:;|$)/i.test(request.headers["content-type"] ?? "")) {
      sendJson(response, 403, { error: "Browser Review origin or content type rejected." });
      return;
    }
    try {
      const input = await readJson(request);
      if (!input || typeof input !== "object" || typeof input.channel !== "string"
        || !Array.isArray(input.args) || input.args.length > 2) {
        throw new Error("Invalid Browser Review command.");
      }
      const value = await invoke(input.channel, input.args);
      sendJson(response, 200, { ok: true, value: value ?? null });
    } catch (error) {
      sendJson(response, 200, { ok: false, error: toPublicError(error) });
    }
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolve);
  });
  return async () => {
    for (const client of clients) client.end();
    await new Promise((resolve) => server.close(resolve));
  };
}
