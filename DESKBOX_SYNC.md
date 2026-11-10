# DeskBox 待办 × 滴答清单：免重启实时互通

目标：手机和 PC 上的滴答清单与 DeskBox 桌面待办格子共用一份数据，
**DeskBox 里能新建/勾选，改动实时刷新，全程不重启 DeskBox，不改上游一行网络代码**。

上游：`Tianyu199509/DeskBox`（GPL-3.0-only，自用 fork 无需开源）。
本 fork：`WangMCweijia/DeskBox`，默认分支 `main`。

## 架构

```
手机滴答 App ─┐
              ├─→ 滴答云端 api.dida365.com ←─→ PC 常驻同步器 ←─→ todo.json ←─→ DeskBox（信号监听 + 热重载）
PC 滴答客户端 ┘                                      └─→ todo.sync 信号文件 ─────┘
```

同步器写完 `todo.json` 后写 `todo.sync`。DeskBox **只监听信号文件**，
所以它自己的 `SaveAsync` 不会触发重载，天然不存在「拉 → 脏 → 推 → 拉」回环。
这对应上游设计文档 `docs/architecture/cloud-backup-audit-and-live-sync-20260921.md` §4.2
的 `ApplyMergedDataAsync` + sync-applied 标记，以及 §4.3-1(b) 的「单一共享待办列表」。

## 仓库里新增/改动的文件

| 路径 | 作用 |
|---|---|
| `src/DeskBox/ViewModels/TodoSyncHotReload.cs` | **新增**。`TodoWidgetViewModel` 的 partial 扩展：`ApplySyncData()` 重建 `Items`/`_tombstones`、详情页编辑时延迟重试；`TodoSyncWatcher` 监听 `todo.sync`。约 220 行 |
| `src/DeskBox/ViewModels/TodoWidgetViewModel.cs` | **不修改**。CI 构建前由注入脚本临时加 2 行：构造函数 `AttachSync(_store);`、Dispose `DetachSync();`。工作树改动只用于出包，不提交 |
| `scripts/apply_sync_patch.py` | 幂等注入脚本。fork 保留上游源码原样，Sync fork 零冲突；本地构建前手动跑一次即可 |
| `.github/workflows/sync-build.yml` | 云端出包：注入校验 → restore → build → publish → zip → artifact |
| `sync-agent/dida_sync.py` | 常驻同步器（Python 标准库） |
| `sync-agent/config.example.json` / `README.md` | 配置模板与使用说明 |

## 第一步：云端出包

`sync-build.yml` 在 push 到 `main` 时自动触发，也可在 Actions 页手动 `Run workflow`。

```
windows-latest → setup-dotnet 10.0.x → apply_sync_patch.py → restore → build → publish → zip → artifact
```

产物：`DeskBox-DidaSync-<版本>-win-x64.zip`（Actions 运行页的 Artifacts，保留 30 天）。

构建参数要点：
- `PublishAot=false` + `SelfContained=true` + `WindowsAppSDKSelfContained=true`：自用不必复刻官方
  Native AOT 零售链（那条链强制 Rust + MSVC + 一堆 PE/token/warning 校验），产物自带运行时，换机器也能跑。
- `DeskBoxRustNative=false` + `DeskBoxShellThumbnailProxy=false`：关掉两个 Rust 组件，云端不需要装 Rust。
- 默认跑上游测试套件；手动触发时可选 `skip_tests=true` 加速。

## 第二步：替换本机 DeskBox

1. 退出正在运行的 DeskBox（托盘右键退出）。
2. **先备份** `%LocalAppData%\DeskBox\data` 整个目录。
3. 解压 zip 到原 DeskBox 安装目录（覆盖前保留一份旧 exe）。
4. 启动 DeskBox，确认待办格子正常显示。
5. 确认写权限：同步器要能写 `data\widgets\<id>\` 下的 `todo.json` 和 `todo.sync`。

> 官方 AOT 零售版与这个普通 Release 版的自带更新器行为可能不同；
> 建议关闭自动更新，或每次上游发版后重新跑一次 workflow 出包。

## 第三步：启动同步器

```bash
cd sync-agent
copy config.example.json config.json     # 填入 API 口令与清单 id
python dida_sync.py --list-projects      # 验证 token
python dida_sync.py --once --dry-run     # 预览，不写任何东西
python dida_sync.py                      # 常驻
```

`config.json`、`sync-state.json`、`*.log` 已在 `.gitignore` 中，不会进仓库。
需要开机自启时，用任务计划程序指向 `pythonw.exe dida_sync.py`（工作目录设为 `sync-agent`）。

## 上游发版后怎么跟上

GitHub fork 页面点 **Sync fork**（或 `git fetch upstream && git merge upstream/main`）。
fork 里**没有任何上游文件的改动**，只有新增文件，因此合并零冲突。
每次云端构建都会由 `scripts/apply_sync_patch.py` 重新注入那 2 行接线；
本地构建前手动跑一次即可：

```bash
python scripts/apply_sync_patch.py           # 补接线
python scripts/apply_sync_patch.py --check  # 只检查，退出码 1 表示缺失
```

如果上游把构造函数 / Dispose 的锚点改了，脚本会明确报 `anchor changed upstream`，
按报错手工补两行调用即可，其余新增文件不受影响。

## 已知边界

| 项 | 说明 |
|---|---|
| 「实时」的真相 | 滴答 Open API 无 webhook。手机 → DeskBox 受轮询周期限制，约 10–15 秒；DeskBox → 滴答约 1–12 秒 |
| 详情页正在编辑 | 热重载会延迟最多 15 秒，不会冲掉输入框内容 |
| 删除 | 两侧都保守：DeskBox 删除只标 `isDeleted`；滴答删除默认不标本地墓碑（完成超 30 天的任务会从 API 视图消失，无法与删除区分） |
| 不同步的字段 | `ColorMarker`、附件、重复规则、提醒偏移、贪睡状态；本地独占，不会被覆盖 |
| 冲突 | 两边同时改同一条按 `updatedAt` / `modifiedTime` LWW，日志记 conflict |
| 取消勾选 | 官方没有 reopen 端点，用 `POST /task/{id}` 带 `status:0` 尽力而为，需在滴答里确认是否生效 |
| 限流 | 100 req/min，超限是 HTTP 500 `exceed_query_limit`，客户端自动等 61 秒 |
| Token | 个人 API 口令约 180 天，401 时脚本退出并提示重新生成 |
| 一个格子 | 对应一个滴答清单。当前本机只有一个 Todo widget，符合用户约定 |

## 排障

| 现象 | 检查 |
|---|---|
| 手机改了 DeskBox 不变 | 看 `dida_sync.log` 是否有 `Wrote todo.json and todo.sync`；确认 `todo.sync` 与 `todo.json` 在**同一目录**；确认跑的是新构建的 exe |
| DeskBox 改了滴答不变 | 确认 `sync-state.json` 里该条有 `remote_id`；看日志 `UPDATE remote <- local` |
| 每次都重复导入 | `sync-state.json` 被删或 `dida_project_id` 变了；恢复备份的状态文件，不要手删 |
| 编译报 `AttachSync` 不存在 | `TodoSyncHotReload.cs` 没进项目，或 `apply_sync_patch.py` 没跑 |
| 启动报缺少 WindowsAppSDK Runtime | 安装 WinAppSDK Runtime，或改 workflow 的 `WindowsAppSDKSelfContained` 参数重新出包 |
