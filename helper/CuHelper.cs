// CuHelper.cs — DSH Computer Use 的 Windows 侧常驻 helper（M1：采集骨架）
//
// 设计要点（对应 DESIGN.md §3/§4/§5）：
//   · 常驻进程，stdin/stdout 一行一条 JSON（请求/响应），避免每次调用重启的进程开销。
//   · 时间戳统一走 QPC（QueryPerformanceCounter），零点是进程启动时刻，单位秒。
//     **绝不按名义帧间隔累加**——抓屏耗时本身有抖动，累加会漂移。
//   · 抓屏是「采集前 / 采集后」两个时间戳都记录：内容对应的是显示管线里的某一刻，
//     夹在这两点之间；具体取值由标定决定，helper 不替调用方做假设。
//   · 屏幕尺寸每次现读系统值，不缓存、不硬编码（换分辨率/换机器自动跟随）。
//   · 帧写到 Windows 临时目录，返回路径；调用方（WSL 侧）经 /mnt/c 读取，
//     避免把 MB 级 base64 塞进 JSON 行。
//
// 编译（Windows 自带编译器，无需 SDK）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:exe /r:System.Drawing.dll /out:CuHelper.exe CuHelper.cs
//
// 语法刻意停在 C# 5（不用字符串插值 / ?. / nameof），以适配自带的 csc。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class CuHelper
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long value);
    [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    /// <summary>虚拟键 ↔ 扫描码换算；MAPVK_VK_TO_VSC = 0。</summary>
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private delegate bool EnumWindowProc(IntPtr window, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION data;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    /// <summary>GetWindow 的取属主参数。有属主的窗口多半是对话框或工具窗，而不是常规主窗口。</summary>
    private const uint GW_OWNER = 4;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 5;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint WHEEL_DELTA = 120;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int PER_MONITOR_DPI_AWARE = 2;

    private static long freq;
    private static long origin;
    private static string outDir;
    private static int seq;
    private static readonly ImageCodecInfo JpegCodec = FindJpegCodec();
    // 复用抓屏缓冲：每帧 new Bitmap(2560×1440) 要分配 14.7 MB，分配与 GC 是 30fps 目标下的真实开销。
    private static Bitmap buffer;
    private static Graphics bufferGraphics;
    private static int bufferWidth;
    private static int bufferHeight;

    private static Bitmap EnsureBuffer(int width, int height)
    {
        if (buffer != null && bufferWidth == width && bufferHeight == height) return buffer;
        if (bufferGraphics != null) bufferGraphics.Dispose();
        if (buffer != null) buffer.Dispose();
        buffer = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        bufferGraphics = Graphics.FromImage(buffer);
        bufferWidth = width;
        bufferHeight = height;
        return buffer;
    }

    private static double Now()
    {
        long value;
        QueryPerformanceCounter(out value);
        return (double)(value - origin) / (double)freq;
    }

    private static ImageCodecInfo FindJpegCodec()
    {
        foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
        }
        return null;
    }

    private static string F(double value)
    {
        return value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static string Esc(string text)
    {
        if (text == null) return "";
        StringBuilder sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("?");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>极简 JSON 字段读取：只支持本协议用到的字符串、数字与布尔字面量。</summary>
    private static string Field(string json, string key, string fallback)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return fallback;
        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return fallback;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length) return fallback;
        if (json[i] == '"')
        {
            StringBuilder sb = new StringBuilder();
            i++;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
                    sb.Append(json[i] == 'n' ? '\n' : json[i] == 't' ? '\t' : json[i]);
                }
                else sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }
        int start = i;
        while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
        return json.Substring(start, i - start).Trim();
    }

    /// <summary>
    /// 读一个字符串数组字段：`["ctrl","c"]` 形态，或 `ctrl+c` 串（协议承诺两种都收）。
    /// 不能用 `Field()` 代替：它遇到数组会在第一个逗号处截断，三个键以上的组合键会被吃掉。
    /// </summary>
    private static string[] StringArrayField(string json, string key)
    {
        System.Collections.Generic.List<string> items = new System.Collections.Generic.List<string>();
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return items.ToArray();
        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return items.ToArray();
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i < json.Length && json[i] == '[')
        {
            int end = json.IndexOf(']', i);
            if (end < 0) end = json.Length;
            int p = i + 1;
            while (p < end)
            {
                while (p < end && (char.IsWhiteSpace(json[p]) || json[p] == ',')) p++;
                if (p >= end) break;
                if (json[p] == '"')
                {
                    p++;
                    StringBuilder sb = new StringBuilder();
                    while (p < end && json[p] != '"')
                    {
                        if (json[p] == '\\' && p + 1 < end) { p++; sb.Append(json[p]); }
                        else sb.Append(json[p]);
                        p++;
                    }
                    if (p < end) p++;
                    items.Add(sb.ToString());
                }
                else
                {
                    int start = p;
                    while (p < end && json[p] != ',') p++;
                    items.Add(json.Substring(start, p - start).Trim());
                }
            }
            return items.ToArray();
        }
        string[] parts = Field(json, key, "").Split(new char[] { '+' });
        for (int k = 0; k < parts.Length; k++)
        {
            string part = parts[k].Trim().Trim('"');
            if (part.Length > 0) items.Add(part);
        }
        return items.ToArray();
    }

    private static int IntField(string json, string key, int fallback)
    {
        int parsed;
        string raw = Field(json, key, null);
        if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
        return fallback;
    }

    private static double DoubleField(string json, string key, double fallback)
    {
        double parsed;
        string raw = Field(json, key, null);
        if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
        return fallback;
    }

    // ── 注入 ──────────────────────────────────────────────────────────────
    // 全权限目标：键鼠能力对齐人类。唯一系统级限制是 Ctrl+Alt+Del（安全注意序列，
    // 普通进程无法注入）。坐标一律是**物理像素**（helper 已声明 per-monitor DPI aware）。
    // 相对移动（mouse_move_by）是一等公民：校准显示绝对坐标的误差随「离光标距离」显著
    // 变大，相对位移则基本与距离无关。

    private static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].data.mouse.dx = dx;
        inputs[0].data.mouse.dy = dy;
        inputs[0].data.mouse.mouseData = data;
        inputs[0].data.mouse.dwFlags = flags;
        inputs[0].data.mouse.time = 0;
        inputs[0].data.mouse.dwExtraInfo = IntPtr.Zero;
        if (SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) != 1)
            throw new InvalidOperationException("SendInput 拒绝了鼠标事件");
    }

    // 最近一次 SendInput 的结果：回执带上"注入了几条事件 + 失败时的 GetLastError"，才能区分
    // 「根本没注入」与「注入了但没人响应」——2026-09-22 排查 VK 通道失效时就卡在这个分辨力上。
    private static uint lastInjectCount;
    private static int lastInjectError;

    /// <summary>虚拟键 → 物理扫描码；换算不出来时返回 0。</summary>
    private static ushort ScanCode(ushort vk)
    {
        return (ushort)(MapVirtualKey(vk, 0) & 0xFF);
    }

    /// <summary>
    /// 前台窗口的输入法是否开着（1 = 中文/组合态，0 = 直通/英文，-1 = 查不到）。
    ///
    /// 为什么键盘回执要带它：IME 开着时，合成注入的**字母键与方向键会被输入法截获**去做拼音组合，
    /// 目标窗口只收得到 keyup、收不到 keydown（2026-09-22 用记录 keydown 序列的探针页面实测）——
    /// 表现与"按键根本没注入"极像，而补救动作完全不同。带上它才能一眼分辨。
    /// </summary>
    private static int ImeOpenStatus()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        IntPtr ime = ImmGetDefaultIMEWnd(hwnd);
        if (ime == IntPtr.Zero) return -1;
        IntPtr result = SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero);
        return result.ToInt32();
    }

    private static void SendKeyRaw(ushort vk, ushort scan, uint flags)
    {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].data.keyboard.wVk = vk;
        inputs[0].data.keyboard.wScan = scan;
        inputs[0].data.keyboard.dwFlags = flags;
        inputs[0].data.keyboard.time = 0;
        inputs[0].data.keyboard.dwExtraInfo = IntPtr.Zero;
        lastInjectCount = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        lastInjectError = lastInjectCount == 1 ? 0 : Marshal.GetLastWin32Error();
        if (lastInjectCount != 1)
            throw new InvalidOperationException("SendInput 拒绝了键盘事件（winerr=" + lastInjectError + "）");
    }

    private static readonly System.Collections.Generic.Dictionary<string, ushort> KeyTable = BuildKeyTable();

    private static System.Collections.Generic.Dictionary<string, ushort> BuildKeyTable()
    {
        System.Collections.Generic.Dictionary<string, ushort> map =
            new System.Collections.Generic.Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = (ushort)(0x41 + (c - 'a'));
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = (ushort)(0x30 + (c - '0'));
        map["backspace"] = 0x08; map["tab"] = 0x09; map["enter"] = 0x0D; map["return"] = 0x0D;
        map["shift"] = 0x10; map["ctrl"] = 0x11; map["control"] = 0x11; map["alt"] = 0x12;
        map["pause"] = 0x13; map["capslock"] = 0x14; map["esc"] = 0x1B; map["escape"] = 0x1B;
        map["space"] = 0x20; map["pageup"] = 0x21; map["pagedown"] = 0x22; map["end"] = 0x23;
        map["home"] = 0x24; map["left"] = 0x25; map["up"] = 0x26; map["right"] = 0x27; map["down"] = 0x28;
        map["printscreen"] = 0x2C; map["insert"] = 0x2D; map["delete"] = 0x2E; map["del"] = 0x2E;
        map["win"] = 0x5B; map["lwin"] = 0x5B; map["rwin"] = 0x5C; map["apps"] = 0x5D;
        map["num0"] = 0x60; map["num1"] = 0x61; map["num2"] = 0x62; map["num3"] = 0x63; map["num4"] = 0x64;
        map["num5"] = 0x65; map["num6"] = 0x66; map["num7"] = 0x67; map["num8"] = 0x68; map["num9"] = 0x69;
        map["multiply"] = 0x6A; map["add"] = 0x6B; map["subtract"] = 0x6D; map["decimal"] = 0x6E; map["divide"] = 0x6F;
        for (int i = 1; i <= 24; i++) map["f" + i] = (ushort)(0x70 + i - 1);
        map["numlock"] = 0x90; map["scrolllock"] = 0x91;
        map["semicolon"] = 0xBA; map["equal"] = 0xBB; map["comma"] = 0xBC; map["minus"] = 0xBD;
        map["period"] = 0xBE; map["slash"] = 0xBF; map["grave"] = 0xC0;
        map["bracketleft"] = 0xDB; map["backslash"] = 0xDC; map["bracketright"] = 0xDD; map["quote"] = 0xDE;
        return map;
    }

    private static ushort ResolveKey(string name)
    {
        ushort vk;
        if (name != null && KeyTable.TryGetValue(name.Trim(), out vk)) return vk;
        throw new InvalidOperationException("unknown key name: " + name);
    }

    private static bool IsExtendedKey(ushort vk)
    {
        // 方向键、Ins/Del/Home/End/PgUp/PgDn、右 Ctrl/Alt、Win 键需要扩展位，否则应用收不到正确键值。
        return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || vk == 0x25 || vk == 0x26
            || vk == 0x27 || vk == 0x28 || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D;
    }

    private static string CursorState()
    {
        POINT point;
        if (!GetCursorPos(out point)) throw new InvalidOperationException("GetCursorPos 失败");
        return "{\"ok\":true,\"x\":" + point.X + ",\"y\":" + point.Y + ",\"t\":" + F(Now()) + "}";
    }

    private static string MouseMoveAbsolute(int x, int y)
    {
        // 绝对坐标按**虚拟桌面**归一化到 0..65535（Windows 的约定），多屏拼接也成立。
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (x < vx || y < vy || x >= vx + vw || y >= vy + vh)
            throw new InvalidOperationException("坐标超出屏幕范围：" + x + "," + y);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        double before = Now();
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, nx, ny, 0);
        return "{\"ok\":true,\"mode\":\"absolute\",\"x\":" + x + ",\"y\":" + y
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    /// <summary>
    /// 光标下那个窗口（标题 + 类名）。动作回执用它回答"这一下点到谁身上了"——没有它，
    /// "点了没反应"只能靠再截一张图来推断，那要多花 3–6 秒。也是诊断"某处到底有没有窗口"的手段。
    /// </summary>
    private static string WindowUnder(int x, int y)
    {
        POINT point;
        point.X = x;
        point.Y = y;
        return DescribeWindow(WindowFromPoint(point));
    }

    private static string DescribeWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return "null";
        StringBuilder title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        StringBuilder cls = new StringBuilder(256);
        GetClassName(window, cls, cls.Capacity);
        return "{\"title\":\"" + Esc(title.ToString()) + "\",\"class\":\"" + Esc(cls.ToString()) + "\"}";
    }

    // 枚举用的静态状态：EnumWindows 的回调不携带数据，结果累积在静态字段里（单线程调用）。
    private static StringBuilder windowList;
    private static int windowCount;
    private static int windowZ;
    private static IntPtr foregroundHandle;

    private static bool CollectWindow(IntPtr window, IntPtr param)
    {
        // z 是 EnumWindows 的访问序号：它按 z 序自顶向下，所以 0 就是最上层——这是回答
        // "谁盖在我看的东西上面"最直接的字段，而被跳过的窗口也要计数，否则序号会失真。
        int z = windowZ++;
        try
        {
            if (!IsWindowVisible(window)) return true;
            RECT rect;
            if (!GetWindowRect(window, out rect)) return true;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 8 || height < 8) return true;
            StringBuilder title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            string text = title.ToString();
            // 无标题的顶层窗口多是输入法/工具提示一类的噪音，列出来只会淹没有用的信息。
            if (text.Length == 0) return true;
            StringBuilder cls = new StringBuilder(256);
            GetClassName(window, cls, cls.Capacity);
            string className = cls.ToString();
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            // 模态判据：Win32 标准对话框类名，或"属主窗口已被禁用"——后者正是模态的含义
            // （属主收不到输入）。只给 owner 布尔值不够：它是判断"谁是挡路那一个"的依据。
            IntPtr ownerWindow = GetWindow(window, GW_OWNER);
            bool modal = className == "#32770"
                || (ownerWindow != IntPtr.Zero && !IsWindowEnabled(ownerWindow));
            if (windowCount > 0) windowList.Append(",");
            windowList.Append("{\"title\":\"" + Esc(text) + "\"")
                .Append(",\"class\":\"" + Esc(className) + "\"")
                .Append(",\"x\":").Append(rect.Left).Append(",\"y\":").Append(rect.Top)
                .Append(",\"w\":").Append(width).Append(",\"h\":").Append(height)
                .Append(",\"pid\":").Append(processId)
                .Append(",\"enabled\":").Append(IsWindowEnabled(window) ? "true" : "false")
                .Append(",\"foreground\":").Append(window == foregroundHandle ? "true" : "false")
                .Append(",\"z\":").Append(z)
                .Append(",\"minimized\":").Append(IsIconic(window) ? "true" : "false")
                .Append(",\"owner\":").Append(ownerWindow != IntPtr.Zero ? "true" : "false")
                .Append(",\"modal\":").Append(modal ? "true" : "false")
                .Append("}");
            windowCount++;
        }
        catch (Exception)
        {
            // 枚举期间窗口可能已被销毁：跳过这一个，不要中断整次枚举。
        }
        return true;
    }

    /// <summary>
    /// 列出屏幕上所有**可见且有标题**的顶层窗口（标题、类名、矩形、进程号、是否前台、z 序、
    /// 是否最小化、是否模态对话框）。
    ///
    /// 它补的是「枚举」这一环：动作回执只说"我点到了哪"，观察只说"我看到了什么"，
    /// 而 agent 常常需要在**没看到**的情况下知道"屏幕上还有哪些窗口"——尤其是浮在最上层、
    /// 把内容挡住的原生对话框。看不到对话框时问它一句，就知道它在不在、在哪、有多大。
    /// </summary>
    private static string ListWindows()
    {
        windowList = new StringBuilder();
        windowCount = 0;
        windowZ = 0;
        foregroundHandle = GetForegroundWindow();
        double before = Now();
        EnumWindows(CollectWindow, IntPtr.Zero);
        return "{\"ok\":true,\"count\":" + windowCount
            + ",\"t_before\":" + F(before) + ",\"t\":" + F(Now())
            + ",\"windows\":[" + windowList.ToString() + "]}";
    }

    /// <summary>
    /// 相对移动：**读当前位置 → 算出目标 → 绝对移动**，并回读落点。
    ///
    /// 为什么不直接发 MOUSEEVENTF_MOVE：本机开着「提高指针精确度」（鼠标加速），系统会给相对
    /// 位移套一条曲线——实测请求 dx=10/40/120/400 分别走出 9/67/300/801 px，比例 0.90→2.50
    /// 还随位移变化，**无法用系数补偿**；而绝对移动不受加速影响（逐点误差 0）。
    /// 所以"相对"在本机只能这样实现：语义仍是相对位移，落点由绝对移动保证。
    /// 越出屏幕的目标收敛到虚拟桌面边界（系统本来也会收敛，这里让它可预期）。
    /// </summary>
    private static string MouseMoveRelative(int dx, int dy)
    {
        POINT origin;
        if (!GetCursorPos(out origin)) throw new InvalidOperationException("GetCursorPos 失败");
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int targetX = Math.Min(Math.Max(origin.X + dx, vx), vx + vw - 1);
        int targetY = Math.Min(Math.Max(origin.Y + dy, vy), vy + vh - 1);
        int nx = (int)Math.Round((targetX - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((targetY - vy) * 65535.0 / (vh - 1));
        double before = Now();
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, nx, ny, 0);
        double after = Now();
        POINT landed;
        string hit = "";
        if (GetCursorPos(out landed))
            hit = ",\"x_after\":" + landed.X + ",\"y_after\":" + landed.Y;
        return "{\"ok\":true,\"mode\":\"relative\",\"dx\":" + dx + ",\"dy\":" + dy
            + ",\"from_x\":" + origin.X + ",\"from_y\":" + origin.Y
            + ",\"target_x\":" + targetX + ",\"target_y\":" + targetY + hit
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(after) + "}";
    }

    private static uint ButtonFlag(string button, bool down)
    {
        if ("right".Equals(button, StringComparison.OrdinalIgnoreCase))
            return down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
        if ("middle".Equals(button, StringComparison.OrdinalIgnoreCase))
            return down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
        return down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
    }

    private static string MouseButton(string button, string action, int count)
    {
        // 落点必须在**注入之前**读：`click` 不移动光标，用的就是当时那个位置。
        // 注入之后再读没有意义——**光标是全局唯一资源**，别的会话（或用户本人）一次 mouse_move
        // 就能把它挪走，那时读到的位置不能证明"点在哪"（2026-09-22 千瞳指出：并发时该字段
        // 会被当成落点验证，属误导）。
        POINT landed;
        bool hasLanded = GetCursorPos(out landed);
        double before = Now();
        if (action == "down") SendMouse(ButtonFlag(button, true), 0, 0, 0);
        else if (action == "up") SendMouse(ButtonFlag(button, false), 0, 0, 0);
        else
        {
            int times = count <= 0 ? 1 : count;
            for (int i = 0; i < times; i++)
            {
                SendMouse(ButtonFlag(button, true), 0, 0, 0);
                SendMouse(ButtonFlag(button, false), 0, 0, 0);
            }
        }
        double after = Now();
        // 回执：点在**哪里**（`x_landed`/`y_landed`，注入前读）、那一点上是**哪个窗口**。
        // 鼠标合成点击对某些窗口无效（原生模态框、以更高权限运行的窗口），有这两项才能立刻分辨
        // "点空了"与"被目标忽略了"，而不是再截一张图猜（那是 3–6 秒）。
        // `x_after`/`y_after` 保留，但语义**只是"读回这一刻光标在哪"**，不能当落点验证。
        string hit = "";
        if (hasLanded)
            hit = ",\"x_landed\":" + landed.X + ",\"y_landed\":" + landed.Y
                + ",\"window\":" + WindowUnder(landed.X, landed.Y);
        POINT point;
        if (GetCursorPos(out point))
            hit += ",\"x_after\":" + point.X + ",\"y_after\":" + point.Y;
        return "{\"ok\":true,\"button\":\"" + Esc(button) + "\",\"action\":\"" + Esc(action) + "\""
            + hit + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(after) + "}";
    }

    /// <summary>
    /// 拖拽：按下 → **分段移动** → 抬起。
    ///
    /// 分段不是装饰：一次发一个大位移，应用会把中间过程当成"跳变"而丢掉 drag 语义
    /// （拖动滚动条、画图、游戏、拖放到目标上尤其明显）。人类的手也是连续移动的，
    /// 所以这里按 steps 拆成若干小步、每步之间留出 stepDelayMs，让接收方看得到轨迹。
    /// </summary>
    private static string Drag(int dx, int dy, string button, int steps, int stepDelayMs)
    {
        if (steps <= 0) steps = 12;
        if (stepDelayMs < 0) stepDelayMs = 10;
        double before = Now();
        SendMouse(ButtonFlag(button, true), 0, 0, 0);
        int sentX = 0, sentY = 0;
        for (int i = 1; i <= steps; i++)
        {
            int targetX = dx * i / steps;
            int targetY = dy * i / steps;
            SendMouse(MOUSEEVENTF_MOVE, targetX - sentX, targetY - sentY, 0);
            sentX = targetX;
            sentY = targetY;
            if (stepDelayMs > 0) Thread.Sleep(stepDelayMs);
        }
        SendMouse(ButtonFlag(button, false), 0, 0, 0);
        return "{\"ok\":true,\"dx\":" + dx + ",\"dy\":" + dy
            + ",\"button\":\"" + Esc(button) + "\",\"steps\":" + steps
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    private static string Scroll(int vertical, int horizontal)
    {
        double before = Now();
        if (vertical != 0) SendMouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)(vertical * (int)WHEEL_DELTA));
        if (horizontal != 0) SendMouse(MOUSEEVENTF_HWHEEL, 0, 0, (uint)(horizontal * (int)WHEEL_DELTA));
        return "{\"ok\":true,\"vertical\":" + vertical + ",\"horizontal\":" + horizontal
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    private static string KeyEvent(string name, string action, int holdMs)
    {
        ushort vk = ResolveKey(name);
        // 扫描码必须一起给：浏览器 canvas 与游戏常按物理扫描码取值，wScan=0 的注入会被它们忽略
        // （2026-09-22：同一时刻 type_text 有效而 press_key 全灭，分叉点就在这条通道）。
        ushort scan = ScanCode(vk);
        uint extended = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
        int imeOpen = ImeOpenStatus();
        double before = Now();
        bool press = action != "down" && action != "up";
        int downCount = 0;
        int upCount = 0;
        if (action == "down")
        {
            SendKeyRaw(vk, scan, extended);
            downCount = (int)lastInjectCount;
        }
        else if (action == "up")
        {
            SendKeyRaw(vk, scan, extended | KEYEVENTF_KEYUP);
            upCount = (int)lastInjectCount;
        }
        else
        {
            // 按下与抬起之间必须留一点时间：零间隔的 down+up 会被按 requestAnimationFrame 轮询按键状态的页面
            // 整帧错过（2026-09-22 评测页 C2 Sky Hop 同页对照：press_key 零响应 injected 全绿，
            // 而 key_state down → wait(80) → up 立即生效）。人类点击的按下时长本就在 50~100ms 量级，
            // 所以默认 50ms 不改变"点一下"的语义；需要严格零间隔的调用方显式传 holdMs=0。
            SendKeyRaw(vk, scan, extended);
            downCount = (int)lastInjectCount;
            if (holdMs > 0) Thread.Sleep(holdMs);
            SendKeyRaw(vk, scan, extended | KEYEVENTF_KEYUP);
            upCount = (int)lastInjectCount;
        }
        return "{\"ok\":true,\"key\":\"" + Esc(name) + "\",\"action\":\"" + Esc(action) + "\""
            + ",\"vk\":" + vk + ",\"scan\":" + scan
            // injected 是**本次事件总数**（旧版误用"最后一次 SendInput 的返回值"，恒为 1，
            // 让人把"回执全绿"读成"注入完整"）；events 再给出 down/up 各几个。
            + ",\"injected\":" + (downCount + upCount)
            + ",\"events\":{\"down\":" + downCount + ",\"up\":" + upCount + "}"
            + ",\"hold_ms\":" + (press ? holdMs : 0)
            + ",\"winerr\":" + lastInjectError + ",\"ime_open\":" + imeOpen
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    /// <summary>组合键：按顺序按下、再逆序抬起，保证修饰键在按键期间保持按住。</summary>
    private static string Hotkey(string[] keys)
    {
        if (keys.Length == 0) throw new InvalidOperationException("hotkey 需要至少一个键");
        ushort[] vks = new ushort[keys.Length];
        ushort[] scans = new ushort[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            vks[i] = ResolveKey(keys[i]);
            scans[i] = ScanCode(vks[i]);
        }
        double before = Now();
        int imeOpen = ImeOpenStatus();
        uint injected = 0;
        for (int i = 0; i < vks.Length; i++)
        {
            SendKeyRaw(vks[i], scans[i], (IsExtendedKey(vks[i]) ? KEYEVENTF_EXTENDEDKEY : 0));
            injected += lastInjectCount;
        }
        for (int i = vks.Length - 1; i >= 0; i--)
        {
            SendKeyRaw(vks[i], scans[i], (IsExtendedKey(vks[i]) ? KEYEVENTF_EXTENDEDKEY : 0) | KEYEVENTF_KEYUP);
            injected += lastInjectCount;
        }
        return "{\"ok\":true,\"keys\":\"" + Esc(string.Join("+", keys)) + "\""
            + ",\"injected\":" + injected
            // 与 key 命令对齐：events 给出 down/up 各几个（千瞳 2026-09-22 的第 ③ 条验收要求：
            // 加了保持时长后要能从回执看出"注入了哪些事件、各几个"）。
            + ",\"events\":{\"down\":" + vks.Length + ",\"up\":" + vks.Length + "}"
            + ",\"winerr\":" + lastInjectError
            + ",\"ime_open\":" + imeOpen
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    /// <summary>
    /// 文本输入走 KEYEVENTF_UNICODE 逐字符注入：不受键盘布局影响，中文与 emoji 都能输入
    /// （代理对按 UTF-16 码元逐个发送）。不走剪贴板，避免污染用户的剪贴板内容。
    /// </summary>
    private static string TypeText(string text)
    {
        if (text == null) text = "";
        int imeOpen = ImeOpenStatus();
        double before = Now();
        uint injected = 0;
        for (int i = 0; i < text.Length; i++)
        {
            ushort unit = text[i];
            SendKeyRaw(0, unit, KEYEVENTF_UNICODE);
            injected += lastInjectCount;
            SendKeyRaw(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            injected += lastInjectCount;
        }
        return "{\"ok\":true,\"chars\":" + text.Length + ",\"injected\":" + injected
            + ",\"winerr\":" + lastInjectError + ",\"ime_open\":" + imeOpen
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    // ── 持续采集（流式录屏 + 按时间戳回看）────────────────────────────────
    // 采集是一条独立的时间线，不该由"请求-响应"驱动：每请求抓一帧会把 stdio 往返
    // 算进帧间隔（实测每帧多约 2 ms），而且调用方请求多快就只能采多快。这里改为
    // **常驻采集线程**按固定间隔抓屏+编码，写入帧环形缓冲；调用方按时间戳取帧。
    // 帧时间戳取自 QPC（见 §时基说明），零点 = 进程启动。

    private class FrameRecord
    {
        public int seq;
        public double t;
        public double tAfter;
        public string path;
        public long bytes;
    }

    private static readonly System.Collections.Generic.List<FrameRecord> ring =
        new System.Collections.Generic.List<FrameRecord>();
    private static readonly object ringLock = new object();
    private static Thread liveThread;
    private static volatile bool liveRunning;
    private static int liveIntervalMs = 33;
    private static int liveQuality = 70;
    private static int liveCapacity = 1800;   // 30 fps × 60 s
    private static double liveStart;
    private static int liveCount;
    // 录像（可选）：采集帧**直接编码写入 mp4 文件**，用一个连续编码器 —— 见 FileEncoder 的注释。
    // 它替代"jpeg 逐帧落盘再事后合成"：同分辨率下 CPU 与内存切片相当，却省掉每帧一个文件的开销。
    private static string liveRecordPath = "";
    private static FileEncoder liveRecorder;
    private static double liveRecordStart;
    private static int liveRecordFrames;
    private static string liveRecordError = "";

    private class PendingFrame
    {
        public int slot;
        public int index;
        public double t;
        public double tAfter;
    }

    // 采集后端：gdi（默认，任何 Windows 都能用）/ dxgi（GPU 侧拷贝，帧率高、CPU 低）
    private static string liveBackend = "gdi";
    /** 帧的存储编码：`jpeg` 逐帧文件（默认，兼容旧行为）或 `h264` 内存切片（20 分钟常驻用）。 */
    private static string liveCodec = "jpeg";
    private static DesktopDuplication liveDxgi;
    private static System.Collections.Concurrent.BlockingCollection<PendingFrame> liveQueue;
    private static Thread encodeThread;
    // 双缓冲：采集线程抓屏进 slot，编码线程编码另一 slot，两者重叠 —— 把 5.1 ms 的编码
    // 从帧间隔里藏掉（单线程时帧间隔 = 抓屏 28.1 + 编码 5.1 = 33.2 ms，卡在 30 fps 边缘）。
    private static Bitmap[] liveBuffers;
    private static Graphics[] liveContexts;
    private static readonly Semaphore[] liveFree = new Semaphore[2];

    private static void LiveLoop()
    {
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        int interval = liveIntervalMs;
        liveBuffers = new Bitmap[2];
        liveContexts = new Graphics[2];
        for (int i = 0; i < 2; i++)
        {
            liveBuffers[i] = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            liveContexts[i] = Graphics.FromImage(liveBuffers[i]);
        }
        liveFree[0] = new Semaphore(1, 1);
        liveFree[1] = new Semaphore(1, 1);
        liveQueue = new System.Collections.Concurrent.BlockingCollection<PendingFrame>(4);

        encodeThread = new Thread(EncodeLoop);
        encodeThread.IsBackground = true;
        encodeThread.Start();

        int index = 0;
        int slot = 0;
        while (liveRunning)
        {
            // 等这块缓冲空出来；编码跟不上时这里形成背压（宁可掉帧也不排队堆积）。
            if (!liveFree[slot].WaitOne(2000)) continue;
            if (!liveRunning) { liveFree[slot].Release(); break; }
            index++;
            double t = Now();
            liveContexts[slot].CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            double after = Now();
            PendingFrame item = new PendingFrame();
            item.slot = slot;
            item.index = index;
            item.t = t;
            item.tAfter = after;
            try { liveQueue.Add(item); }
            catch (InvalidOperationException) { liveFree[slot].Release(); break; }
            liveCount = index;
            slot = 1 - slot;
            // 维持目标节奏：以「启动时刻 + n×间隔」为基准，避免逐帧误差累积。
            // interval <= 0 表示不限速（全速跑），用来测采集能力上限。
            // Windows 的 Sleep 粒度约 15.6 ms，只靠它会把 30 fps 打成 29.5；
            // 所以粗段用 Sleep(1)、最后不到 2 ms 的尾巴用自旋补齐。
            if (interval > 0)
            {
                double next = liveStart + (index + 1) * (interval / 1000.0);
                while (liveRunning)
                {
                    double wait = next - Now();
                    if (wait <= 0.0005) break;
                    if (wait > 0.002) Thread.Sleep(1);
                    else Thread.SpinWait(300);
                }
            }
        }
        for (int i = 0; i < 2; i++)
        {
            if (liveContexts[i] != null) liveContexts[i].Dispose();
            if (liveBuffers[i] != null) liveBuffers[i].Dispose();
            liveContexts[i] = null;
            liveBuffers[i] = null;
        }
    }

    /// <summary>
    /// DXGI 采集循环。与 GDI 那条路不同，这里**单线程**就够了：DXGI 的读回是 GPU 侧拷贝
    /// （实测采集 5~8 ms），加编码也远低于 33 ms 的帧预算，不需要双缓冲去重叠。
    ///
    /// 画面静止时 ReadFrame 会超时（DXGI 只在有变化时给新帧），此时**复用上一帧**继续编码——
    /// 回看要的是"按时间索引的帧序列"，时间轴必须均匀，不能因为画面没动就出现空洞。
    /// </summary>
    private static void LiveLoopDxgi()
    {
        int interval = liveIntervalMs;
        liveDxgi = new DesktopDuplication(null);
        int index = 0;
        int waitingForFirst = 0;
        int reusedFrames = 0;
        try
        {
            while (liveRunning)
            {
                // 首帧要等：DXGI 只在画面**发生变化**时给出图像，完全静止的桌面
                // 会一直 LastPresentTime=0（实测如此）。桌面时钟/鼠标动一下就满足，
                // 所以这里给它最多 8 秒；拿到第一帧之后，后续超时就复用上一帧。
                if (!liveDxgi.HasImage)
                {
                    bool got = liveDxgi.ReadFrame(500);
                    if (!got)
                    {
                        if (++waitingForFirst > 16)
                            throw new InvalidOperationException("DXGI 等了 8 秒仍没有出现任何画面变化，无法取首帧");
                        continue;
                    }
                }
                index++;
                double t = Now();
                bool fresh = liveDxgi.ReadFrame(0);   // 非阻塞；节奏由下面的节流负责
                double after = Now();
                if (!fresh) reusedFrames++;
                string path = Path.Combine(outDir, "live-" + index + ".jpg");
                SaveFrame(liveDxgi.Image, path, false, liveQuality);
                FrameRecord record = new FrameRecord();
                record.seq = index;
                record.t = t;
                record.tAfter = after;
                record.path = path;
                record.bytes = new FileInfo(path).Length;
                lock (ringLock)
                {
                    ring.Add(record);
                    while (ring.Count > liveCapacity)
                    {
                        try { File.Delete(ring[0].path); } catch (IOException) { /* 调用方可能正在读 */ }
                        ring.RemoveAt(0);
                    }
                }
                liveCount = index;
                if (interval > 0)
                {
                    double next = liveStart + (index + 1) * (interval / 1000.0);
                    while (liveRunning)
                    {
                        double wait = next - Now();
                        if (wait <= 0.0005) break;
                        if (wait > 0.002) Thread.Sleep(1);
                        else Thread.SpinWait(300);
                    }
                }
            }
        }
        catch (Exception error)
        {
            // 线程里抛异常不会有人接，必须自己喊出来；否则表现为"采集没反应"而主线程看似卡住。
            Console.Error.WriteLine("dsh-cu: DXGI 采集线程异常: " + error);
        }
        finally
        {
            liveRunning = false;
            if (liveDxgi != null) { liveDxgi.Dispose(); liveDxgi = null; }
        }
    }

    /// <summary>编码线程：把采集线程交来的缓冲编码落盘并写入帧环形缓冲。</summary>
    private static void EncodeLoop()
    {
        System.Collections.Concurrent.BlockingCollection<PendingFrame> queue = liveQueue;
        foreach (PendingFrame item in queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(outDir, "live-" + item.index + ".jpg");
                SaveFrame(liveBuffers[item.slot], path, false, liveQuality);
                FrameRecord record = new FrameRecord();
                record.seq = item.index;
                record.t = item.t;
                record.tAfter = item.tAfter;
                record.path = path;
                record.bytes = new FileInfo(path).Length;
                lock (ringLock)
                {
                    ring.Add(record);
                    while (ring.Count > liveCapacity)
                    {
                        try { File.Delete(ring[0].path); } catch (IOException) { /* 调用方可能正在读，下次再删 */ }
                        ring.RemoveAt(0);
                    }
                }
            }
            catch (Exception) { /* 单帧失败不应拖垮采集循环 */ }
            finally
            {
                try { liveFree[item.slot].Release(); } catch (SemaphoreFullException) { }
            }
        }
    }

    private static string FrameJson(FrameRecord record)
    {
        return "{\"ok\":true,\"seq\":" + record.seq + ",\"t\":" + F(record.t)
            + ",\"t_after\":" + F(record.tAfter) + ",\"path\":\"" + Esc(record.path)
            + "\",\"bytes\":" + record.bytes + "}";
    }

    private static string LiveStart(int intervalMs, int quality, int capacity, string backend,
        string codec, int segmentFrames, double retainSeconds, string recordPath)
    {
        if (liveRunning) return "{\"ok\":true,\"already\":true,\"interval_ms\":" + liveIntervalMs + "}";
        if (backend == "dxgi" || backend == "gdi") liveBackend = backend;
        if (codec == "h264" || codec == "jpeg") liveCodec = codec;
        // 录像要求给定 record_path；编码器只存在于 h264 路径，所以带录像时强制走 h264。
        liveRecordPath = recordPath == null ? "" : recordPath;
        if (liveRecordPath.Length > 0) liveCodec = "h264";
        liveRecordFrames = 0;
        liveRecordError = "";
        if (segmentFrames > 0) h264SegmentFrames = segmentFrames;
        if (retainSeconds > 0) h264RetainSeconds = retainSeconds;
        if (intervalMs > 0) liveIntervalMs = intervalMs;
        if (quality > 0) liveQuality = quality;
        if (capacity > 0) liveCapacity = capacity;
        lock (ringLock) { ring.Clear(); }
        lock (h264Lock) { h264Ring.Clear(); h264TotalBytes = 0; h264SegmentCount = 0; h264LastSeconds = 0; h264SealedUntil = Now(); }
        h264FlushRequested = false;
        h264LastFlushAt = 0;
        liveStart = Now();
        liveCount = 0;
        liveRunning = true;
        if (liveCodec == "h264")
        {
            liveThread = new Thread(LiveLoopH264);
        }
        else
        {
            liveThread = new Thread(liveBackend == "dxgi" ? (ThreadStart)LiveLoopDxgi : (ThreadStart)LiveLoop);
        }
        liveThread.IsBackground = true;
        liveThread.Start();
        return "{\"ok\":true,\"interval_ms\":" + liveIntervalMs + ",\"quality\":" + liveQuality
            + ",\"capacity\":" + liveCapacity + ",\"backend\":\"" + liveBackend + "\""
            + ",\"codec\":\"" + liveCodec + "\",\"segment_frames\":" + h264SegmentFrames
            + ",\"record_path\":\"" + Esc(liveRecordPath) + "\"}";
    }

    private static string LiveStop()
    {
        liveRunning = false;
        if (liveThread != null)
        {
            liveThread.Join(3000);
            liveThread = null;
        }
        if (liveQueue != null)
        {
            try { liveQueue.CompleteAdding(); } catch (InvalidOperationException) { /* 已关闭 */ }
        }
        if (encodeThread != null)
        {
            encodeThread.Join(3000);
            encodeThread = null;
        }
        liveQueue = null;
        // 录像线程已在 LiveLoopH264 的 finally 里 Finish + Dispose（stop 前已经 Join 过采集线程）。
        return "{\"ok\":true,\"frames\":" + liveCount
            + ",\"record_path\":\"" + Esc(liveRecordPath) + "\",\"record_frames\":" + liveRecordFrames
            + ",\"record_error\":\"" + Esc(liveRecordError) + "\"}";
    }

    private static string Latest()
    {
        // h264 模式不写逐帧帧表：最新可用画面是最后一片的末尾。**先把正在写的那片结算掉**，
        // 否则"最近可用画面"实际是 5 秒前的画面，调用方据此往前推窗口会白白错过刚发生的事。
        if (liveCodec == "h264")
        {
            RequestH264Seal();
            H264Slice slice = null;
            lock (h264Lock) { if (h264Ring.Count > 0) slice = h264Ring[h264Ring.Count - 1]; }
            if (slice == null) return "{\"ok\":false,\"error\":\"采集未启动或还没有封好的切片\"}";
            return "{\"ok\":true,\"seq\":0,\"t\":" + F(slice.End)
                + ",\"t_after\":" + F(slice.End) + ",\"path\":\"\",\"bytes\":0}";
        }
        FrameRecord record = null;
        lock (ringLock) { if (ring.Count > 0) record = ring[ring.Count - 1]; }
        if (record == null) return "{\"ok\":false,\"error\":\"采集未启动或还没有帧\"}";
        return FrameJson(record);
    }

    private static string LiveStats()
    {
        int count; double first = 0, last = 0, bytes = 0;
        lock (ringLock)
        {
            count = ring.Count;
            if (count > 0) { first = ring[0].t; last = ring[count - 1].t; }
            for (int i = 0; i < count; i++) bytes += ring[i].bytes;
        }
        int segments; long h264Bytes; double retained = 0;
        double sealedUntil = 0;
        lock (h264Lock)
        {
            // 口径必须与 frames/bytes 一致：三者描述的都是**环里现存**的东西。
            // 累计封片数另放 segments_total——原先把累计值当 segments 报出去会与 frames 对不上
            // （36 分钟长跑实测：frames 36300 帧 = 242 片，而 segments 报 350 片），调用方算不通。
            segments = h264Ring.Count;
            h264Bytes = h264TotalBytes;
            // retained_s 才是"现在还能回看多久"：现存片覆盖的真实时长（首片起点 → 末片终点）。
            if (liveCodec == "h264" && segments > 0) retained = h264Ring[segments - 1].End - h264Ring[0].Start;
            sealedUntil = h264SealedUntil;
        }
        // 未封片窗口：最后一片封片之后积了多少秒。这段时间的画面默认取不到，除非取帧时按需结算
        // （见 RequestH264Seal）——所以它必须报出来，让调用方知道"再等一下或直接取都会拿到"。
        double unsealed = 0;
        if (liveRunning && liveCodec == "h264")
        {
            unsealed = Now() - sealedUntil;
            if (unsealed < 0) unsealed = 0;
        }
        if (liveCodec == "h264")
        {
            count = 0;
            lock (h264Lock)
            {
                for (int i = 0; i < h264Ring.Count; i++) count += h264Ring[i].Frames;
            }
        }
        // 帧率口径要分开算：jpeg 路径的"跨度"是首尾帧时间差；h264 路径必须用
        // **采集开始 → 现在** 配实时帧计数——若沿用"第一片开始"当起点，片内偏移与片尾空档
        // 都会被算进分母，实测会把 29.3 fps 报成 27.2（看起来没达标，其实达标了）。
        double span;
        double fps;
        if (liveCodec == "h264")
        {
            span = Now() - liveStart;
            fps = span > 0 ? liveCount / span : 0;
        }
        else
        {
            span = last - first;
            fps = span > 0 ? (count - 1) / span : 0;
        }
        return "{\"ok\":true,\"backend\":\"" + liveBackend + "\",\"codec\":\"" + liveCodec + "\""
            + ",\"running\":" + (liveRunning ? "true" : "false")
            + ",\"frames\":" + count + ",\"span_s\":" + F(span) + ",\"fps\":" + F(fps)
            + ",\"bytes\":" + (liveCodec == "h264" ? h264Bytes : (long)bytes)
            + ",\"interval_ms\":" + liveIntervalMs
            + ",\"capacity\":" + liveCapacity
            + ",\"segments\":" + segments + ",\"segments_total\":" + h264SegmentCount
            + ",\"retained_s\":" + F(retained)
            + ",\"unsealed_s\":" + F(unsealed)
            + ",\"segment_frames\":" + h264SegmentFrames
            + ",\"retain_s\":" + F(h264RetainSeconds)
            + ",\"record_path\":\"" + Esc(liveRecordPath) + "\",\"record_frames\":" + liveRecordFrames
            + ",\"record_error\":\"" + Esc(liveRecordError) + "\"}";
    }

    /// <summary>按帧时间戳区间取帧（回看用）。limit 限制返回条数，避免一次吐出整段缓冲。</summary>
    private static string FramesIn(double from, double to, int limit)
    {
        if (liveCodec == "h264") return H264FramesIn(from, to, limit);
        System.Collections.Generic.List<FrameRecord> hits =
            new System.Collections.Generic.List<FrameRecord>();
        lock (ringLock)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                if (ring[i].t < from) continue;
                if (ring[i].t > to) break;
                hits.Add(ring[i]);
                if (hits.Count >= limit) break;
            }
        }
        StringBuilder builder = new StringBuilder();
        builder.Append("{\"ok\":true,\"count\":").Append(hits.Count).Append(",\"frames\":[");
        for (int i = 0; i < hits.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(FrameJson(hits[i]));
        }
        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>
    /// 变化检测：把窗口内的帧两两相邻差分，直接报出"哪些矩形区域变了、在什么时候变的"。
    ///
    /// 为什么值得一条单独的命令：插件侧（Node）拿不到像素，"画面变没变、变在哪"只能靠反复取帧
    /// 再让模型比对——那是每轮几秒的观察成本。这里让 helper 用像素算出结论。
    ///
    /// 复用 `FramesIn` 的取帧路径（逐帧 JPEG 直接给环内文件；H.264 解码时已落盘），所以两种存储
    /// 编码都支持；帧文件若已被环形清理就跳过那一对，如实少报、不猜。
    /// </summary>
    private static string DiffFrames(double from, double to, int limit, int cell, double ratio, int maxBoxes)
    {
        System.Collections.Generic.List<double> times = new System.Collections.Generic.List<double>();
        System.Collections.Generic.List<string> paths = new System.Collections.Generic.List<string>();
        CollectFrameFiles(from, to, limit, times, paths);
        if (times.Count < 2)
            return "{\"ok\":true,\"frames\":" + times.Count + ",\"cell\":" + cell + ",\"changes\":[]}";

        StringBuilder builder = new StringBuilder();
        builder.Append("{\"ok\":true,\"frames\":").Append(times.Count)
            .Append(",\"cell\":").Append(cell).Append(",\"changes\":[");
        int pairs = 0;
        for (int i = 1; i < times.Count; i++)
        {
            Bitmap before = null;
            Bitmap after = null;
            try
            {
                before = new Bitmap(paths[i - 1]);
                after = new Bitmap(paths[i]);
            }
            catch
            {
                // 帧文件可能已被清理：跳过这一对，别把"读不到"说成"没变化"。
                if (before != null) before.Dispose();
                if (after != null) after.Dispose();
                continue;
            }
            string boxes = DiffBoxes(before, after, cell, ratio, maxBoxes);
            before.Dispose();
            after.Dispose();
            if (pairs > 0) builder.Append(',');
            builder.Append("{\"t_from\":").Append(F(times[i - 1]))
                .Append(",\"t_to\":").Append(F(times[i]))
                .Append(",\"boxes\":").Append(boxes).Append('}');
            pairs++;
        }
        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>取出窗口内的帧文件与时刻：逐帧 JPEG 走环内记录；H.264 走解码落盘后的同构 JSON。</summary>
    private static void CollectFrameFiles(double from, double to, int limit,
        System.Collections.Generic.List<double> times, System.Collections.Generic.List<string> paths)
    {
        if (liveCodec == "h264")
        {
            string json = H264FramesIn(from, to, limit);
            int index = 0;
            while (true)
            {
                int timeAt = json.IndexOf("\"t\":", index, StringComparison.Ordinal);
                if (timeAt < 0) break;
                int timeStart = timeAt + 4;
                int timeEnd = json.IndexOf(',', timeStart);
                if (timeEnd < 0) break;
                double moment;
                if (!double.TryParse(json.Substring(timeStart, timeEnd - timeStart), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out moment)) break;
                int pathAt = json.IndexOf("\"path\":\"", timeEnd, StringComparison.Ordinal);
                if (pathAt < 0) break;
                int pathStart = pathAt + 8;
                int pathEnd = json.IndexOf('"', pathStart);
                if (pathEnd < 0) break;
                times.Add(moment);
                paths.Add(json.Substring(pathStart, pathEnd - pathStart));
                index = pathEnd;
            }
            return;
        }
        lock (ringLock)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                if (ring[i].t < from) continue;
                if (ring[i].t > to) break;
                times.Add(ring[i].t);
                paths.Add(ring[i].path);
                if (times.Count >= limit) break;
            }
        }
    }

    /// <summary>逐像素差分 → 单元网格比例 → 合并矩形（像素坐标，与屏幕坐标同口径）。</summary>
    private static string DiffBoxes(Bitmap before, Bitmap after, int cell, double ratio, int maxBoxes)
    {
        int width = before.Width;
        int height = before.Height;
        if (after.Width != width || after.Height != height) return "[]";
        Rectangle rect = new Rectangle(0, 0, width, height);
        BitmapData dataBefore = before.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dataAfter = after.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] pixelsBefore = new int[width * height];
        int[] pixelsAfter = new int[width * height];
        Marshal.Copy(dataBefore.Scan0, pixelsBefore, 0, pixelsBefore.Length);
        Marshal.Copy(dataAfter.Scan0, pixelsAfter, 0, pixelsAfter.Length);
        before.UnlockBits(dataBefore);
        after.UnlockBits(dataAfter);

        int cols = (width + cell - 1) / cell;
        int rows = (height + cell - 1) / cell;
        int[] changed = new int[cols * rows];
        int[] totals = new int[cols * rows];
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            int rowCell = (y / cell) * cols;
            for (int x = 0; x < width; x++)
            {
                int cellIndex = rowCell + x / cell;
                totals[cellIndex]++;
                int a = pixelsBefore[rowBase + x];
                int b = pixelsAfter[rowBase + x];
                int delta = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF))
                    + Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF))
                    + Math.Abs((a & 0xFF) - (b & 0xFF));
                // 三通道差之和超过 60（每通道平均 20）才算真变化：JPEG 噪声远低于它。
                if (delta > 60) changed[cellIndex]++;
            }
        }

        bool[] hot = new bool[cols * rows];
        for (int i = 0; i < hot.Length; i++)
            hot[i] = totals[i] > 0 && (double)changed[i] / totals[i] >= ratio;

        System.Collections.Generic.List<int[]> boxes = MergeBoxes(hot, cols, rows);
        boxes.Sort(delegate(int[] left, int[] right)
        {
            int leftArea = (left[2] - left[0] + 1) * (left[3] - left[1] + 1);
            int rightArea = (right[2] - right[0] + 1) * (right[3] - right[1] + 1);
            return rightArea.CompareTo(leftArea);
        });

        StringBuilder builder = new StringBuilder("[");
        int emitted = 0;
        for (int i = 0; i < boxes.Count && emitted < maxBoxes; i++)
        {
            int[] box = boxes[i];
            if (emitted > 0) builder.Append(',');
            int left = box[0] * cell;
            int top = box[1] * cell;
            builder.Append("{\"x\":").Append(left)
                .Append(",\"y\":").Append(top)
                .Append(",\"w\":").Append(Math.Min(width, (box[2] + 1) * cell) - left)
                .Append(",\"h\":").Append(Math.Min(height, (box[3] + 1) * cell) - top)
                .Append('}');
            emitted++;
        }
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>把变化单元合并成矩形：从未访问的单元向右下贪心扩展到最大矩形（够用且实现简单）。</summary>
    private static System.Collections.Generic.List<int[]> MergeBoxes(bool[] hot, int cols, int rows)
    {
        bool[] used = new bool[hot.Length];
        System.Collections.Generic.List<int[]> boxes = new System.Collections.Generic.List<int[]>();
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                if (!hot[row * cols + col] || used[row * cols + col]) continue;
                int right = col;
                while (right + 1 < cols && hot[row * cols + right + 1] && !used[row * cols + right + 1]) right++;
                int bottom = row;
                while (bottom + 1 < rows)
                {
                    bool fits = true;
                    for (int c = col; c <= right; c++)
                    {
                        if (!hot[(bottom + 1) * cols + c] || used[(bottom + 1) * cols + c]) { fits = false; break; }
                    }
                    if (!fits) break;
                    bottom++;
                }
                for (int r = row; r <= bottom; r++)
                    for (int c = col; c <= right; c++) used[r * cols + c] = true;
                boxes.Add(new int[] { col, row, right, bottom });
            }
        }
        return boxes;
    }

    /// <summary>切片时间轴的名义帧率：MemoryEncoder.Write 按 30 fps 写片内时间戳，片内从 0 开始。</summary>
    private const double SliceFrameRate = 30.0;

    /// <summary>一次 frames 调用最多解码多少帧，与工具描述里的"上限 512 帧（≈17 秒 @30fps）"对齐。
    /// 逐帧 JPEG 路径"取 512 帧"只是列文件名，而 H.264 的每一帧都要真解码（实测含落盘约 70–150 ms）
    /// ⇒ 512 帧是几十秒量级的一次调用：调用方要按窗口长度决定要不要一次取这么宽
    /// （1 秒窗口约 30 帧、约 3 秒完成，这才是取逐帧时刻表的常规用法）。</summary>
    private const int H264DecodeBudget = 512;

    private static int h264TakenSeq;

    /// <summary>预建编码器由后台线程写、采集线程读，用锁保证可见性。</summary>
    private static readonly object pendingLock = new object();

    /// <summary>
    /// H.264 环形缓冲按时间戳取帧：定位相交切片 → 片内解码 → 落 JPEG → 返回与逐帧 JPEG
    /// **同构**的 JSON（seq/t/t_after/path/bytes），于是插件层不需要区分存储编码。
    ///
    /// 三条必须知道的语义：
    ///   · 片内时间戳按名义 30 fps 生成，而实际采集速率由抓屏与编码耗时决定（DXGI 实测 30.3 fps、
    ///     GDI 约 23–27 fps）⇒ 换算回真实时刻必须用片首片尾的采集时刻做线性映射；不校正的话
    ///     一片内最多差出 0.4 秒（GDI），"这一刻的画面"就会答错。
    ///   · 只覆盖**已封片**的部分：正在写的那一片不在环里，最近不到一个片长（5 秒）的画面取不到。
    ///   · 窗口内**每一帧**都作为目标；只有帧数超过 H264DecodeBudget（512）时才均匀抽样。
    /// </summary>
    private static string H264FramesIn(double from, double to, int limit)
    {
        EnsureMediaFoundation();
        // 窗口若伸进"还在写的那一片"，先请采集线程结算一次再取——否则最近的画面永远取不到。
        double sealedUntil;
        lock (h264Lock) { sealedUntil = h264SealedUntil; }
        if (to > sealedUntil + 0.05) RequestH264Seal();
        System.Collections.Generic.List<H264Slice> slices = new System.Collections.Generic.List<H264Slice>();
        lock (h264Lock)
        {
            for (int i = 0; i < h264Ring.Count; i++)
            {
                H264Slice slice = h264Ring[i];
                if (slice.End < from) continue;
                if (slice.Start > to) break;
                slices.Add(slice);
            }
        }
        if (slices.Count == 0) return "{\"ok\":true,\"count\":0,\"frames\":[]}";

        int n = slices.Count;
        int take = limit < H264DecodeBudget ? limit : H264DecodeBudget;
        if (take < 1) take = 1;

        // 目标 = 窗口覆盖到的**每一帧**，时刻 = 片首 + 帧序号 × 片内步长（步长由片首片尾的真实时刻
        // 校正）。早先按"片内均匀铺点"生成目标，配合 24 帧的解码预算，交付的是抽样——与工具描述
        // 写的"返回窗口内每一帧"不符（2026-09-22 由评测方实测报出：任意窗口恒约 24 张）。
        // 现在帧够少时就是字面意义的每一帧；超过 take 才均匀抽样，且抽样仍落在真实帧时刻上。
        System.Collections.Generic.List<double> allTargets = new System.Collections.Generic.List<double>();
        System.Collections.Generic.List<int> allSlice = new System.Collections.Generic.List<int>();
        System.Collections.Generic.List<int> allFrame = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n; i++)
        {
            double stepSec = slices[i].Frames > 1
                ? (slices[i].End - slices[i].Start) / (slices[i].Frames - 1)
                : 1.0 / SliceFrameRate;
            if (stepSec <= 0) stepSec = 1.0 / SliceFrameRate;
            double lo = Math.Max(from, slices[i].Start);
            double hi = Math.Min(to, slices[i].End);
            if (hi < lo) continue;
            int firstFrame = (int)Math.Ceiling((lo - slices[i].Start) / stepSec - 1e-6);
            int lastFrame = (int)Math.Floor((hi - slices[i].Start) / stepSec + 1e-6);
            if (firstFrame < 0) firstFrame = 0;
            if (lastFrame > slices[i].Frames - 1) lastFrame = slices[i].Frames - 1;
            for (int k = firstFrame; k <= lastFrame; k++)
            {
                allTargets.Add(slices[i].Start + k * stepSec);
                allSlice.Add(i);
                allFrame.Add(k);
            }
        }
        if (allTargets.Count == 0) return "{\"ok\":true,\"count\":0,\"frames\":[]}";

        // 片内目标递增，于是顺序解码比逐帧 seek 便宜得多（实测 seek 到目标约 71 ms，片内续读只要几毫秒）。
        // 除了真实时刻，还带着「哪一片的第几帧」：解码时直接按帧号算 PTS，不经过真实秒↔名义秒的往返换算。
        System.Collections.Generic.List<double> targets = new System.Collections.Generic.List<double>();
        System.Collections.Generic.List<int> targetSlice = new System.Collections.Generic.List<int>();
        System.Collections.Generic.List<int> targetFrame = new System.Collections.Generic.List<int>();
        if (allTargets.Count <= take)
        {
            targets.AddRange(allTargets);
            targetSlice.AddRange(allSlice);
            targetFrame.AddRange(allFrame);
        }
        else
        {
            int prevPick = -1;
            for (int k = 0; k < take; k++)
            {
                int pick = take == 1
                    ? 0
                    : (int)Math.Round((double)k * (allTargets.Count - 1) / (take - 1));
                if (pick == prevPick) continue;
                prevPick = pick;
                targets.Add(allTargets[pick]);
                targetSlice.Add(allSlice[pick]);
                targetFrame.Add(allFrame[pick]);
            }
        }

        StringBuilder body = new StringBuilder();
        int written = 0;
        for (int s = 0; s < slices.Count; s++)
        {
            H264Slice slice = slices[s];
            // 片内时间戳按名义 30 fps 走，而实际采集速率受抓屏与编码耗时影响（GDI 实测约 27.7 fps）
            // ⇒ 两者必须靠片首片尾的真实时刻对齐：真实秒 = 片内秒 × realSpan / span。
            // 不校正的话，一片内的时间戳最多能差出 0.4 秒——"这一刻的画面"就会答错。
            double span = (slice.Frames - 1) / SliceFrameRate;
            double realSpan = slice.End - slice.Start;
            double step = slice.Frames > 1 ? realSpan / (slice.Frames - 1) : 1.0 / SliceFrameRate;
            Dsh.MediaFoundation.MFFrameDecoder decoder = null;
            long lastStamp = -1;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (targetSlice[i] != s) continue;
                    int frameIndex = targetFrame[i];
                    if (frameIndex < 0) frameIndex = 0;
                    if (frameIndex > slice.Frames - 1) frameIndex = slice.Frames - 1;
                    // PTS 用与编码端**同一个式子**算（`frame * 10000000 / 30`，整数除法）。两边各取整才会
                    // 精确落在同一帧上：此前把真实秒换算成名义秒再取整，目标会落在两帧之间——`ReadAtOrAfter`
                    // 于是既跳帧、下一次又因目标不大于上一时间戳而 seek 回同一帧，同一次调用里两种错同时出现
                    // （2026-09-22 实测：4 秒窗口 122 帧里只有 83 个不同时刻）。帧号也顺带夹住了片尾上限。
                    long local = (long)frameIndex * 10000000L / 30;
                    if (decoder == null) decoder = new Dsh.MediaFoundation.MFFrameDecoder(slice.Data);
                    // 片内续读要求目标严格大于上一个时间戳；跨不过去就退回 seek。
                    Dsh.MediaFoundation.DecodedFrame decoded = decoder.ReadAtOrAfter(local, local <= lastStamp);
                    if (decoded == null) break;
                    try
                    {
                        lastStamp = decoded.Timestamp100ns;
                        double stampSec = decoded.Timestamp100ns / 10000000.0;
                        double at = slice.Start + ((span > 0 && realSpan > 0) ? stampSec * realSpan / span : stampSec);
                        h264TakenSeq++;
                        string path = Path.Combine(outDir, "h264-" + h264TakenSeq + ".jpg");
                        SaveFrame(decoded.Bitmap, path, false, liveQuality);
                        if (written > 0) body.Append(',');
                        body.Append("{\"ok\":true,\"seq\":").Append(h264TakenSeq)
                            .Append(",\"t\":").Append(F(at))
                            .Append(",\"t_after\":").Append(F(at + step))
                            .Append(",\"path\":\"").Append(Esc(path))
                            .Append("\",\"bytes\":").Append(new FileInfo(path).Length).Append('}');
                        written++;
                    }
                    finally { decoded.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("h264 frames: 取帧失败：{0}", ex.Message);
            }
            finally { if (decoder != null) decoder.Dispose(); }
        }
        PruneTakenFrames();
        return "{\"ok\":true,\"count\":" + written + ",\"decoded\":" + written
            + ",\"segments\":" + slices.Count + ",\"frames\":[" + body + "]}";
    }

    /// <summary>取帧会不断落盘 JPEG（每帧约 100–300 KB），按写入时间淘汰旧的、只留最近的一批。
    /// 逐帧 JPEG 采集的帧由 PruneOldFrames 管；这里管的是"从切片解出来交给调用方的帧"。</summary>
    private static void PruneTakenFrames()
    {
        try
        {
            string[] files = Directory.GetFiles(outDir, "h264-*.jpg");
            if (files.Length <= 400) return;
            Array.Sort(files, delegate(string a, string b)
            {
                return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
            });
            for (int i = 0; i < files.Length - 400; i++) File.Delete(files[i]);
        }
        catch (IOException ex) { Console.Error.WriteLine("h264 frames: 清理旧帧失败：{0}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("h264 frames: 清理旧帧失败：{0}", ex.Message); }
    }

    private static string ScreenInfo()
    {
        return "\"primary\":{\"w\":" + GetSystemMetrics(SM_CXSCREEN)
            + ",\"h\":" + GetSystemMetrics(SM_CYSCREEN) + "}"
            + ",\"virtual\":{\"x\":" + GetSystemMetrics(SM_XVIRTUALSCREEN)
            + ",\"y\":" + GetSystemMetrics(SM_YVIRTUALSCREEN)
            + ",\"w\":" + GetSystemMetrics(SM_CXVIRTUALSCREEN)
            + ",\"h\":" + GetSystemMetrics(SM_CYVIRTUALSCREEN) + "}";
    }

    private static void SaveFrame(Bitmap bitmap, string path, bool png, int quality)
    {
        if (png) bitmap.Save(path, ImageFormat.Png);
        else
        {
            EncoderParameters parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(path, JpegCodec, parameters);
        }
    }

    /// <summary>
    /// 采集性能拆解：分别量「抓屏」与「JPEG 编码」的成本，写盘不计入。
    /// 30 fps 需要 33.3 ms 的帧间隔，靠这个数字决定是优化 GDI 还是改走 DXGI。
    /// </summary>
    /// <summary>
    /// DXGI 档的采集成本拆解：单帧「读回」与「JPEG 编码」各自的耗时。
    /// 读回包含了 GPU→CPU 的像素拷贝（2560×1440×4 ≈ 14.7 MB/帧），这是 CPU 侧
    /// 无法回避的一笔——30 fps 就是约 440 MB/s 的持续拷贝，无论是 GDI 还是 DXGI。
    /// </summary>
    private static string BenchDxgi(int frames, int quality)
    {
        if (frames <= 0) frames = 20;
        DesktopDuplication capture = new DesktopDuplication(null);
        try
        {
            // 首帧要等到画面出现变化
            int guard = 0;
            while (!capture.HasImage && guard++ < 40) capture.ReadFrame(500);
            if (!capture.HasImage) throw new InvalidOperationException("DXGI 取不到首帧");
            double readSum = 0, readMax = 0, encodeSum = 0, encodeMax = 0;
            long bytes = 0;
            EncoderParameters parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            using (MemoryStream stream = new MemoryStream(1024 * 1024))
            {
                for (int i = 0; i < frames; i++)
                {
                    double a = Now();
                    capture.ReadFrame(0);   // 非阻塞：有变化就取，没有就复用上一帧（等待由节奏控制负责）
                    double b = Now();
                    stream.SetLength(0);
                    capture.Image.Save(stream, JpegCodec, parameters);
                    double c = Now();
                    double read = b - a, encode = c - b;
                    readSum += read; encodeSum += encode;
                    if (read > readMax) readMax = read;
                    if (encode > encodeMax) encodeMax = encode;
                    bytes = stream.Length;
                }
            }
            return "{\"ok\":true,\"backend\":\"dxgi\",\"frames\":" + frames
                + ",\"read_ms\":" + F(readSum / frames * 1000) + ",\"read_max_ms\":" + F(readMax * 1000)
                + ",\"encode_ms\":" + F(encodeSum / frames * 1000) + ",\"encode_max_ms\":" + F(encodeMax * 1000)
                + ",\"frame_budget_fps\":" + F(1000 / ((readSum + encodeSum) / frames * 1000))
                + ",\"jpeg_bytes\":" + bytes + "}";
        }
        finally { capture.Dispose(); }
    }

    private static string Bench(int frames, int quality)
    {
        if (frames <= 0) frames = 20;
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        Bitmap bitmap = EnsureBuffer(width, height);
        double captureSum = 0, encodeSum = 0, captureMax = 0, encodeMax = 0;
        EncoderParameters parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        using (MemoryStream stream = new MemoryStream(1024 * 1024))
        {
            for (int i = 0; i < frames; i++)
            {
                double a = Now();
                bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                double b = Now();
                stream.SetLength(0);
                bitmap.Save(stream, JpegCodec, parameters);
                double c = Now();
                double cap = b - a, enc = c - b;
                captureSum += cap; encodeSum += enc;
                if (cap > captureMax) captureMax = cap;
                if (enc > encodeMax) encodeMax = enc;
            }
            return "{\"ok\":true,\"frames\":" + frames + ",\"w\":" + width + ",\"h\":" + height
                + ",\"capture_ms\":" + F(captureSum / frames * 1000) + ",\"capture_max_ms\":" + F(captureMax * 1000)
                + ",\"encode_ms\":" + F(encodeSum / frames * 1000) + ",\"encode_max_ms\":" + F(encodeMax * 1000)
                + ",\"frame_budget_fps\":" + F(1000 / ((captureSum + encodeSum) / frames * 1000))
                + ",\"jpeg_bytes\":" + stream.Length + "}";
        }
    }

    /// <summary>
    /// 抓一帧。时间戳取采集前/后两点——内容对应的显示时刻夹在中间，由标定决定取值。
    /// </summary>
    private static string Capture(int quality, bool png)
    {
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("system screen metrics are unavailable");

        double before = Now();
        Bitmap bitmap = EnsureBuffer(width, height);
        bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        double after = Now();
        seq++;
        string path = Path.Combine(outDir, (png ? "frame-" : "shot-") + seq + (png ? ".png" : ".jpg"));
        SaveFrame(bitmap, path, png, quality);
        long bytes = new FileInfo(path).Length;
        return "{\"ok\":true,\"seq\":" + seq
            + ",\"w\":" + width + ",\"h\":" + height
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(after)
            + ",\"path\":\"" + Esc(path) + "\",\"bytes\":" + bytes
            + ",\"format\":\"" + (png ? "png" : "jpg") + "\"}";
    }

    /// <summary>
    /// 裁一块，可按整数倍放大。裁块不缩放时模型看到的是 **1:1 原始像素**（区域 ≤ 模型侧
    /// 640000 像素预算就不会被缩回去），这是"精点小控件"的唯一可靠手段；放大用于把
    /// 更小的区域铺满同一份预算去读细节。
    /// </summary>
    /// <param name="scale">整数放大倍数，1 表示不放大。注意倍数与区域大小必须联动：
    /// 区域内像素 × scale² 超过模型侧预算时，放大出来的图会被缩回去，等于白放大。</param>
    private static string CaptureRegion(int x, int y, int w, int h, int quality, int scale, int center)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("region width/height must be positive");
        if (center != 0)
        {
            // center=1：x/y 当成**中心点**（点击回执附图就靠它），自动夹到屏内。
            // 没有这个模式，调用方得先知道屏幕尺寸才能避免"点靠近边缘就取不到图"，
            // 等于把同一件事在每个调用点重复实现一遍。
            x = x - w / 2;
            y = y - h / 2;
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (w <= 0 || h <= 0 || x >= sw || y >= sh) throw new InvalidOperationException("region center is outside the screen");
            if (x + w > sw) w = sw - x;
            if (y + h > sh) h = sh - y;
        }
        else
        {
            if (x < 0 || y < 0 || x >= sw || y >= sh) throw new InvalidOperationException("region starts outside the screen");
            if (x + w > sw) w = sw - x;
            if (y + h > sh) h = sh - y;
        }
        if (scale < 1) scale = 1;
        if (scale > 16) scale = 16;

        Bitmap full = EnsureBuffer(sw, sh);
        double before = Now();
        bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
        using (Bitmap crop = full.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
        {
            double after = Now();
            Bitmap output = crop;
            Bitmap scaled = null;
            if (scale > 1)
            {
                scaled = ScaleBitmap(crop, w * scale, h * scale);
                output = scaled;
            }
            seq++;
            string path = Path.Combine(outDir, "region-" + seq + ".jpg");
            SaveFrame(output, path, false, quality);
            long bytes = new FileInfo(path).Length;
            string response = "{\"ok\":true,\"seq\":" + seq
                + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                + ",\"scale\":" + scale
                + ",\"out_w\":" + output.Width + ",\"out_h\":" + output.Height
                + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(after)
                + ",\"path\":\"" + Esc(path) + "\",\"bytes\":" + bytes + "}";
            if (scaled != null) scaled.Dispose();
            return response;
        }
    }

    /// <summary>
    /// 对**已经落盘的一帧**裁剪（可选整数倍放大）。
    ///
    /// 为什么要有它：回看取回的帧是整屏图，交给模型时会被像素预算缩掉（本机 2560×1440 → 1066×600，
    /// 十几像素的小球只剩个位像素），要精读就得先裁。原先只有"现抓一帧再裁"的 region，对**历史帧**
    /// 无能为力 ⇒ 调用方只能自己读文件裁剪，多花一轮。这条命令把"裁剪历史帧"变成一次调用。
    /// </summary>
    private static string CropFrame(string path, int x, int y, int w, int h, int scale, int quality)
    {
        if (path == null || path.Length == 0) throw new InvalidOperationException("crop_frame needs a path");
        if (!File.Exists(path)) throw new InvalidOperationException("crop_frame source not found: " + path);
        using (Bitmap source = new Bitmap(path))
        {
            if (w <= 0 || h <= 0) throw new InvalidOperationException("crop_frame width/height must be positive");
            if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) throw new InvalidOperationException("crop_frame rect starts outside the frame");
            if (x + w > source.Width) w = source.Width - x;
            if (y + h > source.Height) h = source.Height - y;
            if (scale < 1) scale = 1;
            if (scale > 16) scale = 16;

            using (Bitmap crop = source.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
            {
                Bitmap output = crop;
                Bitmap scaled = null;
                if (scale > 1)
                {
                    scaled = ScaleBitmap(crop, w * scale, h * scale);
                    output = scaled;
                }
                seq++;
                string outPath = Path.Combine(outDir, "crop-" + seq + ".jpg");
                SaveFrame(output, outPath, false, quality);
                long bytes = new FileInfo(outPath).Length;
                string response = "{\"ok\":true,\"seq\":" + seq
                    + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                    + ",\"scale\":" + scale
                    + ",\"src_w\":" + source.Width + ",\"src_h\":" + source.Height
                    + ",\"out_w\":" + output.Width + ",\"out_h\":" + output.Height
                    + ",\"path\":\"" + Esc(outPath) + "\",\"bytes\":" + bytes + "}";
                if (scaled != null) scaled.Dispose();
                return response;
            }
        }
    }

    /// <summary>
    /// 最近邻整数倍放大。刻意不用插值：放大是为了**看清原始像素**（小字、图标边缘），
    /// 双线性/双三次会造出原图里并不存在的灰度，反而干扰判读。
    /// </summary>
    private static Bitmap ScaleBitmap(Bitmap source, int width, int height)
    {
        Bitmap target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        return target;
    }

    /// <summary>
    /// 高质量缩放（**缩略图**用）。缩略图是给人看方位的：最近邻缩小会造出块状锯齿、
    /// 让小目标与背景纹理混在一起，所以这里用双三次；最近邻只留给**放大**（读原始像素）。
    /// </summary>
    private static Bitmap ScaleBitmapSmooth(Bitmap source, int width, int height)
    {
        Bitmap target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        return target;
    }

    /// <summary>
    /// 抓一帧（或读一张已落盘的帧）、切成 cols×rows 块（每块落盘一张），返回每块对应的**像素矩形**；
    /// `sourcePath` 非空时图源是那张帧文件（回看过去的某一刻），空时抓当前屏。
    /// 可选再存一张**同帧缩略图**（整屏等比缩到 `thumbMaxPixels` 像素以内）。
    ///
    /// 与「连续调 N 次 region」的区别是**同帧**：N 次 region 各自抓一帧，在滚动、动画、
    /// 视频里拼出来的画面现实中并不存在，而模型不会知道。块数由调用方按屏幕尺寸与模型侧
    /// 像素预算算（每块 ≤ 640000 像素才会原样进模型），helper 只提供机制、不写死排版。
    ///
    /// 缩略图与块**同帧**同样要紧：缩略图给方位（一眼看全局），块给精度（1:1 原始像素）；
    /// 两者若来自不同时刻，"在缩略图上找到方位、去块里定位"就可能在动画里指错。
    /// </summary>
    private static string CaptureGrid(int cols, int rows, int quality, int scale, int thumbMaxPixels,
        string sourcePath)
    {
        if (cols < 1 || cols > 16 || rows < 1 || rows > 16)
            throw new InvalidOperationException("grid cols/rows must be within 1..16");
        if (scale < 1) scale = 1;
        if (scale > 16) scale = 16;

        // 图源两条：`sourcePath` 为空抓当前屏；非空则读那张**已经落盘的帧**——回看过去的某一刻
        // 只能走后者（那一刻已不在屏上）。两条路径共用下面同一套切块逻辑，所以块矩形的口径
        // 只有一处实现，不会随路径漂移。
        bool fromFrame = sourcePath != null && sourcePath.Length > 0;
        Bitmap full;
        int sw;
        int sh;
        double before = 0;
        double after = 0;
        if (fromFrame)
        {
            if (!File.Exists(sourcePath))
                throw new InvalidOperationException("grid frame source not found: " + sourcePath);
            // 把帧画进共享缓冲（而不是把 Bitmap 留在外面用）：后续逻辑一行不改，也不多一份
            // 生命周期要管。尺寸不一致时 EnsureBuffer 会按帧尺寸重建缓冲。
            using (Bitmap frame = new Bitmap(sourcePath))
            {
                sw = frame.Width;
                sh = frame.Height;
                full = EnsureBuffer(sw, sh);
                bufferGraphics.DrawImage(frame, 0, 0, sw, sh);
            }
        }
        else
        {
            sw = GetSystemMetrics(SM_CXSCREEN);
            sh = GetSystemMetrics(SM_CYSCREEN);
            full = EnsureBuffer(sw, sh);
            before = Now();
            // 只抓这一次：下面所有块都从这同一帧里 Clone 出来。
            bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
            after = Now();
        }

        // 同帧缩略图：等比缩到 thumbMaxPixels 像素以内（缩略图也占模型预算，超了会被再缩一次）。
        string thumbPath = "";
        int thumbWidth = 0;
        int thumbHeight = 0;
        if (thumbMaxPixels > 0)
        {
            double ratio = Math.Sqrt((double)thumbMaxPixels / (double)(sw * sh));
            if (ratio > 1.0) ratio = 1.0;
            // 向下取整，不四舍五入：2560×1440 四舍五入得 1067×600 = 640200，比预算多 200 像素，
            // 模型侧就会把它再缩一次——白多一次重采样，还会让"原样进模型"这句话不成立。
            thumbWidth = (int)Math.Floor(sw * ratio);
            thumbHeight = (int)Math.Floor(sh * ratio);
            if (thumbWidth < 1) thumbWidth = 1;
            if (thumbHeight < 1) thumbHeight = 1;
            while ((long)thumbWidth * thumbHeight > thumbMaxPixels && thumbWidth > 1) thumbWidth--;
            using (Bitmap thumbnail = ScaleBitmapSmooth(full, thumbWidth, thumbHeight))
            {
                seq++;
                thumbPath = Path.Combine(outDir, "thumb-" + seq + ".jpg");
                SaveFrame(thumbnail, thumbPath, false, quality);
            }
        }

        StringBuilder tiles = new StringBuilder();
        int count = 0;
        int outWidth = 0;
        int outHeight = 0;
        for (int row = 0; row < rows; row++)
        {
            int y = sh * row / rows;
            int y2 = sh * (row + 1) / rows;
            for (int col = 0; col < cols; col++)
            {
                int x = sw * col / cols;
                int x2 = sw * (col + 1) / cols;
                int w = x2 - x;
                int h = y2 - y;
                using (Bitmap crop = full.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
                {
                    Bitmap output = crop;
                    Bitmap scaled = null;
                    if (scale > 1)
                    {
                        scaled = ScaleBitmap(crop, w * scale, h * scale);
                        output = scaled;
                    }
                    seq++;
                    string path = Path.Combine(outDir, "grid-" + seq + ".jpg");
                    SaveFrame(output, path, false, quality);
                    if (count > 0) tiles.Append(",");
                    tiles.Append("{\"row\":" + row + ",\"col\":" + col
                        + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                        + ",\"out_w\":" + output.Width + ",\"out_h\":" + output.Height
                        + ",\"path\":\"" + Esc(path) + "\",\"bytes\":" + new FileInfo(path).Length + "}");
                    outWidth = output.Width;
                    outHeight = output.Height;
                    count++;
                    if (scaled != null) scaled.Dispose();
                }
            }
        }
        return "{\"ok\":true,\"source\":\"" + (fromFrame ? "frame" : "live") + "\",\"cols\":" + cols + ",\"rows\":" + rows
            + ",\"scale\":" + scale
            + ",\"screen_w\":" + sw + ",\"screen_h\":" + sh
            + ",\"tile_w\":" + outWidth + ",\"tile_h\":" + outHeight
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(after)
            + ",\"thumbnail\":{\"path\":\"" + Esc(thumbPath) + "\",\"w\":" + thumbWidth
            + ",\"h\":" + thumbHeight + "}"
            + ",\"tiles\":[" + tiles.ToString() + "]}";
    }

    /// <summary>
    /// 等一块区域发生变化，变化即返回那一帧（超时则返回"没变"）。
    ///
    /// 它解决的是"事件什么时候发生我不知道"：只有盲等 ms 的话，时长只能在"太短抓不到"与
    /// "太长错过窗口"之间赌，而在 1 秒级的展示窗口面前几乎必输。这里由 helper 按间隔轮询，
    /// 模型那几秒的思考时间不再落进窗口里。
    ///
    /// 只抓调用方指定的区域并按网格抽样比较像素，所以轮询很便宜——让调用方说清楚"关心哪块"
    /// （比如牌桌）比整屏轮询既快又准。
    /// </summary>
    private static string WaitChange(int x, int y, int w, int h, int timeoutMs, double threshold, int intervalMs)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("wait_change width/height must be positive");
        if (x < 0 || y < 0 || x >= sw || y >= sh) throw new InvalidOperationException("wait_change region starts outside the screen");
        if (x + w > sw) w = sw - x;
        if (y + h > sh) h = sh - y;
        if (timeoutMs <= 0) timeoutMs = 5000;
        // 上限压在 10 秒：WaitChange 是在**主循环线程里同步轮询**的，期间 helper 不处理任何其它
        // 命令——所有会话的 Computer Use 调用都会排队（2026-09-22 实测被三条独立线索误判成
        // "helper 卡死/进程死了"）。要等更久应当分多次调用，而不是让共享的 helper 停摆一分钟。
        if (timeoutMs > 10000) timeoutMs = 10000;
        if (intervalMs < 20) intervalMs = 20;
        if (threshold <= 0) threshold = 0.01;

        double start = Now();
        Bitmap buffer = EnsureBuffer(sw, sh);
        bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
        Bitmap reference = buffer.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb);
        double referenceAt = Now();
        try
        {
            while (true)
            {
                Thread.Sleep(intervalMs);
                bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
                using (Bitmap current = buffer.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
                {
                    double ratio = DiffRatio(reference, current);
                    double stamp = Now();
                    if (ratio >= threshold)
                    {
                        seq++;
                        string path = Path.Combine(outDir, "change-" + seq + ".jpg");
                        SaveFrame(current, path, false, 80);
                        return "{\"ok\":true,\"changed\":true"
                            + ",\"diff\":" + ratio.ToString("0.####", CultureInfo.InvariantCulture)
                            + ",\"waited_ms\":" + (int)((stamp - start) * 1000)
                            + ",\"reference_t\":" + F(referenceAt) + ",\"t\":" + F(stamp)
                            + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                            + ",\"path\":\"" + Esc(path) + "\"}";
                    }
                    if ((stamp - start) * 1000 >= timeoutMs)
                    {
                        // 超时分支同样给出基准帧时刻：调用方据此判断事件是否已经发生在基准帧
                        // **之前**（动作与等待之间那段间隙），这与"事件还没来"的补救动作不同；
                        // 只回 changed:false 会让这两种成因无法区分（2026-09-22 实测反馈）。
                        return "{\"ok\":true,\"changed\":false"
                            + ",\"diff\":" + ratio.ToString("0.####", CultureInfo.InvariantCulture)
                            + ",\"waited_ms\":" + (int)((stamp - start) * 1000)
                            + ",\"reference_t\":" + F(referenceAt) + ",\"t\":" + F(stamp)
                            + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h + "}";
                    }
                }
            }
        }
        finally
        {
            reference.Dispose();
        }
    }

    /// <summary>
    /// 抽样比较两图的「变化像素比例」（0..1）。抽样是为了让轮询便宜：逐像素比较在
    /// 2560×1440 上每次要几毫秒到几十毫秒，而这里只需要"变没变"这一个比特。
    /// </summary>
    private static double DiffRatio(Bitmap a, Bitmap b)
    {
        int width = Math.Min(a.Width, b.Width);
        int height = Math.Min(a.Height, b.Height);
        int step = Math.Max(1, Math.Min(width, height) / 32);
        int total = 0;
        int changed = 0;
        for (int py = 0; py < height; py += step)
        {
            for (int px = 0; px < width; px += step)
            {
                Color left = a.GetPixel(px, py);
                Color right = b.GetPixel(px, py);
                int delta = Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
                total++;
                if (delta > 30) changed++;
            }
        }
        if (total == 0) return 0;
        return (double)changed / total;
    }

    /// <summary>区域内匹配指定颜色（每通道容差 tol）的像素比例；按步长采样，够判断"有没有出现"。</summary>
    private static double MatchRatio(Bitmap bmp, int r, int g, int b, int tol)
    {
        int width = bmp.Width;
        int height = bmp.Height;
        int step = Math.Max(1, Math.Min(width, height) / 48);
        int total = 0;
        int hit = 0;
        for (int py = 0; py < height; py += step)
        {
            for (int px = 0; px < width; px += step)
            {
                Color c = bmp.GetPixel(px, py);
                total++;
                if (Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol) hit++;
            }
        }
        if (total == 0) return 0;
        return (double)hit / total;
    }

    /// <summary>
    /// 条件触发：盯住一块区域，条件一达成就**当场注入**动作（在同一个循环里）。
    ///
    /// 为什么不能由调用方"看到画面再调工具"：那条链路是「读帧 → 模型推理 → 调用 → 注入」，
    /// 实测批次内的定时精度只有 ±0.2~0.3 秒，而目标窗口可能只有 125ms（2026-09-22 三局失败的
    /// 共同瓶颈）。把"看"和"动"放进同一次抓屏之后，触发到注入只剩注入本身的开销。
    ///
    /// mode=change：区域相对基准帧的变化比例 ≥ ratio 即触发；
    /// mode=match ：区域内匹配 color（每通道容差 tol）的像素比例 ≥ ratio 即触发。
    /// action：none / click（在 ax,ay 点击，未给坐标则点当前位置）/ key（按住 key 保持 hold_ms 再抬起，
    /// hold_ms=0 即瞬时按放）。delay_ms 是触发后再等的毫秒数，补偿"变化被看见时其实已经晚了"的场景。
    ///
    /// prime（可选）：**开始盯之前先点一下 prime_x,prime_y**。时机类任务的通用形态是"点 Start 才开计时"，
    /// 与"盯住目标出现"必须在同一次调用里完成——模型分两次调用会隔着一整轮推理（秒级），
    /// 而事件可能在点下后 1 秒就发生。基准帧在 prime 之后拍，因此点击引起的画面变化不会被误判成触发。
    /// </summary>
    private static string ActWhen(int x, int y, int w, int h, string mode, string color, int tol,
        double ratio, int timeoutMs, int intervalMs, string action, string keyName, int holdMs,
        int ax, int ay, bool hasPoint, string button, int delayMs,
        bool hasPrime, int primeX, int primeY)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("act_when width/height 必须为正");
        if (x < 0 || y < 0 || x >= sw || y >= sh) throw new InvalidOperationException("act_when 区域起点在屏幕外");
        if (x + w > sw) w = sw - x;
        if (y + h > sh) h = sh - y;
        if (timeoutMs <= 0) timeoutMs = 5000;
        // 与 wait_change 同一个理由：本循环跑在主循环线程里，等待期间所有会话的命令都排队。
        if (timeoutMs > 10000) timeoutMs = 10000;
        if (intervalMs < 10) intervalMs = 10;
        if (ratio <= 0) ratio = 0.02;
        if (tol < 0) tol = 0;

        int cr = 0, cg = 0, cb = 0;
        if (mode == "match")
        {
            string hex = (color ?? "").TrimStart('#');
            if (hex.Length != 6) throw new InvalidOperationException("act_when：match 模式需要 color=#RRGGBB");
            cr = Convert.ToInt32(hex.Substring(0, 2), 16);
            cg = Convert.ToInt32(hex.Substring(2, 2), 16);
            cb = Convert.ToInt32(hex.Substring(4, 2), 16);
        }

        double start = Now();
        Bitmap buffer = EnsureBuffer(sw, sh);
        if (hasPrime)
        {
            // 预备动作：先把"开始这件事"点下去，再拍基准帧——顺序反了会把点击造成的画面变化当成触发。
            MouseMoveAbsolute(primeX, primeY);
            MouseButton(button, "click", 1);
            Thread.Sleep(30);
        }
        start = Now();
        bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
        Bitmap reference = buffer.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb);
        double referenceAt = Now();
        try
        {
            while (true)
            {
                Thread.Sleep(intervalMs);
                bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
                using (Bitmap current = buffer.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
                {
                    double metric = mode == "match"
                        ? MatchRatio(current, cr, cg, cb, tol)
                        : DiffRatio(reference, current);
                    double detected = Now();
                    if (metric >= ratio)
                    {
                        seq++;
                        string path = Path.Combine(outDir, "when-" + seq + ".jpg");
                        SaveFrame(current, path, false, 80);
                        if (delayMs > 0) Thread.Sleep(delayMs);
                        string act = "null";
                        if (action == "click")
                        {
                            if (hasPoint) MouseMoveAbsolute(ax, ay);
                            act = MouseButton(button, "click", 1);
                        }
                        else if (action == "key")
                        {
                            if (holdMs > 0)
                            {
                                KeyEvent(keyName, "down", 0);
                                Thread.Sleep(holdMs);
                                act = KeyEvent(keyName, "up", 0);
                            }
                            else act = KeyEvent(keyName, "press", 0);
                        }
                        return "{\"ok\":true,\"fired\":true,\"mode\":\"" + Esc(mode) + "\""
                            + ",\"metric\":" + metric.ToString("0.####", CultureInfo.InvariantCulture)
                            + ",\"waited_ms\":" + (int)((detected - start) * 1000)
                            + ",\"reaction_ms\":" + (int)((Now() - detected) * 1000)
                            + ",\"reference_t\":" + F(referenceAt) + ",\"t\":" + F(detected)
                            + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                            + ",\"act\":" + act + ",\"path\":\"" + Esc(path) + "\"}";
                    }
                    if ((detected - start) * 1000 >= timeoutMs)
                    {
                        return "{\"ok\":true,\"fired\":false,\"mode\":\"" + Esc(mode) + "\""
                            + ",\"metric\":" + metric.ToString("0.####", CultureInfo.InvariantCulture)
                            + ",\"waited_ms\":" + (int)((detected - start) * 1000)
                            + ",\"reference_t\":" + F(referenceAt) + ",\"t\":" + F(detected)
                            + ",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h + "}";
                    }
                }
            }
        }
        finally
        {
            reference.Dispose();
        }
    }

    private static int Main(string[] args)
    {
        try { SetProcessDpiAwareness(PER_MONITOR_DPI_AWARE); }
        catch (Exception) { /* 老系统没有 shcore；本机已实测 100% 缩放，缺失不影响坐标 */ }
        QueryPerformanceFrequency(out freq);
        QueryPerformanceCounter(out origin);
        outDir = Path.Combine(Path.GetTempPath(), "dsh-cu");
        Directory.CreateDirectory(outDir);
        int maxFrames = 400;
        if (args.Length > 0)
        {
            int parsed;
            if (int.TryParse(args[0], out parsed) && parsed > 0) maxFrames = parsed;
        }

        // 两端都必须钉死 UTF-8：插件侧的 stdio 全是 UTF-8，而 .NET 默认按**系统 ANSI 代码页**
        // （本机 CP936）解读 stdin——中文一到就整串乱码（每个汉字变成 1.5 个乱码字符），
        // 而 ASCII 部分照常正常，所以这种缺陷只有真的输入一次中文才暴露。用不带 BOM 的
        // UTF8Encoding：BOM 会被当成正文的第一个字符注入到桌面。
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        bool running = true;
        while (running)
        {
            string line = Console.In.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            string response;
            string cmd = Field(line, "cmd", "");
            try
            {
                if (cmd == "ping")
                {
                    response = "{\"ok\":true,\"protocol\":1,\"t\":" + F(Now())
                        + ",\"outDir\":\"" + Esc(outDir) + "\"," + ScreenInfo() + "}";
                }
                else if (cmd == "capture")
                {
                    response = Capture(IntField(line, "quality", 70), Field(line, "format", "jpg") == "png");
                }
                else if (cmd == "region")
                {
                    response = CaptureRegion(
                        IntField(line, "x", 0), IntField(line, "y", 0),
                        IntField(line, "w", 0), IntField(line, "h", 0),
                        IntField(line, "quality", 70), IntField(line, "scale", 1),
                        IntField(line, "center", 0));
                }
                else if (cmd == "crop_frame")
                {
                    response = CropFrame(Field(line, "path", ""),
                        IntField(line, "x", 0), IntField(line, "y", 0),
                        IntField(line, "w", 0), IntField(line, "h", 0),
                        IntField(line, "scale", 1), IntField(line, "quality", 70));
                }
                else if (cmd == "grid")
                {
                    // 一次抓帧切 cols×rows 块（同帧保证）：块数由调用方按屏幕尺寸与
                    // 模型侧像素预算算，helper 不写死排版。thumb_max_pixels ≤0 表示不出缩略图。
                    response = CaptureGrid(IntField(line, "cols", 2), IntField(line, "rows", 3),
                        IntField(line, "quality", 70), IntField(line, "scale", 1),
                        IntField(line, "thumb_max_pixels", 640000), Field(line, "frame", ""));
                }
                else if (cmd == "bench")
                {
                    response = Field(line, "backend", "gdi") == "dxgi"
                        ? BenchDxgi(IntField(line, "frames", 20), IntField(line, "quality", 70))
                        : Bench(IntField(line, "frames", 20), IntField(line, "quality", 70));
                }
                else if (cmd == "cursor") response = CursorState();
                // 诊断：某坐标上是哪个窗口 / 当前前台窗口。用来回答"那里到底是没东西，
                // 还是有东西但我看不见"——原生模态框就属于后者。
                else if (cmd == "wait_change")
                {
                    // 事件驱动的等待：给一块区域，变化发生就返回那一帧（比盲等 ms 强得多）。
                    response = WaitChange(
                        IntField(line, "x", 0), IntField(line, "y", 0),
                        IntField(line, "w", 0), IntField(line, "h", 0),
                        IntField(line, "timeout_ms", 5000),
                        DoubleField(line, "threshold", 0.01),
                        IntField(line, "interval_ms", 100));
                }
                else if (cmd == "act_when")
                {
                    response = ActWhen(
                        IntField(line, "x", 0), IntField(line, "y", 0),
                        IntField(line, "w", 0), IntField(line, "h", 0),
                        Field(line, "mode", "change"), Field(line, "color", ""), IntField(line, "tol", 24),
                        DoubleField(line, "ratio", 0.02), IntField(line, "timeout_ms", 5000),
                        IntField(line, "interval_ms", 20), Field(line, "action", "none"),
                        Field(line, "name", ""), IntField(line, "hold_ms", 0),
                        IntField(line, "ax", 0), IntField(line, "ay", 0),
                        IntField(line, "has_point", 0) != 0, Field(line, "button", "left"),
                        IntField(line, "delay_ms", 0),
                        IntField(line, "has_prime", 0) != 0, IntField(line, "prime_x", 0), IntField(line, "prime_y", 0));
                }
                else if (cmd == "windows") response = ListWindows();
                else if (cmd == "window_under")
                {
                    int wx = IntField(line, "x", 0);
                    int wy = IntField(line, "y", 0);
                    response = "{\"ok\":true,\"x\":" + wx + ",\"y\":" + wy
                        + ",\"window\":" + WindowUnder(wx, wy) + ",\"t\":" + F(Now()) + "}";
                }
                else if (cmd == "foreground")
                {
                    response = "{\"ok\":true,\"window\":" + DescribeWindow(GetForegroundWindow())
                        + ",\"t\":" + F(Now()) + "}";
                }

                // ── 注入：与插件侧的动作工具一一对应，坐标一律物理像素 ──
                // 只读的输入法状态查询：不注入、不改状态，供"键盘没反应"时先分辨成因。
                else if (cmd == "ime")
                    response = "{\"ok\":true,\"ime_open\":" + ImeOpenStatus() + ",\"t\":" + F(Now()) + "}";
                else if (cmd == "mouse_move")
                    response = MouseMoveAbsolute(IntField(line, "x", 0), IntField(line, "y", 0));
                else if (cmd == "mouse_move_by")
                    response = MouseMoveRelative(IntField(line, "dx", 0), IntField(line, "dy", 0));
                else if (cmd == "click")
                    response = MouseButton(Field(line, "button", "left"),
                        Field(line, "action", "click"), IntField(line, "count", 1));
                else if (cmd == "scroll")
                    response = Scroll(IntField(line, "vertical", 0), IntField(line, "horizontal", 0));
                else if (cmd == "key")
                    response = KeyEvent(Field(line, "name", ""), Field(line, "action", "press"),
                        IntField(line, "holdMs", 50));
                else if (cmd == "hotkey")
                    response = Hotkey(StringArrayField(line, "keys"));
                else if (cmd == "type_text")
                    response = TypeText(Field(line, "text", ""));

                else if (cmd == "wait")
                {
                    // 上限 60 秒：模型不该让会话挂太久，需要更长的等待应该是"分多次"的语义。
                    // 这个等待会占住主循环（其他会话的命令排队），所以插件层只在小额等待时用它。
                    int waitMs = IntField(line, "ms", 0);
                    if (waitMs < 0) waitMs = 0;
                    if (waitMs > 60000) waitMs = 60000;
                    double sleepFrom = Now();
                    Thread.Sleep(waitMs);
                    double sleepTo = Now();
                    // 回执给**实测**：Now() 走 Windows QPC，是物理时间；而调用方（WSL 侧 Node）
                    // 自己的钟比它慢约 12%（2026-09-22 实测：请求 1500ms 在 QPC 上是 1683ms），
                    // 所以"等了多少"必须以这里为准，不能拿调用方的钟去推。
                    response = "{\"ok\":true,\"requested_ms\":" + waitMs
                        + ",\"actual_ms\":" + (int)((sleepTo - sleepFrom) * 1000)
                        + ",\"t\":" + F(sleepTo) + "}";
                }
                else if (cmd == "dxgi_probe")
                {
                    // 诊断用：在**主线程**里直接验证 DXGI 能否工作，用来区分
                    // 「我的采集线程有问题」与「这个进程里 DXGI 就不能用」。
                    double p0 = Now();
                    DesktopDuplication probeCapture = new DesktopDuplication(null);
                    bool got = probeCapture.ReadFrame(1500);
                    double p1 = Now();
                    string probePath = Path.Combine(outDir, "dxgi-probe.jpg");
                    if (got) SaveFrame(probeCapture.Image, probePath, false, 70);
                    response = "{\"ok\":true,\"fresh\":" + (got ? "true" : "false")
                        + ",\"w\":" + probeCapture.Width + ",\"h\":" + probeCapture.Height
                        + ",\"display\":\"" + Esc(probeCapture.DisplayName) + "\""
                        + ",\"read_ms\":" + F((p1 - p0) * 1000)
                        + ",\"path\":\"" + Esc(probePath) + "\"}";
                    probeCapture.Dispose();
                }
                else if (cmd == "drag")
                {
                    response = Drag(IntField(line, "dx", 0), IntField(line, "dy", 0),
                        Field(line, "button", "left"), IntField(line, "steps", 12),
                        IntField(line, "step_delay_ms", 10));
                }
                else if (cmd == "live_start")
                {
                    response = LiveStart(IntField(line, "interval_ms", 33),
                        IntField(line, "quality", 70), IntField(line, "capacity", 1800),
                        Field(line, "backend", "gdi"), Field(line, "codec", "jpeg"),
                        IntField(line, "segment_frames", 150), DoubleField(line, "retain_s", 1200),
                        Field(line, "record_path", ""));
                }
                else if (cmd == "live_stop") response = LiveStop();
                else if (cmd == "live_stats") response = LiveStats();
                else if (cmd == "latest") response = Latest();
                else if (cmd == "frames")
                {
                    response = FramesIn(DoubleField(line, "from", 0),
                        DoubleField(line, "to", double.MaxValue), IntField(line, "limit", 64));
                }
                else if (cmd == "h264_probe")
                {
                    // 验证 MF H.264 编码链路：只抓屏 + 编码，不注入任何输入。
                    response = H264Probe(IntField(line, "frames", 60),
                        (uint)IntField(line, "quality", 90), IntField(line, "segment_frames", 150),
                        Field(line, "backend", "gdi"), IntField(line, "interval_ms", 0));
                }
                else if (cmd == "diff")
                {
                    // 变化检测：只读像素并算差分，不注入任何输入。
                    response = DiffFrames(DoubleField(line, "from", 0), DoubleField(line, "to", 1e9),
                        IntField(line, "limit", 8), IntField(line, "cell", 160),
                        DoubleField(line, "ratio", 0.02), IntField(line, "max_boxes", 8));
                }
                else if (cmd == "quit")
                {
                    LiveStop();
                    response = "{\"ok\":true,\"bye\":true}";
                    running = false;
                }
                else
                {
                    response = "{\"ok\":false,\"error\":\"unknown cmd: " + Esc(cmd) + "\"}";
                }
            }
            catch (Exception error)
            {
                response = "{\"ok\":false,\"error\":\"" + Esc(error.Message) + "\"}";
            }

            Console.Out.Write(response);
            Console.Out.Write("\n");
            Console.Out.Flush();
            PruneOldFrames(maxFrames);
        }
        return 0;
    }

    /// <summary>Media Foundation 是否已在本进程初始化过（常驻进程只需要初始化一次）。</summary>
    private static bool mediaFoundationReady;

    /// <summary>
    /// 首次使用 Media Foundation 之前初始化一次。
    ///
    /// 常驻进程必须自己做这件事：Media Foundation 不会自动就绪，没初始化时
    /// `MFCreateSinkWriterFromURL` 会返回一个与"参数不对"难以区分的错误码（实测 0xC00D3E85）。
    /// 反复 startup/shutdown 既慢又没必要，所以用标志只做一次。
    /// </summary>
    /// <summary>COM 按线程初始化：MFStartup 是进程级、只做一次，但每个真正调用 MF 的线程都要 CoInitializeEx。
    /// 只用一个进程级标记会让"第一个调用者所在的线程"成为唯一被初始化的线程，别的线程上 CoCreateInstance 直接报 CO_E_NOTINITIALIZED。</summary>
    [ThreadStatic] private static bool mediaFoundationOnThread;

    private static void EnsureMediaFoundation()
    {
        if (mediaFoundationReady && mediaFoundationOnThread) return;
        int com = MF.CoInitializeEx(IntPtr.Zero, 0);
        // RPC_E_CHANGED_MODE：本线程已按别的套间模型初始化过。MF 在 MTA 下工作，这里不必失败。
        if (com < 0 && com != unchecked((int)0x80010106))
            throw new InvalidOperationException("CoInitializeEx: 0x" + com.ToString("X8"));
        if (!mediaFoundationReady)
        {
            MF.Check(MF.MFStartup(0x20070, 0), "MFStartup");
            mediaFoundationReady = true;
        }
        mediaFoundationOnThread = true;
    }

    /// <summary>
    /// 在 helper 进程里验证 Media Foundation H.264 编码链路（编码器移植自 Codex 的 MFH264.cs）。
    ///
    /// 只抓屏 + 编码、不注入任何输入：它回答的是"这段代码在本进程能不能跑、跑多快、产出多大"。
    /// **直接抓进 32bpp 缓冲**——MF 的 ARGB32 在内存里就是 BGRA，与 GDI 的 32bpp 布局一致，
    /// 于是整条链不需要任何色彩转换（省掉 14.7 MB/帧的热路径）。
    ///
    /// 切片按 segmentFrames 封片，每片独立可解码——这是 20 分钟环形缓冲的结构基础。
    /// </summary>
    /// <summary>内存里的一片 H.264 切片（独立可解码的 MP4）。</summary>
    private sealed class H264Slice
    {
        public byte[] Data;
        /// <summary>这片第一帧的采集时刻（QPC 秒）。</summary>
        public double Start;
        /// <summary>这片最后一帧的采集时刻（QPC 秒）。片内时间戳按名义 30 fps 走，
        /// 与真实采集速率并不相等，换算回真实时刻要靠 Start/End 这对锚点。</summary>
        public double End;
        public int Frames;
    }

    private static readonly object h264Lock = new object();
    private static System.Collections.Generic.List<H264Slice> h264Ring = new System.Collections.Generic.List<H264Slice>();
    /** 每片帧数（150 = 5 秒 @30fps）。片长是"体积 / CPU / 未封片窗口"三者的折中：
     *  1 秒片要每秒重建编码器（实测单核 35.9%），10 秒片未封窗口太长。 */
    private static int h264SegmentFrames = 150;
    /** 保留时长（秒）。主人定的目标是 20 分钟。 */
    private static double h264RetainSeconds = 1200;
    /** 内存预算上限：时间够长但字节超了也要淘汰，避免画质/内容变化时把内存吃穿。 */
    private static long h264BudgetBytes = 900L * 1024 * 1024;
    private static int h264SegmentCount;
    private static long h264TotalBytes;
    private static double h264LastSeconds;
    /** 已封片覆盖到的最新时刻（QPC 秒）。正在写的那一片不在里面，落在它之后的窗口取不到——
     *  除非先请采集线程结算掉（见 RequestH264Seal）。 */
    private static double h264SealedUntil;
    /** 取帧请求伸进未封片区时置位：采集线程写完当前帧就结算这一片。 */
    private static volatile bool h264FlushRequested;
    /** 上次按需结算的时刻（QPC 秒）。限流用——否则连续的取帧请求会把片切成一帧一片。 */
    private static double h264LastFlushAt;
    /** 按需结算的最小片长（帧）：太短的片不值得重建编码器。 */
    private static readonly int h264SealMinFrames = 2;
    /** 按需结算的最小间隔（秒）。 */
    private static readonly double h264SealMinGap = 1.0;

    /// <summary>按时间与字节两个预算淘汰最老的片（调用方必须持有 h264Lock）。</summary>
    private static void PruneH264()
    {
        double now = Now();
        while (h264Ring.Count > 1)
        {
            bool tooOld = (now - h264Ring[0].Start) > h264RetainSeconds;
            bool tooBig = h264TotalBytes > h264BudgetBytes;
            if (!tooOld && !tooBig) break;
            h264TotalBytes -= h264Ring[0].Data.Length;
            h264Ring.RemoveAt(0);
        }
    }

    /// <summary>把当前编码器结算成一片放进环里——到期封片与按需结算走同一条路径，避免两套行为漂移。</summary>
    private static void SealH264Segment(MemoryEncoder encoder, double segmentStart, double lastFrameAt, int frames)
    {
        byte[] sliceBytes = encoder.Finish();
        encoder.Dispose();
        lock (h264Lock)
        {
            H264Slice slice = new H264Slice();
            slice.Data = sliceBytes;
            slice.Start = segmentStart;
            slice.End = lastFrameAt;
            slice.Frames = frames;
            h264Ring.Add(slice);
            h264TotalBytes += sliceBytes.Length;
            h264SegmentCount++;
            h264LastSeconds = Now();
            if (lastFrameAt > h264SealedUntil) h264SealedUntil = lastFrameAt;
            PruneH264();
        }
    }

    /// <summary>
    /// 请采集线程立刻结算正在写的那一片，并等它完成（最多约 1.2 秒）。
    ///
    /// 存在的理由：未封片的那一片取不到画面，于是"最近不到一个片长（默认 5 秒）"的窗口取不到。
    /// 常态化缩短片长会推高 CPU（1 秒片实测单核 35.9%，5 秒片 27%），所以改成**只在有人真要这段画面时**
    /// 付一次结算代价。两道限流（最短片长、最小间隔）避免连续请求把片切碎。
    /// 代价：等待期间它占住调用方（helper 主循环）——与 `wait_change` 同样的已知约束。
    /// </summary>
    private static void RequestH264Seal()
    {
        if (!liveRunning || liveCodec != "h264") return;
        if (Now() - h264LastFlushAt < h264SealMinGap) return;
        lock (h264Lock)
        {
            if (h264Ring.Count == 0 && liveCount < h264SealMinFrames) return;
        }
        h264LastFlushAt = Now();
        h264FlushRequested = true;
        int guard = 0;
        while (h264FlushRequested && guard++ < 60) Thread.Sleep(20);
    }

    /// <summary>
    /// H.264 采集线程：抓帧 → 硬件编码 → 每 segmentFrames 帧封一片 → 进内存环形，按预算滚动淘汰。
    ///
    /// 为什么不是逐帧 JPEG：同一目标（2560×1440 @30fps、20 分钟）JPEG 要 6–11 GB、单核 44%，
    /// 实测 H.264 只要 **445 MB、单核 27%**。而"每片独立可解"是关键——删最老片就是丢掉一块内存，
    /// 不需要重封装整个文件。
    /// </summary>
    private static void LiveLoopH264()
    {
        int interval = liveIntervalMs;
        int segmentFrames = h264SegmentFrames;
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        DesktopDuplication capture = null;
        Graphics context = null;
        Bitmap grab = null;
        if (liveBackend == "dxgi")
        {
            capture = new DesktopDuplication(null);
            int guard = 0;
            while (!capture.HasImage && guard++ < 16 && liveRunning) capture.ReadFrame(500);
            if (!capture.HasImage) throw new InvalidOperationException("DXGI 等 8 秒仍无画面变化，取不到首帧");
            sw = capture.Width;
            sh = capture.Height;
        }
        else
        {
            grab = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            context = Graphics.FromImage(grab);
        }
        Rectangle rect = new Rectangle(0, 0, sw, sh);
        int stride = sw * 4;
        byte[] pixels = new byte[stride * sh];
        MemoryEncoder encoder = null;
        // 片边界空洞的来源：封片要 Finish+Dispose 旧编码器、再**新建**一个（2560×1440 实测：创建 ~220 ms、
        // Finish+Dispose ~90 ms）⇒ 这 ~310 ms 里采集停摆，时间轴上留下一段没有帧的空洞（实测约 0.12 s
        // ≈ 4 帧）。对策：在当前片还剩若干帧时，用**后台线程先把下一个编码器建好**，封片时直接接手，
        // 停摆只剩 Finish+Dispose。（跨线程创建 + 主线程使用已单独验证可行。）
        MemoryEncoder pending = null;
        Thread pendingThread = null;
        Exception pendingError = null;
        int prebuildLead = segmentFrames > 24 ? segmentFrames / 3 : 8;
        int index = 0;
        int inSegment = 0;
        double segmentStart = 0;
        double lastFrameAt = 0;
        EnsureMediaFoundation();
        // 录像：把同一份像素再喂给一个**连续**的文件编码器（不切片，整段一个 mp4）。
        // 打开失败不能拖垮采集——记下原因继续录内存环，调用方从 live_stats 能看到。
        if (liveRecordPath.Length > 0)
        {
            try
            {
                liveRecorder = new FileEncoder(liveRecordPath, sw, sh, true, "quality", (uint)liveQuality, MF.ARGB32);
                liveRecordFrames = 0;
                liveRecordStart = Now();
            }
            catch (Exception recordFailure)
            {
                liveRecorder = null;
                liveRecordError = recordFailure.Message;
                Console.Error.WriteLine("录像打开失败（继续采集，不落盘）：{0}", recordFailure.Message);
            }
        }
        try
        {
            while (liveRunning)
            {
                if (encoder == null)
                {
                    if (pendingThread != null)
                    {
                        pendingThread.Join();
                        pendingThread = null;
                    }
                    MemoryEncoder built;
                    Exception buildError;
                    lock (pendingLock) { built = pending; pending = null; buildError = pendingError; pendingError = null; }
                    if (buildError != null)
                    {
                        Console.Error.WriteLine("h264 预建编码器失败，回退到同步创建：{0}", buildError.Message);
                    }
                    encoder = built != null
                        ? built
                        : new MemoryEncoder(sw, sh, true, "quality", (uint)liveQuality, MF.ARGB32);
                    inSegment = 0;
                }
                Bitmap source;
                if (capture != null)
                {
                    capture.ReadFrame(0);   // 非阻塞：静止桌面复用上一帧，时间轴不留空洞
                    source = capture.Image;
                }
                else
                {
                    context.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
                    source = grab;
                }
                BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
                try
                {
                    if (data.Stride == stride)
                    {
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    }
                    else
                    {
                        for (int row = 0; row < sh; row++)
                        {
                            Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)row * data.Stride),
                                pixels, row * stride, stride);
                        }
                    }
                }
                finally
                {
                    source.UnlockBits(data);
                }
                // 记下每一帧的真实采集时刻：片内时间戳是按名义 30 fps 写的（见 MemoryEncoder.Write），
                // 而实际采集间隔受抓屏与编码耗时影响（GDI 实测约 27.7 fps）⇒ 片内时间轴会相对真实时间
                // 累积偏差（一片最多约 0.4 秒）。用片首片尾两帧的真实时刻做线性映射，取帧才能报出对的时刻。
                double frameAt = Now();
                if (inSegment == 0) segmentStart = frameAt;
                lastFrameAt = frameAt;
                if (liveRecorder != null)
                {
                    // 时间戳取真实采集时刻（不是帧号）——文件播放速度据此还原，采集率波动时也不会被压缩。
                    try { liveRecorder.Write(pixels, frameAt - liveRecordStart); liveRecordFrames++; }
                    catch (Exception recordFailure)
                    {
                        liveRecordError = recordFailure.Message;
                        liveRecorder = null;
                        Console.Error.WriteLine("录像写入失败，已停写该文件：{0}", recordFailure.Message);
                    }
                }
                encoder.Write(pixels, inSegment);
                inSegment++;
                index++;
                liveCount = index;

                // 到期封片，以及**按需结算**（有取帧请求伸进了还没封的这一片）——两者共用同一条结算路径。
                // 片快写满时先把下一个编码器建好（后台线程）：封片时就不用等那 ~220 ms 的创建。
                if (pendingThread == null && inSegment >= segmentFrames - prebuildLead && liveRunning)
                {
                    MemoryEncoder stale;
                    lock (pendingLock) { stale = pending; pending = null; }
                    if (stale != null) stale.Dispose();
                    pendingThread = new Thread(delegate()
                    {
                        try
                        {
                            MF.CoInitializeEx(IntPtr.Zero, 0);
                            MemoryEncoder fresh = new MemoryEncoder(sw, sh, true, "quality", (uint)liveQuality, MF.ARGB32);
                            lock (pendingLock) { pending = fresh; }
                        }
                        catch (Exception buildFailure)
                        {
                            lock (pendingLock) { pendingError = buildFailure; }
                        }
                    });
                    pendingThread.IsBackground = true;
                    pendingThread.Start();
                }

                bool due = inSegment >= segmentFrames;
                bool onDemand = h264FlushRequested && inSegment >= h264SealMinFrames;
                if (due || onDemand)
                {
                    SealH264Segment(encoder, segmentStart, lastFrameAt, inSegment);
                    encoder = null;
                    h264FlushRequested = false;
                }

                if (interval > 0)
                {
                    double next = liveStart + (index + 1) * (interval / 1000.0);
                    while (liveRunning && Now() < next)
                    {
                        int nap = (int)Math.Round((next - Now()) * 1000);
                        if (nap > 0) Thread.Sleep(nap);
                    }
                }
            }
        }
        finally
        {
            if (pendingThread != null)
            {
                try { pendingThread.Join(3000); }
                catch (ThreadStateException) { Console.Error.WriteLine("h264 预建线程没能启动，跳过等待"); }
                pendingThread = null;
            }
            MemoryEncoder leftover;
            lock (pendingLock) { leftover = pending; pending = null; }
            if (leftover != null) leftover.Dispose();
            // 收尾录像文件：Finalize 才写文件尾（moov 索引），不调用就得到一个播放器读不了的文件。
            if (liveRecorder != null)
            {
                try { liveRecorder.Finish(); }
                catch (Exception finishFailure)
                {
                    liveRecordError = finishFailure.Message;
                    Console.Error.WriteLine("录像收尾失败：{0}", finishFailure.Message);
                }
                liveRecorder.Dispose();
                liveRecorder = null;
            }
            if (encoder != null) encoder.Dispose();
            if (context != null) context.Dispose();
            if (grab != null) grab.Dispose();
            if (capture != null) capture.Dispose();
        }
    }

    private static string H264Probe(int frames, uint quality, int segmentFrames, string backend, int intervalMs)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (frames < 1) frames = 30;
        if (frames > 7200) frames = 7200;
        if (segmentFrames < 1) segmentFrames = 150;
        EnsureMediaFoundation();

        // 帧源开关：dxgi 走 DesktopDuplication（读帧 0.42 ms），gdi 走 CopyFromScreen（约 15 ms）。
        // 它存在的意义是把"采集"与"编码"的成本分开量——瓶颈判错方向会白优化一整轮。
        DesktopDuplication capture = null;
        Graphics context = null;
        Bitmap grab = null;
        if (backend == "dxgi")
        {
            capture = new DesktopDuplication(null);
            int guard = 0;
            while (!capture.HasImage && guard++ < 40) capture.ReadFrame(500);
            if (!capture.HasImage) throw new InvalidOperationException("DXGI 取不到首帧");
            sw = capture.Width;
            sh = capture.Height;
        }
        else
        {
            grab = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            context = Graphics.FromImage(grab);
        }

        Rectangle rect = new Rectangle(0, 0, sw, sh);
        int stride = sw * 4;
        byte[] pixels = new byte[stride * sh];
        System.Collections.Generic.List<int> slices = new System.Collections.Generic.List<int>();
        System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        double cpuBefore = self.TotalProcessorTime.TotalSeconds;
        string encoderName = "";
        MemoryEncoder encoder = null;
        int totalBytes = 0;
        int encoded = 0;
        double started = Now();

        try
        {
            for (int index = 0; index < frames; index++)
            {
                if (index % segmentFrames == 0)
                {
                    if (encoder != null)
                    {
                        byte[] slice = encoder.Finish();
                        slices.Add(slice.Length);
                        totalBytes += slice.Length;
                        encoder.Dispose();
                        encoder = null;
                    }
                    encoder = new MemoryEncoder(sw, sh, true, "quality", quality, MF.ARGB32);
                    if (encoderName.Length == 0 && encoder.EncoderName != null) encoderName = encoder.EncoderName;
                }
                Bitmap source;
                if (capture != null)
                {
                    capture.ReadFrame(0);   // 非阻塞：静止桌面没有新帧时复用上一帧
                    source = capture.Image;
                }
                else
                {
                    context.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
                    source = grab;
                }
                BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
                try
                {
                    if (data.Stride == stride)
                    {
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    }
                    else
                    {
                        // 逐行拷贝：行间距可能带 padding（32bpp 通常等于 width*4，但不假定）。
                        for (int row = 0; row < sh; row++)
                        {
                            Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)row * data.Stride),
                                pixels, row * stride, stride);
                        }
                    }
                }
                finally
                {
                    source.UnlockBits(data);
                }
                // 片内帧号从 0 起：每片是独立 MP4，PTS 不能带着上一片的偏移。
                encoder.Write(pixels, index % segmentFrames);
                encoded++;
                // 节流：只有按目标帧率跑的 CPU 才代表"常驻"的开销——不限速跑出来的百分比
                // 是吞吐上限，不是占用量（两者差好几倍，拿错口径会把结论说反）。
                if (intervalMs > 0)
                {
                    double due = (index + 1) * intervalMs / 1000.0;
                    double spent = Now() - started;
                    if (due > spent) Thread.Sleep((int)Math.Round((due - spent) * 1000));
                }
            }
            if (encoder != null)
            {
                byte[] last = encoder.Finish();
                slices.Add(last.Length);
                totalBytes += last.Length;
                encoder.Dispose();
                encoder = null;
            }
        }
        finally
        {
            if (encoder != null) encoder.Dispose();
            if (context != null) context.Dispose();
            if (grab != null) grab.Dispose();
            if (capture != null) capture.Dispose();
        }

        double wall = Now() - started;
        double cpu = self.TotalProcessorTime.TotalSeconds - cpuBefore;
        StringBuilder list = new StringBuilder();
        for (int i = 0; i < slices.Count; i++)
        {
            if (i > 0) list.Append(",");
            list.Append(slices[i]);
        }
        long projected = encoded > 0 ? (long)totalBytes * 36000 / encoded : 0;
        return "{\"ok\":true,\"frames\":" + encoded + ",\"segments\":" + slices.Count
            + ",\"bytes\":" + totalBytes + ",\"segment_bytes\":[" + list.ToString() + "]"
            + ",\"wall_s\":" + F(wall) + ",\"cpu_s\":" + F(cpu)
            + ",\"cpu_one_core_pct\":" + F(wall > 0 ? cpu * 100.0 / wall : 0)
            + ",\"fps\":" + F(wall > 0 ? encoded / wall : 0)
            + ",\"projected_20min_mb\":" + (projected / 1048576)
            + ",\"encoder\":\"" + Esc(encoderName) + "\",\"input_format\":\"argb32\"}";
    }

    /// <summary>按序号滚动保留最近的帧文件，避免临时目录无限增长。</summary>
    private static void PruneOldFrames(int keep)
    {
        if (seq <= keep) return;
        int cutoff = seq - keep;
        foreach (string file in Directory.GetFiles(outDir))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int dash = name.LastIndexOf('-');
            if (dash < 0) continue;
            int index;
            if (!int.TryParse(name.Substring(dash + 1), out index)) continue;
            if (index <= cutoff)
            {
                try { File.Delete(file); } catch (IOException) { /* 调用方可能正在读，下轮再删 */ }
            }
        }
    }
}
