# 從原始碼建置

## 需求

- Windows 11 x64
- .NET SDK 8.0.423（由根目錄 `global.json` 固定）
- PowerShell 5.1 或更新版本
- Git（只用於 clean-clone／clean-status Gate）

## Public solution

`MouseKeyboardMacro.sln` 只包含：

- MacroCore
- MacroLauncher
- MacroRecorder
- MacroPlayer
- MacroSafetyWatchdog
- MacroCore.Tests
- EmergencySessionTestHost
- MacroPlaybackPerformanceProbe
- PlayerPresentationTestHost

公開 solution 不含 MacroMigration、ManualOnly、live-input driver 或 owner exporter project。Public test count 以實際 TRX 為準；它不必等於 private owner suite 的 900。

## 指令

這些 script 以 `$PSScriptRoot` 解析 Repository，因此可從任意 working directory 執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -SelfContained
```

`Publish-Release.ps1` 會 restore、Release build、執行全部 public tests、publish 四個 apps，並組裝：

- `artifacts/release/MouseKeyboardMacro-v1.0.0-framework-dependent`
- `artifacts/release/MouseKeyboardMacro-v1.0.0-self-contained`

輸出包含五個 CMD、使用者文件、`Program/App`、空的 Logs／Settings／Recordings 與 .NET notices；不依賴私有 `Program/App`。

## 安全自動化

Build、test、publish 與 CI 都設定 project-local TEMP、CLI home 與 NuGet cache；不執行 Hook／SendInput live input、UAC、Registry 修改或桌面截圖。CI 執行全部 public tests，並讓 NuGet High／Critical vulnerability finding 失敗。

## Clean-clone Gate

在乾淨副本執行 restore、build、test、兩種 publish 與 verify；每個階段後 `git status --short` 必須仍為空。

## License

- `LICENSE_INCLUDED`
- `SPDX_IDENTIFIER=MIT`
- `Copyright (c) 2026 ru`

完整 MIT License 條款見 Repository 根目錄的 `LICENSE`。
