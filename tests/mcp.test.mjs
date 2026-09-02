import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const ROOT = path.resolve(import.meta.dirname, "..");

test("MCP server exposes and executes the focus-list task tools", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "focus-list-mcp-"));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(ROOT, "mcp", "server.mjs")],
    cwd: ROOT,
    env: { ...process.env, FOCUS_LIST_DATA_DIR: directory },
    stderr: "pipe",
  });
  const client = new Client({ name: "focus-list-test", version: "1.0.0" });
  try {
    await client.connect(transport);
    const toolList = await client.listTools();
    assert.deepEqual(
      toolList.tools.map(tool => tool.name).sort(),
      ["add_focus_task", "delete_focus_task", "list_focus_tasks", "open_focus_list", "update_focus_task"],
    );

    const created = await client.callTool({
      name: "add_focus_task",
      arguments: { title: "MCP task", dueDate: "2026-09-01", priority: "high" },
    });
    assert.equal(created.isError, undefined);

    const listed = await client.callTool({ name: "list_focus_tasks", arguments: { scope: "all" } });
    assert.equal(listed.isError, undefined);
    assert.equal(listed.structuredContent.tasks.length, 1);
    assert.equal(listed.structuredContent.tasks[0].title, "MCP task");
  } finally {
    await client.close().catch(() => {});
    await rm(directory, { recursive: true, force: true });
  }
});
