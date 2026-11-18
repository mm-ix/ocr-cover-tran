# ShotTrans 截图翻译（ocr-cover-tran）

Windows 10 19044+ / Windows 11 x64 的绿色截图翻译工具：**热键 → 框选 → 本地 OCR → 云端翻译 → 译文原位覆盖原文字**。

> **最终用户请先阅读 [使用说明.md](使用说明.md)**：如何运行、热键、划词，以及英文 OCR 语言包的两种安装方式（图形界面 / 管理员 PowerShell）。

- 技术：C# / .NET 8、WinForms + 原生 Win32 分层窗口、GDI+ 排版、Windows.Media.Ocr、OpenAI 兼容 `/chat/completions`
- 绿色版：自包含单文件 `publish/green/ShotTrans.exe`，不需要安装 .NET、无需管理员、默认不写注册表
- 体积：约 **49.6 MB**（单文件不压缩，优先更快的启动速度）

## 功能

### P0
- 全局热键框选（默认 `Ctrl+Alt+X`，可改键）
- 全屏暗化、实时尺寸、Esc / 右键取消
- 按物理像素截图（PerMonitorV2、多屏虚拟桌面坐标）
- 本地 Windows OCR（逐行文本 + 边界框）
- OpenAI 兼容翻译接口（base_url / api_key / model / 温度 / 超时）
- 译文原位覆盖：字号自适应、自动换行；放不下时卡片向下扩展并与相邻块自动避让，空间不足才缩小/省略
- 背景取原文字块局部主色，按亮度自动黑/白字
- 覆盖层空白区域鼠标穿透；**译文文字可像普通文本一样拖选**，选中后右键菜单复制
- 状态条放在选区外（优先上方，没有空间则下方），不遮挡译文；3 秒后淡出
- Esc / 右键 / 点击外部关闭
- 按住 `Space` 看原文，松开恢复译文
- `Ctrl+C` 复制选中译文；未选中时复制全部译文（阅读顺序）

### P1
- 多显示器 / 跨屏框选（虚拟桌面坐标）
- 高 DPI（100%~200%，PerMonitorV2）
- `WDA_EXCLUDEFROMCAPTURE` 排除自身，避免覆盖层被二次识别
- 设置页面：**OCR 语言包（一键安装英文 OCR / 刷新语言）**、热键、OCR 语言、字号上下限、背景不透明度、超时、术语表、历史、开机自启、**调试日志（默认关闭）**
- 术语保护：代码标识符 / 路径 / URL / 版本号 / 用户词表（`原文=译文`）
- 托盘常驻 + 开机自启（启动文件夹 `.lnk`，不写注册表）
- 本地历史（SQLite，最近 200 条，缩略图 + 原文 + 译文；**默认关闭**，设置中开启）
- 失败可重试：顶部红条 + `R`（复用原截图与 OCR 结果）

### 额外
- **划词翻译（F-23）**：选中文本后按 `Ctrl+Alt+Z`，程序自动保存剪贴板、模拟 `Ctrl+C` 取词、弹出浮动译文；弹窗译文可鼠标拖选、右键复制，`Ctrl+C` 复制选中/全部，Esc / 点击外部关闭，随后恢复原剪贴板。

## 快速使用

1. 运行 `publish/green/ShotTrans.exe`（或运行项目根目录 `build.ps1` 后使用生成文件）。
2. 首次启动打开「设置 → 翻译接口」：
   - Base URL：兼容 OpenAI 格式的根地址（如 `https://api.deepseek.com` 或 `https://host/v1`）或完整 `/chat/completions` 地址
   - API Key、模型名（例如 `hy-mt2-lite`）
   - 可点「测试连接」
3. 托盘菜单：截图翻译 / 划词翻译 / 历史记录 / 设置 / 开机自启 / 退出。
4. 截图翻译：
   - `Ctrl+Alt+X` → 拖拽框选 → 松手等待译文
   - 鼠标拖动选择译文 → 右键 → 复制；`Space` 看原文 · `Ctrl+C` 复制选中/全部 · `R` 重试 · `Esc` / 右键 / 点击外部关闭
5. 划词翻译：
   - 选中文字 → `Ctrl+Alt+Z` → 弹出译文；译文可鼠标选择、右键复制；`Ctrl+C` 复制；`Esc` / 点击外部关闭

## 目录结构

```
ocr-cover-tran/
├─ src/ShotTrans/              # 应用源码
│  ├─ App.cs                   # 托盘、热键、截图/划词流程、设置应用
│  ├─ Capture/                 # 框选、BitBlt 截图
│  ├─ Ocr/                     # Windows.Media.Ocr 封装（含大图缩放）
│  ├─ Translation/             # OpenAI 兼容接口、占位符/术语保护、容错 JSON 解析
│  ├─ Rendering/               # 逐块字号自适应、背景主色、覆盖排版
│  ├─ Overlay/                 # 结果覆盖层（点击穿透、Space、R、Ctrl+C）
│  ├─ Input/                   # RegisterHotKey、低级键鼠钩子
│  ├─ History/                 # SQLite 历史
│  ├─ Settings/HistoryForm/Ui/ # 设置、历史、提示、划词弹窗、托盘图标
│  └─ SelfTest.cs              # 无界面自检
├─ tests/                      # PowerShell UI 自动化 + 本地假翻译服务
├─ docs/验收报告.md            # 验收结果与环境说明
├─ publish/green/              # 绿色单文件发布目录
└─ build.ps1                   # 一键构建绿色版
```

## 构建

本机使用了便携 SDK：`hermes/.dotnet/sdk/dotnet.exe`（8.0.427）。运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建参数要点：
- `--self-contained true`：不依赖目标机 .NET
- `PublishSingleFile`（`EnableCompressionInSingleFile=false`）：单文件不压缩，减少启动时的解压开销以加快启动
- `PublishTrimmed=true / TrimMode=partial` + `_SuppressWinFormsTrimError=true`：WinForms 官方不建议裁剪，但本项目未用设计器/反射 UI；部分裁剪稳定，自检与 UI 冒烟通过
- 自定义 MSBuild 目标从单文件 bundle 移除未使用的 WPF/设计器程序集（约 50MB 原始体积）
- `BuiltInComInteropSupport=true`：开机自启快捷方式使用 `IShellLink` COM
- `InvariantGlobalization=true`：减小体积；中文文本/字体不受影响

## 配置

首次运行自动在程序目录生成 `config.json`（目录不可写时回退 `%APPDATA%\ShotTrans\`）：

| 字段 | 说明 | 默认 |
|---|---|---|
| `ScreenshotHotkey` / `SelectionHotkey` | 截图/划词热键 | `Ctrl+Alt+X` / `Ctrl+Alt+Z` |
| `OcrLanguage` | `auto` 或语言标签，如 `en-US` | `auto` |
| `FontMinSize` / `FontMaxSize` | 覆盖层字号（物理像素） | `8` / `24` |
| `BackgroundOpacity` | 覆盖背景不透明度 | `0.92` |
| `StatusBarFadeSeconds` | 成功状态条淡出秒数 | `3` |
| `TranslationBaseUrl` / `TranslationApiKey` / `TranslationModel` | 翻译接口 | 空（首次启动引导设置） |
| `TranslationTimeoutSeconds` | 单次请求超时 | `20` |
| `ProtectCodeIdentifiers` | 代码标识符自动保护 | `true` |
| `Glossary` | 用户术语，每行 `原文=译文` | 空 |
| `HistoryEnabled` | 本地历史开关 | `false` |
| `EnableDebugLog` | 调试日志开关（写入 `logs` 目录） | `false` |
| `AutoStart` | 开机自启 | `false` |

## 隐私

- 截图与 OCR 结果只在本机内存中处理。
- **只有识别出的文字**会发送到你配置的翻译接口；不发送图片。
- 历史记录默认关闭；开启后保存缩略图/原文/译文到本机 SQLite，可在设置关闭或历史窗口删除/清空。
- **调试日志默认关闭**：关闭时不创建、不写入 `logs` 目录；排查问题时在设置 → 常规中开启。日志只记录时间、耗时、错误与 OCR 语言回退，不记录截图、原文、译文或 API Key。

## 测试

无界面自检（覆盖 OCR、渲染、术语保护、源生成 JSON、SQLite 历史上限、UI 冒烟、真实翻译接口可选）：

```powershell
.\publish\green\ShotTrans.exe --selftest

# 无界面核心链路压力测试：OCR + HTTP 翻译 + 覆盖层生命周期 × 100
.\publish\green\ShotTrans.exe --stress 100
```

真实接口测试时通过环境变量临时提供 Key（不会写入配置）：

```powershell
$env:SHOTRANS_TEST_KEY="<key>"
$env:SHOTRANS_TEST_URL="https://tokenhub.tencentmaas.com/v1/chat/completions"
$env:SHOTRANS_TEST_MODEL="hy-mt2-lite"
.\publish\green\ShotTrans.exe --selftest
```

UI 自动化（需要未锁定的交互式桌面、测试页在 Chrome 中）：

```powershell
# 完整交互：框选、Space、复制、点击外部、无文字、划词
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\e2e.ps1
# 译文鼠标拖选 + 右键菜单复制（截图覆盖层 / 划词弹窗）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\select_copy.ps1
# 100 次真实 UI 连续触发稳定性 + 内存/残留窗口
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\stability.ps1 -Cycles 100
# 100 次无界面核心链路压力（OCR + HTTP + 覆盖层生命周期）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\stress.ps1 -Cycles 100
# 首次失败 + R 重试
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\retry.ps1
# 历史记录 + 设置/历史窗口截图
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\history_settings.ps1
# 框选尺寸标签 / Esc、右键取消 / 覆盖层右键关闭
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\selection.ps1
# 副屏 / 跨屏 / 150% DPI
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\multimonitor.ps1
# F-14 排除自身截图观察
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\exclude.ps1
```

UI 测试依赖：
- 未锁定的交互式桌面（RDP 也可）；
- `tests\e2e.ps1` 的 AppDir（默认 `test-artifacts\e2e-app`）中需有 `config.json`：复制 `tests\e2e-config.example.json` 到该位置并填入你自己的翻译接口（交付目录不含任何 Key）；
- 其他脚本使用本地假翻译服务（`tests\fake_server.py`，零费用），并自带内置测试窗口 `ShotTrans.exe --test-page`。

实测摘要（详见 [docs/验收报告.md](docs/验收报告.md)）：
- 绿色版 **49.6 MB**；自检 0 失败；核心链路 100 次 82ms/次、0 错误、0 残留窗口。
- 真实 UI 100 次连续触发：216s，内存 +6.7MB，0 残留窗口，0 错误。
- 译文鼠标拖选 + 右键菜单复制：截图覆盖层（选中 14 字）与划词弹窗（选中 79 字）均与菜单复制结果一致，0 错误。
- 状态条位于选区外；调试日志默认关闭，关闭时不创建/不写入 `logs`。
- 真实接口：OCR 约 140–160ms；10 块翻译约 1.7–1.8s；20 条 1.19–1.24s；端到端约 1.9s。
- 150% DPI、主屏 + 竖屏副屏、副屏框选与跨屏框选均通过。

## 已知限制

- **独占全屏**（Exclusive Fullscreen）游戏无法通过 GDI 截取，会提示改用无边框窗口；这是 Windows 捕获限制。
- **英文 OCR 质量取决于已安装的 Windows OCR 语言包**。程序已做四层补偿：
  1. 按字符间距 + 常见词重组被拆开的单词（`WO rk` → `work`）；
  2. 字符级混淆修正（`°`→`I/O`、`I/l/1`、`0/O`、`5/S` 等 + 常见词词典）：`L°CENSE` → `LICENSE`、`HeIIo` → `Hello`、`ControI` → `Control`、`tO` → `to`、`kerne132` → `kernel32`；
  3. 收紧“代码标识符保护”，不把 `tO`、`ControI`、`HeIIo` 等 OCR 误判当成代码而跳过翻译；全大写常用词（`LICENSE`/`SETTINGS`/`CONTROL`）也可以翻译，`API`/`HTTP` 等缩写仍保护；
  4. 翻译提示词要求模型在翻译前修正明显的 OCR 拆词/大小写错误（不改代码标识符/路径/URL/版本号）。
  设置 → 常规 → **OCR 语言包 → 安装英文 OCR…** 可一键安装 en-US 语言包（需要联网 + UAC 管理员授权），安装后点「刷新语言」并选择 `en-US`（`auto` 也会优先使用 en-US）。未安装时生僻词/代码词的字符级误识别仍可能存在；本测试机仅有 `zh-Hans-CN`。
- **排除自身（F-14）**：已调用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`；但分层窗口在部分 GDI 截图中仍可能被捕获。程序在每次新截图前都会先关闭旧覆盖层，因此不会被自身二次识别。测试构建可用 `SHOTRANS_DEBUG_CAPTURE_OVERLAY=1` 让覆盖层出现在测试截图里。
- **鼠标交互**：为支持译文鼠标选中复制，**译文文字区域**会接收鼠标（拖选 / 右键菜单）；**空白区域**仍然穿透。文字区域的点击不会再传给下方应用；这是文本选择功能所需的行为。
- 多显示器 / 高 DPI 已在 **150% DPI + 双屏（2560×1440 主屏 + 1440×2560 竖屏副屏）** 下实机验证；混合 DPI（两屏不同缩放）未在当前环境验证。
- 划词翻译通过模拟 `Ctrl+C` 取词，会短暂改变剪贴板；完成后恢复**文本**剪贴板（图片等非文本格式不保留）。复制使用 Win32 剪贴板 API + 超时重试，避免被其他剪贴板工具占用时卡死界面。
- 翻译延迟取决于用户配置的接口；程序端不额外串行化，一次请求批量翻译全部文字块。使用提供的腾讯接口实测 20 条约 1.2s。
- 成功状态条 3 秒后淡出；错误条保留供按 `R` 重试。
- 托盘菜单的 NotifyIcon 与菜单项已创建；本机任务栏为自动隐藏，未做托盘右键的自动化点击验证，可按手动清单确认。

## 验收

详见 [docs/验收报告.md](docs/验收报告.md) 与 [docs/手动验收清单.md](docs/手动验收清单.md)。当前环境已完成：构建、自检、体积、隐私、100 次核心压测、100 次 UI 连续触发、真实界面交互（框选/Space/Ctrl+C/点击关闭/无文字/划词）、失败重试、历史/设置窗口、150% DPI、副屏与跨屏。唯一明确偏差：分层窗口的 `WDA_EXCLUDEFROMCAPTURE` 在部分 GDI 截图中无效（程序已用“截图前关闭旧覆盖层”规避二次识别）；混合 DPI 与托盘右键未做自动化实机操作。
