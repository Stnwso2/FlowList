import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { focusListStore } from "../src/store.mjs";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function textResult(value) {
  return {
    content: [{ type: "text", text: JSON.stringify(value, null, 2) }],
    structuredContent: value,
  };
}

function errorResult(error) {
  return {
    isError: true,
    content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }],
  };
}

function launchDesktop() {
  if (process.platform !== "win32") return { launched: false, reason: "unsupported_platform" };
  const executable = path.join(ROOT, "assets", "desktop", "FocusListFloat.exe");
  if (!existsSync(executable)) return { launched: false, reason: "desktop_host_missing", executable };
  const child = spawn(executable, [], { cwd: ROOT, detached: true, stdio: "ignore" });
  child.unref();
  return { launched: true, pid: child.pid, executable };
}

const server = new McpServer(
  { name: "focus-list", version: "0.1.0" },
  {
    instructions:
      "这是同一份本地任务数据的 Codex 接口和 Windows 悬浮清单。需要展示清单时先调用 open_focus_list；新增任务默认归入今天，除非用户明确给出其他日期。删除任务前确认目标。",
  },
);

server.registerTool(
  "open_focus_list",
  {
    title: "打开焦点清单悬浮窗",
    description: "打开或唤醒可拖动、可收拢并能切换置顶层级的 Windows 任务清单。",
    inputSchema: {},
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async () => {
    try {
      const [floating, snapshot] = await Promise.all([launchDesktop(), focusListStore.snapshot()]);
      return textResult({ floating, snapshot });
    } catch (error) {
      return errorResult(error);
    }
  },
);

server.registerTool(
  "list_focus_tasks",
  {
    title: "列出焦点任务",
    description: "读取今天、本周、以后、已完成或全部任务。今天包含已经逾期但尚未完成的任务。",
    inputSchema: {
      scope: z.enum(["today", "week", "later", "completed", "all"]).default("all"),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ scope }) => {
    try {
      const snapshot = await focusListStore.snapshot();
      const tasks = scope === "all" ? snapshot.tasks : snapshot.groups[scope];
      return textResult({ scope, tasks, counts: snapshot.counts, today: snapshot.today, weekEnd: snapshot.weekEnd });
    } catch (error) {
      return errorResult(error);
    }
  },
);

server.registerTool(
  "add_focus_task",
  {
    title: "添加焦点任务",
    description: "向本地清单添加任务。未提供日期时默认为今天。",
    inputSchema: {
      title: z.string().min(1).max(240),
      dueDate: z.union([z.string().regex(/^\d{4}-\d{2}-\d{2}$/), z.null()]).optional(),
      priority: z.enum(["high", "normal", "low"]).default("normal"),
      note: z.string().max(4000).optional(),
    },
    annotations: { readOnlyHint: false, openWorldHint: false, destructiveHint: false },
  },
  async (input) => {
    try {
      return textResult({ task: await focusListStore.create(input) });
    } catch (error) {
      return errorResult(error);
    }
  },
);

server.registerTool(
  "update_focus_task",
  {
    title: "更新焦点任务",
    description: "修改任务标题、日期、优先级、备注或完成状态。",
    inputSchema: {
      id: z.string().min(1),
      title: z.string().min(1).max(240).optional(),
      dueDate: z.union([z.string().regex(/^\d{4}-\d{2}-\d{2}$/), z.null()]).optional(),
      priority: z.enum(["high", "normal", "low"]).optional(),
      note: z.string().max(4000).optional(),
      completed: z.boolean().optional(),
    },
    annotations: { readOnlyHint: false, openWorldHint: false, destructiveHint: false },
  },
  async ({ id, ...changes }) => {
    try {
      return textResult({ task: await focusListStore.update(id, changes) });
    } catch (error) {
      return errorResult(error);
    }
  },
);

server.registerTool(
  "delete_focus_task",
  {
    title: "删除焦点任务",
    description: "永久删除一条指定任务。",
    inputSchema: { id: z.string().min(1) },
    annotations: { readOnlyHint: false, openWorldHint: false, destructiveHint: true },
  },
  async ({ id }) => {
    try {
      return textResult(await focusListStore.remove(id));
    } catch (error) {
      return errorResult(error);
    }
  },
);

const transport = new StdioServerTransport();
await server.connect(transport);
