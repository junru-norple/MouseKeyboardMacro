# Third-party notices

本專案使用 Microsoft .NET 8 與測試階段的 Microsoft.NET.Test.Sdk、xUnit、xunit.runner.visualstudio。

Portable release 會從實際使用的 .NET SDK 複製官方 `LICENSE.txt` 與 `ThirdPartyNotices.txt`，並放入 release 根目錄。這些第三方條款與本專案的 MIT License 分別適用。

本專案授權狀態：`LICENSE_INCLUDED`、`SPDX_IDENTIFIER=MIT`、`Copyright (c) 2026 ru`。完整專案條款見 Repository 根目錄的 `LICENSE`。

套件版本由 project files 與 lock／restore metadata 決定；發布 Gate 會執行 NuGet dependency audit，High／Critical finding 會阻擋候選包。
