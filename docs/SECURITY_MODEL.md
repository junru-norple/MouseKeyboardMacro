# Security model

## Trust assumptions

- 使用者只錄製與播放可信任的本機工作流程。
- 巨集檔不是可執行程式，但仍可能描述具破壞性的操作；播放前必須檢查來源。
- Windows integrity boundary 保持有效。標準權限模式無法可靠控制系統管理員程式；工具不支援也不繞過 UAC secure desktop。

## Controls

- 一般／系統管理員入口分離。
- 一般 Player 對需要提升權限的巨集 fail closed。
- Recorder 與 Player 不可同時成為活動工具。
- Recorder 在等待狀態長按 F12 5 秒開始，錄製中再長按 5 秒停止並儲存；F12 控制手勢不寫入巨集。
- 播放中長按 F11 2 秒是主要緊急停止。
- Watchdog 與 `99_緊急終止巨集工具.cmd` 外部緊急 cleanup 只對能驗證 PID、start time、process identity、session token 的目前 session 動作。
- 自動 Gate 不執行 live input、UAC、Registry 修改或桌面截圖。

## Out of scope

受保護桌面、driver injection、`uiAccess`、anti-cheat bypass、遠端控制與隱式權限提升均不在本專案範圍。

License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
