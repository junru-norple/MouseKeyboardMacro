# 使用指南

## 錄製

- 06：一般權限 Recorder，建議預設使用。
- 06A：系統管理員 Recorder，只用於明確需要高完整性的目標。
- Standard 搭配 Desktop Safe 是一般情境的預設組合。
- Raw Input Enhanced（Raw Enhanced）可在擷取當下處理 raw mouse delta，必須明確選用且不是預設；新巨集仍會保存有效的桌面絕對座標，可以固定的 AbsoluteDesktop 方式播放。
- Recorder 開啟後保持等待；長按 F12 5 秒才開始錄製。
- 錄製中再長按 F12 5 秒會停止並把巨集寫入本機 `Recordings`。F12 控制手勢不會寫入巨集。
- 標準權限模式無法可靠控制系統管理員程式；只有明確需要時才使用 06A。本工具不支援也不繞過 UAC secure desktop。

## 播放

- 07：一般權限 Player。
- 07A：系統管理員 Player。
- 選取巨集後確認權限、顯示器排列、目標位置與倒數顯示方式，再開始播放。
- 滑鼠重播固定為 AbsoluteDesktop（絕對桌面座標），Player 不提供其他滑鼠重播模式。若顯示器排列或解析度改變，播放前請重新確認位置。
- KeepVisible 保持 Player 可見；MinimizeBeforeCountdown 在倒數前最小化。
- 播放中長按 F11 2 秒是主要緊急停止；若應用程式內控制無法使用，執行 `99_緊急終止巨集工具.cmd`。

一般 Player 遇到需要管理員權限的巨集會拒絕播放；請確認來源後自行改用 07A 並接受正常 Windows UAC。標準權限模式無法可靠控制系統管理員程式，本工具也不支援或繞過 UAC secure desktop。

## 緊急停止

播放中先長按 F11 2 秒。Recorder 在等待狀態長按 F12 5 秒開始，錄製中再長按 F12 5 秒停止並儲存；F12 控制手勢不會寫入巨集。若應用程式內控制無法使用，執行 `99_緊急終止巨集工具.cmd` 做外部緊急 cleanup。它只接受能以 PID、start time、process identity 與 session token 驗證的目前 session。

## 檔案與隱私

- `Recordings`：使用者巨集。
- `Program/State/Settings`：本機偏好。
- `Program/State/Logs`：本機診斷 log。

這些資料不會自動上傳。備份或分享前請自行檢查敏感內容。

## License

- `LICENSE_INCLUDED`
- `SPDX_IDENTIFIER=MIT`
- `Copyright (c) 2026 ru`

完整 MIT License 條款見 Repository 根目錄的 `LICENSE`。
