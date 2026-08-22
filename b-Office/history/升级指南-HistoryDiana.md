# 随宿主 4.0.0 升级：HistoryDiana

> 先读 [升级指南-总纲](升级指南-总纲.md)，本篇只列 Diana **实测**要改的地方。
> 数据采自 2026-08-19 对 `2026-019-HistoryDiana` 的只读扫描。

## 工作量概览

| 项 | 数量 |
|---|---|
| XAML 文件 | **0** |
| 用到的样式键 | **0** |
| 自建样式 / 模板 | **0** |
| 宿主 API 破坏 | **0** |
| 硬编码的旧命令名 | **0**（2026-08-20 用正确模式重扫确认；初版的扫描命令有漏，见总纲第三节）|

**总纲第一到第三节（样式键、自建组件、命令改域）对 Diana 全部不适用。**
Diana 没有界面，只用 `Core.Commands` / `Core.Logging` / `Core.Modules` / `Core.Storage`
四个命名空间，这四个在 4.0.0 都没有破坏性变化。

Diana 的升级重点不在自己身上，而在**它拥有的发布管线**。

## 一、发布管线要登记新产品 HistoryAurora（核心项）

`b-Code/Publish-OneHistoryModule.ps1` 里有一份 `$definitions` 登记表，
目前登记了 `HistoryVulcan`（第 45 行起）等项目，各自带着：

```
ProjectDirectory / VersionProps / BuildScript / TestProject / QualityGates
```

**HistoryAurora（`2026-026-HistoryAurora`）是这次新出现的产品，尚未登记。**
它不是模块，是与宿主并列的独立应用，需要一条自己的定义：

| 字段 | Aurora 的值 |
|---|---|
| `ProjectDirectory` | `2026-026-HistoryAurora` |
| `VersionProps` | `b-Code-Studio\AuroraVersion.props`（属性名 `HistoryAuroraVersion`）|
| `BuildScript` | `b-Code-Studio\eng\Build-HistoryAuroraPackage.ps1` |
| `TestProject` | `b-Code-Verify\Contracts\Contracts.csproj` |
| `QualityGates` | `b-Code-Studio\eng\Test-QualityGate.ps1` |

注意 Aurora 的构建与测试都要带 `-p:NuGetAudit=false`（其 manifest 的 `commands` 里已如此声明）。

### 顺序约束：Aurora 必须在宿主之后发

Aurora 通过 `<Reference HintPath>` 指向 `2026-023-HistoryVulcan/z-Publish/host` 的 DLL。
宿主没先发布新快照，Aurora 就是拿新代码链旧 DLL。

**管线里 Aurora 的构建必须排在宿主 `Build-HistoryVulcanPackage.ps1` 之后**，
否则会出现"两边都绿、装到一起崩"的情况——这种失败在各自的门禁里都看不见。

## 二、宿主快照形状：门禁已经存在，别绕过

宿主的 `Test-QualityGate.ps1` 强制**候选快照必须扁平**：
`z-Publish/` 根部放 `host/`、`docs/`、`manifest.json`、`SHA256SUMS`，
版本化目录只允许出现在 `history/` 下。

这条已经被破坏过两次（提交 `2559bf1` 一次，2026-08-19 又一次），
两次都是发布管线把候选写成了 `z-Publish/HistoryVulcan-vX.Y.Z/host/*`。

> **Diana 侧要确认**：`Publish-OneHistoryModule.ps1` 产出宿主候选时写的是
> `z-Publish/host/`，不是版本化子目录。宿主门禁会拦，但拦下来时已经改乱了工作树。

## 三、宿主形态变化对管线的影响

| 变化 | 对管线的意义 |
|---|---|
| `HistoryVulcan.exe` 不再有前端角色 | 冒烟若期望"启动后出现窗口"，需要改为检查 `endpoint.json` 与 `/api/health` |
| `--service` 退化为兼容开关 | 带不带它行为一样；自启动项里的旧参数**不用改** |
| 解决方案里没有 `HistoryVulcan.Shell` 了 | 若管线断言过工程清单，需要更新 |
| 公开面门禁从 4 个包变成 **3 个** | `Assert-PublicApiBaseline.ps1` 自己已改，管线若硬编码过数量要同步 |

## 四、Diana 自己的代码：只有一处值得看

```
b-Code-HistoryDiana/tests/HistoryDiana.Smoke/Program.cs:173
    && typeof(IUiModule).IsAssignableFrom(type)), "模块不得注册 UI 生命周期"
```

这是一条**否定断言**——Diana 断言自己不注册 UI 生命周期。`IUiModule` 在 4.0.0
仍然存在（C 阶段才会删除），所以本轮这条继续有效，不用动。

> C 阶段删除 `IUiModule` / `IShellUiAware` 时这条会编译失败，届时改为
> 断言"不实现任何 Aurora 页面契约"即可。那是另一轮。

## 五、验收

- [ ] `Publish-OneHistoryModule.ps1` 的 `$definitions` 已登记 HistoryAurora
- [ ] 管线顺序保证 Aurora 构建在宿主快照刷新之后
- [ ] 宿主候选产出为扁平的 `z-Publish/host/`，宿主 `Test-QualityGate.ps1` 通过
- [ ] Diana 自己的构建与 Smoke 全绿（预期无需改任何代码）
- [ ] 走一遍完整发布：宿主 → Aurora → 三个有界面的模块，各自门禁全绿

> Diana 在 Aurora 下已实测可装载（27 条命令），**无界面窗口是正常的**——
> 它是管线模块，本来就不注册 UI。
