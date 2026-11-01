#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod bridge;
mod updates;

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
        .plugin(tauri_plugin_updater::Builder::new().build())
        .manage(Bridge::default())
        .manage(updates::Updates::default())
        .invoke_handler(tauri::generate_handler![
            bridge_request,
            updates::update_state,
            updates::update_preferences,
            updates::check_update,
            updates::install_update
        ])
        .build(tauri::generate_context!())
        .expect("无法启动 Grim Dawn Optimizer")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                app.state::<Bridge>().shutdown();
            }
        });
}
