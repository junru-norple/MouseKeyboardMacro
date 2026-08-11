# Architecture

MouseKeyboardMacro 採取本機、可攜式、明確權限邊界的設計。

## Components

- MacroCore：路徑、序列化、安全狀態、輸入與播放共用邏輯。
- MacroLauncher：解析五個入口、驗證 install root 與權限模式。
- MacroRecorder：預設 Standard／Desktop Safe 與 opt-in Raw Input Enhanced（Raw Enhanced）錄製 UI 與工作流程；兩種錄製模式的新巨集都會保存有效的虛擬桌面絕對座標。
- MacroPlayer：巨集庫、倒數與固定 AbsoluteDesktop（絕對桌面座標）播放；缺少安全絕對座標的舊巨集會在任何輸入送出前 fail closed。
- MacroSafetyWatchdog：驗證目前 session 並提供受限復原。
- Safe test hosts：只做 presentation、performance fake-input 與 emergency-session 驗證。

## Data flow

Recorder 將巨集寫到本機 `Recordings`；Player 只讀取使用者選取的巨集。設定與 log 分別位於 `Program/State/Settings`、`Program/State/Logs`。公開 package 不帶入任何既有使用者資料。

## Publication boundary

Public solution 排除 MacroMigration、ManualOnly、owner exporter 與 live-input driver。Repository 是 source-only；portable release 則由公開 source 重新 publish 四個 apps，不使用私有 `Program/App`。

License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
