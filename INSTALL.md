# 安裝 / Installation

## 選擇發布包

- `MouseKeyboardMacro-v1.0.0-win-x64-framework-dependent.zip`：需要 Microsoft .NET 8 Desktop Runtime x64。
- `MouseKeyboardMacro-v1.0.0-win-x64-self-contained.zip`：已包含 runtime，檔案較大。

先用 `SHA256SUMS.txt` 驗證 ZIP，再完整解壓縮到單一可寫入資料夾。不要在 ZIP 內直接執行，也不要只複製 EXE。

## 第一次啟動

1. 從解壓縮根目錄執行 06、06A、07、07A 或 99 其中之一。
2. Launcher 只在目前使用者的 `HKCU\Software\MouseKeyboardMacro\InstallRoot` 記錄安裝根目錄。
3. 可把五個根目錄 CMD 複製到桌面；不要複製 `Program/App` 內的 EXE。
4. 若搬移整個資料夾，再從新位置執行任一根目錄 CMD 以更新 InstallRoot。

一般操作使用 06／07。只有確定目標需要高完整性時才使用 06A／07A；UAC 必須由使用者自行確認。

## 第一次錄製與播放

1. 預設使用 Standard 錄製模式與 Desktop Safe 安全策略。Raw Input Enhanced（Raw Enhanced）是 opt-in 進階模式，不是預設。
2. Recorder 開啟後保持等待；長按 F12 5 秒開始錄製。
3. 錄製中再長按 F12 5 秒會停止並儲存到 `Recordings`；F12 控制手勢不會寫入巨集。
4. Raw Input Enhanced 錄製的新巨集同樣會保存有效的桌面絕對座標；Player 的滑鼠重播固定為 AbsoluteDesktop（絕對桌面座標）。
5. 顯示器排列或解析度改變後，播放前先確認目標位置。
6. 播放中長按 F11 2 秒是主要緊急停止。
7. 若應用程式內控制無法使用，執行 `99_緊急終止巨集工具.cmd` 做外部緊急 cleanup。

標準權限模式無法可靠控制系統管理員程式。只有明確需要時才改用 06A／07A 並接受正常 Windows UAC；本工具不支援也不繞過 UAC secure desktop。

## 解除安裝

關閉工具後刪除完整資料夾。若要移除目前使用者的路徑記錄，可執行：

```powershell
Remove-ItemProperty -LiteralPath 'HKCU:\Software\MouseKeyboardMacro' -Name InstallRoot -ErrorAction SilentlyContinue
```

本工具不寫入 HKLM、不安裝 service 或 driver。

## License

- `LICENSE_INCLUDED`
- `SPDX_IDENTIFIER=MIT`
- `Copyright (c) 2026 ru`

本專案採用 MIT License，完整條款見 Repository 根目錄的 `LICENSE`。
