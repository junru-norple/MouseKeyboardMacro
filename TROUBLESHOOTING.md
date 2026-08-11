# 疑難排解

## framework-dependent 無法啟動

確認已安裝 Microsoft .NET 8 Desktop Runtime x64。若無法安裝 runtime，改用 `self-contained` ZIP。發布檔名一律使用 `framework-dependent`。

## CMD 顯示找不到安裝根目錄

回到完整解壓縮資料夾，從根目錄執行任一 CMD 一次。搬移資料夾後也要重做一次。不要只複製 EXE。

## 一般 Player 拒絕管理員巨集

這是預期的 fail-closed 行為。標準權限模式無法可靠控制系統管理員程式；確認巨集來源與目標後，由使用者自行改用 07A 並接受正常 Windows UAC。工具不會自動提升權限，也不支援或繞過 UAC secure desktop。

## 顯示配置改變後滑鼠位置不符預期

Player 的滑鼠重播固定使用 AbsoluteDesktop（絕對桌面座標）。確認顯示器排列、解析度、DPI 與目標視窗位置是否與錄製時一致；配置改變時請先停止，並以新版 Recorder 重新錄製。

## 舊巨集顯示無法安全播放

若舊巨集沒有有效的桌面絕對座標，Player 會在倒數及任何輸入送出前 fail closed。請使用新版 Recorder 重新錄製；工具不會猜測位置、靜默轉換或覆寫舊巨集。

## Player 視窗行為不符預期

在 Player 重新選擇 KeepVisible 或 MinimizeBeforeCountdown。設定只保存在本機 `Program/State/Settings`。

## 工具沒有回應

播放中先長按 F11 2 秒。Recorder 在等待狀態長按 F12 5 秒開始，錄製中再長按 F12 5 秒停止並儲存；F12 控制手勢不會寫入巨集。若應用程式內控制無法使用，執行 `99_緊急終止巨集工具.cmd` 做外部緊急 cleanup；它只終止能驗證的目前 session，不會廣泛依 process name 清除程序。

## 防毒或 SmartScreen 警告

binary 尚未簽章。保持防毒啟用，驗證 ZIP SHA-256，掃描解壓縮內容；不要建立 AV exclusion。若來源或 hash 不符，請停止執行。

## 回報問題

請提供版本、flavor、重現步驟與已去識別化的錯誤訊息。不要附上巨集、設定、完整 log、本機絕對路徑、PID、HWND 或私人視窗標題。詳見 [PRIVACY.md](PRIVACY.md)。

License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
