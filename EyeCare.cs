// ============================================================================
// EyeCare —— Windows 电脑使用时长护眼提醒
//   · 可循环：连续用机 → 小休 →（累计几次小休）→ 大休，无限循环
//   · 参数可改：同目录 config.ini 手改（保存后约 3 秒自动生效），或托盘「设置…」界面
//   · 可开机自启动：写当前用户登录启动项（托盘勾选 / --enable-autostart）
//   · 护眼口径参考高星开源项目：Stretchly（循环式小休/大休）+ Workrave（当日累计
//     工时上限、闲置不计入），并用「无键鼠输入 = 人不在」避免空计时。
// 单文件 C#，用系统自带的 csc.exe（.NET Framework 4.8）编译，无需安装 SDK。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace EyeCare
{
    // ---------------------------------------------------------------- 0. Win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public int cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO pli);

        [DllImport("kernel32.dll")]
        static extern uint GetTickCount();

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        public const int ATTACH_PARENT_PROCESS = -1;
        public const int STD_OUTPUT_HANDLE = -11;

        // 距最后一次鼠标/键盘活动的秒数：判断人是否真的在电脑前
        public static double IdleSeconds()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return 0.0;
            uint now = GetTickCount();
            unchecked { return (double)(uint)(now - lii.dwTime) / 1000.0; }
        }

        // 系统 DPI 缩放（100% => 1.0，125% => 1.25）
        public static double DpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    double s = (double)g.DpiX / 96.0;
                    if (s < 1.0) s = 1.0;
                    if (s > 3.0) s = 3.0;
                    return s;
                }
            }
            catch { return 1.0; }
        }
    }

    // ---------------------------------------------------------------- 1. 路径
    static class Paths
    {
        public static string Root = AppContext.BaseDirectory;

        public static string Config { get { return Path.Combine(Root, "config.ini"); } }
        public static string LogDir { get { return Path.Combine(Root, "log"); } }
        public static string UsageLog { get { return Path.Combine(LogDir, "usage.log"); } }
        public static string DailyCsv { get { return Path.Combine(LogDir, "daily.csv"); } }
        public static string SelfTestReport { get { return Path.Combine(Root, "selftest-report.txt"); } }

        public static void EnsureLogDir()
        {
            try { if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir); }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 2. 配置
    sealed class Config
    {
        public bool enabled = true;
        public double idle_timeout_sec = 120;
        public bool notify_balloon = true;

        public double work_min = 45;
        public double micro_break_min = 5;
        public double long_break_min = 20;
        public int micros_per_long = 4;
        public double postpone_min = 5;
        public int postpones_max = 2;
        public double grace_sec = 20;

        public double daily_limit_min = 360;
        public int daily_warn_pct = 90;
        public int daily_reset_hour = 4;
        public int daily_reset_min = 0;
        public bool daily_lock = false;
        public double daily_notify_every_min = 15;

        public int opacity_pct = 72;
        public int font_size = 30;
        public string backdrop_color = "#101826";
        public string card_color = "#16233A";
        public string accent_color = "#4FD1A5";
        public bool show_tip = true;
        public bool keep_topmost = true;
        public bool cover_taskbar = false;

        public bool usage_log = true;
        public int keep_days = 30;

        public Config Clone()
        {
            Config c = new Config();
            c.enabled = enabled; c.idle_timeout_sec = idle_timeout_sec; c.notify_balloon = notify_balloon;
            c.work_min = work_min; c.micro_break_min = micro_break_min; c.long_break_min = long_break_min;
            c.micros_per_long = micros_per_long; c.postpone_min = postpone_min; c.postpones_max = postpones_max;
            c.grace_sec = grace_sec;
            c.daily_limit_min = daily_limit_min; c.daily_warn_pct = daily_warn_pct;
            c.daily_reset_hour = daily_reset_hour; c.daily_reset_min = daily_reset_min;
            c.daily_lock = daily_lock; c.daily_notify_every_min = daily_notify_every_min;
            c.opacity_pct = opacity_pct; c.font_size = font_size;
            c.backdrop_color = backdrop_color; c.card_color = card_color; c.accent_color = accent_color;
            c.show_tip = show_tip; c.keep_topmost = keep_topmost; c.cover_taskbar = cover_taskbar;
            c.usage_log = usage_log; c.keep_days = keep_days;
            return c;
        }

        // 手改参数被夹紧到可用范围：配错不会崩
        public void Clamp()
        {
            idle_timeout_sec = Bound(idle_timeout_sec, 15, 3600);
            work_min = Bound(work_min, 0.05, 600);
            micro_break_min = Bound(micro_break_min, 0.05, 120);
            long_break_min = Bound(long_break_min, 0.05, 240);
            micros_per_long = (int)Bound(micros_per_long, 0, 50);
            postpone_min = Bound(postpone_min, 0.05, 120);
            postpones_max = (int)Bound(postpones_max, 0, 20);
            grace_sec = Bound(grace_sec, 0, 600);
            daily_limit_min = Bound(daily_limit_min, 1, 1440);
            daily_warn_pct = (int)Bound(daily_warn_pct, 10, 100);
            daily_reset_hour = (int)Bound(daily_reset_hour, 0, 23);
            daily_reset_min = (int)Bound(daily_reset_min, 0, 59);
            daily_notify_every_min = Bound(daily_notify_every_min, 1, 240);
            opacity_pct = (int)Bound(opacity_pct, 30, 95);
            font_size = (int)Bound(font_size, 14, 72);
            keep_days = (int)Bound(keep_days, 1, 3650);
        }

        static double Bound(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public Color BackdropColor { get { return ParseColor(backdrop_color, Color.FromArgb(16, 24, 38)); } }
        public Color CardColor { get { return ParseColor(card_color, Color.FromArgb(22, 35, 58)); } }
        public Color AccentColor { get { return ParseColor(accent_color, Color.FromArgb(79, 209, 165)); } }

        static Color ParseColor(string s, Color fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(s))
                {
                    s = s.Trim();
                    string h = (s.Length == 7 && s[0] == '#') ? s.Substring(1) : s;
                    if (h.Length == 6)
                    {
                        int r = int.Parse(h.Substring(0, 2), NumberStyles.HexNumber);
                        int g = int.Parse(h.Substring(2, 2), NumberStyles.HexNumber);
                        int b = int.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
                        return Color.FromArgb(r, g, b);
                    }
                }
            }
            catch { }
            return fallback;
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) { c.Clamp(); c.Save(path); return c; }
            Dictionary<string, string> kv;
            try { kv = Ini.Read(path); }
            catch { kv = new Dictionary<string, string>(); }
            c.Apply(kv);
            c.Clamp();
            return c;
        }

        public void Apply(Dictionary<string, string> kv)
        {
            enabled = GetB(kv, "enabled", enabled);
            idle_timeout_sec = GetD(kv, "idle_timeout_sec", idle_timeout_sec);
            notify_balloon = GetB(kv, "notify_balloon", notify_balloon);
            work_min = GetD(kv, "work_min", work_min);
            micro_break_min = GetD(kv, "micro_break_min", micro_break_min);
            long_break_min = GetD(kv, "long_break_min", long_break_min);
            micros_per_long = (int)GetD(kv, "micros_per_long", micros_per_long);
            postpone_min = GetD(kv, "postpone_min", postpone_min);
            postpones_max = (int)GetD(kv, "postpones_max", postpones_max);
            grace_sec = GetD(kv, "grace_sec", grace_sec);
            daily_limit_min = GetD(kv, "daily_limit_min", daily_limit_min);
            daily_warn_pct = (int)GetD(kv, "daily_warn_pct", daily_warn_pct);
            string reset = GetS(kv, "daily_reset_at", "");
            if (reset.Length > 0) { int h, m; SplitClock(reset, out h, out m); daily_reset_hour = h; daily_reset_min = m; }
            daily_lock = GetB(kv, "daily_lock", daily_lock);
            daily_notify_every_min = GetD(kv, "daily_notify_every_min", daily_notify_every_min);
            opacity_pct = (int)GetD(kv, "opacity_pct", opacity_pct);
            font_size = (int)GetD(kv, "font_size", font_size);
            backdrop_color = GetS(kv, "backdrop_color", backdrop_color);
            card_color = GetS(kv, "card_color", card_color);
            accent_color = GetS(kv, "accent_color", accent_color);
            show_tip = GetB(kv, "show_tip", show_tip);
            keep_topmost = GetB(kv, "keep_topmost", keep_topmost);
            cover_taskbar = GetB(kv, "cover_taskbar", cover_taskbar);
            usage_log = GetB(kv, "usage_log", usage_log);
            keep_days = (int)GetD(kv, "keep_days", keep_days);
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; EyeCare 护眼提醒 · 配置文件");
            sb.AppendLine("; 保存后约 3 秒内自动生效，不必重启；也可在托盘右键「设置…」用界面改。");
            sb.AppendLine("; 数值允许小数；写错或超范围会被自动夹紧到下面标注的合法区间。");
            sb.AppendLine();
            sb.AppendLine("[general]");
            sb.AppendLine("enabled = " + B(enabled) + "                 ; 总开关（false = 不打扰，但仍统计当日时长）");
            sb.AppendLine("idle_timeout_sec = " + N(idle_timeout_sec) + "          ; 无键鼠输入超过这些秒数视为离座，暂停计时 [15~3600]");
            sb.AppendLine("notify_balloon = " + B(notify_balloon) + "           ; 右下角气泡提示");
            sb.AppendLine();
            sb.AppendLine("[cycle]");
            sb.AppendLine("work_min = " + N(work_min) + "                 ; 连续用机多少分钟提醒休息（循环）[0.05~600]");
            sb.AppendLine("micro_break_min = " + N(micro_break_min) + "         ; 小休时长 [0.05~120]");
            sb.AppendLine("long_break_min = " + N(long_break_min) + "        ; 大休时长 [0.05~240]");
            sb.AppendLine("micros_per_long = " + micros_per_long + "           ; 几次小休之后来一次大休（0 = 只有小休）");
            sb.AppendLine("postpone_min = " + N(postpone_min) + "             ; 按「延后」推迟多少分钟");
            sb.AppendLine("postpones_max = " + postpones_max + "            ; 一次提醒最多可延后几次（0 = 不可延后）");
            sb.AppendLine("grace_sec = " + N(grace_sec) + "                ; 提前几秒预告「马上提醒」[0~600]");
            sb.AppendLine();
            sb.AppendLine("[daily]");
            sb.AppendLine("daily_limit_min = " + N(daily_limit_min) + "          ; 每日用机上限（分钟）[1~1440]");
            sb.AppendLine("daily_warn_pct = " + daily_warn_pct + "            ; 到达上限的这个百分比时先提醒一次 [10~100]");
            sb.AppendLine("daily_reset_at = " + string.Format("{0:00}:{1:00}", daily_reset_hour, daily_reset_min) + "              ; 每日统计的“日界”：早于此时刻仍算前一天");
            sb.AppendLine("daily_lock = " + B(daily_lock) + "               ; 超限后用遮罩提示（false = 只发气泡）");
            sb.AppendLine("daily_notify_every_min = " + N(daily_notify_every_min) + "   ; 超限后每隔多少分钟再提醒一次");
            sb.AppendLine();
            sb.AppendLine("[overlay]");
            sb.AppendLine("opacity_pct = " + opacity_pct + "                ; 遮罩不透明度 30~95");
            sb.AppendLine("font_size = " + font_size + "                  ; 休息卡片字号 14~72（再按屏幕缩放自动放大）");
            sb.AppendLine("backdrop_color = " + backdrop_color + "         ; 遮罩底色");
            sb.AppendLine("card_color = " + card_color + "              ; 卡片底色");
            sb.AppendLine("accent_color = " + accent_color + "           ; 倒计时颜色");
            sb.AppendLine("show_tip = " + B(show_tip) + "                 ; 显示护眼建议文字");
            sb.AppendLine("keep_topmost = " + B(keep_topmost) + "            ; 休息时把遮罩顶回最上层");
            sb.AppendLine("cover_taskbar = " + B(cover_taskbar) + "            ; true = 连任务栏一起遮住");
            sb.AppendLine();
            sb.AppendLine("[log]");
            sb.AppendLine("usage_log = " + B(usage_log) + "                ; 写 log/usage.log 与 log/daily.csv");
            sb.AppendLine("keep_days = " + keep_days + "                  ; 汇总保留天数");
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); }
            catch { }
        }

        static string B(bool v) { return v ? "true" : "false"; }
        public static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static bool GetB(Dictionary<string, string> kv, string k, bool d)
        {
            string s;
            if (!kv.TryGetValue(k, out s)) return d;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on" || s == "y") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off" || s == "n") return false;
            return d;
        }

        static double GetD(Dictionary<string, string> kv, string k, double d)
        {
            string s;
            if (!kv.TryGetValue(k, out s)) return d;
            double v;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return d;
        }

        static string GetS(Dictionary<string, string> kv, string k, string d)
        {
            string s;
            if (!kv.TryGetValue(k, out s)) return d;
            s = s.Trim();
            return s.Length == 0 ? d : s;
        }

        public static void SplitClock(string s, out int hour, out int minute)
        {
            hour = 4; minute = 0;
            try
            {
                string[] p = s.Split(':');
                int h, m;
                if (p.Length >= 2 && int.TryParse(p[0], out h) && int.TryParse(p[1], out m))
                {
                    if (h >= 0 && h <= 23) hour = h;
                    if (m >= 0 && m <= 59) minute = m;
                }
            }
            catch { }
        }

        public string SummaryCycle()
        {
            string tail = micros_per_long > 0
                ? string.Format("，每 {0} 次小休后来一次 {1} 分钟大休", micros_per_long, N(long_break_min))
                : "";
            return string.Format("连续用机 {0} 分钟提醒休息 {1} 分钟{2}", N(work_min), N(micro_break_min), tail);
        }
    }

    static class Ini
    {
        public static Dictionary<string, string> Read(string path)
        {
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("[", StringComparison.Ordinal)) continue;
                int cut = line.IndexOf(';');
                if (cut < 0) cut = line.IndexOf('#');
                if (cut >= 0) line = line.Substring(0, cut).Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || eq <= 0) continue;
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return kv;
        }
    }

    // ---------------------------------------------------------------- 3. 每日统计 / 日志
    sealed class DailyRow
    {
        public string Date = "";
        public double UseSec;
        public double BreakSec;
        public int Micro;
        public int Long;
        public int Skips;
        public int Postpones;

        public DailyRow Clone()
        {
            DailyRow r = new DailyRow();
            r.Date = Date; r.UseSec = UseSec; r.BreakSec = BreakSec;
            r.Micro = Micro; r.Long = Long; r.Skips = Skips; r.Postpones = Postpones;
            return r;
        }
    }

    sealed class Store
    {
        readonly object gate = new object();
        Dictionary<string, DailyRow> rows = new Dictionary<string, DailyRow>(StringComparer.Ordinal);
        int writes;
        int keepDays = 30;

        /// 由配置驱动：daily.csv 最多保留最近多少个自然日。
        public void SetKeepDays(int days)
        {
            lock (gate) { keepDays = days < 1 ? 1 : days; }
        }

        public void Load()
        {
            lock (gate)
            {
                rows.Clear();
                try
                {
                    if (!File.Exists(Paths.DailyCsv)) return;
                    string[] lines = File.ReadAllLines(Paths.DailyCsv, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith("date,", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] p = line.Split(',');
                        if (p.Length < 7) continue;
                        DailyRow r = new DailyRow();
                        r.Date = p[0];
                        double d;
                        if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.UseSec = d * 60.0;
                        if (double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.BreakSec = d * 60.0;
                        int n;
                        if (int.TryParse(p[3], out n)) r.Micro = n;
                        if (int.TryParse(p[4], out n)) r.Long = n;
                        if (int.TryParse(p[5], out n)) r.Skips = n;
                        if (int.TryParse(p[6], out n)) r.Postpones = n;
                        rows[r.Date] = r;
                    }
                }
                catch { }
            }
        }

        public DailyRow Get(string date)
        {
            lock (gate)
            {
                DailyRow r;
                if (rows.TryGetValue(date, out r)) return r.Clone();
                DailyRow fresh = new DailyRow();
                fresh.Date = date;
                return fresh;
            }
        }

        public void Put(DailyRow row)
        {
            lock (gate)
            {
                rows[row.Date] = row.Clone();
                writes++;
                if (writes % 20 == 1) Flush();
            }
        }

        public void FlushNow()
        {
            lock (gate) { Flush(); }
        }

        void Flush()
        {
            try
            {
                Paths.EnsureLogDir();
                List<string> dates = new List<string>(rows.Keys);
                dates.Sort(StringComparer.Ordinal);
                // keep_days：只保留最近 N 天的汇总，超出的旧行不再写出
                int keep = keepDays;
                if (keep < 1) keep = 1;
                int from = dates.Count - keep;
                if (from < 0) from = 0;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("date,used_min,break_min,micro_breaks,long_breaks,skips,postpones");
                for (int i = from; i < dates.Count; i++)
                {
                    DailyRow r = rows[dates[i]];
                    sb.AppendLine(string.Join(",", new string[] {
                        r.Date,
                        (r.UseSec / 60.0).ToString("0.0", CultureInfo.InvariantCulture),
                        (r.BreakSec / 60.0).ToString("0.0", CultureInfo.InvariantCulture),
                        r.Micro.ToString(CultureInfo.InvariantCulture),
                        r.Long.ToString(CultureInfo.InvariantCulture),
                        r.Skips.ToString(CultureInfo.InvariantCulture),
                        r.Postpones.ToString(CultureInfo.InvariantCulture) }));
                }
                File.WriteAllText(Paths.DailyCsv, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public List<DailyRow> Last(int days)
        {
            lock (gate)
            {
                List<DailyRow> list = new List<DailyRow>();
                List<string> dates = new List<string>(rows.Keys);
                dates.Sort(StringComparer.Ordinal);
                int from = dates.Count - days;
                if (from < 0) from = 0;
                for (int i = from; i < dates.Count; i++) list.Add(rows[dates[i]].Clone());
                return list;
            }
        }

        public void Log(string message)
        {
            try
            {
                Paths.EnsureLogDir();
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
                File.AppendAllText(Paths.UsageLog, line + Environment.NewLine, new UTF8Encoding(false));
                FileInfo fi = new FileInfo(Paths.UsageLog);
                if (fi.Length > 3 * 1024 * 1024) TrimLog();
            }
            catch { }
        }

        void TrimLog()
        {
            try
            {
                string[] all = File.ReadAllLines(Paths.UsageLog, Encoding.UTF8);
                int keep = all.Length / 2;
                if (keep > 300)
                {
                    string[] tail = new string[keep];
                    Array.Copy(all, all.Length - keep, tail, 0, keep);
                    File.WriteAllLines(Paths.UsageLog, tail, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 4. 计时状态机（与界面无耦合，可离线自检）
    sealed class Engine
    {
        public const string S_WORKING = "working";
        public const string S_PREALERT = "prealert";
        public const string S_POSTPONED = "postponed";
        public const string S_MICRO = "microbreak";
        public const string S_LONG = "longbreak";
        public const string S_DAILY = "dailyover";
        public const string S_AWAY = "away";
        public const string S_DISABLED = "disabled";

        public const string K_MICRO = "micro";
        public const string K_LONG = "long";
        public const string K_DAILY = "daily";

        Config cfg;
        Store store;
        DailyRow row;
        string dateKey = "";

        double workSec;
        double preLeft;
        double breakLeft;
        double breakTotal;
        double postLeft;
        double flushSec;
        double dailyNotifyLeft;
        int microSinceLong;
        int postponesUsed;
        string pendingKind = K_MICRO;
        bool dailyWarned;
        bool dailyOver;
        double dailyAgainLeft;

        public string State = S_WORKING;
        public Func<DateTime> Clock = delegate { return DateTime.Now; };
        public List<string> Notices = new List<string>();

        public Action<string, double> PreAlertStarted = delegate { };
        public Action<string, double> BreakStarted = delegate { };
        public Action<double, double> BreakTicked = delegate { };
        public Action<string, bool> BreakFinished = delegate { };
        public Action StateChanged = delegate { };

        public Engine(Config cfg, Store store)
        {
            this.cfg = cfg;
            this.store = store;
            store.Load();
            dateKey = LogicalDate(Clock(), cfg);
            row = store.Get(dateKey);
            State = cfg.enabled ? S_WORKING : S_DISABLED;
        }

        public Config Cfg { get { return cfg; } }
        public Store Store { get { return store; } }

        public static string LogicalDate(DateTime now, Config cfg)
        {
            DateTime resetToday = now.Date.AddHours(cfg.daily_reset_hour).AddMinutes(cfg.daily_reset_min);
            DateTime d = now.TimeOfDay < resetToday.TimeOfDay ? now.Date.AddDays(-1) : now.Date;
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public string TodayDate { get { return dateKey; } }
        public double UsedTodayMin { get { return row.UseSec / 60.0; } }
        public double BreakTodayMin { get { return row.BreakSec / 60.0; } }
        public int MicroToday { get { return row.Micro; } }
        public int LongToday { get { return row.Long; } }
        public int SkipsToday { get { return row.Skips; } }
        public int PostponesToday { get { return row.Postpones; } }
        public bool DailyOverState { get { return dailyOver; } }
        public double BreakRemaining { get { return breakLeft < 0 ? 0 : breakLeft; } }
        public double BreakTotalSec { get { return breakTotal; } }
        public string BreakKindLabel { get { return pendingKind; } }
        public bool InBreak { get { return State == S_MICRO || State == S_LONG || State == S_DAILY; } }
        public int PostponesInARow { get { return postponesUsed; } }

        public double WorkTargetSec { get { return cfg.work_min * 60.0; } }

        public double SecondsToReminder
        {
            get
            {
                if (State == S_PREALERT) return preLeft < 0 ? 0 : preLeft;
                if (State == S_POSTPONED) return postLeft < 0 ? 0 : postLeft;
                if (InBreak) return 0;
                double left = WorkTargetSec - workSec;
                return left < 0 ? 0 : left;
            }
        }

        public void SetConfig(Config next)
        {
            next.Clamp();
            cfg = next;
            if (!cfg.enabled && State != S_DISABLED)
            {
                if (InBreak) CancelBreak();
                State = S_DISABLED;
                StateChanged();
            }
            else if (cfg.enabled && State == S_DISABLED)
            {
                State = S_WORKING;
                StateChanged();
            }
        }

        public void ToggleEnabled()
        {
            cfg.enabled = !cfg.enabled;
            cfg.Save(Paths.Config);
            SetConfig(cfg);
            Notice("护眼提醒已" + (cfg.enabled ? "恢复" : "暂停") + "（已写入 config.ini）");
            Log("TOGGLE enabled=" + (cfg.enabled ? "yes" : "no"));
        }

        public string Notice(string s)
        {
            Notices.Add(s);
            return s;
        }

        void Log(string s)
        {
            if (cfg.usage_log) store.Log(s);
        }

        double BreakSecFor(string kind)
        {
            if (kind == K_LONG) return cfg.long_break_min * 60.0;
            if (kind == K_DAILY) return cfg.long_break_min * 60.0;
            return cfg.micro_break_min * 60.0;
        }

        string NextKind()
        {
            if (cfg.micros_per_long <= 0) return K_MICRO;
            return (microSinceLong + 1) > cfg.micros_per_long ? K_LONG : K_MICRO;
        }

        void Persist()
        {
            store.Put(row);
        }

        // 核心一步：dt 秒内用户是否在场（present）。全部计时都只在这里推进。
        public void Step(double dt, bool present)
        {
            if (dt <= 0) dt = 0;
            if (dt > 10) dt = 10;

            if (!cfg.enabled)
            {
                if (State != S_DISABLED) { State = S_DISABLED; StateChanged(); }
                if (present) { row.UseSec += dt; Persist(); }
                return;
            }

            if (!present)
            {
                if (InBreak) CancelBreak();
                if (State == S_PREALERT || State == S_POSTPONED) { preLeft = 0; postLeft = 0; State = S_WORKING; }
                if (State != S_AWAY) { State = S_AWAY; StateChanged(); }
                return;
            }

            if (State == S_AWAY || State == S_DISABLED) { State = S_WORKING; StateChanged(); }

            Rollover();

            if (InBreak)
            {
                breakLeft -= dt;
                row.BreakSec += dt;
                Persist();
                BreakTicked(breakLeft < 0 ? 0 : breakLeft, breakTotal);
                if (breakLeft <= 0) FinishBreak(false);
                return;
            }

            if (State == S_PREALERT)
            {
                preLeft -= dt;
                if (preLeft <= 0) StartBreak(pendingKind);
                return;
            }

            if (State == S_POSTPONED)
            {
                CountUse(dt);
                postLeft -= dt;
                if (postLeft <= 0) StartBreak(pendingKind);
                return;
            }

            CountUse(dt);
            CheckDaily(dt);
            if (State != S_WORKING) return;

            if (workSec >= WorkTargetSec)
            {
                pendingKind = NextKind();
                postponesUsed = 0;
                preLeft = cfg.grace_sec;
                State = S_PREALERT;
                StateChanged();
                PreAlertStarted(pendingKind, preLeft);
                if (preLeft <= 0) StartBreak(pendingKind);
            }
        }

        void CountUse(double dt)
        {
            workSec += dt;
            row.UseSec += dt;
            flushSec += dt;
            if (flushSec >= 60) { flushSec = 0; Persist(); }
        }

        void CheckDaily(double dt)
        {
            double usedMin = row.UseSec / 60.0;
            double warnAt = cfg.daily_limit_min * cfg.daily_warn_pct / 100.0;
            if (!dailyWarned && usedMin >= warnAt)
            {
                dailyWarned = true;
                Notice(string.Format("今天已用机 {0:0} 分钟，距每日上限还剩约 {1} 分钟",
                    usedMin, (int)Math.Ceiling(cfg.daily_limit_min - usedMin)));
                Log(string.Format("DAILY_WARN used_min={0:0.0} limit={1:0.0}", usedMin, cfg.daily_limit_min));
            }
            if (!dailyOver && usedMin >= cfg.daily_limit_min)
            {
                dailyOver = true;
                dailyNotifyLeft = cfg.daily_notify_every_min * 60.0;
                dailyAgainLeft = cfg.daily_notify_every_min * 60.0;
                Log(string.Format("DAILY_OVER used_min={0:0.0} limit={1:0.0} lock={2}",
                    usedMin, cfg.daily_limit_min, cfg.daily_lock ? "yes" : "no"));
                if (cfg.daily_lock) { if (State == S_WORKING) StartBreak(K_DAILY); }
                else Notice(string.Format("今天用机 {0:0} 分钟，已超过上限 {1:0} 分钟，让眼睛歇歇吧",
                    usedMin, cfg.daily_limit_min));
                return;
            }
            if (dailyOver && cfg.daily_lock && State == S_WORKING)
            {
                dailyAgainLeft -= dt;
                if (dailyAgainLeft <= 0)
                {
                    dailyAgainLeft = cfg.daily_notify_every_min * 60.0;
                    StartBreak(K_DAILY);
                }
                return;
            }
            if (dailyOver && !cfg.daily_lock && State == S_WORKING)
            {
                dailyNotifyLeft -= dt;
                if (dailyNotifyLeft <= 0)
                {
                    Notice(string.Format("仍在超时使用：今天 {0:0} 分钟 / 上限 {1:0} 分钟",
                        row.UseSec / 60.0, cfg.daily_limit_min));
                    dailyNotifyLeft = cfg.daily_notify_every_min * 60.0;
                }
            }
        }

        void StartBreak(string kind)
        {
            pendingKind = kind;
            breakTotal = BreakSecFor(kind);
            breakLeft = breakTotal;
            State = kind == K_LONG ? S_LONG : (kind == K_DAILY ? S_DAILY : S_MICRO);
            StateChanged();
            BreakStarted(kind, breakTotal);
            Log(string.Format("BREAK_START kind={0} total_sec={1:0.##} used_today_min={2:0.0}",
                kind, breakTotal, row.UseSec / 60.0));
        }

        void FinishBreak(bool skipped)
        {
            string kind = pendingKind;
            if (kind == K_MICRO) { microSinceLong++; if (!skipped) row.Micro += 1; }
            else if (kind == K_LONG) { microSinceLong = 0; if (!skipped) row.Long += 1; }
            if (skipped) row.Skips += 1;
            if (kind == K_DAILY) dailyAgainLeft = cfg.daily_notify_every_min * 60.0;
            if (kind != K_DAILY) workSec = 0;
            postponesUsed = 0;
            breakLeft = 0;
            State = S_WORKING;
            Persist();
            BreakFinished(kind, skipped);
            StateChanged();
            Log(string.Format("BREAK_END kind={0} skipped={1}", kind, skipped ? "yes" : "no"));
        }

        void CancelBreak()
        {
            string kind = pendingKind;
            if (kind != K_DAILY) workSec = 0;
            breakLeft = 0;
            State = S_WORKING;
            if (kind == K_DAILY) dailyAgainLeft = cfg.daily_notify_every_min * 60.0;
            Persist();
            BreakFinished(kind, true);
            StateChanged();
            Log(string.Format("BREAK_END kind={0} skipped=auto-away", kind));
        }

        /// 跳过本次休息（daily_lock 遮罩不可跳过）
        public bool Skip()
        {
            if (!InBreak) return false;
            if (State == S_DAILY) { Notice("已超过每日上限，这次只能延后，不能跳过"); return false; }
            FinishBreak(true);
            return true;
        }

        /// 延后：先收掉遮罩，postpone_min 分钟后再提醒
        public bool Postpone()
        {
            if (!InBreak) return false;
            string kind = pendingKind;
            if (kind != K_DAILY && postponesUsed >= cfg.postpones_max)
            {
                Notice(string.Format("已经延后 {0} 次了，这次先把休息做完", postponesUsed));
                return false;
            }
            postponesUsed++;
            row.Postpones += 1;
            postLeft = kind == K_DAILY ? 600.0 : cfg.postpone_min * 60.0;
            breakLeft = 0;
            State = S_POSTPONED;
            Persist();
            BreakFinished(kind, false);
            StateChanged();
            Log(string.Format("BREAK_POSTPONE kind={0} until_sec={1:0.##} used={2}", kind, postLeft, postponesUsed));
            if (kind == K_DAILY) Notice("已延后 10 分钟，之后还会再提醒");
            return true;
        }

        /// 立即开始休息（托盘菜单）
        public void BreakNow()
        {
            if (InBreak) return;
            if (State == S_PREALERT || State == S_POSTPONED) { StartBreak(pendingKind); return; }
            postponesUsed = 0;
            StartBreak(NextKind());
        }

        /// 重置本轮工作计时（托盘「重新开始计时」）
        public void ResetWork()
        {
            if (InBreak) return;
            workSec = 0;
            microSinceLong = 0;
            postponesUsed = 0;
            State = S_WORKING;
            StateChanged();
            Log("WORK_RESET");
        }

        void Rollover()
        {
            string d = LogicalDate(Clock(), cfg);
            if (d == dateKey) return;
            Log("DAY_ROLLOVER from=" + dateKey);
            Persist();
            dateKey = d;
            row = store.Get(d);
            workSec = 0;
            microSinceLong = 0;
            postponesUsed = 0;
            dailyWarned = false;
            dailyOver = false;
            dailyNotifyLeft = 0;
            dailyAgainLeft = 0;
            if (InBreak) CancelBreak();
            State = S_WORKING;
            StateChanged();
        }

        public string StatusText
        {
            get
            {
                if (!cfg.enabled) return "已关闭（仅统计）";
                switch (State)
                {
                    case S_AWAY: return "离座暂停 · 今日 " + H(UsedTodayMin);
                    case S_PREALERT: return "准备休息 · " + ((int)Math.Ceiling(preLeft)) + " 秒后遮罩";
                    case S_POSTPONED: return "已延后 · " + ((int)Math.Ceiling(postLeft / 60.0)) + " 分钟后再提醒";
                    case S_MICRO: return "小休中 " + Cms(breakLeft);
                    case S_LONG: return "大休中 " + Cms(breakLeft);
                    case S_DAILY: return "已达每日上限 " + Cms(breakLeft);
                    default:
                        return string.Format("距提醒 {0:0} 分钟 · 今日 {1}",
                            Math.Ceiling(SecondsToReminder / 60.0), H(UsedTodayMin));
                }
            }
        }

        static string H(double min)
        {
            return min < 60 ? string.Format("{0:0} 分钟", min) : string.Format("{0:0.0} 小时", min / 60.0);
        }

        public static string Cms(double sec)
        {
            if (sec < 0) sec = 0;
            int s = (int)Math.Round(sec);
            return string.Format("{0:00}:{1:00}", s / 60, s % 60);
        }
    }

    // ---------------------------------------------------------------- 5. 遮罩界面（多屏覆盖 + 居中卡片）
    sealed class Backdrop : Form
    {
        public bool HoldTopmost;

        public Backdrop(Rectangle bounds, Color color, double opacity, bool holdTopmost, bool fullScreen)
        {
            HoldTopmost = holdTopmost;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            TopMost = true;
            BackColor = color;
            Opacity = opacity;
            if (fullScreen) Bounds = bounds;
            else
            {
                Rectangle wa = Rectangle.Empty;
                foreach (Screen s in Screen.AllScreens) if (s.Bounds == bounds) { wa = s.WorkingArea; break; }
                Bounds = wa == Rectangle.Empty ? bounds : wa;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Enabled = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (HoldTopmost && !IsDisposed && Visible) { TopMost = true; BringToFront(); }
        }
    }

    sealed class BreakCard : Form
    {
        Label title = new Label();
        Label countdown = new Label();
        Label sub = new Label();
        Label hint = new Label();
        Button btnPostpone = new Button();
        Button btnSkip = new Button();
        Panel bar = new Panel();
        readonly double scale;
        readonly int cardW;
        readonly int cardH;
        int barMax;
        public Action OnPostpone = delegate { };
        public Action OnSkip = delegate { };

        public BreakCard(Config cfg, double dpiScale, string kind)
        {
            scale = dpiScale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = cfg.CardColor;
            AutoScaleMode = AutoScaleMode.None;
            Font = F(cfg.font_size, false);

            cardW = S(470);
            cardH = S(cfg.show_tip ? 306 : 244);
            ClientSize = new Size(cardW, cardH);

            title.SetBounds(S(26), S(16), cardW - S(52), S(34));
            title.ForeColor = Color.White;
            title.Font = F(cfg.font_size + 3, true);
            title.Text = TitleFor(kind);

            countdown.SetBounds(S(26), S(54), cardW - S(52), S(66));
            countdown.Font = F(cfg.font_size + 26, true);
            countdown.ForeColor = cfg.AccentColor;
            countdown.TextAlign = ContentAlignment.MiddleCenter;
            countdown.Text = "00:00";

            bar.SetBounds(S(26), S(128), cardW - S(52), S(6));
            bar.BackColor = cfg.AccentColor;
            barMax = cardW - S(52);

            sub.SetBounds(S(26), S(142), cardW - S(52), S(38));
            sub.ForeColor = Color.FromArgb(214, 228, 242);
            sub.Font = F(Math.Max(14, cfg.font_size - 6), false);

            hint.SetBounds(S(26), S(184), cardW - S(52), S(cfg.show_tip ? 58 : 8));
            hint.ForeColor = Color.FromArgb(172, 192, 212);
            hint.Font = F(Math.Max(14, cfg.font_size - 8), false);
            hint.Text = cfg.show_tip ? TipFor(kind) : "";

            btnPostpone.SetBounds(S(26), cardH - S(58), S(192), S(38));
            btnPostpone.Text = kind == Engine.K_DAILY ? "延后 10 分钟" : "延后一会儿";
            Style(btnPostpone, cfg);
            btnPostpone.Click += delegate { OnPostpone(); };

            btnSkip.SetBounds(cardW - S(218), cardH - S(58), S(192), S(38));
            btnSkip.Text = "跳过本次休息";
            Style(btnSkip, cfg);
            btnSkip.Click += delegate { OnSkip(); };
            btnSkip.Visible = kind != Engine.K_DAILY;

            Controls.Add(title);
            Controls.Add(countdown);
            Controls.Add(bar);
            Controls.Add(sub);
            Controls.Add(hint);
            Controls.Add(btnPostpone);
            Controls.Add(btnSkip);
        }

        void Style(Button b, Config cfg)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(96, 200, 232, 255);
            b.BackColor = Color.FromArgb(38, 70, 110);
            b.ForeColor = Color.White;
            b.Font = F(Math.Max(14, cfg.font_size - 8), false);
            b.TabStop = true;
        }

        Font F(int px, bool bold)
        {
            FontStyle st = bold ? FontStyle.Bold : FontStyle.Regular;
            return new Font("Microsoft YaHei UI", (float)(px * scale), GraphicsUnit.Pixel);
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        static string TitleFor(string kind)
        {
            if (kind == Engine.K_LONG) return "大休时间到";
            if (kind == Engine.K_DAILY) return "今日用机已达上限";
            return "该休息一下了";
        }

        static string TipFor(string kind)
        {
            if (kind == Engine.K_DAILY) return "今天的配额已经用完：站起来走走、看看远处，剩下的明天再做也来得及。";
            if (kind == Engine.K_LONG) return "起身走一圈、喝口水；远眺 6 米以外 20 秒，转转肩颈和手腕。";
            return "眼睛离开屏幕，看 6 米以外的地方，眨眨眼 —— 这段别拿来刷手机。";
        }

        public void SetCountdown(double remaining, double total, string status)
        {
            if (IsDisposed) return;
            countdown.Text = Engine.Cms(remaining);
            sub.Text = status;
            int w = total <= 0 ? 0 : (int)Math.Round(barMax * (1.0 - remaining / total));
            if (w < 0) w = 0;
            if (w > barMax) w = barMax;
            bar.Width = w;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                if (btnSkip.Visible) OnSkip(); else OnPostpone();
            }
        }
    }

    sealed class OverlayView : IDisposable
    {
        readonly List<Backdrop> backdrops = new List<Backdrop>();
        BreakCard card;
        Rectangle virtualBounds;

        public Action OnPostpone = delegate { };
        public Action OnSkip = delegate { };

        public bool Visible { get { return card != null && !card.IsDisposed && card.Visible; } }

        public void Show(string kind, Config cfg, double total)
        {
            Close();
            double opacity = cfg.opacity_pct / 100.0;
            Color back = cfg.BackdropColor;
            virtualBounds = SystemInformation.VirtualScreen;
            foreach (Screen s in Screen.AllScreens)
            {
                Backdrop b = new Backdrop(s.Bounds, back, opacity, cfg.keep_topmost, cfg.cover_taskbar);
                b.Show();
                backdrops.Add(b);
            }
            card = new BreakCard(cfg, Native.DpiScale(), kind);
            card.OnPostpone = OnPostpone;
            card.OnSkip = OnSkip;
            Rectangle wa = CursorScreen().WorkingArea;
            card.Location = new Point(wa.Left + (wa.Width - card.Width) / 2,
                                      Math.Max(wa.Top + 8, wa.Top + (wa.Height - card.Height) / 2 - S(20)));
            card.Show();
            card.Activate();
            card.Focus();
        }

        int S(int v) { return (int)Math.Round(v * Native.DpiScale()); }

        Screen CursorScreen()
        {
            try
            {
                foreach (Screen s in Screen.AllScreens) if (s.Bounds.Contains(Cursor.Position)) return s;
            }
            catch { }
            return Screen.PrimaryScreen;
        }

        public void SetCountdown(double remaining, double total, string status)
        {
            if (card != null && !card.IsDisposed) card.SetCountdown(remaining, total, status);
        }

        public void Close()
        {
            if (card != null) { try { card.Close(); card.Dispose(); } catch { } card = null; }
            for (int i = backdrops.Count - 1; i >= 0; i--)
            {
                try { backdrops[i].Close(); backdrops[i].Dispose(); } catch { }
            }
            backdrops.Clear();
        }

        public void Dispose() { Close(); }
    }

    // ---------------------------------------------------------------- 6. 开机自启动（当前用户登录启动项）
    static class Autostart
    {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string ValueName = "EyeCare";

        public static string ExePath
        {
            get
            {
                try { return Application.ExecutablePath; }
                catch { return Path.Combine(Paths.Root, "EyeCare.exe"); }
            }
        }

        public static string Command { get { return "\"" + ExePath + "\" --min"; } }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    if (v == null) return false;
                    string s = Convert.ToString(v).Trim().Trim('"');
                    string want = Command.Trim().Trim('"');
                    return string.Equals(s, want, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static string Current()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return "(启动项不存在)";
                    object v = k.GetValue(ValueName);
                    return v == null ? "(未设置)" : Convert.ToString(v);
                }
            }
            catch (Exception e) { return "(读取失败: " + e.Message + ")"; }
        }

        public static string Set(bool on, out bool ok)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) { ok = false; return "无法打开当前用户的登录启动项"; }
                    if (on) k.SetValue(ValueName, Command, RegistryValueKind.String);
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                ok = true;
                return on ? "已开启开机自启动：" + Command : "已关闭开机自启动";
            }
            catch (Exception e) { ok = false; return "失败：" + e.Message; }
        }
    }

    // ---------------------------------------------------------------- 7. 设置界面（图形改参数，写回 config.ini）
    sealed class SettingsForm : Form
    {
        Config cfg;
        readonly Action<Config> apply;
        readonly Store store;

        CheckBox cbEnabled = new CheckBox();
        CheckBox cbBalloon = new CheckBox();
        CheckBox cbDailyLock = new CheckBox();
        CheckBox cbShowTip = new CheckBox();
        CheckBox cbCoverTaskbar = new CheckBox();
        CheckBox cbKeepTop = new CheckBox();
        CheckBox cbUsageLog = new CheckBox();
        CheckBox cbAutostart = new CheckBox();

        NumericUpDown numIdle = Row();
        NumericUpDown numWork = Row();
        NumericUpDown numMicro = Row();
        NumericUpDown numLong = Row();
        NumericUpDown numMicrosPerLong = Row();
        NumericUpDown numPostpone = Row();
        NumericUpDown numPostponeMax = Row();
        NumericUpDown numGrace = Row();
        NumericUpDown numDailyLimit = Row();
        NumericUpDown numWarnPct = Row();
        NumericUpDown numResetHour = Row();
        NumericUpDown numResetMin = Row();
        NumericUpDown numDailyEvery = Row();
        NumericUpDown numOpacity = Row();
        NumericUpDown numFont = Row();
        NumericUpDown numKeepDays = Row();

        static NumericUpDown Row() { NumericUpDown n = new NumericUpDown(); n.Width = 120; return n; }

        public SettingsForm(Config cfg, Store store, Action<Config> apply)
        {
            this.cfg = cfg.Clone();
            this.store = store;
            this.apply = apply;

            Text = "EyeCare 护眼提醒 · 设置（改完点「保存并生效」）";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(560, 560);
            ClientSize = new Size(580, 640);
            Font = new Font("Microsoft YaHei UI", 9f);
            ShowInTaskbar = true;

            FlowLayoutPanel body = new FlowLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;
            body.Padding = new Padding(14, 10, 14, 8);
            body.WrapContents = false;
            body.FlowDirection = FlowDirection.TopDown;

            Head(body, "总开关与闲置判定");
            Line(body, cbEnabled, "启用护眼提醒（关闭时只统计不提醒）", cfg.enabled);
            Line(body, cbBalloon, "显示右下角气泡提示", cfg.notify_balloon);
            Field(body, "无键鼠输入多少秒算离座（暂停计时）15~3600", numIdle, 15, 3600, 1, cfg.idle_timeout_sec, 0);

            Head(body, "连续使用循环");
            Field(body, "连续用机多少分钟提醒休息", numWork, 1, 600, 1, cfg.work_min, 1);
            Field(body, "小休时长（分钟）", numMicro, 1, 120, 1, cfg.micro_break_min, 1);
            Field(body, "大休时长（分钟）", numLong, 1, 240, 1, cfg.long_break_min, 1);
            Field(body, "几次小休后安排一次大休（0=不做大休）", numMicrosPerLong, 0, 50, 1, cfg.micros_per_long, 0);
            Field(body, "提前几秒预告「马上提醒」（0=直接弹遮罩）", numGrace, 0, 600, 5, cfg.grace_sec, 0);
            Field(body, "按「延后」推迟多少分钟", numPostpone, 1, 120, 1, cfg.postpone_min, 1);
            Field(body, "一次提醒最多可延后几次（0=不可延后）", numPostponeMax, 0, 20, 1, cfg.postpones_max, 0);

            Head(body, "每日累计上限");
            Field(body, "每日用机上限（分钟，6=6 小时）", numDailyLimit, 1, 1440, 10, cfg.daily_limit_min, 0);
            Field(body, "达到上限的百分之多少时先提醒", numWarnPct, 10, 100, 5, cfg.daily_warn_pct, 0);
            Field(body, "日界「小时」（早于此点算前一天）", numResetHour, 0, 23, 1, cfg.daily_reset_hour, 0);
            Field(body, "日界「分钟」", numResetMin, 0, 59, 5, cfg.daily_reset_min, 0);
            Line(body, cbDailyLock, "超过上限后用遮罩提示（可延后，不可跳过）", cfg.daily_lock);
            Field(body, "超限后每隔多少分钟再提醒一次", numDailyEvery, 1, 240, 1, cfg.daily_notify_every_min, 0);

            Head(body, "遮罩外观与记录");
            Field(body, "遮罩不透明度（30~95）", numOpacity, 30, 95, 1, cfg.opacity_pct, 0);
            Field(body, "休息卡片字号（14~72，已按屏幕缩放放大）", numFont, 14, 72, 2, cfg.font_size, 0);
            Line(body, cbShowTip, "显示护眼建议文字", cfg.show_tip);
            Line(body, cbKeepTop, "休息时把遮罩顶回最上层", cfg.keep_topmost);
            Line(body, cbCoverTaskbar, "遮住任务栏（关掉可留个退出口）", cfg.cover_taskbar);
            Line(body, cbUsageLog, "记录 log/usage.log 与 log/daily.csv", cfg.usage_log);
            Field(body, "汇总保留天数", numKeepDays, 1, 3650, 1, cfg.keep_days, 0);

            Head(body, "开机自启动");
            Line(body, cbAutostart, "开机自动启动 EyeCare（写当前用户登录启动项）", Autostart.IsEnabled());

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 58;

            Button btnSave = Btn("保存并生效", 12, 0, 120);
            btnSave.Click += Save;
            Button btnFile = Btn("打开 config.ini", 142, 0, 130);
            btnFile.Click += delegate { OpenWithShell(Paths.Config); };
            Button btnLog = Btn("打开日志目录", 282, 0, 110);
            btnLog.Click += delegate { Paths.EnsureLogDir(); OpenWithShell(Paths.LogDir); };
            Button btnDemo = Btn("预览遮罩", 402, 0, 90);
            btnDemo.Click += Demo;
            Button btnClose = Btn("关闭", 502, 0, 66);
            btnClose.Click += delegate { Close(); };
            bottom.Controls.Add(btnSave);
            bottom.Controls.Add(btnFile);
            bottom.Controls.Add(btnLog);
            bottom.Controls.Add(btnDemo);
            bottom.Controls.Add(btnClose);

            Controls.Add(body);
            Controls.Add(bottom);
            AcceptButton = btnSave;
        }

        static Button Btn(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 32);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            return b;
        }

        void Head(FlowLayoutPanel p, string text)
        {
            Label l = new Label();
            l.Text = "—— " + text + " ——";
            l.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(30, 96, 138);
            l.AutoSize = true;
            l.Margin = new Padding(2, 12, 0, 2);
            p.Controls.Add(l);
        }

        void Line(FlowLayoutPanel p, CheckBox c, string text, bool val)
        {
            c.Text = text;
            c.Checked = val;
            c.AutoSize = true;
            c.Margin = new Padding(2, 3, 0, 3);
            p.Controls.Add(c);
        }

        void Field(FlowLayoutPanel p, string label, NumericUpDown n, double lo, double hi, int step, double val, int decimals)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.WrapContents = false;
            row.Margin = new Padding(2, 2, 0, 2);

            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Width = 300;
            l.Margin = new Padding(0, 4, 0, 0);

            n.Minimum = Dec(lo);
            n.Maximum = Dec(hi);
            n.Increment = step;
            n.DecimalPlaces = decimals;
            n.Value = Dec(Math.Min(hi, Math.Max(lo, val)));
            row.Controls.Add(l);
            row.Controls.Add(n);
            p.Controls.Add(row);
        }

        static decimal Dec(double v) { return (decimal)Math.Round(v, 2); }

        Config Collect()
        {
            Config n = cfg.Clone();
            n.enabled = cbEnabled.Checked;
            n.notify_balloon = cbBalloon.Checked;
            n.idle_timeout_sec = (double)numIdle.Value;
            n.work_min = (double)numWork.Value;
            n.micro_break_min = (double)numMicro.Value;
            n.long_break_min = (double)numLong.Value;
            n.micros_per_long = (int)numMicrosPerLong.Value;
            n.grace_sec = (double)numGrace.Value;
            n.postpone_min = (double)numPostpone.Value;
            n.postpones_max = (int)numPostponeMax.Value;
            n.daily_limit_min = (double)numDailyLimit.Value;
            n.daily_warn_pct = (int)numWarnPct.Value;
            n.daily_reset_hour = (int)numResetHour.Value;
            n.daily_reset_min = (int)numResetMin.Value;
            n.daily_lock = cbDailyLock.Checked;
            n.daily_notify_every_min = (double)numDailyEvery.Value;
            n.opacity_pct = (int)numOpacity.Value;
            n.font_size = (int)numFont.Value;
            n.show_tip = cbShowTip.Checked;
            n.keep_topmost = cbKeepTop.Checked;
            n.cover_taskbar = cbCoverTaskbar.Checked;
            n.usage_log = cbUsageLog.Checked;
            n.keep_days = (int)numKeepDays.Value;
            n.Clamp();
            return n;
        }

        bool saving;

        void Save(object sender, EventArgs e)
        {
            if (saving) return;
            saving = true;
            Config next = Collect();
            bool autoWant = cbAutostart.Checked;
            string autoMsg;
            bool autoOk;
            if (autoWant != Autostart.IsEnabled()) autoMsg = Autostart.Set(autoWant, out autoOk);
            else { autoOk = true; autoMsg = "开机自启动保持" + (autoWant ? "开启" : "关闭"); }

            next.Save(Paths.Config);
            if (apply != null) apply(next);
            if (store != null) { store.FlushNow(); store.Log("SETTINGS_SAVED work_min=" + Config.N(next.work_min)
                + " micro=" + Config.N(next.micro_break_min) + " long=" + Config.N(next.long_break_min)
                + " micros_per_long=" + next.micros_per_long + " daily=" + Config.N(next.daily_limit_min)
                + " idle=" + Config.N(next.idle_timeout_sec) + " autostart=" + (autoWant ? "on" : "off")); }
            cfg = next;
            MessageBox.Show(this, "已写入 config.ini 并立即生效。\n开机自启动：" + autoMsg,
                "EyeCare", MessageBoxButtons.OK, autoOk ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            saving = false;
            Close();
        }

        void Demo(object sender, EventArgs e)
        {
            Config preview = Collect();
            OverlayView ov = new OverlayView();
            Timer t = new Timer();
            finalLeft = preview.micro_break_min * 60.0;
            finalTotal = finalLeft;
            DateTime start = DateTime.Now;
            ov.OnSkip = delegate { t.Stop(); ov.Dispose(); };
            ov.OnPostpone = delegate { t.Stop(); ov.Dispose(); };
            ov.Show(Engine.K_MICRO, preview, finalTotal);
            t.Tick += delegate
            {
                double left = finalTotal - (DateTime.Now - start).TotalSeconds;
                ov.SetCountdown(left < 0 ? 0 : left, finalTotal, "预览：这就是休息遮罩，实际时长见 config.ini");
                if (left <= 0) { t.Stop(); ov.Dispose(); }
            };
            t.Start();
        }

        double finalLeft, finalTotal;

        static void OpenWithShell(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 8. 统计窗口
    sealed class StatsForm : Form
    {
        public StatsForm(Engine e)
        {
            Text = "EyeCare · 使用统计（" + e.TodayDate + "）";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 430);
            Font = new Font("Microsoft YaHei UI", 9f);

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.Dock = DockStyle.Fill;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.BackColor = Color.White;
            tb.Text = Build(e);

            Button b = new Button();
            b.Text = "打开日志目录";
            b.Dock = DockStyle.Bottom;
            b.Height = 30;
            b.Click += delegate
            {
                Paths.EnsureLogDir();
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.LogDir) { UseShellExecute = true }); } catch { }
            };

            Controls.Add(tb);
            Controls.Add(b);
        }

        public static string Build(Engine e)
        {
            Config c = e.Cfg;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("今日（{0}，日界 {1:00}:{2:00}）", e.TodayDate, c.daily_reset_hour, c.daily_reset_min));
            sb.AppendLine(string.Format("  用机 {0:0.0} 分钟 / 上限 {1:0} 分钟    休息 {2:0.0} 分钟",
                e.UsedTodayMin, c.daily_limit_min, e.BreakTodayMin));
            sb.AppendLine(string.Format("  完成小休 {0} 次 · 大休 {1} 次 · 跳过 {2} 次 · 延后 {3} 次",
                e.MicroToday, e.LongToday, e.SkipsToday, e.PostponesToday));
            sb.AppendLine();
            sb.AppendLine("最近 7 天");
            sb.AppendLine("  日期          用机(分)  休息(分)  小休  大休  跳过  延后");
            List<DailyRow> rows = e.Store.Last(7);
            rows.Reverse();
            for (int i = 0; i < rows.Count; i++)
            {
                DailyRow r = rows[i];
                sb.AppendLine(string.Format("  {0}  {1,7:0.0}  {2,7:0.0}  {3,4}  {4,4}  {5,4}  {6,4}",
                    r.Date, r.UseSec / 60.0, r.BreakSec / 60.0, r.Micro, r.Long, r.Skips, r.Postpones));
            }
            sb.AppendLine();
            sb.AppendLine("当前循环：" + c.SummaryCycle());
            sb.AppendLine("当前状态：" + e.StatusText);
            sb.AppendLine("开机自启动：" + (Autostart.IsEnabled() ? "已开启" : "未开启"));
            sb.AppendLine("配置文件：" + Paths.Config);
            sb.AppendLine("统计文件：" + Paths.DailyCsv);
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- 9. 托盘常驻 + 运行时装配
    sealed class AppCtx : ApplicationContext
    {
        readonly Engine engine;
        readonly Store store;
        NotifyIcon tray;
        Timer tick;
        Timer configWatch;
        readonly OverlayView overlay = new OverlayView();
        DateTime lastReal = DateTime.Now;
        double lastWriteTicks;
        SettingsForm settings;
        StatsForm stats;
        RegisteredWaitHandle showWait;
        RegisteredWaitHandle stopWait;
        Mutex single;
        Icon lastIcon;
        Form pump;
        ContextMenu menu;
        int tickCount;
        bool exiting;

        public bool Ready { get { return single != null; } }

        public AppCtx(bool startMin)
        {
            bool created;
            single = new Mutex(true, "Local\\EyeCare.Gov.v1", out created);
            if (!created) return;

            Config cfg = Config.Load(Paths.Config);
            store = new Store();
            store.SetKeepDays(cfg.keep_days);
            engine = new Engine(cfg, store);

            engine.PreAlertStarted = delegate(string kind, double secs)
            {
                Log("PREALERT kind=" + kind + " grace_sec=" + Config.N(secs));
                Balloon("马上要提醒休息了", secs > 0
                    ? "再 " + (int)Math.Ceiling(secs) + " 秒后遮罩，先把手上的活收一下尾。"
                    : "遮罩即将出现。", 3000);
            };
            engine.BreakStarted = delegate(string kind, double total) { ShowBreak(kind, total); };
            engine.BreakTicked = delegate(double left, double total)
            { overlay.SetCountdown(left, total, engine.StatusText); };
            engine.BreakFinished = delegate(string kind, bool skipped) { overlay.Close(); };
            engine.StateChanged = delegate { UpdateTray(); };

            overlay.OnSkip = delegate
            {
                bool done = engine.Skip();
                if (!done) { }
            };
            overlay.OnPostpone = delegate { engine.Postpone(); };

            pump = new Form();
            pump.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            pump.ShowInTaskbar = false;
            pump.Size = new Size(2, 2);
            pump.StartPosition = FormStartPosition.Manual;
            pump.Location = new Point(-4000, -4000);
            pump.CreateControl();
            IntPtr dummy = pump.Handle;

            tray = new NotifyIcon();
            tray.Icon = MakeIcon(true);
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenSettings(); };
            BuildMenu();

            try { showWait = ThreadPool.RegisterWaitForSingleObject(
                ShowEvent(), delegate { BeginInvokeSafe(delegate { OpenSettings(); }); }, null, -1, false); } catch { }
            try { stopWait = ThreadPool.RegisterWaitForSingleObject(
                StopEvent(), delegate { BeginInvokeSafe(delegate { ExitApp(); }); }, null, -1, false); } catch { }

            tick = new Timer();
            tick.Interval = 1000;
            tick.Tick += OnTick;
            tick.Start();

            configWatch = new Timer();
            configWatch.Interval = 3000;
            configWatch.Tick += OnConfigWatch;
            configWatch.Start();
            try { lastWriteTicks = File.GetLastWriteTimeUtc(Paths.Config).Ticks; } catch { }

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPower;

            UpdateTray();
            if (cfg.usage_log) store.Log("START min=" + (startMin ? "yes" : "no")
                + " work_min=" + Config.N(cfg.work_min)
                + " micro=" + Config.N(cfg.micro_break_min)
                + " long=" + Config.N(cfg.long_break_min)
                + " micros_per_long=" + cfg.micros_per_long
                + " daily=" + Config.N(cfg.daily_limit_min)
                + " autostart=" + (Autostart.IsEnabled() ? "on" : "off"));
            if (!startMin)
                Balloon("EyeCare 护眼提醒已启动",
                    cfg.SummaryCycle() + "\n每日上限 " + Config.N(cfg.daily_limit_min) + " 分钟。"
                    + "\n双击托盘图标改参数，右键菜单可暂停 / 立即休息 / 开机自启。", 5000);
        }

        static EventWaitHandle ShowEvent()
        {
            bool exists;
            try { return EventWaitHandle.OpenExisting("Local\\EyeCare.Show.v1"); }
            catch { }
            return new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EyeCare.Show.v1", out exists);
        }

        static EventWaitHandle StopEvent()
        {
            try { return EventWaitHandle.OpenExisting("Local\\EyeCare.Stop.v1"); }
            catch { }
            bool exists;
            return new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EyeCare.Stop.v1", out exists);
        }

        public static void SignalShow()
        {
            using (EventWaitHandle h = ShowEvent()) h.Set();
        }

        public static bool SignalStop()
        {
            try { using (EventWaitHandle h = EventWaitHandle.OpenExisting("Local\\EyeCare.Stop.v1")) { h.Set(); return true; } }
            catch { return false; }
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff || e.Reason == SessionSwitchReason.ConsoleDisconnect)
            {
                overlay.Close();
                Log("SESSION " + e.Reason);
            }
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (overlay.Visible)
            {
                overlay.Close();
                overlay.Show(engine.BreakKindLabel, engine.Cfg, engine.BreakTotalSec);
                Log("OVERLAY_REBUILD display_changed");
            }
        }

        void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) { overlay.Close(); Log("POWER suspend"); }
        }

        void BeginInvokeSafe(MethodInvoker m)
        {
            try
            {
                if (pump == null || pump.IsDisposed) return;
                if (!pump.IsHandleCreated) return;
                pump.BeginInvoke(m);
            }
            catch { }
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            double dt = (now - lastReal).TotalSeconds;
            lastReal = now;
            if (dt < 0 || dt > 120) dt = 1.0;
            bool present = Native.IdleSeconds() <= engine.Cfg.idle_timeout_sec;
            engine.Step(dt, present);

            tickCount++;
            if (engine.Notices.Count > 0 && tickCount % 1 == 0)
            {
                foreach (string s in engine.Notices) Balloon("EyeCare", s, 4000);
                engine.Notices.Clear();
            }
            if (tickCount % 600 == 0) { store.FlushNow(); Log("HEARTBEAT state=" + engine.State + " used_min=" + engine.UsedTodayMin.ToString("0.0")); }
        }

        void OnConfigWatch(object sender, EventArgs e)
        {
            try
            {
                double t = File.GetLastWriteTimeUtc(Paths.Config).Ticks;
                if (t == lastWriteTicks) return;
                lastWriteTicks = t;
                Config next = Config.Load(Paths.Config);
                store.SetKeepDays(next.keep_days);
                engine.SetConfig(next);
                Log("CONFIG_RELOAD work_min=" + Config.N(next.work_min)
                    + " micro=" + Config.N(next.micro_break_min)
                    + " long=" + Config.N(next.long_break_min)
                    + " micros_per_long=" + next.micros_per_long
                    + " daily=" + Config.N(next.daily_limit_min)
                    + " enabled=" + (next.enabled ? "yes" : "no"));
                Balloon("配置已更新", next.SummaryCycle() + "\n每日上限 " + Config.N(next.daily_limit_min) + " 分钟", 3000);
            }
            catch (Exception ex) { Log("CONFIG_RELOAD_ERROR " + ex.Message); }
        }

        void ShowBreak(string kind, double total)
        {
            try
            {
                overlay.Show(kind, engine.Cfg, total);
                overlay.SetCountdown(total, total, engine.StatusText);
                Log("OVERLAY_SHOW kind=" + kind + " screens=" + Screen.AllScreens.Length);
            }
            catch (Exception e) { Log("OVERLAY_ERROR " + e.Message); }
        }

        void BuildMenu()
        {
            menu = new ContextMenu();
            MenuItem status = new MenuItem("状态：载入中");
            status.Enabled = false;
            menu.MenuItems.Add(status);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("暂停护眼提醒（保留统计）", delegate { engine.ToggleEnabled(); }));
            menu.MenuItems.Add(new MenuItem("立即休息一次", delegate { engine.BreakNow(); }));
            menu.MenuItems.Add(new MenuItem("跳过本次休息", delegate { engine.Skip(); }));
            menu.MenuItems.Add(new MenuItem("重新开始本轮计时", delegate { engine.ResetWork(); }));
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("设置…（改参数）", delegate { OpenSettings(); }));
            menu.MenuItems.Add(new MenuItem("今日与最近统计…", delegate { OpenStats(); }));
            menu.MenuItems.Add(new MenuItem("-"));
            MenuItem auto = new MenuItem("开机自动启动", delegate
            {
                bool ok;
                string msg = Autostart.Set(!Autostart.IsEnabled(), out ok);
                Log("AUTOSTART " + (Autostart.IsEnabled() ? "on" : "off") + " " + msg);
                Balloon("开机自启动", msg, 3500);
            });
            auto.Tag = "auto";
            menu.MenuItems.Add(auto);
            menu.MenuItems.Add(new MenuItem("打开程序目录", delegate { OpenWithShell(Paths.Root); }));
            menu.MenuItems.Add(new MenuItem("退出 EyeCare", delegate { ExitApp(); }));
            menu.Popup += OnMenuPopup;
            tray.ContextMenu = menu;
        }

        void OnMenuPopup(object sender, EventArgs e)
        {
            menu.MenuItems[0].Text = "状态：" + engine.StatusText;
            menu.MenuItems[2].Text = engine.Cfg.enabled ? "暂停护眼提醒（保留统计）" : "恢复护眼提醒";
            foreach (MenuItem mi in menu.MenuItems)
                if (mi.Tag != null && (string)mi.Tag == "auto")
                {
                    mi.Checked = Autostart.IsEnabled();
                    mi.Text = "开机自动启动" + (Autostart.IsEnabled() ? "（已开启）" : "（未开启）");
                }
        }

        void OpenSettings()
        {
            try
            {
                if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
                settings = new SettingsForm(engine.Cfg, store, delegate(Config next)
                {
                    lastWriteTicks = SafeTicks();
                    engine.SetConfig(next);
                });
                settings.FormClosed += delegate { settings = null; };
                settings.Show();
                settings.BringToFront();
                settings.Activate();
            }
            catch (Exception e) { Log("SETTINGS_ERROR " + e.Message); }
        }

        void OpenStats()
        {
            try
            {
                if (stats != null && !stats.IsDisposed) { stats.Activate(); return; }
                store.FlushNow();
                stats = new StatsForm(engine);
                stats.FormClosed += delegate { stats = null; };
                stats.Show();
            }
            catch (Exception e) { Log("STATS_ERROR " + e.Message); }
        }

        static double SafeTicks()
        {
            try { return File.GetLastWriteTimeUtc(Paths.Config).Ticks; } catch { return 0; }
        }

        void UpdateTray()
        {
            if (tray == null) return;
            string t = "EyeCare｜" + engine.StatusText;
            if (t.Length > 60) t = t.Substring(0, 60);
            if (tray.Text != t) tray.Text = t;
            tray.Icon = MakeIcon(engine.Cfg.enabled && engine.State != Engine.S_DISABLED);
        }

        void Balloon(string title, string body, int ms)
        {
            if (tray == null) return;
            if (!engine.Cfg.notify_balloon) return;
            try
            {
                if (body.Length > 190) body = body.Substring(0, 190);
                tray.ShowBalloonTip(ms, title, body.Replace("\\n", "\n"), ToolTipIcon.Info);
            }
            catch { }
        }

        Icon MakeIcon(bool on)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color c = on ? Color.FromArgb(79, 209, 165) : Color.FromArgb(158, 158, 158);
                using (Pen p = new Pen(c, 3f)) g.DrawEllipse(p, 2, 10, 28, 13);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 12, 12, 8, 9);
                using (Pen p = new Pen(Color.FromArgb(210, 255, 255, 255), 1.8f))
                {
                    g.DrawLine(p, 8, 4, 13, 10);
                    g.DrawLine(p, 24, 4, 19, 10);
                }
                if (!on) using (Pen p = new Pen(Color.FromArgb(210, 90, 90), 2.4f)) g.DrawLine(p, 4, 26, 28, 6);
            }
            IntPtr h = bmp.GetHicon();
            Icon ico = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
            bmp.Dispose();
            if (lastIcon != null) { try { lastIcon.Dispose(); } catch { } }
            lastIcon = ico;
            return ico;
        }

        static void OpenWithShell(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }

        void Log(string s)
        {
            if (engine != null && engine.Cfg.usage_log) store.Log(s);
        }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            try
            {
                tick.Stop();
                configWatch.Stop();
                overlay.Dispose();
                store.FlushNow();
                Log("EXIT used_today_min=" + engine.UsedTodayMin.ToString("0.0"));
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPower;
                if (showWait != null) showWait.Unregister(null);
                if (stopWait != null) stopWait.Unregister(null);
                if (tray != null) tray.Visible = false;
                if (pump != null) { try { pump.Close(); pump.Dispose(); } catch { } }
            }
            catch { }
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            try { if (tray != null) tray.Visible = false; } catch { }
            base.ExitThreadCore();
        }
    }

    // ---------------------------------------------------------------- 10. 入口与命令行
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            List<string> a = new List<string>();
            if (args != null) for (int i = 0; i < args.Length; i++) a.Add(args[i].ToLowerInvariant());

            bool console = Has(a, "--selftest") || Has(a, "--today") || Has(a, "--report") || Has(a, "--help")
                || Has(a, "-h") || Has(a, "--version") || Has(a, "--enable-autostart") || Has(a, "--disable-autostart")
                || Has(a, "--autostart-status") || Has(a, "--autostart-command") || Has(a, "--stop") || Has(a, "--demo-break") || Has(a, "--reset-stats");
            if (console) TryAttachConsole();

            if (Has(a, "--help") || Has(a, "-h")) { Console.Out.Write(Help()); Console.Out.Flush(); return; }
            if (Has(a, "--version")) { Say("EyeCare 1.0 (WinForms / .NET Framework 4.8)"); return; }

            if (Has(a, "--selftest")) { Environment.Exit(SelfTest.Run()); return; }

            if (Has(a, "--enable-autostart") || Has(a, "--disable-autostart"))
            {
                bool on = Has(a, "--enable-autostart");
                bool ok;
                string msg = Autostart.Set(on, out ok);
                Say((ok ? "OK   " : "FAIL ") + msg);
                Environment.Exit(ok ? 0 : 1);
            }
            if (Has(a, "--autostart-status"))
            {
                Say((Autostart.IsEnabled() ? "enabled" : "disabled") + "｜当前值：" + Autostart.Current());
                return;
            }
            if (Has(a, "--autostart-command"))
            {
                Say("将写入 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 的值：");
                Say("  值名：EyeCare");
                Say("  数据：" + Autostart.Command);
                Say("  当前：" + Autostart.Current());
                Say("  判定：" + (Autostart.IsEnabled() ? "已开启（与上面数据一致）" : "未开启"));
                return;
            }
            if (Has(a, "--stop"))
            {
                Say(AppCtx.SignalStop() ? "已通知运行中的 EyeCare 退出" : "没有正在运行的 EyeCare");
                return;
            }
            if (Has(a, "--reset-stats"))
            {
                try
                {
                    if (File.Exists(Paths.DailyCsv)) File.Delete(Paths.DailyCsv);
                    if (File.Exists(Paths.UsageLog)) File.Delete(Paths.UsageLog);
                    Say("已清空 log/daily.csv 与 log/usage.log");
                }
                catch (Exception e) { Say("失败：" + e.Message); }
                return;
            }
            if (Has(a, "--today") || Has(a, "--report"))
            {
                int days = Has(a, "--report") ? IntArg(a, "--report", 7) : 7;
                Say(ReportText(days));
                return;
            }
            if (Has(a, "--demo-break"))
            {
                string kind = StrArg(a, "--demo-break", Engine.K_MICRO);
                if (kind != Engine.K_LONG && kind != Engine.K_DAILY) kind = Engine.K_MICRO;
                RunDemo(kind, IntArg(a, "--secs", 15));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool created;
            using (Mutex probe = new Mutex(true, "Local\\EyeCare.Gov.v1", out created))
            {
                if (!created)
                {
                    AppCtx.SignalShow();
                    Say("EyeCare 已在运行：已让它把设置窗口带到前台。");
                    return;
                }
                probe.ReleaseMutex();
            }

            AppCtx ctx = new AppCtx(Has(a, "--min"));
            if (!ctx.Ready)
            {
                AppCtx.SignalShow();
                Say("EyeCare 已在运行。");
                return;
            }
            Application.Run(ctx);
        }

        static void RunDemo(string kind, int secs)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config cfg = Config.Load(Paths.Config);
            double total = secs;
            OverlayView ov = new OverlayView();
            Timer t = new Timer();
            DateTime start = DateTime.Now;
            ov.OnSkip = delegate { t.Stop(); ov.Dispose(); Application.Exit(); };
            ov.OnPostpone = delegate { t.Stop(); ov.Dispose(); Application.Exit(); };
            ov.Show(kind, cfg, total);
            t.Interval = 200;
            t.Tick += delegate
            {
                double left = total - (DateTime.Now - start).TotalSeconds;
                ov.SetCountdown(left < 0 ? 0 : left, total,
                    "演示遮罩（" + kind + "）：真实时长由 config.ini 决定，" + Engine.Cms(left) + " 后自动关闭");
                if (left <= 0) { t.Stop(); ov.Dispose(); Application.Exit(); }
            };
            t.Start();
            Application.Run();
        }

        static string ReportText(int days)
        {
            Config cfg = Config.Load(Paths.Config);
            Store st = new Store();
            st.Load();
            string today = Engine.LogicalDate(DateTime.Now, cfg);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("EyeCare 用机统计（日界 " + string.Format("{0:00}:{1:00}", cfg.daily_reset_hour, cfg.daily_reset_min) + "）");
            DailyRow r = st.Get(today);
            sb.AppendLine(string.Format("今日 {0}：用机 {1:0.0} 分钟 / 上限 {2:0} 分钟 · 休息 {3:0.0} 分钟 · 小休 {4} 大休 {5} 跳过 {6} 延后 {7}",
                today, r.UseSec / 60.0, cfg.daily_limit_min, r.BreakSec / 60.0, r.Micro, r.Long, r.Skips, r.Postpones));
            sb.AppendLine();
            sb.AppendLine("最近 " + days + " 天");
            sb.AppendLine("日期          用机(分)  休息(分)  小休  大休  跳过  延后");
            List<DailyRow> rows = st.Last(days);
            rows.Reverse();
            for (int i = 0; i < rows.Count; i++)
            {
                DailyRow d = rows[i];
                sb.AppendLine(string.Format("{0}  {1,7:0.0}  {2,7:0.0}  {3,4}  {4,4}  {5,4}  {6,4}",
                    d.Date, d.UseSec / 60.0, d.BreakSec / 60.0, d.Micro, d.Long, d.Skips, d.Postpones));
            }
            sb.AppendLine();
            sb.AppendLine("循环设置：" + cfg.SummaryCycle());
            sb.AppendLine("开机自启动：" + (Autostart.IsEnabled() ? "已开启（" + Autostart.Command + "）" : "未开启"));
            try { File.WriteAllText(Path.Combine(Paths.Root, "report.txt"), sb.ToString(), new UTF8Encoding(true)); } catch { }
            return sb.ToString();
        }

        static void Say(string s)
        {
            try { Console.Out.WriteLine(s); Console.Out.Flush(); } catch { }
            try { File.WriteAllText(Path.Combine(Paths.Root, "cli-last.txt"), s, new UTF8Encoding(true)); } catch { }
        }

        static bool Has(List<string> a, string k)
        {
            foreach (string s in a) if (s == k || s.StartsWith(k + "=", StringComparison.Ordinal)) return true;
            return false;
        }

        static int IntArg(List<string> a, string key, int d)
        {
            foreach (string s in a)
                if (s.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    int v;
                    if (int.TryParse(s.Substring(key.Length + 1), out v) && v > 0) return v;
                }
            int i = a.IndexOf(key);
            if (i >= 0 && i + 1 < a.Count)
            {
                int v;
                if (int.TryParse(a[i + 1], out v) && v > 0) return v;
            }
            return d;
        }

        static string StrArg(List<string> a, string key, string d)
        {
            foreach (string s in a) if (s.StartsWith(key + "=", StringComparison.Ordinal)) return s.Substring(key.Length + 1);
            int i = a.IndexOf(key);
            if (i >= 0 && i + 1 < a.Count)
            {
                string v = a[i + 1];
                if (v.Length > 0 && !v.StartsWith("-", StringComparison.Ordinal)) return v;
            }
            return d;
        }

        static string Help()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("EyeCare —— 电脑使用时长护眼提醒（可循环 · 参数可改 · 可开机自启动）");
            sb.AppendLine();
            sb.AppendLine("用法：");
            sb.AppendLine("  EyeCare.exe                        启动（托盘常驻）");
            sb.AppendLine("  EyeCare.exe --min                  启动并最小化（开机自启动用的就是这个）");
            sb.AppendLine("  EyeCare.exe --demo-break [kind]    预览休息遮罩，kind = micro|long|daily，配 --secs 15");
            sb.AppendLine("  EyeCare.exe --today                打印今日统计");
            sb.AppendLine("  EyeCare.exe --report 7             打印最近 7 天统计（同时写 report.txt）");
            sb.AppendLine("  EyeCare.exe --enable-autostart     开启开机自启动");
            sb.AppendLine("  EyeCare.exe --disable-autostart    关闭开机自启动");
            sb.AppendLine("  EyeCare.exe --autostart-status     查看自启动状态");
            sb.AppendLine("  EyeCare.exe --autostart-command    打印将要写入的启动命令（不写任何东西）");
            sb.AppendLine("  EyeCare.exe --reset-stats          清空统计与日志");
            sb.AppendLine("  EyeCare.exe --stop                 退出正在运行的实例");
            sb.AppendLine("  EyeCare.exe --selftest             状态机自检（写 selftest-report.txt）");
            sb.AppendLine();
            sb.AppendLine("改参数：编辑同目录 config.ini（保存后约 3 秒自动生效），或托盘右键「设置…」。");
            sb.AppendLine("  work_min 连续用机分钟 · micro_break_min 小休 · long_break_min 大休");
            sb.AppendLine("  micros_per_long 几次小休配一次大休 · daily_limit_min 每日上限");
            sb.AppendLine("  idle_timeout_sec 离座判定秒 · daily_lock 超限是否遮罩 · postpones_max 可延后次数");
            sb.AppendLine("记录：log/usage.log（事件流）、log/daily.csv（每日汇总）。");
            return sb.ToString();
        }

        // 本程序是 GUI 子系统（winexe），平时没有控制台。命令行用法需要先挂回父进程的控制台，
        // 这样在 cmd / PowerShell 里运行才能看到输出；挂不上（比如被资源管理器直接双击）就安静跳过，
        // 此时结果仍会写到 cli-last.txt / selftest-report.txt / report.txt，信息不会丢。
        // 注意：只 AttachConsole，绝不 Console.SetOut —— 一旦换掉输出流，重定向到文件的管道会被打断。
        static void TryAttachConsole()
        {
            try { Native.AttachConsole(Native.ATTACH_PARENT_PROCESS); }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 11. 自检：确定性仿真状态机
    static class SelfTest
    {
        static int passCount;
        static int failCount;
        static readonly List<string> lines = new List<string>();

        public static int Run()
        {
            Paths.Root = Path.Combine(AppContext.BaseDirectory, "_selftest");
            try { if (!Directory.Exists(Paths.Root)) Directory.CreateDirectory(Paths.Root); } catch { }
            try { if (File.Exists(Paths.DailyCsv)) File.Delete(Paths.DailyCsv); } catch { }
            try { if (File.Exists(Paths.UsageLog)) File.Delete(Paths.UsageLog); } catch { }

            Check("ini 读写往返 + 越界夹紧", T_Ini());
            Check("连续工作 → 小休 → 循环", T_Micro());
            Check("几次小休之后一次大休", T_Long());
            Check("闲置暂停：离座不计时", T_Away());
            Check("休息中途离座自动收遮罩", T_AwayInBreak());
            Check("延后与可延后次数上限", T_Postpone());
            Check("跳过：计数与继续计时", T_Skip());
            Check("每日预警与超限（仅气泡）", T_Daily());
            Check("daily_lock：超限遮罩可延后不可跳过", T_DailyLock());
            Check("日界跨天清零", T_Rollover());
            Check("关闭开关后只统计不提醒", T_Disabled());
            Check("预告（grace_sec）先行", T_Prealert());
            Check("参数夹紧不崩", T_Clamp());
            Check("usage.log / daily.csv 落盘", T_Files());
            Check("多屏覆盖 API 可用", T_Screens());
            Check("keep_days 裁剪历史行", T_KeepDays());

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("EyeCare selftest  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("csc/.NET " + Environment.Version + "  屏幕数 " + SafeScreens());
            for (int i = 0; i < lines.Count; i++) sb.AppendLine(lines[i]);
            sb.AppendLine();
            sb.AppendLine(string.Format("PASS={0} FAIL={1}  =>  {2}", passCount, failCount,
                failCount == 0 ? "ALL GREEN" : "HAS FAILURE"));
            string text = sb.ToString();
            // 自检把 Paths.Root 指到 _selftest 子目录以免覆盖真实 config.ini，
            // 这里同时往程序目录放一份报告，保证 build.bat 提示的路径可查。
            try { File.WriteAllText(Paths.SelfTestReport, text, new UTF8Encoding(true)); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-report.txt"), text, new UTF8Encoding(true));
            }
            catch { }
            try { Console.Out.Write(text); Console.Out.Flush(); } catch { }
            return failCount == 0 ? 0 : 1;
        }

        static string SafeScreens()
        {
            try { return Screen.AllScreens.Length.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        static void Check(string name, string err)
        {
            if (err == null) { passCount++; lines.Add("PASS  " + name); }
            else { failCount++; lines.Add("FAIL  " + name + "  ->  " + err); }
        }

        static Config Cfg()
        {
            Config c = new Config();
            c.enabled = true;
            c.notify_balloon = false;
            c.usage_log = true;
            c.work_min = 45;
            c.micro_break_min = 5;
            c.long_break_min = 20;
            c.micros_per_long = 4;
            c.postpone_min = 5;
            c.postpones_max = 2;
            c.grace_sec = 0;
            c.daily_limit_min = 360;
            c.daily_warn_pct = 90;
            c.daily_lock = false;
            return c;
        }

        sealed class Sim
        {
            public Engine e;
            DateTime now;

            public Sim(Config cfg, DateTime start)
            {
                // 用例隔离：每次仿真都从空的 daily.csv / usage.log 开始，
                // 否则同一个逻辑日的累计值会从前一个用例带进来。
                try { if (File.Exists(Paths.DailyCsv)) File.Delete(Paths.DailyCsv); } catch { }
                try { if (File.Exists(Paths.UsageLog)) File.Delete(Paths.UsageLog); } catch { }
                Store st = new Store();
                now = start;
                e = new Engine(cfg, st);
                e.Clock = delegate { return now; };
            }

            public void Run(double seconds, bool present)
            {
                double done = 0;
                while (done < seconds)
                {
                    double step = seconds - done;
                    if (step > 1) step = 1;
                    now = now.AddSeconds(step);
                    e.Step(step, present);
                    done += step;
                }
            }
        }

        static DateTime T0() { return new DateTime(2026, 2, 14, 10, 0, 0); }

        static string T_Ini()
        {
            Config c = Cfg();
            c.work_min = 37.5;
            c.daily_reset_hour = 5;
            c.daily_reset_min = 30;
            c.backdrop_color = "#20304F";
            c.Save(Paths.Config);
            Config d = Config.Load(Paths.Config);
            if (Math.Abs(d.work_min - 37.5) > 0.001) return "work_min 往返失败，得到 " + d.work_min;
            if (d.daily_reset_hour != 5 || d.daily_reset_min != 30) return "daily_reset_at 往返失败";
            if (d.backdrop_color != "#20304F") return "颜色往返失败";
            string txt = File.ReadAllText(Paths.Config, Encoding.UTF8)
                + Environment.NewLine + "work_min = 99999" + Environment.NewLine
                + "opacity_pct = -5" + Environment.NewLine + "micros_per_long = abc" + Environment.NewLine;
            File.WriteAllText(Paths.Config, txt, new UTF8Encoding(true));
            Config bad = Config.Load(Paths.Config);
            if (bad.work_min > 600) return "越界值未被夹紧：" + bad.work_min;
            if (bad.opacity_pct < 30) return "opacity_pct 未被夹紧：" + bad.opacity_pct;
            if (bad.micros_per_long != 4) return "非法值应回退默认，得到 " + bad.micros_per_long;
            return null;
        }

        static string T_Micro()
        {
            Sim s = new Sim(Cfg(), T0());
            s.Run(44 * 60 + 59, true);
            if (s.e.State != Engine.S_WORKING) return "44分59秒不该提醒，状态=" + s.e.State;
            if (s.e.SecondsToReminder > 1.5) return "剩余应约 1 秒，实际 " + s.e.SecondsToReminder;
            s.Run(2, true);
            if (s.e.State != Engine.S_MICRO) return "满 45 分钟应进入小休，状态=" + s.e.State;
            if (Math.Abs(s.e.BreakTotalSec - 300) > 0.6) return "小休应 300 秒，实际 " + s.e.BreakTotalSec;
            // 弹遮罩的那一步已用掉 1 秒，所以这里还剩 299 秒
            s.Run(4 * 60 + 58, true);
            if (s.e.State != Engine.S_MICRO) return "小休提前结束（应为 300 秒）";
            s.Run(2, true);
            if (s.e.State != Engine.S_WORKING) return "小休后应回到工作，状态=" + s.e.State;
            if (s.e.MicroToday != 1) return "应完成 1 次小休，实际 " + s.e.MicroToday;
            if (Math.Abs(s.e.UsedTodayMin - 45) > 0.3) return "当日工作时长应约 45 分钟（休息另记），实际 " + s.e.UsedTodayMin;
            if (Math.Abs(s.e.BreakTodayMin - 5) > 0.3) return "当日休息时长应约 5 分钟，实际 " + s.e.BreakTodayMin;
            // 小休结束的那一步已把 1 秒计入新一轮工作计时，所以这里只差 2 秒才提醒
            s.Run(44 * 60 + 57, true);
            if (s.e.State == Engine.S_MICRO) return "第 2 次提醒过早（还剩约 3 秒）";
            if (s.e.SecondsToReminder < 1.5 || s.e.SecondsToReminder > 4.0)
                return "第 2 轮剩余提醒时间应在 2~3 秒附近，实际 " + s.e.SecondsToReminder.ToString("0.##");
            s.Run(4, true);
            if (s.e.State != Engine.S_MICRO) return "补足后应再次提醒，状态=" + s.e.State;
            if (s.e.MicroToday != 1) return "第 2 次小休进行中的同时，应仍记着已完成的 1 次，实际 " + s.e.MicroToday;
            return null;
        }

        static string T_Long()
        {
            Sim s = new Sim(Cfg(), T0());
            for (int i = 0; i < 4; i++)
            {
                s.Run(45 * 60 + 1, true);
                if (s.e.State != Engine.S_MICRO) return "第 " + (i + 1) + " 次应为小休，实际 " + s.e.State;
                s.Run(5 * 60 + 1, true);
            }
            if (s.e.MicroToday != 4) return "应有 4 次小休，实际 " + s.e.MicroToday;
            s.Run(45 * 60 + 1, true);
            if (s.e.State != Engine.S_LONG) return "第 5 次应是大休，实际 " + s.e.State;
            if (Math.Abs(s.e.BreakTotalSec - 1200) > 0.6) return "大休应 1200 秒，实际 " + s.e.BreakTotalSec;
            s.Run(20 * 60 + 1, true);
            if (s.e.LongToday != 1) return "大休应计数，实际 " + s.e.LongToday;
            s.Run(45 * 60 + 1, true);
            if (s.e.State != Engine.S_MICRO) return "大休后应重新从小休开始循环，实际 " + s.e.State;
            Config noLong = Cfg();
            noLong.micros_per_long = 0;
            Sim s2 = new Sim(noLong, T0());
            for (int i = 0; i < 6; i++)
            {
                s2.Run(45 * 60 + 1, true);
                if (s2.e.State != Engine.S_MICRO) return "micros_per_long=0 时不该有大休，第 " + (i + 1) + " 次=" + s2.e.State;
                s2.Run(5 * 60 + 1, true);
            }
            return null;
        }

        static string T_Away()
        {
            Sim s = new Sim(Cfg(), T0());
            s.Run(30 * 60, true);
            double before = s.e.UsedTodayMin;
            s.Run(120, false);
            if (s.e.State != Engine.S_AWAY) return "离座应进入 away，实际 " + s.e.State;
            if (Math.Abs(s.e.UsedTodayMin - before) > 0.01) return "离座期间不应累计使用时长";
            s.Run(14 * 60, true);
            if (s.e.State == Engine.S_AWAY) return "回来后应恢复计时";
            if (s.e.State == Engine.S_MICRO) return "累计只 44 分钟，不该提醒，实际 " + s.e.State;
            s.Run(70, true);
            if (s.e.State != Engine.S_MICRO) return "补满 45 分钟应提醒，实际 " + s.e.State;
            return null;
        }

        static string T_AwayInBreak()
        {
            Sim s = new Sim(Cfg(), T0());
            s.Run(45 * 60 + 1, true);
            if (s.e.State != Engine.S_MICRO) return "应先进入小休";
            s.Run(60, false);
            if (s.e.State != Engine.S_AWAY) return "休息中离座应自动收遮罩，实际 " + s.e.State;
            s.Run(1, true);
            if (s.e.State != Engine.S_WORKING && s.e.State != Engine.S_AWAY) return "回来后状态应合法，实际 " + s.e.State;
            return null;
        }

        static string T_Postpone()
        {
            Sim s = new Sim(Cfg(), T0());
            s.Run(45 * 60 + 1, true);
            if (s.e.State != Engine.S_MICRO) return "应先进入小休";
            if (!s.e.Postpone()) return "第 1 次延后应成功";
            if (s.e.State != Engine.S_POSTPONED) return "延后后应为 postponed，实际 " + s.e.State;
            s.Run(4 * 60, true);
            if (s.e.State != Engine.S_POSTPONED) return "延后未满 5 分钟不该再提醒";
            s.Run(70, true);
            if (s.e.State != Engine.S_MICRO) return "延后到点应重新休息，实际 " + s.e.State;
            if (s.e.PostponesToday != 1) return "延后应计入统计，实际 " + s.e.PostponesToday;
            if (!s.e.Postpone()) return "第 2 次延后应成功（postpones_max=2）";
            s.Run(5 * 60 + 1, true);
            if (s.e.State != Engine.S_MICRO) return "第 2 次延后到点应重新休息";
            if (s.e.Postpone()) return "延后次数已满，第 3 次应被拒绝";
            s.Run(5 * 60 + 1, true);
            if (s.e.State != Engine.S_WORKING) return "这次应把休息做完，实际 " + s.e.State;
            if (s.e.MicroToday != 1) return "做完的这次应计入完成次数";
            return null;
        }

        static string T_Skip()
        {
            Sim s = new Sim(Cfg(), T0());
            s.Run(45 * 60 + 1, true);
            if (!s.e.Skip()) return "跳过应成功";
            if (s.e.State != Engine.S_WORKING) return "跳过应回到工作，实际 " + s.e.State;
            if (s.e.MicroToday != 0) return "被跳过的小休不该计入完成次数";
            if (s.e.SkipsToday != 1) return "应记 1 次跳过，实际 " + s.e.SkipsToday;
            double used = s.e.UsedTodayMin;
            s.Run(60, true);
            if (s.e.UsedTodayMin <= used) return "跳过后应继续正常计时";
            s.Run(45 * 60 + 1, true);
            s.Run(5 * 60 + 1, true);
            if (s.e.MicroToday != 1) return "完成一次小休应计数，实际 " + s.e.MicroToday;
            return null;
        }

        static string T_Daily()
        {
            Config c = Cfg();
            c.work_min = 500;
            c.daily_limit_min = 120;
            c.daily_warn_pct = 90;
            c.daily_notify_every_min = 15;
            Sim s = new Sim(c, T0());
            s.Run(100 * 60, true);
            if (s.e.DailyOverState) return "100 分钟不该算超限";
            if (s.e.State != Engine.S_WORKING) return "不该弹遮罩，状态=" + s.e.State;
            s.Run(10 * 60, true);
            if (s.e.DailyOverState) return "110 分钟不该算超限（上限 120）";
            s.Run(11 * 60, true);
            if (!s.e.DailyOverState) return "121 分钟应判定超限";
            if (s.e.State == Engine.S_DAILY) return "未开 daily_lock 时超限不该弹遮罩，状态=" + s.e.State;
            bool warn = false, over = false;
            foreach (string n in s.e.Notices)
            {
                if (n.IndexOf("距每日上限") >= 0) warn = true;
                if (n.IndexOf("已超过上限") >= 0) over = true;
            }
            if (!warn) return "缺少 90% 预警提示";
            if (!over) return "缺少超限提示";
            return null;
        }

        static string T_DailyLock()
        {
            Config c = Cfg();
            c.work_min = 500;
            c.daily_limit_min = 60;
            c.daily_warn_pct = 90;
            c.daily_lock = true;
            c.daily_notify_every_min = 10;
            Sim s = new Sim(c, T0());
            s.Run(59 * 60 + 30, true);
            if (s.e.State == Engine.S_DAILY) return "未到上限就弹遮罩了";
            s.Run(40, true);
            if (s.e.State != Engine.S_DAILY) return "开 daily_lock 后超限应弹遮罩，实际 " + s.e.State;
            if (s.e.Skip()) return "超限遮罩不应可跳过";
            if (s.e.State != Engine.S_DAILY) return "跳过被拒后遮罩应仍在，实际 " + s.e.State;
            if (!s.e.Postpone()) return "超限遮罩应可延后";
            if (s.e.State != Engine.S_POSTPONED) return "延后后应收起遮罩，实际 " + s.e.State;
            s.Run(9 * 60, true);
            if (s.e.State != Engine.S_POSTPONED) return "延后未满 10 分钟不该再遮罩";
            s.Run(70, true);
            if (s.e.State != Engine.S_DAILY) return "延后到点应再次遮罩，实际 " + s.e.State;
            return null;
        }

        static string T_Rollover()
        {
            Config c = Cfg();
            c.work_min = 400;
            c.daily_limit_min = 600;
            c.daily_reset_hour = 4;
            c.daily_reset_min = 0;
            Sim s = new Sim(c, new DateTime(2026, 2, 14, 23, 0, 0));
            s.Run(2 * 60 * 60, true);
            if (s.e.TodayDate != "2026-02-14") return "凌晨 1 点应仍算 02-14（日界 04:00），实际 " + s.e.TodayDate;
            if (Math.Abs(s.e.UsedTodayMin - 120) > 1.0) return "应有约 120 分钟累计，实际 " + s.e.UsedTodayMin;
            s.e.Store.FlushNow();
            s.Run(3 * 60 * 60 + 60, true);
            s.e.Store.FlushNow();
            if (s.e.TodayDate != "2026-02-15") return "04:01 应已切到 02-15，实际 " + s.e.TodayDate;
            if (s.e.UsedTodayMin > 2.0) return "跨日后新一天应从零开始，实际 " + s.e.UsedTodayMin;
            if (s.e.DailyOverState) return "跨日后超限标记应复位";
            Store st = new Store();
            st.Load();
            DailyRow prev = st.Get("2026-02-14");
            if (prev.UseSec < 290 * 60) return "前一天记录应保留（" + (prev.UseSec / 60.0).ToString("0.0") + " 分）";
            return null;
        }

        static string T_Disabled()
        {
            Config c = Cfg();
            c.enabled = false;
            Sim s = new Sim(c, T0());
            s.Run(60 * 60, true);
            if (s.e.State != Engine.S_DISABLED) return "关闭后状态应为 disabled，实际 " + s.e.State;
            if (Math.Abs(s.e.UsedTodayMin - 60) > 0.3) return "关闭时仍应统计当日时长，实际 " + s.e.UsedTodayMin;
            if (s.e.InBreak) return "关闭时不应弹遮罩";
            return null;
        }

        static string T_Prealert()
        {
            Config c = Cfg();
            c.grace_sec = 20;
            Sim s = new Sim(c, T0());
            s.Run(44 * 60 + 45, true);
            if (s.e.State != Engine.S_WORKING) return "预告过早";
            s.Run(20, true);
            if (s.e.State != Engine.S_PREALERT) return "应先短暂进入 prealert 再遮罩，实际 " + s.e.State;
            s.Run(25, true);
            if (s.e.State != Engine.S_MICRO) return "预告结束后应弹遮罩，实际 " + s.e.State;
            if (Math.Abs(s.e.BreakTotalSec - 300) > 0.6) return "小休时长不对";
            return null;
        }

        static string T_Clamp()
        {
            Config c = new Config();
            c.work_min = -3;
            c.opacity_pct = 500;
            c.micros_per_long = -9;
            c.daily_limit_min = 100000;
            c.font_size = 0;
            c.Clamp();
            if (c.work_min < 0.05) return "work_min 未夹紧";
            if (c.opacity_pct > 95) return "opacity_pct 未夹紧";
            if (c.micros_per_long != 0) return "micros_per_long 未夹紧";
            if (c.daily_limit_min > 1440) return "daily_limit_min 未夹紧";
            if (c.font_size < 14) return "font_size 未夹紧";
            Sim s = new Sim(c, T0());
            s.Run(30, true);
            if (s.e.State == Engine.S_DAILY) return "夹紧后不应异常弹遮罩";
            return null;
        }

        static string T_Files()
        {
            Config c = Cfg();
            c.work_min = 10;
            c.daily_limit_min = 18;
            Sim s = new Sim(c, T0());
            s.Run(30 * 60, true);
            s.e.Store.FlushNow();
            Store st = new Store();
            st.Load();
            DailyRow r = st.Get(s.e.TodayDate);
            if (r.UseSec < 18 * 60) return "daily.csv 累计不足（" + (r.UseSec / 60.0).ToString("0.0") + " 分）";
            if (r.Micro < 1) return "daily.csv 未记录完成的小休";
            if (!File.Exists(Paths.UsageLog)) return "usage.log 不存在";
            string log = File.ReadAllText(Paths.UsageLog, Encoding.UTF8);
            if (log.IndexOf("BREAK_START kind=micro") < 0) return "usage.log 缺 BREAK_START";
            if (log.IndexOf("BREAK_END") < 0) return "usage.log 缺 BREAK_END";
            if (log.IndexOf("DAILY_OVER") < 0) return "usage.log 缺 DAILY_OVER";
            if (!File.Exists(Paths.DailyCsv)) return "daily.csv 不存在";
            string csv = File.ReadAllText(Paths.DailyCsv, Encoding.UTF8);
            if (csv.IndexOf("used_min") < 0) return "daily.csv 缺表头";
            return null;
        }

        static string T_KeepDays()
        {
            try
            {
                Store st = new Store();
                st.SetKeepDays(3);
                for (int i = 1; i <= 10; i++)
                {
                    DailyRow r = new DailyRow();
                    r.Date = "2026-03-" + i.ToString("00");
                    r.UseSec = 60 * i;
                    st.Put(r);
                }
                st.FlushNow();
                string[] lines = File.ReadAllLines(Paths.DailyCsv, Encoding.UTF8);
                int data = 0;
                foreach (string l in lines) if (l.Length > 0 && l.IndexOf("date,") < 0) data++;
                if (data != 3) return "keep_days=3 应只留 3 行，实际 " + data;
                if (lines[lines.Length - 1].IndexOf("2026-03-10") < 0) return "留下的应是最近的日期";
                Store st2 = new Store();
                st2.Load();
                if (st2.Get("2026-03-10").UseSec < 1) return "重新载入后最新一天应可读";
                return null;
            }
            catch (Exception e) { return "异常：" + e.Message; }
        }

        static string T_Screens()
        {
            try
            {
                Screen[] ss = Screen.AllScreens;
                if (ss == null || ss.Length < 1) return "屏幕枚举为空";
                Rectangle v = SystemInformation.VirtualScreen;
                if (v.Width <= 0 || v.Height <= 0) return "虚拟屏幕尺寸非法";
                foreach (Screen x in ss) if (x.Bounds.Width <= 0) return "某块屏幕宽度非法";
                double dpi = Native.DpiScale();
                if (dpi < 1.0 || dpi > 3.0) return "DPI 缩放异常：" + dpi;
                return null;
            }
            catch (Exception e) { return "屏幕 API 异常：" + e.Message; }
        }
    }
}
