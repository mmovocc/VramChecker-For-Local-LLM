// GpuVramWidget.cs - 轻量 GPU 显存浮窗 (纯 WinForms + D3DKMT)
// 编译: powershell -ExecutionPolicy Bypass -File build.ps1
//
// 命令行参数:
//   --top N            收起时单独显示的进程数 (默认 6)
//   --always a,b,c     无论排名始终单独显示的进程名 (逗号分隔)
//   --interval 毫秒    刷新间隔 (默认 2000)
//   --w 宽 --h 高      初始窗口尺寸 (默认 400x400)
//
// 界面 (点阵风格) 在 ui/, 启动/清理逻辑在 launcher/。启动配置即现有 llama-server .bat 脚本,
// 设置保存在 %APPDATA%\GpuVramWidget\settings.ini (见 launcher/)。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GpuVramWidget
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        internal static FontFamily UiFont;
        // 根集合: 防止 PrivateFontCollection 被 GC 回收导致自定义字体失效
        internal static readonly List<System.Drawing.Text.PrivateFontCollection> FontCollections = new List<System.Drawing.Text.PrivateFontCollection>();

        // 加载自定义字体: 支持 ttf/otf 直连, 也支持 zip (自动解压, 优先 Regular 字重)
        private static void LoadCustomFont()
        {
            string path = Options.FontPath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            try
            {
                var files = new List<string>();
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GpuVramWidgetFonts");
                    System.IO.Directory.CreateDirectory(dir);
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                    {
                        foreach (var e in zip.Entries)
                        {
                            try
                            {
                                string ext = System.IO.Path.GetExtension(e.FullName).ToLowerInvariant();
                                string baseName = System.IO.Path.GetFileName(e.FullName);
                                if ((ext != ".ttf" && ext != ".otf") || baseName.StartsWith("._")) continue;
                                // macOS 打包的非 UTF-8 条目名可能解码出非法字符, 清洗后再落盘
                                foreach (char ch in System.IO.Path.GetInvalidFileNameChars())
                                {
                                    baseName = baseName.Replace(ch, '_');
                                }
                                string target = System.IO.Path.Combine(dir, baseName);
                                using (var es = e.Open())
                                using (var fs = System.IO.File.Create(target)) { es.CopyTo(fs); }
                                files.Add(target);
                            }
                            catch { }
                        }
                    }
                }
                else { files.Add(path); }

                files.Sort(delegate (string a, string b)
                {
                    bool ra = a.IndexOf("regular", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool rb = b.IndexOf("regular", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (ra != rb) return ra ? -1 : 1;
                    return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });

                foreach (string f in files)
                {
                    try
                    {
                        var pfc = new System.Drawing.Text.PrivateFontCollection();
                        pfc.AddFontFile(f);
                        // 必须静态持有, 否则 GC 回收集合后非托管字体失效
                        FontCollections.Add(pfc);
                        if (pfc.Families.Length > 0) { UiFont = pfc.Families[0]; return; }
                    }
                    catch { }
                }
            }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Options.Parse(Environment.GetCommandLineArgs());
            AppSettings.Load();
            GpuPreference.TakeStartupSnapshot(); // baseline for "restore GPU preferences"
            if (Options.MaterialOverride != null)
                AppSettings.Material = Options.MaterialOverride == "light" ? GlassMaterial.Light : Options.MaterialOverride == "legacy" ? GlassMaterial.Legacy : GlassMaterial.Dark;
            LoadCustomFont();
            // 结构体布局守护: 系统升级导致尺寸变化时给出明确提示而非崩溃
            if (Marshal.SizeOf(typeof(D3DKMT_QUERYSTATISTICS)) != 0x328)
            {
                MessageBox.Show("D3DKMT 查询结构与当前系统不匹配 (期望 0x328，实际 0x" +
                    Marshal.SizeOf(typeof(D3DKMT_QUERYSTATISTICS)).ToString("X") + ")。\n请更新本程序以适配新系统。",
                    "GpuVramWidget", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WidgetForm());
        }
    }

    internal static class Options
    {
        public static bool Glass = false;   // default: solid card (no shader); --glass or Material menu for glass
        public static int Fps = 60;
        public static bool Economy = false;
        public static int SampleHz = 0; // 0 = adaptive; 1..144 = fixed capture target.
        public static bool DragBoost = true;
        public static bool SyncDrag = true;
        public static float PredictionMs = 0;
        public static float Refraction = 19;
        public static float Blur = 1.2f;
        public static float Tint = 0.035f;
        public static bool Diagnostic = false;
        public static string MaterialOverride;   // --material dark|light|legacy: this run only, not saved
        public static int Top = 6;
        public static int IntervalMs = 2000;
        public static int Width = 400;
        public static int Height = 400;
        public static double CornerRadius = 18;   // card radius (dot-matrix style: 18)
        public static double CornerSmooth = 5.0;  // 超椭圆指数 n: 越大越接近 Apple squircle 观感
        public static string FontPath = "";       // 自定义字体: ttf/otf/zip
        public static int Supersample = 1;        // 超采样倍数 (1=关; SS2 会啃直边, 已由描边羽化替代)
        public static readonly List<string> Always = new List<string>();

        public static void Parse(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
            {
                string a = (args[i] ?? "").TrimStart('-', '/').ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                try
                {
                    if (a == "classic") { Glass = false; }
                    else if (a == "glass") { Glass = true; }
                    else if (a == "economy") { Economy = true; }
                    else if (a == "no-drag-boost") { DragBoost = false; }
                    else if (a == "drag-boost") { DragBoost = true; }
                    else if (a == "sync-drag") { SyncDrag = true; }
                    else if (a == "no-sync-drag") { SyncDrag = false; }
                    else if (a == "prediction-ms" && next != null) { PredictionMs = Math.Max(0, Math.Min(8, float.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "sample-hz" && next != null) { SampleHz = Math.Max(0, Math.Min(144, int.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "diagnostic") { Diagnostic = true; }
                    else if (a == "material" && next != null) { MaterialOverride = next.ToLowerInvariant(); i++; }
                    else if (a == "fps" && next != null) { Fps = Math.Max(10, Math.Min(144, int.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "refraction" && next != null) { Refraction = Math.Max(0, Math.Min(40, float.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "blur" && next != null) { Blur = Math.Max(0, Math.Min(12, float.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "tint" && next != null) { Tint = Math.Max(0, Math.Min(1, float.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "top" && next != null) { Top = Math.Max(1, int.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "interval" && next != null) { IntervalMs = Math.Max(500, int.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "w" && next != null) { Width = Math.Max(300, int.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "h" && next != null) { Height = Math.Max(150, int.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "smooth" && next != null) { CornerSmooth = Math.Max(2, double.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "corner" && next != null) { CornerRadius = Math.Max(4, double.Parse(next, CultureInfo.InvariantCulture)); i++; }
                    else if (a == "font" && next != null) { FontPath = next; i++; }
                    else if (a == "ss" && next != null) { Supersample = Math.Max(1, Math.Min(4, int.Parse(next, CultureInfo.InvariantCulture))); i++; }
                    else if (a == "always" && next != null)
                    {
                        foreach (string s in next.Split(',')) { if (s.Trim().Length > 0) Always.Add(s.Trim()); }
                        i++;
                    }
                }
                catch { }
            }
        }
    }

    // ================= D3DKMT P/Invoke (与 WDK d3dkmthk.h 对齐, x64, sizeof=0x328) =================
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    public struct COUNTER { public uint Count; public ulong Bytes; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_MEMORY
    {
        public uint AllocsCommitted;
        public COUNTER P0; public COUNTER P1; public COUNTER P2; public COUNTER P3; public COUNTER P4;
        public COUNTER NonPreferred;
        public ulong TotalBytesEvictedDueToPreparation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_SEGMENT_INFORMATION
    {
        public ulong BytesCommitted;
        public ulong MaximumWorkingSet;
        public ulong MinimumWorkingSet;
        public uint NbReferencedAllocationEvictedInPeriod;
        public uint Padding;
        public VIDEO_MEMORY VideoMemory;
        public ulong Policy;
        public ulong R0, R1, R2, R3, R4, R5, R6, R7;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SEGMENT_INFORMATION
    {
        public ulong CommitLimit;
        public ulong BytesCommitted;
        public ulong BytesResident;
        public ulong MemTotalBytesEvicted;
        public uint MemAllocsCommitted;
        public uint MemAllocsResident;
        public uint Aperture;
        public ulong E0, E1, E2, E3, E4;
        public ulong SystemMemoryEndAddress;
        public ulong PowerFlags;
        public ulong SegmentProperties;
        public ulong R0, R1, R2, R3, R4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ADAPTER_INFORMATION
    {
        public uint NbSegments;
        public uint NodeCount;
        public uint VidPnSourceCount;
        public uint VSyncEnabled;
        public uint TdrDetectedCount;
        public ulong ZeroLengthDmaBuffers;
        public ulong RestartedPeriod;
    }

    [StructLayout(LayoutKind.Explicit, Size = 776)]
    public struct QUERY_RESULT
    {
        [FieldOffset(0)] public ADAPTER_INFORMATION AdapterInformation;
        [FieldOffset(0)] public SEGMENT_INFORMATION SegmentInformation;
        [FieldOffset(0)] public PROCESS_SEGMENT_INFORMATION ProcessSegmentInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_QUERYSTATISTICS
    {
        public uint Type;
        public LUID AdapterLuid;
        public IntPtr hProcess;
        public QUERY_RESULT QueryResult;
        public uint QuerySegment;
    }

    internal static class Native
    {
        public const int QT_ADAPTER = 0;
        public const int QT_SEGMENT = 3;
        public const int QT_PROCESS_SEGMENT = 4;
        public const uint PROCESS_QUERY_INFORMATION = 0x400;

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 adapters);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS query);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDc);
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER closeAdapter);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID openAdapter);

        public const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int CX; public int CY; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

    // ================= 数据采集 =================
    internal sealed class ProcRow
    {
        public int Pid;
        public string Name;
        public double MB;
    }

    internal sealed class VramSample
    {
        public double UsedMB;
        public double TotalMB;
        public List<ProcRow> Rows;
    }

    internal static class GpuStats
    {
        internal sealed class AdapterVram
        {
            public LUID Luid;
            public uint hAdapter;      // 仅枚举期间有效 (取名用), 关闭后不可再使用
            public List<int> Segments;
            public double TotalCommitMB;
        }

        private static List<AdapterVram> _adapters;
        internal static string AdapterName;

        // 访问失败退避 30 秒；零占用进程 4 秒后重新发现，缓存随退出进程清理。
        private static readonly Dictionary<int, DateTime> _deadPids = new Dictionary<int, DateTime>();
        private const int DeadPidRetrySeconds = 30;

        private static D3DKMT_QUERYSTATISTICS NewQuery(int type, LUID luid)
        {
            var q = new D3DKMT_QUERYSTATISTICS();
            q.Type = (uint)type;
            q.AdapterLuid = luid;
            return q;
        }

        private static List<AdapterVram> EnumAdapters()
        {
            var result = new List<AdapterVram>();
            var ea = new D3DKMT_ENUMADAPTERS2();
            ea.NumAdapters = 16;
            int infoSize = Marshal.SizeOf(typeof(D3DKMT_ADAPTERINFO));
            IntPtr buf = Marshal.AllocHGlobal(16 * infoSize);
            try
            {
                ea.pAdapters = buf;
                if (Native.D3DKMTEnumAdapters2(ref ea) < 0) return result;

                var seen = new HashSet<string>();
                var openHandles = new List<uint>();
                for (int i = 0; i < ea.NumAdapters; i++)
                {
                    var info = (D3DKMT_ADAPTERINFO)Marshal.PtrToStructure(
                        new IntPtr(buf.ToInt64() + i * infoSize), typeof(D3DKMT_ADAPTERINFO));
                    openHandles.Add(info.hAdapter);

                    var q = NewQuery(Native.QT_ADAPTER, info.AdapterLuid);
                    if (Native.D3DKMTQueryStatistics(ref q) < 0) continue;
                    int nbSegments = (int)q.QueryResult.AdapterInformation.NbSegments;
                    if (nbSegments <= 0 || nbSegments > 64) continue;

                    var segIds = new List<int>();
                    var sigParts = new List<string>();
                    double commitSum = 0;
                    for (int s = 0; s < nbSegments; s++)
                    {
                        var qs = NewQuery(Native.QT_SEGMENT, info.AdapterLuid);
                        qs.QuerySegment = (uint)s;
                        if (Native.D3DKMTQueryStatistics(ref qs) >= 0)
                        {
                            var si = qs.QueryResult.SegmentInformation;
                            if (si.Aperture == 0)
                            {
                                segIds.Add(s);
                                sigParts.Add(s + ":" + si.CommitLimit);
                                commitSum += si.CommitLimit;
                            }
                        }
                    }
                    if (segIds.Count == 0) continue;

                    // 同一物理 GPU 会以多个 LUID 重复枚举, 按段签名去重避免双倍计数
                    string sig = string.Join(",", sigParts);
                    if (seen.Add(sig))
                    {
                        var av = new AdapterVram();
                        av.Luid = info.AdapterLuid;
                        av.hAdapter = info.hAdapter;
                        av.Segments = segIds;
                        av.TotalCommitMB = commitSum / 1048576.0;
                        result.Add(av);
                    }
                }

                // 标题: 以 VRAM 最大的适配器为准按 LUID 精确查询型号。
                // Parsec 等虚拟适配器没有 VRAM 段, 在上面的筛选中已被天然排除
                if (result.Count > 0 && string.IsNullOrEmpty(AdapterName))
                {
                    AdapterVram best = result[0];
                    foreach (var av in result) { if (av.TotalCommitMB > best.TotalCommitMB) best = av; }
                    string nm = QueryAdapterNameByLUID(best.Luid, best.hAdapter);
                    if (nm != null) AdapterName = nm;
                }

                // 枚举返回的适配器句柄必须关闭, 否则每次枚举泄漏内核句柄
                foreach (uint hh in openHandles)
                {
                    var ca = new D3DKMT_CLOSEADAPTER { hAdapter = hh };
                    Native.D3DKMTCloseAdapter(ref ca);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            // 显卡型号: KMTQAITYPE_ADAPTERREGISTRYINFO 在新系统上返回 OBJECT_NAME_NOT_FOUND,
            // 直接读注册表 DriverDesc (显示类 GUID)
            if (string.IsNullOrEmpty(AdapterName)) QueryAdapterNameFromRegistry();
            return result;
        }

        private static string CleanAdapterName(string raw)
        {
            string name = (raw ?? "").Trim();
            if (name.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(15);
            else if (name.StartsWith("AMD Radeon ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(11);
            else if (name.StartsWith("Intel(R) ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(9);
            return name.Length > 0 ? name : null;
        }

        private static string TryRegistryInfo(uint hAdapter)
        {
            IntPtr nameBuf = Marshal.AllocHGlobal(2080);
            try
            {
                var qa = new D3DKMT_QUERYADAPTERINFO();
                qa.hAdapter = hAdapter;
                qa.Type = Native.KMTQAITYPE_ADAPTERREGISTRYINFO;
                qa.pPrivateDriverData = nameBuf;
                qa.PrivateDriverDataSize = 2080;
                if (Native.D3DKMTQueryAdapterInfo(ref qa) < 0) return null;
                var ri = (D3DKMT_ADAPTERREGISTRYINFO)Marshal.PtrToStructure(
                    nameBuf, typeof(D3DKMT_ADAPTERREGISTRYINFO));
                return CleanAdapterName(ri.AdapterString);
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(nameBuf); }
        }

        private static string QueryAdapterNameByLUID(LUID luid, uint enumHandle)
        {
            // 1) 枚举句柄直查
            string s = TryRegistryInfo(enumHandle);
            if (s != null) return s;
            // 2) 部分系统上枚举句柄会 OBJECT_NAME_NOT_FOUND, 用 LUID 重新打开适配器再查
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
            if (Native.D3DKMTOpenAdapterFromLuid(ref open) >= 0)
            {
                try { s = TryRegistryInfo(open.hAdapter); }
                finally
                {
                    var ca = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                    Native.D3DKMTCloseAdapter(ref ca);
                }
            }
            return s;
        }

        private static void QueryAdapterNameFromRegistry()
        {
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return;
                    var subs = new List<string>(cls.GetSubKeyNames());
                    subs.Sort(StringComparer.Ordinal);
                    string first = null;
                    foreach (string sub in subs)
                    {
                        int n;
                        if (!int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                        using (var k = cls.OpenSubKey(sub))
                        {
                            string desc = k != null ? (k.GetValue("DriverDesc") as string) : null;
                            if (string.IsNullOrEmpty(desc)) continue;
                            // 兜底过滤: 跳过虚拟/间接显示适配器, 防止 "Parsec Virtual Display" 之类顶掉真显卡
                            string dl = desc.ToLowerInvariant();
                            if (dl.Contains("virtual") || dl.Contains("indirect") || dl.Contains("basic") ||
                                dl.Contains("parsec") || dl.Contains("mirror") || dl.Contains("remote")) continue;
                            string name = CleanAdapterName(desc);
                            if (name == null) continue;
                            if (first == null) first = name;
                            // 多 GPU 机器上 0000 子键未必是跟踪的独显, 优先离散卡关键词
                            bool discrete = IsDiscreteAdapterName(desc);
                            if (discrete) { AdapterName = name; return; }
                        }
                    }
                    if (first != null) AdapterName = first;
                }
            }
            catch { }
        }

        private static bool IsDiscreteAdapterName(string raw)
        {
            // Classify before removing vendor/product prefixes for the title.
            string lower = (raw ?? "").ToLowerInvariant();
            return lower.Contains("geforce") || lower.Contains("quadro") || lower.Contains("titan") ||
                lower.Contains("rtx ") || lower.Contains("gtx ") || lower.Contains("radeon rx") ||
                lower.Contains("radeon pro") || lower.Contains("radeon vii") || lower.Contains("arc a") || lower.Contains("arc b");
        }

        private static void SumTotals(List<AdapterVram> adapters, out double usedMB, out double totalMB)
        {
            usedMB = 0; totalMB = 0;
            foreach (var av in adapters)
            {
                foreach (int s in av.Segments)
                {
                    var q = NewQuery(Native.QT_SEGMENT, av.Luid);
                    q.QuerySegment = (uint)s;
                    if (Native.D3DKMTQueryStatistics(ref q) >= 0)
                    {
                        usedMB += q.QueryResult.SegmentInformation.BytesResident / 1048576.0;
                        totalMB += q.QueryResult.SegmentInformation.CommitLimit / 1048576.0;
                    }
                }
            }
        }

        // Totals only (no per-process scan); used to watch VRAM settle after cleanup.
        internal static void QueryTotals(out double usedMB, out double totalMB)
        {
            var adapters = _adapters ?? (_adapters = EnumAdapters());
            SumTotals(adapters, out usedMB, out totalMB);
        }

        internal static VramSample Collect()
        {
            if (_adapters == null) _adapters = EnumAdapters();

            double usedMB, totalMB;
            SumTotals(_adapters, out usedMB, out totalMB);

            // 自愈: 总容量不可用时重枚举；合法的零占用不触发重复枚举。
            if (totalMB <= 0)
            {
                _adapters = EnumAdapters();
                SumTotals(_adapters, out usedMB, out totalMB);
            }

            var rows = new List<ProcRow>();
            var livePids = new HashSet<int>();
            DateTime now = DateTime.UtcNow;

            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                livePids.Add(p.Id);
                if (p.Id <= 4) continue;
                DateTime deadAt;
                lock (_deadPids)
                {
                    if (_deadPids.TryGetValue(p.Id, out deadAt) &&
                        now < deadAt) continue;
                }

                IntPtr h = IntPtr.Zero;
                try
                {
                    h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) { lock (_deadPids) { _deadPids[p.Id] = now.AddSeconds(DeadPidRetrySeconds); } continue; }

                    ulong bytes = 0;
                    foreach (var av in _adapters)
                    {
                        foreach (int s in av.Segments)
                        {
                            var q = NewQuery(Native.QT_PROCESS_SEGMENT, av.Luid);
                            q.hProcess = h;
                            q.QuerySegment = (uint)s;
                            if (Native.D3DKMTQueryStatistics(ref q) >= 0)
                            {
                                var vm = q.QueryResult.ProcessSegmentInformation.VideoMemory;
                                ulong counters = vm.P0.Bytes + vm.P1.Bytes + vm.P2.Bytes + vm.P3.Bytes
                                               + vm.P4.Bytes + vm.NonPreferred.Bytes;
                                // build 26200+: VideoMemory 分层计数器恒为 0, 真实值移至首字段 BytesCommitted;
                                // 老系统计数器有效时优先使用
                                bytes += counters > 0 ? counters
                                    : q.QueryResult.ProcessSegmentInformation.BytesCommitted;
                            }
                        }
                    }
                    if (bytes > 0)
                    {
                        lock (_deadPids) { _deadPids.Remove(p.Id); }
                        var row = new ProcRow();
                        row.Pid = p.Id;
                        row.Name = p.ProcessName;
                        row.MB = bytes / 1048576.0;
                        rows.Add(row);
                    }
                    else
                    {
                        // Zero is not a permanent failure: discover new GPU users quickly.
                        lock (_deadPids) { _deadPids[p.Id] = now.AddSeconds(4); }
                    }
                }
                catch { }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                }
            }
            lock (_deadPids)
            {
                var stale = new List<int>();
                foreach (int pid in _deadPids.Keys) if (!livePids.Contains(pid)) stale.Add(pid);
                foreach (int pid in stale) _deadPids.Remove(pid);
            }

            rows.Sort(delegate (ProcRow a, ProcRow b) { return b.MB.CompareTo(a.MB); });

            var sample = new VramSample();
            sample.UsedMB = usedMB;
            sample.TotalMB = totalMB > 0 ? totalMB : 16376;
            sample.Rows = rows;
            return sample;
        }
    }

}
