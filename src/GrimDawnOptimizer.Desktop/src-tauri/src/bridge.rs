use grim_dawn_bridge_protocol::{
    fail_pending, is_allowed_command, parse_reply, request_timeout, response_event, Pending,
    PendingRequest, Reply,
};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::Emitter;
use tokio::sync::oneshot;

// The renderer cannot execute arbitrary processes or issue shell commands. All
// engine traffic goes to one fixed, bundled executable through anonymous pipes.
#[derive(Default)]
pub struct Bridge {
    process: Mutex<Option<EngineProcess>>,
    next_id: AtomicU64,
    generation: Arc<AtomicU64>,
}

struct EngineProcess {
    child: Child,
    stdin: Option<ChildStdin>,
    pending: Pending,
}

impl Drop for EngineProcess {
    fn drop(&mut self) {
        // EOF asks the engine to cancel pending work and flush local preferences.
        // Allow that flush to finish before a bounded fallback for a stuck engine.
        self.stdin.take();
        // The engine reserves up to five seconds for cancellation before its
        // preference flush; the shell deadline must outlive that contract.
        for _ in 0..160 {
            if matches!(self.child.try_wait(), Ok(Some(_))) {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        if !matches!(self.child.try_wait(), Ok(Some(_))) {
            let _ = self.child.kill();
        }
        let _ = self.child.wait();
        fail_pending(&self.pending, "计算引擎已关闭。");
    }
}

impl Bridge {
    pub async fn request(&self, app: &tauri::AppHandle, command: &str, payload: Value) -> Reply {
        if !is_allowed_command(command) {
            return Err("不支持的操作。".to_owned());
        }

        let id = self.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        let mut request =
            serde_json::to_vec(&json!({ "id": id, "command": command, "payload": payload }))
                .map_err(|error| error.to_string())?;
        if request.len() > 1024 * 1024 {
            return Err("操作数据过大，请重新载入角色。".to_owned());
        }
        request.push(b'\n');
        let (sender, receiver) = oneshot::channel();
        let pending = {
            let mut process = self.process.lock().map_err(|_| "计算引擎状态不可用。")?;
            let stopped = process
                .as_mut()
                .is_some_and(|engine| !matches!(engine.child.try_wait(), Ok(None)));
            if stopped {
                process.take();
                if command != "state" && command != "trainerState" && command != "pickPath" {
                    return Err("计算引擎已停止，请刷新应用并重新载入角色。".to_owned());
                }
            }
            if process.is_none() {
                let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
                *process = Some(EngineProcess::spawn(
                    app,
                    self.generation.clone(),
                    generation,
                )?);
            }
            let engine = process.as_mut().ok_or("计算引擎未启动。")?;
            let pending = engine.pending.clone();
            pending
                .lock()
                .map_err(|_| "计算引擎请求队列不可用。")?
                .insert(
                    id,
                    PendingRequest {
                        sender,
                        state_event: response_event(command),
                    },
                );
            let written = engine
                .stdin
                .as_mut()
                .ok_or("计算引擎连接已关闭。")?
                .write_all(&request);
            if written.is_err() {
                if let Ok(mut requests) = pending.lock() {
                    requests.remove(&id);
                }
                process.take();
                return Err("无法连接计算引擎，请刷新应用并重新载入角色。".to_owned());
            }
            pending
        };

        match tokio::time::timeout(request_timeout(command), receiver).await {
            Ok(Ok(response)) => response,
            Ok(Err(_)) => Err("计算引擎连接已中断，请重新载入角色。".to_owned()),
            Err(_) => {
                if let Ok(mut requests) = pending.lock() {
                    requests.remove(&id);
                }
                Err(if command == "pickPath" {
                    "文件选择等待超时，请重新打开选择窗口。"
                } else {
                    "计算引擎响应超时；请等待当前操作结束后重试。"
                }
                .to_owned())
            }
        }
    }

    pub fn shutdown(&self) {
        if let Ok(mut process) = self.process.lock() {
            process.take();
        }
    }
}

impl EngineProcess {
    fn spawn(
        app: &tauri::AppHandle,
        active_generation: Arc<AtomicU64>,
        generation: u64,
    ) -> Result<Self, String> {
        let executable = engine_path()?;
        let mut command = Command::new(executable);
        // Opt-in isolated profile directory for integration tests and portable
        // workspaces; normal launches retain the existing application's data.
        if let Some(directory) = std::env::var_os("GRIM_DAWN_DATA_DIR") {
            if !PathBuf::from(&directory).is_absolute() {
                return Err("GRIM_DAWN_DATA_DIR 必须是绝对路径。".to_owned());
            }
            command.arg("--data-dir").arg(directory);
        }
        command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000); // CREATE_NO_WINDOW
        }
        let mut child = command
            .spawn()
            .map_err(|error| format!("无法启动本地计算引擎：{error}"))?;
        let stdin = child.stdin.take().ok_or("无法建立计算引擎输入通道。")?;
        let stdout = child.stdout.take().ok_or("无法建立计算引擎输出通道。")?;
        let pending: Pending = Arc::new(Mutex::new(HashMap::new()));
        let reader_pending = pending.clone();
        let app = app.clone();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                if active_generation.load(Ordering::SeqCst) != generation {
                    break;
                }
                if line.trim().is_empty() {
                    continue;
                }
                match serde_json::from_str::<Value>(&line) {
                    Ok(message) => {
                        if message.get("event").and_then(Value::as_str) == Some("state") {
                            if let Some(state) = message.get("state") {
                                let _ = app.emit("bridge-state", state);
                            }
                        } else if message.get("event").and_then(Value::as_str) == Some("trainer") {
                            if let Some(state) = message.get("state") {
                                let _ = app.emit("trainer-state", state);
                            }
                        } else if message.get("event").and_then(Value::as_str) == Some("error") {
                            if let Some(state) = message.get("state") {
                                let _ = app.emit("bridge-state", state);
                            }
                            let error = message
                                .get("error")
                                .and_then(Value::as_str)
                                .unwrap_or("操作未完成，请重试。");
                            let _ = app.emit("bridge-error", error);
                        } else if let Some(id) = message.get("id").and_then(Value::as_u64) {
                            if let Ok(mut requests) = reader_pending.lock() {
                                if let Some(request) = requests.remove(&id) {
                                    if message.get("ok").and_then(Value::as_bool) != Some(true) {
                                        if let (Some(event), Some(state)) =
                                            (request.state_event, message.get("state"))
                                        {
                                            let _ = app.emit(event, state);
                                        }
                                    }
                                    let _ = request.sender.send(parse_reply(&message));
                                }
                            }
                        }
                    }
                    Err(_) => {
                        fail_pending(&reader_pending, "计算引擎返回了无效数据，请重新载入角色。");
                        let _ =
                            app.emit("bridge-error", "计算引擎返回了无效数据，请重新载入角色。");
                        let _ = app.emit(
                            "bridge-disconnected",
                            "计算引擎返回了无效数据，请重新载入角色。",
                        );
                    }
                }
            }
            fail_pending(
                &reader_pending,
                "计算引擎已停止，请刷新应用并重新载入角色。",
            );
            if active_generation.load(Ordering::SeqCst) == generation {
                let _ = app.emit("bridge-error", "计算引擎已停止，请刷新应用并重新载入角色。");
                let _ = app.emit(
                    "bridge-disconnected",
                    "计算引擎已停止，请刷新应用并重新载入角色。",
                );
            }
        });
        Ok(Self {
            child,
            stdin: Some(stdin),
            pending,
        })
    }
}

fn engine_path() -> Result<PathBuf, String> {
    let executable = std::env::current_exe().map_err(|error| error.to_string())?;
    let sibling = executable
        .parent()
        .ok_or("无法确定程序目录。")?
        .join("grim-dawn-engine.exe");
    if sibling.is_file() {
        return Ok(sibling);
    }
    #[cfg(debug_assertions)]
    {
        let development = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("binaries/grim-dawn-engine-x86_64-pc-windows-msvc.exe");
        if development.is_file() {
            return Ok(development);
        }
    }
    Err("未找到本地计算引擎。请将程序和 grim-dawn-engine.exe 放在同一目录，或重新安装。".to_owned())
}
