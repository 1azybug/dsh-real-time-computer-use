# CuHelper —— Windows 侧采集/注入 helper

DSH Computer Use 的 Windows 半边。**常驻进程**，与调用方（WSL 侧的 DSH 插件）用
**stdin/stdout 一行一条 JSON** 通信：一行请求进、一行响应出。

## 编译（Windows 自带编译器，无需 SDK）

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe ^
  /r:System.Drawing.dll /out:CuHelper.exe CuHelper.cs
```

⚠️ **只能写 C# 5**：字符串插值 `$""`、`?.`、`nameof`、表达式体成员都会编译失败。
⚠️ `Encoder` 在 `System.Drawing.Imaging` 与 `System.Text` 之间歧义，要写全名
`System.Drawing.Imaging.Encoder.Quality`。

## 运行

```
CuHelper.exe [保留帧数]        # 默认 400
```

从 WSL 一侧可直接执行（interop），或用管道喂 JSON：

```
printf '{"cmd":"ping"}\n{"cmd":"quit"}\n' | ./CuHelper.exe
```

## 时间戳口径（重要）

- **所有时间戳都是 Windows 侧 QPC**（`QueryPerformanceCounter`），零点 = **进程启动时刻**，单位**秒**（保留 3 位）。
- **绝不用 WSL 侧的时钟做任何时间推算**：本机 WSL 的 `CLOCK_MONOTONIC` 比真实时间**慢约 10%**
  （外部基准定标：−10.02% / −8.81%），而 Windows QPC、Windows 墙钟、WSL 墙钟都是准的。
  跨系统混用会造成 10% 量级的系统性偏差。
- 帧记录 `t`（抓屏开始）与 `t_after`（抓屏结束）**两个**时间戳：画面内容对应的显示时刻夹在
  这两点之间，具体取值由**标定**决定，helper 不替调用方做假设。

## 命令

### 信息

| cmd | 说明 |
|---|---|
| `ping` | 返回协议版本、当前 QPC 秒、帧输出目录、屏幕尺寸（主屏与虚拟桌面） |
| `windows` | **枚举**可见且有标题的顶层窗口：`title`/`class`/`x`/`y`/`w`/`h`/`pid`/`enabled`/`foreground`/`z`/`minimized`/`owner`/`modal`。`z` 是 z 序（0 = 最上层，被跳过的窗口也占号，所以稀疏但有序）；`modal` = Win32 对话框类 `#32770` 或**属主窗口被禁用**（模态的定义就是属主收不到输入） |
| `foreground` | 当前前台窗口的标题与类名 |
| `window_under` | 参数 `x`、`y`：该坐标上的窗口（标题与类名） |

### 单帧采集

| cmd | 参数 | 说明 |
|---|---|---|
| `capture` | `quality`(默认 70)、`format`(`jpg`\|`png`) | 抓一帧到临时目录，返回路径 + 尺寸 + 双时间戳 + 字节数 |
| `region` | `x`、`y`、`w`、`h`、`quality`、`scale`(默认 1) | 裁一块（用于"精修"路径；裁块 ≤640000 px 时给模型不触发缩放）。`scale` 是**整数倍最近邻放大**：把更小的区域铺满同一份像素预算。倍数与区域要联动——`w×h×scale²` 超过模型侧预算时放大出来的图会被缩回去，等于白放大（本机 2560×1440：266×150 的 4× = 1064×600 = 638400 px，刚好卡住） |
| `grid` | `cols`、`rows`、`quality`、`scale` | **一次抓帧**切成 cols×rows 块并全部落盘，返回每块的**屏幕矩形**（`x`/`y`/`w`/`h`）与路径。与连续调 N 次 `region` 的区别是**同帧**——后者各抓各的帧，在滚动/动画/视频里拼出来的画面现实中并不存在 |
| `bench` | `frames`、`quality` | **不开线程、不写盘**，只量「抓屏」与「JPEG 编码」各自的耗时，用来判断后端能力上限 |

### 持续采集（流式录屏 + 按时间戳回看）

| cmd | 参数 | 说明 |
|---|---|---|
| `live_start` | `interval_ms`（默认 33，**≤0 表示全速不限速**）、`quality`、`capacity`（默认 1800） | 启动常驻采集线程 |
| `live_stop` | — | 停采集与编码线程 |
| `live_stats` | — | 帧数、跨度、**实测 fps**、总字节、目标间隔、容量 |
| `latest` | — | 最新一帧的路径与时间戳 |
| `frames` | `from`、`to`、`limit` | **按帧时间戳区间取帧**（回看用） |

采集线程按固定间隔抓屏进双缓冲的某一格，**编码线程**处理另一格 —— 两者重叠，把 5.1 ms 的
编码从帧间隔里藏掉。编码跟不上时通过信号量形成背压（宁可掉帧，不排队堆积）。

### 注入（全权限；坐标一律是**物理像素**）

| cmd | 参数 | 说明 |
|---|---|---|
| `cursor` | — | 读光标位置 |
| `mouse_move` | `x`、`y` | **绝对**移动（按虚拟桌面归一化到 0..65535） |
| `mouse_move_by` | `dx`、`dy` | **相对**移动 —— **校准显示它比绝对坐标更准**（绝对误差随"离光标距离"变大，相对基本无关） |
| `click` | `button`(`left`/`right`/`middle`)、`action`(`down`/`up`/`click`)、`count` | 在当前位置点击/按下/抬起 |
| `scroll` | `vertical`、`horizontal` | 滚轮（单位为"格"，内部乘 120） |
| `key` | `name`、`action`(`down`/`up`/`press`) | 单键；键名见 `KeyTable` |
| `hotkey` | `keys`（数组或 `a+b+c` 串） | 组合键：按顺序按下、逆序抬起 |
| `type_text` | `text` | **`KEYEVENTF_UNICODE` 逐字符注入**——不受键盘布局影响，中文与 emoji 都能输入，且不碰剪贴板 |

**系统级限制**：`Ctrl+Alt+Del` 属安全注意序列，普通进程无法注入（Windows 硬限制）。
其余（Win 键、Alt+Tab、多键组合、拖拽、滚轮、多显示器整屏）均可。

### 生命周期

| cmd | 说明 |
|---|---|
| `quit` | 停采集线程后退出 |

## 已知限制

- **DPI**：helper 启动时声明 per-monitor DPI aware。本机实测为 100%（无虚拟化），但换到
  125%/150% 的机器上，这一点是坐标正确的前提。
- **屏幕**：目前按主屏抓取（`SM_CXSCREEN×SM_CYSCREEN`）；多屏拼接需改用虚拟桌面尺寸
  （`SM_*VIRTUALSCREEN`，注入的绝对坐标已经按虚拟桌面归一化）。
- **帧交付**：帧写到 Windows 临时目录（`%TEMP%\dsh-cu`），调用方经 `/mnt/c/...` 读取。
  大块 base64 塞进 JSON 行不划算。环形缓冲按 `capacity` 滚动删除最旧的帧文件。
- **存储量**：2560×1440 JPEG q70 约 **312 KB/帧** ⇒ 30 fps 跑 20 分钟约 **11 GB**，
  放不下内存，必须靠 H.264 编码或降分辨率（见设计稿的存储方案）。

## 采集后端

| 后端 | 状态 | 实测 |
|---|---|---|
| **GDI**（`Graphics.CopyFromScreen`） | 已实现，回退路径 | 上限 **29.9 fps**（抓屏 28.1 ms + 编码 5.1 ms），30 fps 目标卡在边缘、**没有余量**；占单核 50.8% |
| **DXGI Desktop Duplication** | 已实现，**部署默认**（`cordis.patch.yml` 的 `backend: dxgi`） | 30 fps 目标（`interval_ms` 33）实测 **30.85 fps**（6.45 s / 200 帧）；读帧 0.42 ms、30 fps 时占单核 44.1% |

⚠️ **瓶颈不在采集**：DXGI 把读帧从 28.9 ms 压到 0.42 ms，30 fps 的 CPU 却只从 50.8% 降到 44.1%——
大头是 **JPEG 编码（约 7.5 ms/帧）与每帧写盘**（约 167 KB/帧 ⇒ 30 fps ≈ 5 MB/s）。所以要再省 CPU，
得改帧的存放方式（内存环形缓冲、只在被取时才落盘），而不是继续换采集后端。

DXGI 的两个前提已在真机验证：`CreateDXGIFactory1` + 有输出的适配器 + `D3D11CreateDevice(BGRA_SUPPORT)`
→ `IDXGIOutput1::DuplicateOutput` 成功。
⚠️ **声明 `IDXGIOutput1` 时绝不能漏 `GetDisplaySurfaceData1`**（它在 `DuplicateOutput` 之前），
否则调用错位、返回 `DXGI_ERROR_INVALID_CALL`，而该错误码不在 MSDN 的返回值表里、极易被误判成"驱动不支持"。
