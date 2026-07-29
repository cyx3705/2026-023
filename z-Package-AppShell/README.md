# AppShell 3.0.3 当前正式接入包

本目录只承载 AppShell 当前发布快照的展开内容，不保存历史版本目录或候选构建。

- `feed/`：当前正式版本的 Core、Services、Shell、ServiceHost 四个 `.nupkg`。
- `docs/`：与当前快照一同发布的消费合同。
- `AppShell.reuse.md`：供消费项目和 AI 优先读取的精简复用合同。
- `README.md`：由发布脚本从本模板生成，不在 z 级目录内直接维护。
- `manifest.json`、`SHA256SUMS`：当前快照元数据和全文件校验和。

所有历史版本、符号包、Demo、归档 manifest、归档校验和及虚拟发布统一保存在 `b-Publish/`。发布新快照时，脚本整体替换本目录，禁止把旧版本文件留在这里。
