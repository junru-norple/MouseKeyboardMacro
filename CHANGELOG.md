# Changelog

## 1.0.0

- 完成預設 Standard／Desktop Safe 與 opt-in Raw Input Enhanced 錄製；新巨集一律保存有效的桌面絕對座標。
- 滑鼠重播固定為 AbsoluteDesktop（絕對桌面座標）；舊巨集只有位移資料而缺少有效桌面座標時，會在任何輸入送出前 fail closed。
- 加入 KeepVisible／MinimizeBeforeCountdown、Recorder F12 5 秒開始／再次 5 秒停止儲存、Player F11 2 秒主要緊急停止、99 外部 cleanup、Watchdog 與單一活動工具保護；F12 控制手勢不寫入巨集。
- 加入一般／系統管理員明確入口與 fail-closed privilege gate。
- 明確記錄標準權限模式無法可靠控制系統管理員程式，且不支援或繞過 UAC secure desktop。
- 建立可重現的 public source export、framework-dependent／self-contained portable release 與 SHA-256 Gate。
- 將 owner publication tests、MacroMigration、private data、logs、settings 與 binaries 排除於公開 Repository。
- 加入五張由安全 UI layout probe 產生的 sanitized 1280×720 PNG。
- 正式採用 MIT License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。
