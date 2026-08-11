MouseKeyboardMacro v1.0.0 操作手冊
=================================

LICENSE_INCLUDED
SPDX_IDENTIFIER=MIT
Copyright (c) 2026 ru

開始前
------
1. 請先完整解壓縮整個發行 ZIP，不要直接在 ZIP 預覽視窗中執行。
2. framework-dependent 版需要安裝 .NET 8 Desktop Runtime x64；self-contained 版不需另外安裝 Runtime。
3. 發行檔未簽章。請核對 SHA256SUMS.txt，保持防毒啟用，且不要建立防毒排除。

五個日常入口
------------
1. 06_啟動錄製器_一般模式.cmd：以一般權限開啟 Recorder。
2. 06A_啟動錄製器_管理員模式.cmd：接受正常 UAC 提示後，以管理員權限開啟 Recorder。
3. 07_選擇並重播巨集_一般模式.cmd：以一般權限選擇並播放巨集。
4. 07A_選擇並重播巨集_管理員模式.cmd：接受正常 UAC 提示後，以管理員權限選擇並播放巨集。
5. 99_緊急終止巨集工具.cmd：外部緊急清理入口，只處理目前登記且精確驗證的 session。

錄製
----
1. Standard 與 Desktop Safe 是一般用途預設；Raw Input Enhanced 必須由使用者明確選擇，不會預設啟用。Raw Input Enhanced 可在擷取當下處理 raw mouse delta，但新巨集同樣會保存有效的桌面絕對座標。
2. Recorder 開啟並處於等待狀態時，切到可信任目標，長按 F12 5 秒開始錄製。
3. 錄製中再次長按 F12 5 秒，停止並儲存到 Recordings。
4. F12 開始／停止控制動作本身不會寫入 macro。
5. 一般權限 Recorder 遇到管理員程式時會安全阻止開始；需要時請改用 06A。
6. 不要錄製密碼、API key、信用卡、驗證碼或其他敏感資料。

播放
----
1. 由 07 或 07A 開啟清單，選擇巨集後確認權限、螢幕排列、目標位置與倒數顯示方式。
2. 按下開始後有固定 5 秒倒數；請在倒數結束前切到正確目標。
3. 滑鼠重播方式固定為 AbsoluteDesktop（絕對桌面座標），Player 不提供其他滑鼠重播模式。
4. 顯示器排列、解析度或視窗位置改變後，播放前請重新確認目標位置；必要時使用新版 Recorder 重新錄製。
5. 舊巨集若只有位移資料而沒有有效桌面座標，會在倒數及任何輸入送出前 fail closed；工具不會猜測位置或覆寫舊檔。
6. KeepVisible 會保持 Player 可見、暫時置頂並讓滑鼠穿透；Minimize 會在倒數前最小化 Player。
7. 播放期間長按 F11 2 秒是第一優先緊急停止。
8. 若介面無法操作，可執行 99_緊急終止巨集工具.cmd 進行外部緊急清理。

權限與安全限制
--------------
- 一般權限不能可靠控制管理員程式；必要時使用 06A 或 07A，並由使用者接受正常 UAC 提示。
- 不支援也不會繞過 UAC 安全桌面、登入畫面、driver/injection 或 anti-cheat 保護。
- 同一時間只允許一個 Recorder 或 Player；新工具啟動前會先安全結束舊工具。
- 雙螢幕排列、解析度、DPI、視窗位置、輸入法與載入速度應盡量與錄製時一致。
- 程式沒有網路、遙測、雲端同步、開機自啟或隱藏錄製。

操作手冊檔案
------------
「開啟操作手冊」會開啟 Program/Docs/README_操作手冊.txt。正式發行 Gate 會驗證此檔存在、非空白、UTF-8、無私人資料，並與發行根目錄的 README_操作手冊.txt 逐位元一致。

完整說明另見 START_HERE.txt、USER_GUIDE.md、TROUBLESHOOTING.md 與 docs/FAQ.md。
