# Security

## 安全邊界

MouseKeyboardMacro 是本機桌面自動化工具，不是安全邊界繞過工具。標準權限模式無法可靠控制系統管理員程式；本工具不安裝 driver、不使用 `uiAccess`、不注入其他程序、不支援或繞過 UAC secure desktop，也不承諾控制受保護桌面或高完整性目標。

- 一般與系統管理員入口分離。
- 一般 Player 對管理員巨集 fail closed。
- Recorder 在等待狀態長按 F12 5 秒開始，錄製中再長按 5 秒停止並儲存；F12 控制手勢不寫入巨集。
- 播放中長按 F11 2 秒是主要緊急停止。
- `99_緊急終止巨集工具.cmd` 是外部緊急 cleanup，只終止能驗證 PID、start time、process identity 與 session token 的目前 session。
- 單一活動工具機制避免 Recorder／Player 同時作用。

## 安全使用

只播放可信任且已檢查的巨集。保持 Windows 與防毒更新；驗證 ZIP SHA-256；不要建立 AV exclusion。未簽章 binary 可能觸發 SmartScreen。

自動測試與 CI 不執行 live input、UAC 或 Registry 修改。詳細設計見 [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md)。

本專案採用 MIT License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
