# HistoryVulcan 测试区

`HistoryVulcan.Tests/` 是唯一测试项目，已加入根解决方案；所有合同、回归和质量门禁测试放在这里，不在产品或示例目录另建测试项目，也不引用邻接模块源码。

| 内容 | 位置 |
| --- | --- |
| 精确行为及测试对应关系 | [技术合同](../b-Office/current/技术合同.md) |
| 执行命令、隔离要求与交付检查 | [验证合同](../b-Office/current/验证合同.md) |
| 通用设置/日志夹具 | HistoryVulcan.Tests/TestSupport.cs；故障、进度等专用夹具留在所属测试 |
| 示例接入 | DemoModuleSampleTests，将本仓 DemoModule 装载为完整运行包 |

质量门禁禁止活动源码的警告抑制标记，产品 `.cs`/`.xaml` 不得超过 1000 行。CLI 测试通过组合工厂隔离 AppData。迁移或新增测试项目须同步解决方案和验证合同；已迁出的界面、网关及退役的 NuGet 烟测不在本仓恢复。
