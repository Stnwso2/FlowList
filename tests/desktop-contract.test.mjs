import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";

const ROOT = path.resolve(import.meta.dirname, "..");

test("desktop host exposes the required floating-window controls", async () => {
  const source = await readFile(path.join(ROOT, "desktop", "Program.cs"), "utf8");
  assert.match(source, /TopMost = true/);
  assert.match(source, /TopMost = !TopMost/);
  assert.match(source, /ToggleCollapsed/);
  assert.match(source, /BeginNativeDrag/);
  assert.match(source, /SaveWindowState/);
  assert.match(source, /FocusListFloatingWindow/);
});

test("web UI contains planning, history, and CRUD interactions", async () => {
  const page = await readFile(path.join(ROOT, "web", "index.html"), "utf8");
  for (const scope of ["today", "week", "later", "completed", "history"]) {
    assert.match(page, new RegExp(`data-scope="${scope}"`));
  }
  assert.doesNotMatch(page, /写下一件要完成的事/);
  assert.match(page, /method: "POST"/);
  assert.match(page, /method: "PATCH"/);
  assert.match(page, /method: "DELETE"/);
  assert.match(page, /X-Focus-List-Token/);
  const scripts = [...page.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]);
  assert.equal(scripts.length, 1);
  assert.doesNotThrow(() => new Function(scripts[0]));
});
