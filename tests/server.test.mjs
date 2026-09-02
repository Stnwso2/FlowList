import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

const ROOT = path.resolve(import.meta.dirname, "..");

async function startServer() {
  const directory = await mkdtemp(path.join(os.tmpdir(), "focus-list-server-"));
  const token = "test-token-that-is-long-enough-123456";
  const child = spawn(process.execPath, [
    path.join(ROOT, "scripts", "focus-list-server.mjs"),
    "--token", token,
    "--port", "0",
    "--parent-pid", String(process.pid),
  ], {
    cwd: ROOT,
    env: { ...process.env, FOCUS_LIST_DATA_DIR: directory },
    stdio: ["ignore", "pipe", "pipe"],
  });

  const firstLine = await new Promise((resolve, reject) => {
    let buffer = "";
    const timeout = setTimeout(() => reject(new Error("server start timeout")), 10000);
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", chunk => {
      buffer += chunk;
      const newline = buffer.indexOf("\n");
      if (newline >= 0) {
        clearTimeout(timeout);
        resolve(buffer.slice(0, newline));
      }
    });
    child.once("exit", code => reject(new Error(`server exited early (${code})`)));
  });
  const { port } = JSON.parse(firstLine);
  return { child, directory, token, base: `http://127.0.0.1:${port}` };
}

test("loopback server protects and mutates the shared task store", async () => {
  const running = await startServer();
  try {
    const forbidden = await fetch(`${running.base}/api/snapshot`);
    assert.equal(forbidden.status, 403);

    const headers = { "Content-Type": "application/json", "X-Focus-List-Token": running.token };
    const createdResponse = await fetch(`${running.base}/api/tasks`, {
      method: "POST",
      headers,
      body: JSON.stringify({ title: "Server task", dueDate: "2026-09-01", priority: "high" }),
    });
    assert.equal(createdResponse.status, 201);
    const { task } = await createdResponse.json();

    const updatedResponse = await fetch(`${running.base}/api/tasks/${task.id}`, {
      method: "PATCH",
      headers,
      body: JSON.stringify({ completed: true }),
    });
    assert.equal(updatedResponse.status, 200);

    const snapshotResponse = await fetch(`${running.base}/api/snapshot`, { headers });
    const snapshot = await snapshotResponse.json();
    assert.equal(snapshot.groups.completed.length, 1);
    assert.equal(snapshot.groups.completed[0].title, "Server task");
  } finally {
    running.child.kill();
    await rm(running.directory, { recursive: true, force: true });
  }
});
