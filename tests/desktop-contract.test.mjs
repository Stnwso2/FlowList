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
  assert.match(source, /ApplyWindowRegion/);
  assert.match(source, /CreateRoundedRectangle\(ClientRectangle, 22\)/);
  assert.match(source, /WebMessageReceived/);
  assert.match(source, /PostWebMessageAsJson/);
  assert.match(source, /BeginNativeDragFromWeb/);
  assert.match(source, /DefaultBackgroundColor = Color\.FromArgb\(238, 244, 253\)/);
  assert.doesNotMatch(source, /Opacity = 0\.98/);
  assert.match(source, /CompactWidth = 176/);
  assert.match(source, /CompactHeight = 56/);
  assert.match(source, /ConfigureCompactPanel/);
  assert.match(source, /ApplyCompactVisualState/);
  assert.match(source, /_webView\.Visible = false/);
  assert.match(source, /expandedWidth = _expandedWidth/);
  assert.match(source, /compactTopmostButton/);
  assert.match(source, /compactCloseButton/);
});

test("web UI contains planning, history, and CRUD interactions", async () => {
  const page = await readFile(path.join(ROOT, "web", "index.html"), "utf8");
  for (const scope of ["today", "week", "later", "completed", "history"]) {
    assert.match(page, new RegExp(`data-scope="${scope}"`));
  }
  assert.doesNotMatch(page, /写下一件要完成的事/);
  assert.doesNotMatch(page, /Today&#39;s focus|Today's focus/);
  assert.match(page, /priority-picker/);
  assert.doesNotMatch(page, /<select/);
  assert.match(page, /window-toolbar/);
  assert.match(page, /data-window-action="topmost"/);
  assert.match(page, /data-window-action="collapse"/);
  assert.match(page, /data-window-action="close"/);
  assert.match(page, /button:focus \{ outline: none; \}/);
  assert.match(page, /outline: none !important/);
  assert.match(page, /method: "POST"/);
  assert.match(page, /method: "PATCH"/);
  assert.match(page, /method: "DELETE"/);
  assert.match(page, /X-Focus-List-Token/);
  const scripts = [...page.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]);
  assert.equal(scripts.length, 1);
  assert.doesNotThrow(() => new Function(scripts[0]));
});
