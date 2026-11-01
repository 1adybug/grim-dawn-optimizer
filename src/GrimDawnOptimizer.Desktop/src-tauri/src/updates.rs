use crate::bridge::Bridge;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{fs, path::PathBuf, sync::Mutex, time::Duration};
use tauri::{Emitter, Manager};
use tauri_plugin_updater::{Update, UpdaterExt};

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct Preferences {
    schema_version: u32,
    auto_check: bool,
    auto_install: bool,
}

impl Default for Preferences {
    fn default() -> Self {
        Self {
            schema_version: 1,
            auto_check: true,
            auto_install: true,
        }
    }
}

#[derive(Default)]
pub struct Updates {
    operation: tokio::sync::Mutex<()>,
    preferences: Mutex<()>,
    available: Mutex<Option<Update>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    version: String,
    supported: bool,
    preferences: Preferences,
    warning: Option<String>,
}

#[derive(Serialize)]
pub struct Release {
    version: String,
    notes: Option<String>,
}

fn installed(app: &tauri::AppHandle) -> bool {
    // The same executable is used by both packages. Only NSIS installs this
    // resource; the portable launcher intentionally omits it from its payload.
    !cfg!(debug_assertions)
        && app
            .path()
            .resource_dir()
            .is_ok_and(|path| path.join("installation.json").is_file())
}

fn require_installed(app: &tauri::AppHandle) -> Result<(), String> {
    installed(app)
        .then_some(())
        .ok_or_else(|| "自动更新仅适用于安装版。".into())
}

fn preferences_path() -> Result<PathBuf, String> {
    let directory = if let Some(path) = std::env::var_os("GRIM_DAWN_DATA_DIR") {
        let path = PathBuf::from(path);
        if !path.is_absolute() {
            return Err("配置目录必须是绝对路径。".into());
        }
        path
    } else {
        PathBuf::from(std::env::var_os("LOCALAPPDATA").ok_or("无法找到本地配置目录。")?)
            .join("GrimDawnOptimizer")
    };
    Ok(directory.join("update-settings.json"))
}

fn load(path: &std::path::Path) -> Result<Preferences, String> {
    match fs::read(path) {
        Ok(bytes) => {
            let preferences: Preferences = serde_json::from_slice(&bytes)
                .map_err(|_| "更新设置无法读取，请重新保存更新选项。")?;
            if preferences.schema_version != 1 {
                return Err("更新设置版本不受支持。".into());
            }
            Ok(preferences)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(Preferences::default()),
        Err(_) => Err("更新设置无法读取，请检查配置目录权限。".into()),
    }
}

#[tauri::command]
pub fn update_state(
    app: tauri::AppHandle,
    updates: tauri::State<'_, Updates>,
) -> Result<UpdateInfo, String> {
    let _guard = updates
        .preferences
        .lock()
        .map_err(|_| "更新设置暂不可用。")?;
    let (preferences, warning) = match load(&preferences_path()?) {
        Ok(preferences) => (preferences, None),
        Err(warning) => (
            Preferences {
                auto_check: false,
                auto_install: false,
                ..Preferences::default()
            },
            Some(warning),
        ),
    };
    Ok(UpdateInfo {
        version: app.package_info().version.to_string(),
        supported: installed(&app),
        preferences,
        warning,
    })
}

#[tauri::command]
pub fn update_preferences(
    app: tauri::AppHandle,
    updates: tauri::State<'_, Updates>,
    auto_check: bool,
    auto_install: bool,
) -> Result<UpdateInfo, String> {
    require_installed(&app)?;
    let _guard = updates
        .preferences
        .lock()
        .map_err(|_| "更新设置暂不可用。")?;
    let path = preferences_path()?;
    let preferences = Preferences {
        schema_version: 1,
        auto_check,
        auto_install,
    };
    fs::create_dir_all(path.parent().ok_or("配置目录无效。")?)
        .map_err(|_| "无法创建更新配置目录。")?;
    let temporary = path.with_extension("json.tmp");
    let save = || -> Result<(), String> {
        fs::write(
            &temporary,
            serde_json::to_vec_pretty(&preferences).map_err(|e| e.to_string())?,
        )
        .map_err(|_| "无法保存更新设置，请检查目录权限。")?;
        fs::rename(&temporary, &path).map_err(|_| "无法替换更新设置，请检查目录权限。".to_owned())
    };
    let result = save();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result?;
    Ok(UpdateInfo {
        version: app.package_info().version.to_string(),
        supported: true,
        preferences,
        warning: None,
    })
}

#[tauri::command]
pub async fn check_update(
    app: tauri::AppHandle,
    updates: tauri::State<'_, Updates>,
) -> Result<Option<Release>, String> {
    require_installed(&app)?;
    let _guard = updates
        .operation
        .try_lock()
        .map_err(|_| "更新操作正在进行。")?;
    let before_exit = app.clone();
    let updater = app
        .updater_builder()
        .timeout(Duration::from_secs(120))
        .on_before_exit(move || {
            before_exit.state::<Bridge>().shutdown();
            before_exit.cleanup_before_exit();
        })
        .build()
        .map_err(|error| error.to_string())?;
    let update = updater
        .check()
        .await
        .map_err(|error| format!("无法检查 GitHub 更新，请检查网络后重试。{error}"))?;
    let release = update.as_ref().map(|update| Release {
        version: update.version.clone(),
        notes: update.body.clone(),
    });
    *updates.available.lock().map_err(|_| "更新状态暂不可用。")? = update;
    Ok(release)
}

async fn require_idle(app: &tauri::AppHandle) -> Result<(), String> {
    let bridge = app.state::<Bridge>();
    let state = bridge.request(app, "state", json!(null)).await?;
    let trainer = bridge.request(app, "trainerState", json!(null)).await?;
    if state["isBusy"] != false || trainer["connected"] != false || trainer["connecting"] != false {
        return Err("请先完成分析并断开实战工具，再安装更新。".into());
    }
    Ok(())
}

#[tauri::command]
pub async fn install_update(
    app: tauri::AppHandle,
    updates: tauri::State<'_, Updates>,
) -> Result<(), String> {
    require_installed(&app)?;
    let _guard = updates
        .operation
        .try_lock()
        .map_err(|_| "更新操作正在进行。")?;
    require_idle(&app).await?;
    let update = updates
        .available
        .lock()
        .map_err(|_| "更新状态暂不可用。")?
        .clone()
        .ok_or("请先检查更新。")?;
    let mut downloaded = 0u64;
    let bytes = update
        .download(
            |chunk, total| {
                downloaded += chunk as u64;
                let _ = app.emit(
                    "update-progress",
                    json!({ "downloaded": downloaded, "total": total }),
                );
            },
            || {},
        )
        .await
        .map_err(|error| format!("更新下载或签名校验失败，请重试。{error}"))?;
    // A game may have started during the download. Recheck before any exit or
    // installation; the updater verifies the signed package before installing.
    require_idle(&app).await?;
    app.state::<Bridge>()
        .request(&app, "trainerDisconnect", json!(null))
        .await?;
    update
        .install(bytes)
        .map_err(|error| format!("更新安装失败，请重试。{error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preferences_default_and_disabled_options_round_trip() {
        let defaults: Preferences = serde_json::from_str("{}").unwrap();
        assert!(defaults.auto_check && defaults.auto_install);
        let disabled = Preferences {
            auto_check: false,
            auto_install: false,
            ..defaults
        };
        assert_eq!(
            serde_json::from_slice::<Preferences>(&serde_json::to_vec(&disabled).unwrap()).unwrap(),
            disabled
        );
        assert!(serde_json::from_str::<Preferences>(r#"{"autoCheck":"false"}"#).is_err());
    }

    #[test]
    fn unreadable_or_future_preferences_are_not_overwritten() {
        let path = std::env::temp_dir().join(format!(
            "gdo-update-{}-{}.json",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        assert_eq!(load(&path).unwrap(), Preferences::default());
        for bytes in [
            "{",
            r#"{"schemaVersion":2,"autoCheck":true}"#,
            r#"{"autoCheck":"false"}"#,
        ] {
            fs::write(&path, bytes).unwrap();
            assert!(load(&path).is_err());
            assert_eq!(fs::read_to_string(&path).unwrap(), bytes);
        }
        fs::remove_file(path).unwrap();
    }
}
