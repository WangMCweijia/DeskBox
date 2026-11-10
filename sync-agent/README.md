# 参考实现：外部 Python 同步器（已被内置引擎取代）

> **已不再需要运行本目录。** DeskBox 现在内置了滴答清单同步
> （`src/DeskBox/Services/DidaSyncService.cs`），配置在
> 「设置 → 功能小组件 → 待办 → 滴答清单同步」里填写。

保留这份实现的原因：

1. 字段映射、LWW 合并、adopt 去重、限流处理这些逻辑最早在这里验证过，
   内置引擎是它的等价 C# 移植，排查时可以直接对照。
2. 应急：DeskBox 起不来时，可以用它单独把数据同步进 `todo.json`
   （仍然依赖 `todo.sync` 信号，所以 DeskBox 必须能启动才能看到变化）。

⚠️ **不要和内置引擎同时运行**，两边会互相覆盖。
要临时改用它，先在设置里把「滴答清单同步」开关关掉。

```bash
python dida_sync.py --list-projects     # 验证口令
python dida_sync.py --once --dry-run    # 预览，不写任何东西
python dida_sync.py                     # 常驻
```

`config.json` 里的口令与内置引擎的 `%LocalAppData%\DeskBox\dida-sync.json` 是两份独立配置。
