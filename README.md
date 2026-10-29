# EyeCare —— 电脑使用时长护眼提醒

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![Runtime](https://img.shields.io/badge/.NET-Framework%204.8-5C2D91)
![License](https://img.shields.io/badge/license-MIT-green)
![Size](https://img.shields.io/badge/exe-~140%20KB-success)

Windows 桌面小工具：**可循环**提醒休息、**参数随时可改**、**可开机自启动**，
另带**每日累计用机上限**与**闲置自动暂停**（人不在电脑前不会白计时）。

零依赖：只用 Windows 自带的 .NET Framework 4.8 编译器，产物是单个
`EyeCare.exe`（约 140 KB），不需要安装 Python / Node / SDK，也不需要管理员权限。

## 下载

到 [Releases](../../releases) 页下载 `EyeCare.exe`（单文件，双击即用），
或 `git clone` 本仓库后按下一节自行编译 —— 都不需要联网安装任何东西。

## 自己编译
Windows 桌面小工具：**可循环**提醒休息、**参数随时可改**、**可开机自启动**，
另带**每日累计用机上限**与**闲置自动暂停**（人不在电脑前不会白计时）。

零依赖：只用 Windows 自带的 .NET Framework 4.8 编译器，产物是单个
`EyeCare.exe`（约 75 KB），不需要安装 Python / Node / SDK，也不需要管理员权限。

---

## 30 秒上手

1. 双击 **`安装-编译并设置自启动.bat`**
   → 编译出 `EyeCare.exe` → 跑一遍自检 → 设为开机自启动 → 启动到托盘。
2. 托盘图标（绿色眼睛）：
   - **双击** = 打开设置界面改参数
   - **右键** = 暂停 / 立即休息 / 跳过 / 开机自启动 / 退出
3. 想改间隔：编辑 **`config.ini`** 保存即可，约 3 秒自动生效，不用重启。

> 只想临时用一下、不要开机自启：双击 `build.bat` 编译，再双击 `EyeCare.exe`。
> 不想要了：双击 **`卸载-停止并取消自启动.bat`**。

---

## 它怎么工作

两条计时线同时跑，可在 `config.ini` 各自调整：

**① 连续使用循环（参考开源项目 Stretchly）**

```
连续用机 work_min 分钟 → 提前 grace_sec 秒预告 → 全屏遮罩休息 micro_break_min 分钟
   → 再 work_min 分钟 → …… 每攒满 micros_per_long 次小休，安排一次 long_break_min 大休
```

**② 每日累计上限（参考开源项目 Workrave）**

```
当日累计用机达到 daily_limit_min 的 daily_warn_pct% → 气泡提醒还剩多少
达到上限 → daily_lock=false 只发气泡；daily_lock=true 弹遮罩（可延后，不可跳过）
```

**闲置自动暂停**：超过 `idle_timeout_sec` 秒没有键盘鼠标输入，判定为你不在电脑前，
计时冻结、遮罩自动收起，回来后继续。锁屏、注销、睡眠也会立刻收掉遮罩。

**遮罩行为**：所有显示器一起盖住（`cover_taskbar=true` 可连任务栏一起遮），
居中卡片显示倒计时、进度条与护眼建议。两个出口：

| 按钮 | 作用 |
|---|---|
| 延后一会儿 | 收掉遮罩，`postpone_min` 分钟后再提醒；最多 `postpones_max` 次，用完必须做休息 |
| 跳过本次休息 | 立刻回到工作状态，计入「跳过」统计（每日上限遮罩不可跳过） |

按 `Esc` 等效于点跳过（不可跳过时等效于延后）。

---

## 参数速查（config.ini）

| 键 | 默认 | 含义 | 合法区间 |
|---|---|---|---|
| `enabled` | true | 总开关，false 时只统计不提醒 | true/false |
| `idle_timeout_sec` | 120 | 无键鼠输入多少秒算离座 | 15~3600 |
| `work_min` | 45 | 连续用机多少分钟提醒一次 | 0.05~600 |
| `micro_break_min` | 5 | 小休时长 | 0.05~120 |
| `long_break_min` | 20 | 大休时长 | 0.05~240 |
| `micros_per_long` | 4 | 几次小休后一次大休，0=不做大休 | 0~50 |
| `grace_sec` | 20 | 提前几秒预告，0=直接弹遮罩 | 0~600 |
| `postpone_min` | 5 | 「延后」推迟多少分钟 | 0.05~120 |
| `postpones_max` | 2 | 一次提醒最多延后几次，0=不可延后 | 0~20 |
| `daily_limit_min` | 360 | 每日用机上限（分钟） | 1~1440 |
| `daily_warn_pct` | 90 | 到上限的百分之多少先提醒 | 10~100 |
| `daily_reset_at` | 04:00 | 日界，早于此点算前一天（熬夜友好） | 00:00~23:59 |
| `daily_lock` | false | 超限后用遮罩而不只是气泡 | true/false |
| `daily_notify_every_min` | 15 | 超限后每隔多久再提醒一次 | 1~240 |
| `opacity_pct` | 72 | 遮罩不透明度 | 30~95 |
| `font_size` | 30 | 卡片字号（再按屏幕缩放自动放大） | 14~72 |
| `backdrop_color` / `card_color` / `accent_color` | #101826 / #16233A / #4FD1A5 | 遮罩底色 / 卡片底色 / 倒计时色 | #RRGGBB |
| `show_tip` | true | 显示护眼建议文字 | true/false |
| `keep_topmost` | true | 休息时把遮罩顶回最上层 | true/false |
| `cover_taskbar` | false | 连任务栏一起遮住 | true/false |
| `usage_log` | true | 写日志与每日汇总 | true/false |
| `keep_days` | 30 | 汇总保留天数 | 1~3650 |

**写错不会崩**：超范围的值被自动夹紧，无法解析的值回退默认（自检里有专门一条验证）。

常用预设，直接改这几行即可：

```ini
# 20-20-20 护眼法则（每 20 分钟看 6 米外 20 秒）
work_min = 20
micro_break_min = 0.33
grace_sec = 0
micros_per_long = 0

# 温和版：一小时一次，别太打扰
work_min = 60
micro_break_min = 5
long_break_min = 15
micros_per_long = 3

# 严格版：适合正在戒长时间刷屏的
work_min = 30
micro_break_min = 6
grace_sec = 5
postpones_max = 0
daily_limit_min = 240
daily_lock = true
```

---

## 开机自启动

只写当前用户、只加一个值，不动别人的启动项，不需要管理员权限：

```
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
  值名 EyeCare = "…\EyeCare.exe" --min
```

`--min` 表示开机后静默进托盘、不弹欢迎气泡。三种开关方式：

- 托盘右键 → 「开机自动启动」（勾选状态实时反映注册表）
- 设置界面底部勾选
- 命令行 `EyeCare.exe --enable-autostart` / `--disable-autostart`

查看当前会写入什么（不写任何东西）：`EyeCare.exe --autostart-command`

---

## 命令行

```
EyeCare.exe                        启动（托盘常驻）
EyeCare.exe --min                  启动并最小化（开机自启动用的就是这个）
EyeCare.exe --demo-break [kind]    预览休息遮罩，kind = micro|long|daily，配 --secs 15
EyeCare.exe --today                打印今日统计
EyeCare.exe --report 7             打印最近 7 天统计（同时写 report.txt）
EyeCare.exe --enable-autostart     开启开机自启动
EyeCare.exe --disable-autostart    关闭开机自启动
EyeCare.exe --autostart-status     查看自启动状态
EyeCare.exe --autostart-command    打印将要写入的启动命令（只读）
EyeCare.exe --reset-stats          清空统计与日志
EyeCare.exe --stop                 退出正在运行的实例
EyeCare.exe --selftest             计时状态机自检（15 项，写 selftest-report.txt）
EyeCare.exe --help                 帮助
```

> 注意：程序是 GUI 子系统（无黑框），在 cmd 里直接运行不会等待它。
> 想让输出留在屏幕上，用 `start /wait "" EyeCare.exe --help`，
> 或 PowerShell 的 `Start-Process -Wait -RedirectStandardOutput out.txt`。
> 无控制台时结果仍会写入 `cli-last.txt` / `report.txt` / `selftest-report.txt`。

---

## 记录与隐私

全部留在本机本目录，不联网、不上传、无账号：

- `log/usage.log` —— 事件流：启动/退出、遮罩弹出与结束、延后/跳过、配置热重载、每日超限
- `log/daily.csv` —— 每日汇总：用机分钟、休息分钟、小休/大休/跳过/延后次数，可直接用 Excel 打开
- 托盘「今日与最近统计…」或 `--today` / `--report` 查看

---

## 自己改代码

单文件 `EyeCare.cs`（约 2500 行），改完跑 `build.bat` 即可，它会编译并自动执行自检。

| 文件 | 作用 |
|---|---|
| `EyeCare.cs` | 全部源码：Win32 互操作、配置、状态机、遮罩 UI、设置界面、自启动、CLI、自检 |
| `app.manifest` | asInvoker 权限 + PerMonitorV2 DPI 声明（高分屏不糊） |
| `build.bat` | 调系统 csc.exe 编译 + 自检 |
| `config.ini` | 参数（删掉会自动重新生成默认值） |

结构（按源码内分节注释）：

```
0 Win32        1 路径        2 配置        3 每日统计/日志
4 计时状态机（Engine，与界面完全解耦，可离线仿真）
5 遮罩 UI      6 开机自启动  7 设置界面    8 统计窗口
9 托盘常驻     10 入口/CLI   11 自检
```

**改逻辑先看 `Engine`**：它只暴露 `Step(dt, present)` 一个推进函数，
不碰任何控件，所以 `--selftest` 能用假时钟把它整跑一遍。
新增提醒规则 → 改 Engine；新增界面元素 → 改 BreakCard / OverlayView；
新增参数 → Config 里加字段 + Apply/Save 两行 + 设置界面一行。

```bat
build.bat            :: 编译 + 15 项状态机自检
EyeCare.exe --demo-break long --secs 8   :: 看大休遮罩长什么样
```

---

## 已知边界

- 计时基于**键鼠输入**判断在场，所以「盯着屏幕看视频但不碰键鼠」超过
  `idle_timeout_sec` 会被判离座而暂停计时 —— 这是刻意的（人不在才算超时）。
  看视频为主就把 `idle_timeout_sec` 调大，例如 900。
- 遮罩是普通窗口 + `TopMost`，不是系统级锁屏：可以按 Alt+Tab 切走。
  想更强硬就把 `postpones_max` 设 0、`daily_lock` 设 true。
- 单实例锁用命名 Mutex，第二个实例会把已运行实例的设置窗口叫到前台后自行退出。
- 多显示器混合 DPI 时遮罩按各屏 Bounds 铺满，卡片按光标所在屏居中。
- 休眠恢复后按挂钟差续算，超过 120 秒的间隔按 1 秒处理，避免「睡一晚回来立刻弹遮罩」。

## 参考

设计口径取自两个高星开源项目（本机无法联网，未核对当前星数）：
**Stretchly**（Electron，循环式 mini/micro break）、**Workrave**（GTK，
当日工时上限 + 闲置检测 + 严格程度分级）。本项目用零依赖的 .NET WinForms
复刻这两条主线，并加上每日上限与循环并存、参数热重载、多屏遮罩。
