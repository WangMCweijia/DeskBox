# DeskBox <-> 滴答清单同步器

只依赖 Python 3.10+ 标准库，常驻在 PC 上运行。它负责所有网络请求、字段映射、
LWW 合并与写盘；DeskBox 本身只监听同目录下的 `todo.sync` 信号文件并热重载内存。

## 首次配置

1. 滴答清单网页版：`头像 -> 设置 -> 账户与安全 -> API 口令`，生成并复制 Bearer Token。
2. 复制 `config.example.json` 为 `config.json`，填写：
   - `dida_token`：上一步的口令
   - `dida_project_id`：先跑 `python dida_sync.py --config config.example.json --list-projects` 查清单 id
     （`--list-projects` 只读 token，不校验 project_id）
   - `todo_json`：本机 `todo.json` 绝对路径。当前机器实测路径为
     `C:/Users/wang/AppData/Local/DeskBox/data/widgets/7c119a1e-892e-4211-8e39-966f2a80f9b7/todo.json`
3. 确认账号是国内版 `dida365.com`。国际版 `ticktick.com` 数据不互通。

## 验证顺序

```bash
python dida_sync.py --list-projects                 # 1. token 有效、能读到清单
python dida_sync.py --once --dry-run                # 2. 只打印计划，不写任何东西
python dida_sync.py --once                          # 3. 跑一轮，检查 todo.json 与 todo.sync
python dida_sync.py                                 # 4. 常驻（12 秒轮询）
```

日志同时输出到控制台和 `dida_sync.log`。

## 行为约定

| 方向 | 行为 |
|---|---|
| 滴答新建 / 改标题 / 改期 / 完成 | 合并进 `todo.json`，写 `todo.sync` 触发 DeskBox 免重启刷新，延迟约 10–15 秒 |
| DeskBox 里新建 / 勾选 / 取消勾选 / 改文本 | 文件变化下一轮推到滴答，延迟约 1–12 秒 |
| 两边同时改同一条 | 按 `updatedAt` 与 `modifiedTime` 取新者（LWW），日志会记 conflict |
| DeskBox 里删除 | 只标本地 `isDeleted`，**不删滴答任务**（保守，避免误删） |
| 滴答里删除 | 默认**不**标本地墓碑。`/task/completed` 有 30 天回看窗口，超过窗口的已完成任务会"消失"，无法与真删除区分 |
| `ColorMarker`、附件、重复规则、提醒 | 不同步。这些 DeskBox 字段由本地独占，同步器不会覆盖 |
| 步骤（checklist） | 双向同步文本与完成态，但 DeskBox 侧步骤 id 会在远端变化时重建 |

`completed_lookback_days` 默认 30 天；`/task/completed` 单次最多返回 200 条。

## 限流与错误

- 官方 API 限 100 请求/分钟，超限返回 **HTTP 500 `exceed_query_limit`**（不是 429），客户端自动冷却 61 秒。
- 401 表示 token 失效（个人 API 口令约 180 天），脚本会退出并提示重新生成。
- `sync-state.json` 保存三路合并基线，删除它会重新全量导入，可能产生重复任务，不要手删。
