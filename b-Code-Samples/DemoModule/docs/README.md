# DemoModule

HistoryVulcan 起步示例。模块名 `DemoModule`，命令域 `demomodule`。

| 命令 | 作用 |
| --- | --- |
| `demomodule.calc.add` | 两整数相加 |
| `demomodule.calc.reverse` | 反转字符串 |
| `demomodule.host.ready` | 整轮装载完成后的就绪钩子；不进通用目录 |

身份、总线登记与完整包形状见源码 `ModuleInfo` / `Module`。复制到真实模块后改为引用已发布宿主快照，不要保留对本宿主源码工程的 `ProjectReference`。
