# DeskBox + 滴答清单内置同步（fork 自用版）

把 DeskBox 的待办与滴答清单（国内版 dida365.com）双向同步，**同步引擎直接编进 DeskBox**，
不再需要任何外部 Python 脚本、常驻窗口或启动 bat。

上游：`Tianyu199509/DeskBox`（GPL-3.0-only，自用 fork 无需开源）。
本 fork：`WangMCweijia/DeskBox`，默认分支 `main`。

## 交付物

| 文件 | 说明 |
|---|---|
| `DeskBox-DidaSync-Setup.exe` | 双击运行的覆盖安装器：备份 → 安装 → 写入同步配置 |
| `DESKBOX_SYNC.md` | 本文件 |

## 装完是什么状态

1. `C:\Program Files\DeskBox` 换成 **Native AOT 零售形态**的 DeskBox：
   - `DeskBox.exe`（>20 MB 单文件，与官方同一种编译方式）
   - `DeskBox.ThumbnailProxy.exe`（**文件夹图标必需**，普通 publish 不会产出）
   - `deskbox_native.dll`（Rust 原生模块：回收站、快捷方式、音量等）
   - `DeskBox.pri`（2.7 MB 合并版，含 WinUI 主题资源）
   - `Microsoft.WindowsAppRuntime.Insights.Resource.dll`
2. `%LocalAppData%\DeskBox\dida-sync.json` 已写入并**默认开启**，口令与清单已填好。
3. 旧程序整体移动到 `%LocalAppData%\DeskBox\backup\program-<时间戳>`，可回滚。

## 设置在哪

**设置 → 功能小组件 → 待办 → 滴答清单同步**（页面底部）

| 控件 | 作用 |
|---|---|
| 开关 | 启用/停用同步 |
| API 口令 | 粘贴 `dp_` 开头的口令 |
| 测试并载入清单 | 验证口令并拉取清单列表填充下拉框 |
| 保存 | 写入 `dida-sync.json` |
| 清单下拉 | 选择待办落点 |
| 状态行 | 显示最近一次同步结果 |

改完保存后，主进程在下一轮轮询（默认 12 秒）内按新配置工作，**不需要重启 DeskBox**。

口令获取：滴答清单网页版 → 头像 → 设置 → 账户与安全 → **API 口令**。

## 同步行为

```
手机滴答 App ─┐
              ├─→ 滴答云端 api.dida365.com ←─→ DeskBox 内置同步引擎 ←─→ todo.json
PC 滴答客户端 ┘                                   └─→ todo.sync 信号 ─→ 热重载 UI
```

同步引擎写完 `todo.json` 后写 `todo.sync`。DeskBox **只监听信号文件**，
所以它自己的 `SaveAsync` 不触发重载，天然不存在「拉 → 脏 → 推 → 拉」回环。

| 场景 | 表现 |
|---|---|
| 手机改 → DeskBox | 约 12 秒（滴答没有 webhook，只能轮询） |
| DeskBox 改 → 手机 | 下一轮推送，约 1–12 秒 |
| 两边同时改同一条 | 按本地 `UpdatedAt` 与远端 `modifiedTime` 比时间，新者胜 |
| 删除 | **保守**：DeskBox 侧删除只在本地标记，不删滴答；滴答侧删除不删本地 |
| 冲突保护 | 详情页正在编辑时，合并最多推迟 15 秒，不冲掉输入 |
| 首次同步 | 本地已有同名任务时**复用**远端任务（按标题 + 完成状态匹配），不会产生重复 |

同步字段：标题、备注、完成状态、截止时间、重要标记、子任务步骤。
**不同步**：颜色标记（滴答无对应）、附件、重复规则细节、格子归属（一个 Todo 格子 ↔ 一个滴答清单）。

## 实现结构（fork 里只有新增文件）

| 文件 | 作用 |
|---|---|
| `src/DeskBox/ViewModels/TodoSyncHotReload.cs` | 监听 `todo.sync` 信号 → 热合并 `Items`（不重启、不闪屏） |
| `src/DeskBox/Services/DidaSyncConfig.cs` | 读写 `dida-sync.json` |
| `src/DeskBox/Services/DidaSyncService.cs` | 轮询滴答 Open API、LWW 合并、写回 todo.json + 信号 |
| `src/DeskBox/Views/SettingsWindow.DidaSync.cs` | 设置页交互（口令校验、清单下拉、状态） |
| `scripts/apply_sync_patch.py` | 构建前幂等注入接线（2 行 C# + 设置页 XAML 块） |
| `.github/workflows/sync-build.yml` | 云端 Native AOT 出包 |

上游文件在 fork 里**一个都没改**：那 2 行接线和设置页 XAML 块由 CI 在 checkout 的工作树里注入，不提交。
好处是 GitHub 点 **Sync fork** 永远零冲突。

## 云端重新出包

fork：`https://github.com/WangMCweijia/DeskBox` → Actions → **Build DeskBox with Todo sync (Native AOT)** → Run workflow。

流程：注入接线 → restore → Rust 1.96 + MSVC → **Native AOT publish** → 从框架 MSIX 提取 Insights 资源 → 校验 → 打包上传。约 8 分钟。

产物：`DeskBox-DidaSync-<version>-win-x64.zip`（约 100 MB）。

## 出包的两个坑（都已在 CI 里堵死）

1. **`PublishAot=false` 的包不能用**：产物是 339 KB apphost + DLL，没有 `DeskBox.ThumbnailProxy.exe`
   （文件夹图标全空）、没有 `deskbox_native.dll`；PRI 只有 615 KB，缺 WinUI 主题资源索引，
   启动直接 `Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`。
   必须 `PublishAot=true + DeskBoxRustNative=true`，并装 Rust 1.96 + MSVC。
2. **`Microsoft.WindowsAppRuntime.Insights.Resource.dll` 不会自动进包**：WinAppSDK 2.5.1 的自包含
   组件payload 缺这个签名资源 DLL，需从 `Microsoft.WindowsAppSDK.Runtime` 包的
   `tools/MSIX/win10-x64/Microsoft.WindowsAppRuntime.2.msix` 里提取（CI 已自动做）。

安装器也加了护栏：`DeskBox.exe < 20 MB` 或缺 ThumbnailProxy / deskbox_native 时**拒绝安装**。

## 已知边界

- **准实时**，不是秒级：滴答 Open API 无 webhook，12 秒是轮询下限。
- **取消完成**：滴答没有 reopen 端点，用 `status:0` 尽力而为，偶尔需在滴答侧手动确认。
- **限流**：100 请求/分钟；超限返回 HTTP 500 `exceed_query_limit`（不是 429），代码按失败退避重试。
- **口令有效期**约 180 天，过期后设置页状态提示 401，重新生成即可。
- 自建 AOT 版**建议关闭 DeskBox 自动更新**，否则会被官方版覆盖（同步功能消失）。
- 上游发版后：Sync fork → 重跑 workflow → 重装。注入脚本带 `--check`，锚点失效会直接报错，不会静默产出坏包。

## 回滚

```
退出 DeskBox
删除 C:\Program Files\DeskBox
把 %LocalAppData%\DeskBox\backup\program-<时间戳> 整个移回 C:\Program Files\DeskBox
```

待办数据在 `%LocalAppData%\DeskBox\data`，安装器不会删除，只会另存一份到 `backup\data-<时间戳>`。

## 旧的 Python 同步器

`sync-agent/` 保留作为参考与应急手段（字段映射逻辑就是从这里移植进 C# 的）。
**内置引擎启用后不需要再运行它**；两者同时跑会互相覆盖，请只保留一种。
