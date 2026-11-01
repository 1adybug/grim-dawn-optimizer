//! GUI-independent JSONL reply handling and command policy used by the desktop
//! shell. Tests exercise this production code without building Tauri or WebView.

use serde_json::Value;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::sync::oneshot;

pub type Reply = Result<Value, String>;
pub type Pending = Arc<Mutex<HashMap<u64, PendingRequest>>>;

pub struct PendingRequest {
    pub sender: oneshot::Sender<Reply>,
    pub state_event: Option<&'static str>,
}

fn is_trainer_command(command: &str) -> bool {
    matches!(
        command,
        "trainerState"
            | "trainerSelectExecutable"
            | "trainerConnect"
            | "trainerSetAutoConnect"
            | "trainerDisconnect"
            | "trainerSetFeature"
            | "trainerSetMovementMultiplier"
            | "trainerDisableAll"
            | "trainerLaunch"
    )
}

pub fn response_event(command: &str) -> Option<&'static str> {
    if command == "pickPath" {
        None
    } else if is_trainer_command(command) {
        Some("trainer-state")
    } else {
        Some("bridge-state")
    }
}

pub fn request_timeout(command: &str) -> Duration {
    // A native file dialog waits for the user. The engine closes its dialog at
    // 590 seconds or stdin EOF, before this outer transport deadline expires.
    Duration::from_secs(if command == "pickPath" { 600 } else { 30 })
}

pub fn is_allowed_command(command: &str) -> bool {
    is_trainer_command(command)
        || matches!(
            command,
            "state"
                | "pickPath"
                | "discover"
                | "load"
                | "refresh"
                | "demo"
                | "optimize"
                | "cancel"
                | "selectCharacter"
                | "configure"
                | "selectResult"
                | "selectGameDirectory"
                | "selectSaveDirectory"
                | "deleteGameDirectory"
                | "deleteSaveDirectory"
                | "saveProfile"
                | "configureBuff"
                | "compareItem"
                | "navigate"
        )
}

pub fn parse_reply(message: &Value) -> Reply {
    if message.get("ok").and_then(Value::as_bool) == Some(true) {
        message
            .get("state")
            .cloned()
            .ok_or_else(|| "计算引擎没有返回状态。".to_owned())
    } else {
        Err(message
            .get("error")
            .and_then(Value::as_str)
            .unwrap_or("操作未完成，请重试。")
            .to_owned())
    }
}

pub fn fail_pending(pending: &Pending, message: &str) {
    if let Ok(mut requests) = pending.lock() {
        for (_, request) in requests.drain() {
            let _ = request.sender.send(Err(message.to_owned()));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn successful_response_requires_state() {
        assert_eq!(
            parse_reply(&json!({"id": 1, "ok": true, "state": {"ready": true}})),
            Ok(json!({"ready": true}))
        );
        assert!(parse_reply(&json!({"id": 1, "ok": true})).is_err());
    }

    #[test]
    fn engine_failures_keep_the_actionable_message() {
        assert_eq!(
            parse_reply(&json!({"ok": false, "error": "请先载入角色。"})),
            Err("请先载入角色。".to_owned())
        );
        assert!(parse_reply(&json!({"ok": false})).is_err());
    }

    #[test]
    fn exit_rejects_all_pending_requests() {
        let pending: Pending = Arc::new(Mutex::new(HashMap::new()));
        let (sender, mut receiver) = oneshot::channel();
        pending.lock().unwrap().insert(
            7,
            PendingRequest {
                sender,
                state_event: Some("bridge-state"),
            },
        );
        fail_pending(&pending, "已停止");
        assert_eq!(receiver.try_recv().unwrap(), Err("已停止".to_owned()));
        assert!(pending.lock().unwrap().is_empty());
    }

    #[test]
    fn trainer_commands_and_states_are_separate_from_optimizer() {
        assert!(is_trainer_command("trainerState"));
        assert!(is_trainer_command("trainerSetFeature"));
        assert!(is_trainer_command("trainerLaunch"));
        assert!(!is_trainer_command("trainerExecute"));
        assert!(!is_trainer_command("state"));
        assert!(is_allowed_command("state"));
        assert!(is_allowed_command("trainerState"));
        assert!(!is_allowed_command("trainerExecute"));
        assert_eq!(response_event("trainerState"), Some("trainer-state"));
        assert_eq!(response_event("state"), Some("bridge-state"));
    }

    #[test]
    fn picker_replies_never_emit_domain_states_and_allow_user_time() {
        assert!(is_allowed_command("pickPath"));
        assert_eq!(response_event("pickPath"), None);
        assert_eq!(request_timeout("pickPath"), Duration::from_secs(600));
        assert_eq!(request_timeout("optimize"), Duration::from_secs(30));
        assert_eq!(
            parse_reply(&json!({"id": 1, "ok": true, "state": {"path": null}})),
            Ok(json!({"path": null}))
        );
    }
}
