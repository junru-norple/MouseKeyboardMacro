# Input modes

## Capture

### Standard

預設錄製模式，搭配 Desktop Safe 安全策略，適用於一般鍵盤與滑鼠工作流程。標準權限模式無法可靠控制系統管理員程式；使用前仍應確認目標應用程式與權限層級。本工具不支援也不繞過 UAC secure desktop。

### Raw Input Enhanced（Raw Enhanced）

這是 opt-in 進階錄製模式，不是預設。明確選用後可在擷取當下處理 raw mouse delta，但新巨集同樣必須保存有效的虛擬桌面絕對座標，不會需要另一種滑鼠重播模式。它不是較高權限模式；標準權限仍不能可靠控制系統管理員程式。

## Playback

### AbsoluteDesktop

這是 Player 唯一的滑鼠重播方式，依巨集記錄的虛擬桌面絕對座標播放，並正確處理多螢幕與負座標。Player 不提供其他滑鼠重播模式。顯示器排列、解析度或視窗位置改變後，播放前應先重新確認目標位置。只有舊式位移資料、沒有有效桌面座標的巨集會在任何輸入送出前 fail closed，不會猜測位置或覆寫舊檔。

錄製與播放都受倒數、單一活動工具、privilege gate 與 emergency session 驗證約束。Recorder 在等待狀態長按 F12 5 秒開始，錄製中再長按 5 秒停止並儲存，且 F12 控制手勢不寫入巨集；播放中長按 F11 2 秒是主要緊急停止；`99_緊急終止巨集工具.cmd` 是外部緊急 cleanup。本工具不支援也不繞過 UAC secure desktop。

License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
