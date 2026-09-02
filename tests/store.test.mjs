import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { FocusListStore, endOfWeekString, localDateString } from "../src/store.mjs";

async function withStore(run) {
  const directory = await mkdtemp(path.join(os.tmpdir(), "focus-list-test-"));
  try {
    await run(new FocusListStore(directory), directory);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test("local date helpers use calendar dates", () => {
  const sample = new Date(2026, 8, 1, 10, 30);
  assert.equal(localDateString(sample), "2026-09-01");
  assert.equal(endOfWeekString(sample), "2026-09-06");
});

test("creates, updates, scopes and removes tasks", async () => {
  await withStore(async (store) => {
    const today = await store.create({ title: "  Prepare   weekly plan  ", dueDate: "2026-09-01", priority: "high" });
    const week = await store.create({ title: "Buy supplies", dueDate: "2026-09-04", priority: "normal" });
    const later = await store.create({ title: "Long-range idea", dueDate: null, priority: "low" });

    assert.equal(today.title, "Prepare weekly plan");
    const snapshot = await store.snapshot(new Date(2026, 8, 1, 12, 0));
    assert.deepEqual(snapshot.counts, { today: 1, week: 2, later: 1, completed: 0 });

    const completed = await store.update(week.id, { completed: true });
    assert.equal(completed.completed, true);
    assert.ok(completed.completedAt);

    await store.remove(later.id);
    const after = await store.snapshot(new Date(2026, 8, 1, 12, 0));
    assert.deepEqual(after.counts, { today: 1, week: 1, later: 0, completed: 1 });
  });
});

test("persists valid versioned JSON atomically", async () => {
  await withStore(async (store, directory) => {
    await store.create({ title: "Persistent task", dueDate: "2026-09-01" });
    const payload = JSON.parse(await readFile(path.join(directory, "tasks.json"), "utf8"));
    assert.equal(payload.version, 1);
    assert.equal(payload.tasks.length, 1);
    assert.equal(payload.tasks[0].title, "Persistent task");
  });
});

test("rejects invalid titles, dates and priorities", async () => {
  await withStore(async (store) => {
    await assert.rejects(() => store.create({ title: " " }), /不能为空/);
    await assert.rejects(() => store.create({ title: "Bad date", dueDate: "2026-02-31" }), /日期无效/);
    await assert.rejects(() => store.create({ title: "Bad priority", priority: "urgent" }), /优先级/);
  });
});

test("serializes concurrent writes from separate store instances", async () => {
  await withStore(async (store, directory) => {
    const secondProcessView = new FocusListStore(directory);
    await Promise.all(Array.from({ length: 12 }, (_, index) => {
      const writer = index % 2 === 0 ? store : secondProcessView;
      return writer.create({ title: `Concurrent ${index}`, dueDate: "2026-09-01" });
    }));
    const snapshot = await store.snapshot(new Date(2026, 8, 1, 12, 0));
    assert.equal(snapshot.tasks.length, 12);
    assert.equal(new Set(snapshot.tasks.map(task => task.title)).size, 12);
  });
});
