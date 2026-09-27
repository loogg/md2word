import assert from "node:assert/strict";
import { createServer } from "node:http";
import { test } from "node:test";
import { startBrowserReviewBridge } from "./dev-browser-bridge.mjs";

async function availablePort() {
  const server = createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  await new Promise((resolve) => server.close(resolve));
  return port;
}

test("development bridge requires its token and trusted browser origin", async () => {
  const port = await availablePort();
  const secret = "a".repeat(64);
  const calls = [];
  let emit = () => {};
  const stop = await startBrowserReviewBridge({
    port,
    secret,
    invoke: async (channel, args) => {
      calls.push({ channel, args });
      if (channel !== "allowed") throw Object.assign(new Error("Forbidden command"), { code: "IPC_FORBIDDEN" });
      return { value: args[0] };
    },
    subscribe: (listener) => { emit = listener; return () => { emit = () => {}; }; },
    toPublicError: (error) => ({ code: error.code ?? "BRIDGE_INVALID", message: error.message, retryable: false }),
  });
  const base = `http://127.0.0.1:${port}`;
  const headers = { "x-md2word-review-token": secret, Origin: "http://127.0.0.1:4173", "Content-Type": "application/json" };
  try {
    assert.equal((await fetch(`${base}/health`)).status, 403);
    assert.equal((await fetch(`${base}/health`, { headers })).status, 200);
    assert.equal((await fetch(`${base}/invoke`, {
      method: "POST", headers: { ...headers, Origin: "https://other.example" }, body: JSON.stringify({ channel: "allowed", args: [1] }),
    })).status, 403);
    assert.deepEqual(await (await fetch(`${base}/invoke`, {
      method: "POST", headers, body: JSON.stringify({ channel: "allowed", args: [7] }),
    })).json(), { ok: true, value: { value: 7 } });
    assert.deepEqual(calls, [{ channel: "allowed", args: [7] }]);
    const forbidden = await (await fetch(`${base}/invoke`, {
      method: "POST", headers, body: JSON.stringify({ channel: "forbidden", args: [] }),
    })).json();
    assert.equal(forbidden.ok, false);
    assert.equal(forbidden.error.code, "IPC_FORBIDDEN");

    const abort = new AbortController();
    const events = await fetch(`${base}/events`, { headers, signal: abort.signal });
    assert.equal(events.status, 200);
    const reader = events.body.getReader();
    await reader.read(); // Initial SSE comment.
    emit({ jobId: "synthetic-job", timestamp: "2026-09-27T00:00:00Z", kind: "stage" });
    const frame = new TextDecoder().decode((await reader.read()).value);
    assert.match(frame, /synthetic-job/);
    abort.abort();
  } finally {
    await stop();
  }
});
