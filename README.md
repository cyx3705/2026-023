# HistoryVulcan 3.11.2

本仓库是 OneHistory HistoryVulcan（原 AppShell，3.2.0 起改名）的独立源码、合同与发布资产真值。
`3.0.3` 是 V3 冻结基线，冻结标签为 `v3.0.3`；版本线不再与 HistoryJanus 对齐，`0.7.x` 仅保留用于回滚。

当前源码与正式 Z 快照均为 `3.11.2`；`HistoryVulcan.Core` 公开面自 `3.9.0` 起冻结。
3.11.2 恢复参数候选的连续推进：提交参数名后显示注册的允许值/常用值，提交参数值后继续到下一个参数；
不改变文本或光标的位置参数结构提示会立即关闭，避免候选无限重弹。
3.11.1 将命令助手限定到控制台最大化/聚焦布局：普通停靠布局输入字母或 Tab 均不打开 Popup、
不切换命令集、不回填命令集选中项；聚焦布局仍连续完成域 → 类 → 方法 → 参数。
`vulcan.command.show` 兼容性增加命令注册注解，供 Mercury 读取注册方声明的动态候选。Core、CommandBus、解析器和注册协议均未修改。
3.5.0 新增稳定语义命令
`vulcan.app.focusconsole`，由服务端、前端、远程中继和 `--focus-console` 启动共同复用；
Mercury 的双 `/` 只绑定该命令，`mercury.shortcut.wakeconsole` 仅作为兼容包装。
3.4.0（DEC-025）恢复受控的两段直接方法与域聚焦，`mercury.go` 是首个正式用例。
3.3.2（DEC-023）指令类从 13 个收敛为 **9 类**
（`app`/`command`/`ui`/`log`/`mcp`/`module`/`prompt`/`svc`/`web`），退役影子域 `debug`；
**模块指令域去掉 `History` 品牌前缀**（模块名仍叫 `HistoryJanus`，指令域是 `janus`）；
测试项目不再跨仓库引用 HistoryMercury，CI 冻结门禁恢复可通过。
3.3.0（DEC-022）建立三段式 `vulcan.<类>.<方法>`（Domain=`vulcan`），命令集列为域|类|方法；
全局快捷键与命令工作台由 HistoryMercury 拥有。3.2.2 完成严格域/类共享状态与 Z manifest 模块发现。
`3.1.8` 仅是内部过渡版本，不作为稳定支持版本；`3.1.9` 是旧名 AppShell 的最后快照。
3.1.10 对“轻松指令”和中央命令集做了内部高内聚重构：
两种交互共享由 `CommandBus` 驱动的目录快照、详情缓存、检索和选择状态，不新增公开 API 或改变命令语义。
控制台聚焦时输入框上方显示命令、参数名和允许值候选，
`Shift+W`/`Shift+S` 上下选择、`Tab` 写入当前候选并立即展开下一层而不执行；3.1.7 建立的 UI 风格合同继续统一嵌入页面的色板、字体、字号、
圆角、间距、控件尺寸、顶栏归属和响应式验收规则。HistoryVulcan 采用单 EXE 双进程运行模型，后台服务承载
命令、模块和日志；全局快捷键与命令工作台由 Mercury 提供。双 `/` 唤出并聚焦控制台（需 Mercury）；前端关闭默认隐藏而不停止后台。
删除 HistoryVulcan
内置资源/Workspace 与演示电机页，并保留 `ModulesView` 作为唯一模块管理页面。资源浏览未来由独立模块提供；
HistoryVulcan 独立可执行宿主显式启用模块生命周期与模块管理页；
消费方仍按最小能力原则自行决定是否启用，Janus 不再维护第二套模块宿主或管理页面。V3.1/V3.2 方案与实施记录已归档到
`b-Office/history/`；当前规则见 [断头指令审计表](b-Office/current/断头指令审计表.md)。

## AI 与维护入口

| 入口 | 用途 |
| --- | --- |
| [AI 工作合同](AGENTS.md) | 读取顺序、真值、冻结与修改边界 |
| [项目清单](project.manifest.json) | 项目身份、活动路径、命令、归档和上下文排除项 |
| [项目概览](b-Office/current/项目概览.md) | 目标、范围、冻结状态与最近验证 |
| [技术合同](b-Office/current/技术合同.md) | 现行需求、架构和不变量 |
| [验证合同](b-Office/current/验证合同.md) | 本地、CI、包和消费方门禁 |
| [文档中心](b-Office/文档中心.md) | current、package、history 与发布资产边界 |

常规维护不要扫描 `b-Office/history/`、`b-Publish/` 或生成目录；跨项目消费直接读取
`z-HistoryVulcan/HistoryVulcan.reuse.md`。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `b-Code-HistoryVulcan/` | Core、Services、Shell、ServiceHost、演示宿主与工程脚本 |
| `b-Code-Tests/` | HistoryVulcan 回归、布局、命令、安全与包合同测试 |
| `b-Code-Samples/` | 模块开发示例 |
| `b-Office/package/` | 消费文档与嵌入页面 UI 风格合同编辑源 |
| `b-Office/current/` | 现行项目、验证、升级与发布合同 |
| `b-Code-HistoryVulcan/eng/release/` | 发布清单和生成模板等机器输入 |
| `b-Office/` | 冻结契约、内部设计与执行证据 |
| `b-Publish/current/` | 唯一一份可覆盖的当前候选和完整发布测试结果 |
| `b-Publish/history/<版本>/` | 与当时 Z 快照同构的最小正式历史副本 |
| `z-HistoryVulcan/` | 当前发布快照的展开内容，不保存历史版本目录 |
| `z-HistoryVulcan/installer/` | Windows 安装包（Setup.exe）与便携 7z（不计入快照 SHA256SUMS） |

根级 `HistoryVulcan.sln` 是仓库验收入口，只包含六个冻结项目；组件目录内的
`b-Code-HistoryVulcan/HistoryVulcan.sln` 是发布脚本使用的等价入口。

## 构建与测试

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Debug --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\HistoryVulcan.sln -c Release --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Release --no-build --no-restore
dotnet format .\HistoryVulcan.sln --verify-no-changes --no-restore
```

## 宿主候选与正式部署

```powershell
# 生成 b-Publish/current 下的宿主 + 文档完整候选
.\b-Code-HistoryVulcan\eng\Build-HistoryVulcanPackage.ps1

# 候选审核通过后，将同一完整快照一次性部署到 z-HistoryVulcan
powershell -NoProfile -ExecutionPolicy Bypass -File ..\2026-019-HistoryDiana\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish

# 从正式 Z 快照生成 Windows 安装包与便携压缩包
.\b-Code-HistoryVulcan\eng\Pack-HistoryVulcanInstaller.ps1 -Version 3.3.2
```

当前交付物是可直接运行的 HistoryVulcan 宿主，不是 NuGet 包。安装包位于
`z-HistoryVulcan/installer/`（`*-Setup.exe` 与 `*-win-x64.7z`）。

3.3.2 起旧名四包发布脚本 `Publish-AppShell.ps1` 与 `b-Publish/history/0.5.0`、`0.7.2` 两个
0.7 线归档一并退役（DEC-023）：版本线已明确不再与 HistoryJanus 对齐，0.7.x 回滚路径两年内
未被使用，保留一套指向旧包 ID 的生成链只会让发布入口有两个真值。需要回溯 0.7 线时从
Git 历史取回。现行回滚仍由 `b-Publish/history/<版本>/` 的 3.x 同构副本承担。

宿主候选位于 `b-Publish/current`。当前正式快照为 3.11.1：运行入口 `z-HistoryVulcan/host/HistoryVulcan.exe`，
UI 风格合同 `z-HistoryVulcan/docs/HistoryVulcan_UI风格与嵌入页面规范.md`。
旧名 `z-Package-AppShell/` 的 3.1.9 快照已随 3.2.0 发布退役删除（同构副本入库于 `b-Publish/history/3.1.9/`）。
旧候选整体归档到 `b-Publish/history/<版本>/`。宿主部署脚本不会执行 Git commit、tag、push，
也不会生成或推送 NuGet 包。

桌面消费者通常引用 `OneHistory.HistoryVulcan.Shell`；服务化宿主额外引用
`OneHistory.HistoryVulcan.ServiceHost`。当前已验证消费方为 HistoryJanus（020）和 WBall（022）。

维护入口见 [b-Office/文档中心.md](b-Office/文档中心.md)。其他项目和 AI 先读取
`z-HistoryVulcan/HistoryVulcan.reuse.md`，再按需索引同一当前快照中的 `z-HistoryVulcan/docs/`；历史版本文档与发布证据从 `b-Publish` 查阅。
