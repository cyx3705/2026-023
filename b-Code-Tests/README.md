# HistoryVulcan 测试区

`b-Code-Tests/` 是 HistoryVulcan 唯一的自动化测试根目录。所有合同测试、安全测试、停靠与顶栏回归、模块测试、
质量门禁测试和包消费烟测都必须放在这里；`b-Code-HistoryVulcan/` 只保存产品源码、工程脚本和发布输入。

## 项目

- `HistoryVulcan.Tests/`：引用源码工程的 Debug/Release 合同与回归测试。
- `PackageSmoke/`：只从隔离 NuGet 源消费候选四包的独立烟测，不引用源码工程，也不加入产品解决方案。

质量合同测试、停靠/顶栏回归、服务/MCP、模块/命令和 PackageSmoke 均归档在本目录；产品源码目录不得新增测试项目或测试夹具。

发布脚本单独恢复、构建并运行 `PackageSmoke/PackageSmoke.csproj`。测试迁移、拆分或新增时，必须同步更新根解决方案、
发布脚本和 `b-Office/current/验证合同.md`，禁止在 `b-Code-HistoryVulcan/tests`、`src` 或示例目录新增测试项目。

源码质量门禁由 `b-Code-HistoryVulcan/eng/Test-QualityGate.ps1` 执行：活动源码不得包含警告抑制标记，生产 `.cs`/`.xaml` 文件不得超过 1000 行。
