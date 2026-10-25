#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod bridge;

use bridge::Bridge;
use serde_json::Value;
use tauri::Manager;

#[tauri::command]
async fn bridge_request(
    app: tauri::AppHandle,
    bridge: tauri::State<'_, Bridge>,
    command: String,
    payload: Option<Value>,
) -> Result<Value, String> {
    bridge
        .request(&app, &command, payload.unwrap_or(Value::Null))
        .await
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(Bridge::default())
        .invoke_handler(tauri::generate_handler![bridge_request])
        .build(tauri::generate_context!())
        .expect("无法启动 Grim Dawn Optimizer")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                app.state::<Bridge>().shutdown();
            }
        });
}
