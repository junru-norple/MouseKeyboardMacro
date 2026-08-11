# Contributing

本專案採用 MIT License：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。提交貢獻與散布內容前請閱讀 Repository 根目錄的 `LICENSE`。

提交變更時：

1. 不要加入巨集、設定、log、binary、PDB、本機路徑或私人視窗資訊。
2. 不要新增 live-input、UAC 或 Registry side effect 到自動測試。
3. 使用 .NET SDK 8.0.423。
4. 執行 public solution 的全部安全自動測試；CI 不使用 test filter。
5. 執行兩種 `Publish-Release.ps1` flavor 與 `Verify-Release.ps1`。
6. 確認 restore、build、test、publish 後 Git 工作樹仍乾淨。
7. High／Critical NuGet vulnerability finding 必須修正或阻擋合併。

安全問題請只提供已去識別化的最小重現，不要附上私人巨集或完整 log。
