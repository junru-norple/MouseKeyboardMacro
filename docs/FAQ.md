# FAQ

## 應選哪個 ZIP？

已有 Microsoft .NET 8 Desktop Runtime x64 可選 `framework-dependent`；否則選較大的 `self-contained`。兩者功能相同。

## 為何一般 Player 拒絕某些巨集？

標準權限模式無法可靠控制系統管理員程式；巨集標記為需要較高完整性時會 fail closed。確認來源後由使用者自行選 07A 並接受正常 UAC。本工具不支援也不繞過 UAC secure desktop。

## 預設應使用哪種模式？

預設使用 Standard 錄製模式與 Desktop Safe 安全策略。Raw Input Enhanced（Raw Enhanced）是 opt-in 進階模式，不是預設；只有理解 raw mouse delta、硬體與 DPI 差異時才啟用。

## Raw Input Enhanced 錄製的巨集如何播放？

Raw Input Enhanced 可在擷取當下處理 raw mouse delta，但新巨集同樣會保存有效的桌面絕對座標。Player 的滑鼠重播固定使用 AbsoluteDesktop（絕對桌面座標），不需要也不提供另一種滑鼠重播模式。

## 為何播放前要確認螢幕位置？

滑鼠動作依虛擬桌面絕對座標播放。若顯示器排列、解析度或視窗位置已改變，請先重新確認目標位置，必要時以新版 Recorder 重新錄製。

## 工具會上傳資料嗎？

不會。沒有 telemetry 或雲端同步。巨集、設定與 log 都留在本機。

## 為何防毒或 SmartScreen 警告？

目前 binary 未做 Authenticode 簽章。請驗證 SHA-256 並掃描檔案，不要建立 AV exclusion。

## 如何停止？

Recorder 開啟後保持等待：長按 F12 5 秒開始錄製；錄製中再長按 F12 5 秒停止並儲存，且 F12 控制手勢不會寫入巨集。播放中長按 F11 2 秒是主要緊急停止。若應用程式內控制無法使用，執行 `99_緊急終止巨集工具.cmd` 做外部 exact-session 緊急 cleanup。

## License 是什麼？

- `LICENSE_INCLUDED`
- `SPDX_IDENTIFIER=MIT`
- `Copyright (c) 2026 ru`

本專案採用 MIT License，完整條款見 Repository 根目錄的 `LICENSE`。公開前仍需 owner 完成 AV 掃描與兩種 flavor 的實機 smoke test。
