# HistoryVulcan 测试区

`b-Code-Tests/` 是 HistoryVulcan 唯一的自动化测试根目录。合同测试、安全测试、模块测试和质量门禁测试都必须放在这里；`b-Code-HistoryVulcan/` 只保存产品源码、工程脚本和发布输入。

## 项目

- `HistoryVulcan.Tests/`：引用源码工程的 Debug/Release 合同与回归测试，已加入根解决方案。

停靠/顶栏、局域网 Web 网关和四包 NuGet 消费烟测已随宿主轻量化迁出或退役：前端回归在 HistoryAurora，Web 边界在 HistoryPortunus，宿主不再 `dotnet pack`，因此本目录不再保留 `PackageSmoke/`。测试迁移、拆分或新增时，必须同步更新根解决方案和 `b-Office/current/验证合同.md`，禁止在 `b-Code-HistoryVulcan/tests`、`src` 或示例目录新增测试项目。

源码质量门禁由宿主进程内 `QualityGates` 执行（`ReleasePipelineTests.QualityGatesPassOnThisRepository`）：活动源码不得包含警告抑制标记，生产 `.cs`/`.xaml` 文件不得超过 1000 行。
