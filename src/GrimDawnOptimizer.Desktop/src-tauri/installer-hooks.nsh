!include LogicLib.nsh

; Tauri includes hooks before MUI_LANGUAGE defines the LANG_* constants.
LangString TrainerBridgeInUse 2052 "实战组件仍被游戏或其他程序占用，安装无法继续。请先保存并退出《恐怖黎明》，再点击“重试”。仅断开实战工具不会释放组件。若游戏已退出，请检查文件权限。"
LangString TrainerBridgeInUse 1033 "A trainer component is still in use, or cannot be replaced. Save and exit Grim Dawn, then click Retry. Disconnecting the trainer does not unload it. If the game has exited, check file permissions."

!macro CheckTrainerBridgeFiles
  Push $0
  Push $1
  Push $2
  FindFirst $0 $1 "$INSTDIR\trainer-native\CairnBridge.*.dll"
  ${DoWhile} $1 != ""
    ${Do}
      ; OPEN_EXISTING + GENERIC_WRITE checks replacement access without
      ; truncating the DLL. FileOpen in write mode would corrupt an idle file.
      System::Call 'kernel32::CreateFileW(w "$INSTDIR\trainer-native\$1", i 0x40000000, i 7, p 0, i 3, i 0, p 0) p.r2'
      ${If} $2 != -1
        System::Call 'kernel32::CloseHandle(p r2)'
        ${ExitDo}
      ${EndIf}
      ; Never kill the game or offer Ignore: either can lose a user's progress
      ; or leave the application with a mixture of old and new components.
      MessageBox MB_ICONEXCLAMATION|MB_RETRYCANCEL "$(TrainerBridgeInUse)" /SD IDCANCEL IDRETRY +3
        FindClose $0
        Abort
    ${Loop}
    FindNext $0 $1
  ${Loop}
  FindClose $0
  Pop $2
  Pop $1
  Pop $0
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro CheckTrainerBridgeFiles
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  !insertmacro CheckTrainerBridgeFiles
!macroend
