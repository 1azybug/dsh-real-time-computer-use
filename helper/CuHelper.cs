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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
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

    private static void SendKeyRaw(ushort vk, ushort scan, uint flags)
    {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].data.keyboard.wVk = vk;
        inputs[0].data.keyboard.wScan = scan;
        inputs[0].data.keyboard.dwFlags = flags;
        inputs[0].data.keyboard.time = 0;
        inputs[0].data.keyboard.dwExtraInfo = IntPtr.Zero;
        if (SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) != 1)
            throw new InvalidOperationException("SendInput 拒绝了键盘事件（目标窗口可能以更高权限运行）");
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
        // 回执：点在**哪里**、那一点上是**哪个窗口**。鼠标合成点击对某些窗口无效（原生模态框、
        // 以更高权限运行的窗口），有了这两项才能立刻分辨"点空了"与"被目标忽略了"，
        // 而不是再截一张图猜（那是 3–6 秒）。
        POINT point;
        string hit = "";
        if (GetCursorPos(out point))
            hit = ",\"x_after\":" + point.X + ",\"y_after\":" + point.Y
                + ",\"window\":" + WindowUnder(point.X, point.Y);
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

    private static string KeyEvent(string name, string action)
    {
        ushort vk = ResolveKey(name);
        uint extended = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
        double before = Now();
        if (action == "down") SendKeyRaw(vk, 0, extended);
        else if (action == "up") SendKeyRaw(vk, 0, extended | KEYEVENTF_KEYUP);
        else
        {
            SendKeyRaw(vk, 0, extended);
            SendKeyRaw(vk, 0, extended | KEYEVENTF_KEYUP);
        }
        return "{\"ok\":true,\"key\":\"" + Esc(name) + "\",\"action\":\"" + Esc(action) + "\""
            + ",\"vk\":" + vk + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    /// <summary>组合键：按顺序按下、再逆序抬起，保证修饰键在按键期间保持按住。</summary>
    private static string Hotkey(string[] keys)
    {
        if (keys.Length == 0) throw new InvalidOperationException("hotkey 需要至少一个键");
        ushort[] vks = new ushort[keys.Length];
        for (int i = 0; i < keys.Length; i++) vks[i] = ResolveKey(keys[i]);
        double before = Now();
        for (int i = 0; i < vks.Length; i++)
            SendKeyRaw(vks[i], 0, (IsExtendedKey(vks[i]) ? KEYEVENTF_EXTENDEDKEY : 0));
        for (int i = vks.Length - 1; i >= 0; i--)
            SendKeyRaw(vks[i], 0, (IsExtendedKey(vks[i]) ? KEYEVENTF_EXTENDEDKEY : 0) | KEYEVENTF_KEYUP);
        return "{\"ok\":true,\"keys\":\"" + Esc(string.Join("+", keys)) + "\""
            + ",\"t_before\":" + F(before) + ",\"t_after\":" + F(Now()) + "}";
    }

    /// <summary>
    /// 文本输入走 KEYEVENTF_UNICODE 逐字符注入：不受键盘布局影响，中文与 emoji 都能输入
    /// （代理对按 UTF-16 码元逐个发送）。不走剪贴板，避免污染用户的剪贴板内容。
    /// </summary>
    private static string TypeText(string text)
    {
        if (text == null) text = "";
        double before = Now();
        for (int i = 0; i < text.Length; i++)
        {
            ushort unit = text[i];
            SendKeyRaw(0, unit, KEYEVENTF_UNICODE);
            SendKeyRaw(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
        return "{\"ok\":true,\"chars\":" + text.Length
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
        string codec, int segmentFrames, double retainSeconds)
    {
        if (liveRunning) return "{\"ok\":true,\"already\":true,\"interval_ms\":" + liveIntervalMs + "}";
        if (backend == "dxgi" || backend == "gdi") liveBackend = backend;
        if (codec == "h264" || codec == "jpeg") liveCodec = codec;
        if (segmentFrames > 0) h264SegmentFrames = segmentFrames;
        if (retainSeconds > 0) h264RetainSeconds = retainSeconds;
        if (intervalMs > 0) liveIntervalMs = intervalMs;
        if (quality > 0) liveQuality = quality;
        if (capacity > 0) liveCapacity = capacity;
        lock (ringLock) { ring.Clear(); }
        lock (h264Lock) { h264Ring.Clear(); h264TotalBytes = 0; h264SegmentCount = 0; h264LastSeconds = 0; }
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
            + ",\"codec\":\"" + liveCodec + "\",\"segment_frames\":" + h264SegmentFrames + "}";
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
        return "{\"ok\":true,\"frames\":" + liveCount + "}";
    }

    private static string Latest()
    {
        // h264 模式不写逐帧帧表：最新可用画面是最后一片的末尾（正在写的那片还没封，取不到），
        // 调用方拿这个时刻往前推窗口，才不会去要一段还没有封片、注定取不到的时间。
        if (liveCodec == "h264")
        {
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
        lock (h264Lock)
        {
            // 口径必须与 frames/bytes 一致：三者描述的都是**环里现存**的东西。
            // 累计封片数另放 segments_total——原先把累计值当 segments 报出去会与 frames 对不上
            // （36 分钟长跑实测：frames 36300 帧 = 242 片，而 segments 报 350 片），调用方算不通。
            segments = h264Ring.Count;
            h264Bytes = h264TotalBytes;
            // retained_s 才是"现在还能回看多久"：现存片覆盖的真实时长（首片起点 → 末片终点）。
            if (liveCodec == "h264" && segments > 0) retained = h264Ring[segments - 1].End - h264Ring[0].Start;
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
            + ",\"segment_frames\":" + h264SegmentFrames
            + ",\"retain_s\":" + F(h264RetainSeconds) + "}";
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

    /// <summary>切片时间轴的名义帧率：MemoryEncoder.Write 按 30 fps 写片内时间戳，片内从 0 开始。</summary>
    private const double SliceFrameRate = 30.0;

    /// <summary>一次 frames 调用最多解码多少帧。逐帧 JPEG 路径"取 512 帧"只是列文件名，
    /// 而 H.264 的每一帧都要真解码（实测含落盘约 70–150 ms），照数解码会慢到几十秒。</summary>
    private const int H264DecodeBudget = 24;

    private static int h264TakenSeq;

    /// <summary>
    /// H.264 环形缓冲按时间戳取帧：定位相交切片 → 片内解码 → 落 JPEG → 返回与逐帧 JPEG
    /// **同构**的 JSON（seq/t/t_after/path/bytes），于是插件层不需要区分存储编码。
    ///
    /// 三条必须知道的语义：
    ///   · 片内时间戳按名义 30 fps 生成，而实际采集速率由抓屏与编码耗时决定（DXGI 实测 30.3 fps、
    ///     GDI 约 23–27 fps）⇒ 换算回真实时刻必须用片首片尾的采集时刻做线性映射；不校正的话
    ///     一片内最多差出 0.4 秒（GDI），"这一刻的画面"就会答错。
    ///   · 只覆盖**已封片**的部分：正在写的那一片不在环里，最近不到一个片长（5 秒）的画面取不到。
    ///   · 窗口内均匀抽样，抽到的点数受 H264DecodeBudget 限制——不是"窗口内每一帧"。
    /// </summary>
    private static string H264FramesIn(double from, double to, int limit)
    {
        EnsureMediaFoundation();
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

        int take = limit < H264DecodeBudget ? limit : H264DecodeBudget;
        if (take < 1) take = 1;

        // 目标时刻**按片分配**，不是在整窗口上均匀铺开：窗口端点几乎不会正好落在某一片的
        // Start/End 上，铺出来的点一旦落到所有片之外就被静默丢掉——实测请求 2 个点只回了 1 帧
        // （那次窗口起点比第一片的 End 晚了几毫秒）。按片分配保证每个点都落在某片自己的区间里。
        int n = slices.Count;
        double[] lower = new double[n];
        double[] upper = new double[n];
        System.Collections.Generic.List<int> activeIdx = new System.Collections.Generic.List<int>();
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            lower[i] = Math.Max(from, slices[i].Start);
            upper[i] = Math.Min(to, slices[i].End);
            if (upper[i] <= lower[i]) continue;
            activeIdx.Add(i);
            total += upper[i] - lower[i];
        }
        if (activeIdx.Count == 0) return "{\"ok\":true,\"count\":0,\"frames\":[]}";

        int[] alloc = new int[n];
        if (activeIdx.Count >= take)
        {
            // 片比点数还多：均匀挑 take 个片，每片一个点（落在该片与窗口交集的中点）。
            for (int k = 0; k < take; k++)
            {
                int pick = activeIdx[(int)Math.Round((double)k * (activeIdx.Count - 1) / Math.Max(1, take - 1))];
                alloc[pick] = 1;
            }
        }
        else
        {
            // 点数够覆盖每一片：先各给一个，余量按各片可用长度分配，再从最长的片往回削到预算内。
            int rest = take - activeIdx.Count;
            for (int a = 0; a < activeIdx.Count; a++)
            {
                int i = activeIdx[a];
                alloc[i] = 1 + (total > 0 ? (int)Math.Round(rest * (upper[i] - lower[i]) / total) : 0);
            }
            int sum = 0;
            for (int i = 0; i < n; i++) sum += alloc[i];
            while (sum > take)
            {
                int heaviest = -1;
                for (int i = 0; i < n; i++)
                    if (alloc[i] > 1 && (heaviest < 0 || alloc[i] > alloc[heaviest])) heaviest = i;
                if (heaviest < 0) break;
                alloc[heaviest]--;
                sum--;
            }
        }

        // 片内目标递增，于是顺序解码比逐帧 seek 便宜得多（实测 seek 到目标约 71 ms，片内续读只要几毫秒）。
        System.Collections.Generic.List<double> targets = new System.Collections.Generic.List<double>();
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < alloc[i]; k++)
            {
                double ratio = alloc[i] == 1 ? 0.5 : (double)k / (alloc[i] - 1);
                targets.Add(lower[i] + (upper[i] - lower[i]) * ratio);
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
                    if (targets[i] < slice.Start || targets[i] > slice.End) continue;
                    double want = targets[i] - slice.Start;
                    double localSec = (realSpan > 0 && span > 0) ? want * span / realSpan : want;
                    long local = (long)Math.Round(localSec * 10000000.0);
                    if (local < 0) local = 0;
                    // 上限必须是**末帧的 PTS**：写成 Frames*1e7/30-1 会比末帧大 333333（一帧），
                    // 于是靠近片尾的目标点读到 EOF 返回 null、被当成"没有帧"丢掉（实测只回 1 帧）。
                    long cap = (long)(slice.Frames - 1) * 10000000L / 30;
                    if (local > cap) local = cap;
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
    private static string CaptureRegion(int x, int y, int w, int h, int quality, int scale)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("region width/height must be positive");
        if (x < 0 || y < 0 || x >= sw || y >= sh) throw new InvalidOperationException("region starts outside the screen");
        if (x + w > sw) w = sw - x;
        if (y + h > sh) h = sh - y;
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
    /// 一次抓帧、切成 cols×rows 块（每块落盘一张），返回每块对应的**屏幕矩形**；
    /// 可选再存一张**同帧缩略图**（整屏等比缩到 `thumbMaxPixels` 像素以内）。
    ///
    /// 与「连续调 N 次 region」的区别是**同帧**：N 次 region 各自抓一帧，在滚动、动画、
    /// 视频里拼出来的画面现实中并不存在，而模型不会知道。块数由调用方按屏幕尺寸与模型侧
    /// 像素预算算（每块 ≤ 640000 像素才会原样进模型），helper 只提供机制、不写死排版。
    ///
    /// 缩略图与块**同帧**同样要紧：缩略图给方位（一眼看全局），块给精度（1:1 原始像素）；
    /// 两者若来自不同时刻，"在缩略图上找到方位、去块里定位"就可能在动画里指错。
    /// </summary>
    private static string CaptureGrid(int cols, int rows, int quality, int scale, int thumbMaxPixels)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (cols < 1 || cols > 16 || rows < 1 || rows > 16)
            throw new InvalidOperationException("grid cols/rows must be within 1..16");
        if (scale < 1) scale = 1;
        if (scale > 16) scale = 16;

        Bitmap full = EnsureBuffer(sw, sh);
        double before = Now();
        // 只抓这一次：下面所有块都从这同一帧里 Clone 出来。
        bufferGraphics.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh), CopyPixelOperation.SourceCopy);
        double after = Now();

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
        return "{\"ok\":true,\"cols\":" + cols + ",\"rows\":" + rows
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
                        return "{\"ok\":true,\"changed\":false"
                            + ",\"diff\":" + ratio.ToString("0.####", CultureInfo.InvariantCulture)
                            + ",\"waited_ms\":" + (int)((stamp - start) * 1000)
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
                        IntField(line, "quality", 70), IntField(line, "scale", 1));
                }
                else if (cmd == "grid")
                {
                    // 一次抓帧切 cols×rows 块（同帧保证）：块数由调用方按屏幕尺寸与
                    // 模型侧像素预算算，helper 不写死排版。thumb_max_pixels ≤0 表示不出缩略图。
                    response = CaptureGrid(IntField(line, "cols", 2), IntField(line, "rows", 3),
                        IntField(line, "quality", 70), IntField(line, "scale", 1),
                        IntField(line, "thumb_max_pixels", 640000));
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
                    response = KeyEvent(Field(line, "name", ""), Field(line, "action", "press"));
                else if (cmd == "hotkey")
                    response = Hotkey(StringArrayField(line, "keys"));
                else if (cmd == "type_text")
                    response = TypeText(Field(line, "text", ""));

                else if (cmd == "wait")
                {
                    // 上限 60 秒：模型不该让会话挂太久，需要更长的等待应该是"分多次"的语义。
                    int waitMs = IntField(line, "ms", 0);
                    if (waitMs < 0) waitMs = 0;
                    if (waitMs > 60000) waitMs = 60000;
                    Thread.Sleep(waitMs);
                    response = "{\"ok\":true,\"waited_ms\":" + waitMs + ",\"t\":" + F(Now()) + "}";
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
                        IntField(line, "segment_frames", 150), DoubleField(line, "retain_s", 1200));
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
        int index = 0;
        int inSegment = 0;
        double segmentStart = 0;
        double lastFrameAt = 0;
        EnsureMediaFoundation();
        try
        {
            while (liveRunning)
            {
                if (encoder == null)
                {
                    encoder = new MemoryEncoder(sw, sh, true, "quality", (uint)liveQuality, MF.ARGB32);
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
                encoder.Write(pixels, inSegment);
                inSegment++;
                index++;
                liveCount = index;

                if (inSegment >= segmentFrames)
                {
                    byte[] sliceBytes = encoder.Finish();
                    encoder.Dispose();
                    encoder = null;
                    lock (h264Lock)
                    {
                        H264Slice slice = new H264Slice();
                        slice.Data = sliceBytes;
                        slice.Start = segmentStart;
                        slice.End = lastFrameAt;
                        slice.Frames = inSegment;
                        h264Ring.Add(slice);
                        h264TotalBytes += sliceBytes.Length;
                        h264SegmentCount++;
                        h264LastSeconds = Now();
                        PruneH264();
                    }
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
