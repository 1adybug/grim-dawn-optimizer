import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { existsSync } from "node:fs";
import type { IncomingMessage, ServerResponse } from "node:http";
import { resolve } from "node:path";
import type { Plugin } from "vite";

const origin = "http://127.0.0.1:1420";
const host = "127.0.0.1:1420";
const bodyLimit = 64 * 1024;
const outputLimit = 16 * 1024 * 1024;

type Pending = {
  channel: "state" | "trainer" | null;
  resolve: (state: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
};

type BufferedEvent = { name: string; message: string; bytes: number };

/** A false write result means the whole frame was accepted and needs draining,
 * not that the connection failed. Keep at most the latest unsent state while
 * preserving error events, with a hard byte/count limit for stalled clients. */
export class SseChannel {
  private queue: BufferedEvent[] = [];
  private queuedBytes = 0;
  private blocked = false;
  private closed = false;
  private readonly response: ServerResponse;
  private readonly onClose: () => void;

  constructor(response: ServerResponse, onClose: () => void) {
    this.response = response;
    this.onClose = onClose;
    this.blocked = response.writableNeedDrain;
    response.on("drain", this.drain);
    response.once("close", this.finish);
    response.once("error", this.finish);
  }

  send(name: string, data: unknown) {
    if (this.closed) return;
    if (this.response.destroyed || this.response.writableEnded) {
      this.finish();
      return;
    }
    const message = `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
    const event = { name, message, bytes: Buffer.byteLength(message) };
    if (event.bytes > outputLimit) {
      this.close();
      return;
    }
    if (!this.blocked) {
      this.write(event);
      return;
    }

    // Superseded state is never useful to a slow browser. Keep its most recent
    // revision; errors stay ordered and are not silently discarded.
    if (name === "state" || name === "trainer") {
      const previous = this.queue.findIndex((item) => item.name === name);
      if (previous >= 0)
        this.queuedBytes -= this.queue.splice(previous, 1)[0].bytes;
    }
    if (
      this.response.writableLength + this.queuedBytes + event.bytes >
        outputLimit ||
      this.queue.length >= 64
    ) {
      this.close();
      return;
    }
    this.queue.push(event);
    this.queuedBytes += event.bytes;
  }

  close() {
    if (this.closed) return;
    this.finish();
    if (!this.response.destroyed && !this.response.writableEnded)
      this.response.end();
  }

  private write(event: BufferedEvent) {
    if (this.response.writableLength + event.bytes > outputLimit) {
      this.close();
      return;
    }
    try {
      this.blocked = !this.response.write(event.message);
    } catch {
      this.close();
    }
  }

  private drain = () => {
    if (this.closed) return;
    this.blocked = false;
    while (!this.blocked && this.queue.length) {
      const event = this.queue.shift()!;
      this.queuedBytes -= event.bytes;
      this.write(event);
    }
  };

  private finish = () => {
    if (this.closed) return;
    this.closed = true;
    this.queue = [];
    this.queuedBytes = 0;
    this.response.off("drain", this.drain);
    this.response.off("close", this.finish);
    this.response.off("error", this.finish);
    this.onClose();
  };
}

/** Local development transport only. It is never included in a production build. */
export function devEngine(): Plugin {
  let engine: ChildProcessWithoutNullStreams | undefined;
  let sequence = 0;
  let latestState: unknown;
  let latestTrainer: unknown;
  let closing = false;
  const pending = new Map<string, Pending>();
  const listeners = new Set<SseChannel>();

  function event(name: string, data: unknown) {
    for (const channel of listeners) channel.send(name, data);
  }

  function fail(error: Error) {
    for (const request of pending.values()) {
      clearTimeout(request.timer);
      request.reject(error);
    }
    pending.clear();
    latestState = undefined;
    latestTrainer = undefined;
    if (!closing)
      event("engine-error", { error: error.message, transport: true });
  }

  function start(root: string) {
    if (engine && !engine.killed) return engine;
    if (closing) throw new Error("开发服务器正在关闭。");
    const project = resolve(root, "../GrimDawnOptimizer.Bridge");
    const candidates = [
      resolve(
        root,
        "src-tauri/binaries/grim-dawn-engine-x86_64-pc-windows-msvc.exe",
      ),
      resolve(
        project,
        "bin/Debug/net8.0-windows/win-x64/GrimDawnOptimizer.Bridge.exe",
      ),
      resolve(
        project,
        "bin/Release/net8.0-windows/win-x64/GrimDawnOptimizer.Bridge.exe",
      ),
      resolve(project, "bin/Debug/net8.0-windows/GrimDawnOptimizer.Bridge.dll"),
      resolve(
        project,
        "bin/Release/net8.0-windows/GrimDawnOptimizer.Bridge.dll",
      ),
    ];
    const executable = candidates.find(existsSync);
    if (!executable) {
      throw new Error(
        "计算程序尚未构建。请先运行 dotnet build ../GrimDawnOptimizer.Bridge，再刷新页面。",
      );
    }
    const args = executable.endsWith(".dll") ? [executable] : [];
    if (process.env.GRIM_DAWN_DATA_DIR)
      args.push("--data-dir", resolve(process.env.GRIM_DAWN_DATA_DIR));
    const child = spawn(
      executable.endsWith(".dll") ? "dotnet" : executable,
      args,
      {
        cwd: root,
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"],
        env: {
          ...process.env,
          GRIM_DAWN_TRAINER_NATIVE_DIR:
            process.env.GRIM_DAWN_TRAINER_NATIVE_DIR ||
            resolve(root, "src-tauri/binaries/trainer-native"),
        },
      },
    );
    engine = child;
    let buffer = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      stderr = (stderr + chunk).slice(-4096);
    });
    child.stdout.on("data", (chunk: string) => {
      if (engine !== child || closing) return;
      buffer += chunk;
      if (buffer.length > outputLimit) {
        fail(new Error("计算程序返回的数据过大，请重启开发服务器。"));
        child.kill();
        return;
      }
      let lineEnd: number;
      while ((lineEnd = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, lineEnd).trim();
        buffer = buffer.slice(lineEnd + 1);
        if (!line) continue;
        try {
          const message = JSON.parse(line) as {
            id?: string | number;
            ok?: boolean;
            state?: unknown;
            error?: string;
            event?: string;
          };
          if (message.event === "state" && message.state !== undefined) {
            latestState = message.state;
            event("state", message.state);
          }
          if (message.event === "trainer" && message.state !== undefined) {
            latestTrainer = message.state;
            event("trainer", message.state);
          }
          if (message.event === "error") {
            if (message.state !== undefined) {
              latestState = message.state;
              event("state", message.state);
            }
            event("engine-error", {
              error: message.error || "计算程序发生错误。",
            });
          }
          if (message.id !== undefined) {
            const request = pending.get(String(message.id));
            if (!request) continue;
            pending.delete(String(message.id));
            clearTimeout(request.timer);
            if (message.ok === false) {
              if (message.state !== undefined && request.channel !== null) {
                if (request.channel === "trainer")
                  latestTrainer = message.state;
                else latestState = message.state;
                event(request.channel, message.state);
              }
              request.reject(new Error(message.error || "计算请求失败。"));
            } else {
              if (request.channel === "trainer") latestTrainer = message.state;
              else if (request.channel === "state") latestState = message.state;
              request.resolve(message.state);
            }
          }
        } catch {
          fail(new Error("计算程序返回了无效数据，请重启开发服务器。"));
          child.kill();
          return;
        }
      }
    });
    child.on("error", (error) => {
      if (engine === child) {
        engine = undefined;
        fail(new Error(`无法启动计算程序：${error.message}`));
      }
    });
    child.on("exit", (code) => {
      if (engine === child) {
        engine = undefined;
        fail(
          new Error(`计算程序已退出（${code ?? "中断"}）。${stderr.trim()}`),
        );
      }
    });
    child.stdin.on("error", (error) => {
      if (engine === child)
        fail(new Error(`无法连接计算程序：${error.message}`));
    });
    return child;
  }

  function request(root: string, command: string, payload: unknown) {
    if (pending.size >= 32)
      return Promise.reject(new Error("请求过多，请稍后重试。"));
    const child = start(root);
    const id = `web-${++sequence}`;
    const picker = command === "pickPath";
    return new Promise<unknown>((resolveRequest, reject) => {
      const timer = setTimeout(
        () => {
          pending.delete(id);
          reject(
            new Error(
              picker
                ? "文件选择等待超时，请重新打开选择窗口。"
                : "计算程序未及时响应。请检查状态后重试。",
            ),
          );
        },
        picker ? 600_000 : 30_000,
      );
      pending.set(id, {
        channel: picker
          ? null
          : command.startsWith("trainer")
            ? "trainer"
            : "state",
        resolve: resolveRequest,
        reject,
        timer,
      });
      child.stdin.write(
        `${JSON.stringify({ id, command, payload })}\n`,
        (error) => {
          if (error) {
            clearTimeout(timer);
            pending.delete(id);
            reject(error);
          }
        },
      );
    });
  }

  function allowed(req: IncomingMessage) {
    const address = req.socket.remoteAddress;
    return (
      ["127.0.0.1", "::ffff:127.0.0.1", "::1"].includes(address ?? "") &&
      req.headers.host === host &&
      (!req.headers.origin || req.headers.origin === origin) &&
      (!req.headers["sec-fetch-site"] ||
        ["same-origin", "none"].includes(String(req.headers["sec-fetch-site"])))
    );
  }

  function json(response: ServerResponse, status: number, value: unknown) {
    if (response.destroyed || response.writableEnded) return;
    response.writeHead(status, {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    });
    response.end(JSON.stringify(value));
  }

  async function receive(
    root: string,
    req: IncomingMessage,
    response: ServerResponse,
  ) {
    try {
      let size = 0;
      let oversized = false;
      const chunks: Buffer[] = [];
      for await (const chunk of req) {
        size += chunk.length;
        if (size > bodyLimit) {
          if (!oversized) {
            oversized = true;
            chunks.length = 0;
            json(response, 413, { error: "请求内容过大。" });
          }
          // Drain without buffering. Returning from this async iterator would
          // destroy the request/socket before the 413 is reliably delivered.
          continue;
        }
        chunks.push(Buffer.from(chunk));
      }
      if (oversized) return;
      let message: { command?: unknown; payload?: unknown };
      try {
        message = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      } catch {
        json(response, 400, { error: "请求不是有效 JSON。" });
        return;
      }
      if (
        !message ||
        typeof message.command !== "string" ||
        message.command.length > 64 ||
        !/^[A-Za-z]+$/.test(message.command)
      ) {
        json(response, 400, { error: "请求缺少有效命令。" });
        return;
      }
      json(
        response,
        200,
        await request(root, message.command, message.payload ?? {}),
      );
    } catch (error) {
      json(response, 503, {
        error: error instanceof Error ? error.message : "无法连接计算程序。",
      });
    }
  }

  function cleanup() {
    if (closing) return;
    closing = true;
    for (const channel of listeners) channel.close();
    listeners.clear();
    fail(new Error("开发服务器已关闭。"));
    const child = engine;
    engine = undefined;
    if (child) {
      child.stdin.end();
      const timer = setTimeout(() => child.kill(), 8000);
      timer.unref();
      child.once("exit", () => clearTimeout(timer));
    }
  }

  return {
    name: "grim-dawn-local-dev-engine",
    apply: "serve",
    configureServer(server) {
      server.httpServer?.once("close", cleanup);
      server.middlewares.use((req, response, next) => {
        const path = req.url?.split("?")[0];
        if (path !== "/__engine/request" && path !== "/__engine/events")
          return next();
        if (!allowed(req))
          return json(response, 403, {
            error: "此开发接口仅允许本机同源访问。",
          });
        if (path === "/__engine/events") {
          if (req.method !== "GET")
            return json(response, 405, { error: "请求方法不支持。" });
          response.writeHead(200, {
            "Content-Type": "text/event-stream; charset=utf-8",
            "Cache-Control": "no-store",
            Connection: "keep-alive",
            "X-Accel-Buffering": "no",
          });
          response.write(": connected\n\n");
          const channel = new SseChannel(response, () =>
            listeners.delete(channel),
          );
          listeners.add(channel);
          if (latestState !== undefined) channel.send("state", latestState);
          if (latestTrainer !== undefined)
            channel.send("trainer", latestTrainer);
          return;
        }
        if (req.method !== "POST")
          return json(response, 405, { error: "请求方法不支持。" });
        if (
          !String(req.headers["content-type"]).startsWith("application/json")
        ) {
          return json(response, 415, { error: "请求需要 application/json。" });
        }
        void receive(server.config.root, req, response);
      });
    },
    closeBundle: cleanup,
  };
}
