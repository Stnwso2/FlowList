import { randomUUID } from "node:crypto";
import { mkdir, open, readFile, rename, stat, unlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";

const PRIORITIES = new Set(["high", "normal", "low"]);
const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

export function localDateString(date = new Date()) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function endOfWeekString(date = new Date()) {
  const end = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const day = end.getDay() || 7;
  end.setDate(end.getDate() + (7 - day));
  return localDateString(end);
}

export function defaultDataDirectory() {
  const override = process.env.FOCUS_LIST_DATA_DIR?.trim();
  if (override) return path.resolve(override);
  const localAppData = process.env.LOCALAPPDATA?.trim();
  return localAppData
    ? path.join(localAppData, "FocusList")
    : path.join(os.homedir(), ".focus-list");
}

function cleanTitle(value) {
  const title = String(value ?? "").trim().replace(/\s+/g, " ");
  if (!title) throw new Error("任务标题不能为空。");
  if (title.length > 240) throw new Error("任务标题不能超过 240 个字符。");
  return title;
}

function cleanNote(value) {
  const note = String(value ?? "").trim();
  if (note.length > 4000) throw new Error("任务备注不能超过 4000 个字符。");
  return note;
}

function cleanPriority(value) {
  const priority = String(value ?? "normal").toLowerCase();
  if (!PRIORITIES.has(priority)) throw new Error("优先级必须是 high、normal 或 low。");
  return priority;
}

function cleanDueDate(value, fallback = null) {
  const dueDate = value === undefined ? fallback : value === null || value === "" ? null : String(value);
  if (dueDate === null) return null;
  if (!DATE_PATTERN.test(dueDate)) throw new Error("日期必须使用 YYYY-MM-DD 格式。");
  const [year, month, day] = dueDate.split("-").map(Number);
  const parsed = new Date(year, month - 1, day);
  if (
    parsed.getFullYear() !== year ||
    parsed.getMonth() !== month - 1 ||
    parsed.getDate() !== day
  ) {
    throw new Error("日期无效。");
  }
  return dueDate;
}

function normalizeTask(raw) {
  const now = new Date().toISOString();
  return {
    id: String(raw.id || randomUUID()),
    title: cleanTitle(raw.title),
    note: cleanNote(raw.note),
    dueDate: cleanDueDate(raw.dueDate),
    priority: cleanPriority(raw.priority),
    completed: Boolean(raw.completed),
    createdAt: String(raw.createdAt || now),
    updatedAt: String(raw.updatedAt || now),
    completedAt: raw.completed ? String(raw.completedAt || raw.updatedAt || now) : null,
    order: Number.isFinite(Number(raw.order)) ? Number(raw.order) : 0,
  };
}

function priorityRank(priority) {
  if (priority === "high") return 0;
  if (priority === "low") return 2;
  return 1;
}

export function sortTasks(tasks) {
  return [...tasks].sort((left, right) => {
    if (left.completed !== right.completed) return left.completed ? 1 : -1;
    if (left.completed) {
      return String(right.completedAt || right.updatedAt).localeCompare(
        String(left.completedAt || left.updatedAt),
      );
    }
    const leftDate = left.dueDate || "9999-12-31";
    const rightDate = right.dueDate || "9999-12-31";
    if (leftDate !== rightDate) return leftDate.localeCompare(rightDate);
    const priorityDifference = priorityRank(left.priority) - priorityRank(right.priority);
    if (priorityDifference !== 0) return priorityDifference;
    if (left.order !== right.order) return left.order - right.order;
    return left.createdAt.localeCompare(right.createdAt);
  });
}

export function taskScope(task, now = new Date()) {
  if (task.completed) return "completed";
  const today = localDateString(now);
  const weekEnd = endOfWeekString(now);
  if (task.dueDate && task.dueDate <= today) return "today";
  if (task.dueDate && task.dueDate <= weekEnd) return "week";
  return "later";
}

export class FocusListStore {
  constructor(dataDirectory = defaultDataDirectory()) {
    this.dataDirectory = dataDirectory;
    this.dataPath = path.join(dataDirectory, "tasks.json");
    this.lockPath = path.join(dataDirectory, "tasks.lock");
    this.writeQueue = Promise.resolve();
  }

  async ensure() {
    await mkdir(this.dataDirectory, { recursive: true });
  }

  async load() {
    await this.ensure();
    try {
      const parsed = JSON.parse(await readFile(this.dataPath, "utf8"));
      const items = Array.isArray(parsed) ? parsed : parsed.tasks;
      return sortTasks((Array.isArray(items) ? items : []).map(normalizeTask));
    } catch (error) {
      if (error?.code === "ENOENT") return [];
      if (error instanceof SyntaxError) {
        throw new Error(`任务数据文件损坏：${this.dataPath}`);
      }
      throw error;
    }
  }

  async save(tasks) {
    const normalized = sortTasks(tasks.map(normalizeTask));
    this.writeQueue = this.writeQueue.catch(() => {}).then(async () => {
      await this.ensure();
      const temporaryPath = `${this.dataPath}.${process.pid}.tmp`;
      const payload = JSON.stringify({ version: 1, tasks: normalized }, null, 2);
      await writeFile(temporaryPath, payload, "utf8");
      await rename(temporaryPath, this.dataPath);
    });
    await this.writeQueue;
    return normalized;
  }

  async withMutationLock(operation) {
    await this.ensure();
    let handle;
    for (let attempt = 0; attempt < 80; attempt += 1) {
      try {
        handle = await open(this.lockPath, "wx");
        await handle.writeFile(`${process.pid} ${Date.now()}\n`, "utf8");
        break;
      } catch (error) {
        if (error?.code !== "EEXIST") throw error;
        try {
          const lockInfo = await stat(this.lockPath);
          if (Date.now() - lockInfo.mtimeMs > 15_000) await unlink(this.lockPath);
        } catch (lockError) {
          if (lockError?.code !== "ENOENT") throw lockError;
        }
        await delay(25 + Math.min(attempt, 10) * 5);
      }
    }
    if (!handle) throw new Error("任务数据正被另一个进程占用，请稍后再试。");
    try {
      return await operation();
    } finally {
      await handle.close().catch(() => {});
      await unlink(this.lockPath).catch(error => {
        if (error?.code !== "ENOENT") throw error;
      });
    }
  }

  async snapshot(now = new Date()) {
    const tasks = await this.load();
    const today = localDateString(now);
    const weekEnd = endOfWeekString(now);
    const buckets = { today: [], week: [], later: [], completed: [] };
    for (const task of tasks) buckets[taskScope(task, now)].push(task);
    const groups = {
      today: buckets.today,
      week: sortTasks([...buckets.today, ...buckets.week]),
      later: buckets.later,
      completed: buckets.completed,
    };
    return {
      tasks,
      groups,
      counts: Object.fromEntries(Object.entries(groups).map(([key, value]) => [key, value.length])),
      today,
      weekEnd,
      dataPath: this.dataPath,
    };
  }

  async create(input) {
    return this.withMutationLock(async () => {
      const tasks = await this.load();
      const now = new Date().toISOString();
      const task = normalizeTask({
        id: randomUUID(),
        title: input.title,
        note: input.note,
        dueDate: cleanDueDate(input.dueDate, localDateString()),
        priority: input.priority,
        completed: false,
        createdAt: now,
        updatedAt: now,
        order: tasks.length,
      });
      await this.save([...tasks, task]);
      return task;
    });
  }

  async update(id, changes) {
    return this.withMutationLock(async () => {
      const tasks = await this.load();
      const index = tasks.findIndex((task) => task.id === id);
      if (index < 0) throw new Error("任务不存在。");
      const current = tasks[index];
      const completed = changes.completed === undefined ? current.completed : Boolean(changes.completed);
      const now = new Date().toISOString();
      const updated = normalizeTask({
        ...current,
        ...(changes.title === undefined ? {} : { title: changes.title }),
        ...(changes.note === undefined ? {} : { note: changes.note }),
        ...(changes.dueDate === undefined ? {} : { dueDate: cleanDueDate(changes.dueDate) }),
        ...(changes.priority === undefined ? {} : { priority: changes.priority }),
        completed,
        updatedAt: now,
        completedAt: completed ? current.completedAt || now : null,
      });
      tasks[index] = updated;
      await this.save(tasks);
      return updated;
    });
  }

  async remove(id) {
    return this.withMutationLock(async () => {
      const tasks = await this.load();
      const remaining = tasks.filter((task) => task.id !== id);
      if (remaining.length === tasks.length) throw new Error("任务不存在。");
      await this.save(remaining);
      return { id, removed: true };
    });
  }
}

export const focusListStore = new FocusListStore();
