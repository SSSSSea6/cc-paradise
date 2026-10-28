// Claude 自动切号：托盘、屏幕下方的额度悬浮条，以及轮换窗口的启动入口。
//
// 轮换窗口是用单独数据目录打开的 Claude 桌面端，只有它的编程会话经本机代理 claude_pool.py
// 发出，哪个账号还有额度就用哪个，同一个会话里无感换号；其他窗口照常直连。这个程序让
// 代理一直在跑、管理账号，显示各账号额度和每次切换。
//
//   ClaudeSwitcher.exe                 托盘（已在运行就直接退出）
//   ClaudeSwitcher.exe --open-window   打开轮换窗口，托盘没在运行就一并启动
//
// 编译：build.cmd（.NET Framework 自带的编译器，不用另装东西）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace ClaudeSwitcher
{
    static class Program
    {
        public const string Title = "Claude 自动切号";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report((Exception)e.ExceptionObject);
            // Started by an MSIX-packaged program (Codex, for one), this process and everything it starts
            // would write AppData into that package's private copy, where the rotation window's sessions
            // then go missing. The desktop shell starts it again outside.
            if (Native.Packaged() && Native.RunOutside(Paths.Exe, string.Join(" ", args))) return;
            Directory.CreateDirectory(Paths.Data);

            if (args.Contains("--open-window"))
            {
                Rotation.Open();
                Rotation.WaitAndTag(TimeSpan.FromSeconds(25));
                // The tray keeps the proxy alive and announces switches; start it too when it is not running.
                Mutex existing;
                if (!Mutex.TryOpenExisting(Paths.Mutex, out existing)) Process.Start(Paths.Exe);
                else existing.Dispose();
                return;
            }
            bool isNew;
            using (new Mutex(true, Paths.Mutex, out isNew))
                if (isNew) Application.Run(new Tray());
        }

        static void Report(Exception error)
        {
            try { File.AppendAllText(Paths.ErrorLog, string.Format("[{0:s}] {1}\r\n", DateTime.Now, error)); } catch { }
            MessageBox.Show(error.Message + "\r\n\r\n日志：" + Paths.ErrorLog, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// Runs a menu action; what goes wrong is shown, not fatal.
        public static void Safely(Action action)
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    static class Paths
    {
        // The program runs from wherever it was built; tokens, certificates and state are kept
        // apart from it, so a checkout never holds anything private.
        public static readonly string Exe = Application.ExecutablePath;
        public static readonly string App = Path.GetDirectoryName(Exe);
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSwitcher");
        public static readonly string PoolScript = Path.Combine(App, "claude_pool.py");
        public static readonly string Icon = Path.Combine(App, @"icons\switcher.ico");
        public static readonly string ErrorLog = Path.Combine(Data, "switcher-error.log");
        public static readonly string PoolConfig = Path.Combine(Data, "pool.json");
        public static readonly string PoolState = Path.Combine(Data, "pool-state.json");
        public static readonly string WidgetFile = Path.Combine(Data, "widget.json");
        /// The rotation window's Claude data folder: "rotation_profile" in pool.json, or one of its own.
        public static readonly string RotationProfile = Json.Text(Json.Read(PoolConfig), "rotation_profile") ?? Path.Combine(Data, "rotation-profile");
        public static readonly string MainProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        public static readonly string ClaudeExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AnthropicClaude\claude.exe");
        public static readonly string Startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude 切号器.lnk");
        public const string Mutex = @"Local\ClaudeProfileSwitcher";
        public const int Port = 47630;

        static string python;
        public static string Python
        {
            get
            {
                if (python != null) return python;
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (dir.Length == 0 || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string candidate = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(candidate)) return python = candidate;
                }
                throw new InvalidOperationException("没找到 python.exe，本机代理需要它。");
            }
        }
    }

    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static Dictionary<string, object> Read(string path)
        {
            try { return Serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
            catch (IOException) { return new Dictionary<string, object>(); }
            catch (ArgumentException) { return new Dictionary<string, object>(); }  // caught mid-write
        }

        public static void Write(string path, object value) { File.WriteAllText(path, Serializer.Serialize(value)); }

        public static object Get(object dict, string key)
        {
            var d = dict as Dictionary<string, object>;
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        public static string Text(object dict, string key) { return Get(dict, key) as string; }
        public static double Number(object dict, string key) { object v = Get(dict, key); return v == null ? 0 : Convert.ToDouble(v); }
        public static bool Flag(object dict, string key) { return Get(dict, key) as bool? ?? false; }

        public static string[] Names(object config)
        {
            var accounts = Get(config, "accounts") as object[] ?? new object[0];
            return accounts.Select(a => Text(a, "name")).Where(n => n != null).ToArray();
        }
    }

    // ------------------------------------------------------------------ pool --

    static class Pool
    {
        /// Runs a claude_pool.py command; throws its message when it fails.
        public static void Invoke(string input, params string[] args)
        {
            var start = new ProcessStartInfo(Paths.Python, string.Join(" ", new[] { Quote(Paths.PoolScript) }.Concat(args.Select(Quote))))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8,
            };
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (var process = Process.Start(start))
            {
                if (input != null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                string errors = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(errors.Length > 0 ? errors : "claude_pool.py " + args[0] + " 失败");
            }
        }

        static string Quote(string arg) { return "\"" + arg + "\""; }

        public static bool Listening()
        {
            using (var client = new TcpClient())
                try { return client.ConnectAsync("127.0.0.1", Paths.Port).Wait(500); }
                catch (AggregateException) { return false; }
        }

        // Kept running for as long as the tray runs, whether switching is on or not: sessions that
        // started while it was on keep sending through it, so it is never stopped from here.
        public static bool Start()
        {
            if (Listening()) return true;
            Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Paths.Python), "pythonw.exe"), Quote(Paths.PoolScript) + " serve")
            {
                UseShellExecute = false, CreateNoWindow = true,
            }).Dispose();
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (Listening()) return true;
            }
            return false;
        }
    }

    // --------------------------------------------------------- rotation window --

    static class Rotation
    {
        public const string Id = "ClaudeSwitcher.Rotation";
        public static readonly string Relaunch = "\"" + Paths.Exe + "\" --open-window";
        // A window draws with the icon handle it was given, so the icon lives as long as this process.
        static readonly Icon WindowIcon = new Icon(Paths.Icon, 256, 256);

        /// The proxy address goes into this one Claude's environment, so only its Code sessions
        /// pass through the proxy. Opened while it is already running, Claude just brings the
        /// existing window forward, keeping whatever it was started with.
        public static void Open()
        {
            if (!Pool.Start()) throw new InvalidOperationException("本机代理没能启动，轮换窗口没有打开。");
            Pool.Invoke(null, "trust");
            var start = new ProcessStartInfo(Paths.ClaudeExe, "--user-data-dir=\"" + Paths.RotationProfile + "\"") { UseShellExecute = false };
            start.EnvironmentVariables["HTTPS_PROXY"] = "http://127.0.0.1:" + Paths.Port;
            start.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
            Process.Start(start).Dispose();
        }

        /// Tagged while Electron is still creating the window, so its button starts out separate.
        public static void WaitAndTag(TimeSpan limit)
        {
            var until = DateTime.Now + limit;
            while (DateTime.Now < until && !Tag()) Thread.Sleep(200);
        }

        static int[] seen = new int[0];
        static int pid;

        /// The rotation Claude's main process: the one started with its data folder (the Claude
        /// Code processes of its sessions live in that folder too, but are not started with it).
        /// Command lines are read only when the set of claude.exe processes changed, which keeps
        /// the every-few-seconds check instant.
        public static int ProcessId()
        {
            var processes = Process.GetProcessesByName("claude");
            int[] ids = processes.Select(p => p.Id).OrderBy(i => i).ToArray();
            foreach (var p in processes) p.Dispose();
            if (ids.SequenceEqual(seen)) return pid;
            seen = ids;
            pid = ids.FirstOrDefault(i =>
            {
                string line = Native.CommandLine(i);
                return line != null && !line.Contains("--type=")
                    && line.IndexOf("--user-data-dir=\"" + Paths.RotationProfile + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            return pid;
        }

        /// Tags the rotation window's windows; returns whether one of them is on screen.
        public static bool Tag()
        {
            int id = ProcessId();
            if (id == 0) return false;
            bool shown = false;
            foreach (IntPtr window in Taskbar.WindowsOf((uint)id))
            {
                try
                {
                    Taskbar.Tag(window, Id, Relaunch, "Claude 轮换", Paths.Icon + ",0", WindowIcon.Handle);
                    if (Taskbar.IsWindowVisible(window)) shown = true;
                }
                catch (Exception) { }  // the window went away meanwhile
            }
            return shown;
        }

        /// Whether any process of the rotation Claude (its windows' helpers included) still runs.
        public static bool Running()
        {
            var processes = Process.GetProcessesByName("claude");
            try
            {
                return processes.Any(p =>
                {
                    string line = Native.CommandLine(p.Id);
                    return line != null && line.IndexOf("--user-data-dir=\"" + Paths.RotationProfile + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }
            finally { foreach (var p in processes) p.Dispose(); }
        }
    }

    // ---------------------------------------------------------------- sessions --

    /// Code sessions of the main window (the default one, outside the rotation), moved one at a time into
    /// the rotation window. A session there is only an index file; its conversation is in
    /// ~/.claude/projects, shared by every window, so moving the file carries the conversation.
    /// Claude reads these files when it starts, so a moved session shows after a restart.
    public static class Sessions
    {
        public class Entry { public string File, Title, Conversation; public long Active; }

        public static readonly string Projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".claude\projects");
        public static string MainStore { get { return Store(Path.Combine(Paths.MainProfile, "claude-code-sessions")); } }
        public static string RotationStore { get { return Store(Path.Combine(Paths.RotationProfile, "claude-code-sessions")); } }

        // The account\organization folder of the account signed in now: the one written last.
        static string Store(string root)
        {
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root).SelectMany(Directory.GetDirectories).OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault();
        }

        /// The most recently active sessions that are not archived and have a conversation.
        public static Entry[] Recent(string store, int count)
        {
            var entries = new List<Entry>();
            foreach (string file in Directory.GetFiles(store, "local_*.json"))
            {
                var index = Json.Read(file);
                string conversation = Json.Text(index, "cliSessionId");
                if (conversation == null || Json.Flag(index, "isArchived")) continue;
                entries.Add(new Entry { File = file, Title = Json.Text(index, "title") ?? "（无标题）", Conversation = conversation, Active = (long)Json.Number(index, "lastActivityAt") });
            }
            return entries.OrderByDescending(e => e.Active).Take(count).ToArray();
        }

        /// Whether the main window is replying in it now: a Claude Code process holds the
        /// conversation and has written to it in the last two minutes. (Those processes stay
        /// around for hours after a session goes quiet, so being held alone means nothing.)
        public static bool Replying(Entry entry, string projects)
        {
            string transcript = Directory.Exists(projects) ? Directory.GetFiles(projects, entry.Conversation + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (transcript == null || DateTime.Now - File.GetLastWriteTime(transcript) > TimeSpan.FromMinutes(2)) return false;
            var processes = Process.GetProcessesByName("claude");
            try
            {
                return processes.Any(p =>
                {
                    string line = Native.CommandLine(p.Id);
                    return line != null && line.Contains(entry.Conversation);
                });
            }
            finally { foreach (var p in processes) p.Dispose(); }
        }

        /// Writes the index into the other store, shown as not archived, and recycles the original.
        public static void Move(Entry entry, string target)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(entry.File), "\"isArchived\"\\s*:\\s*true", "\"isArchived\":false");
            File.WriteAllText(Path.Combine(target, Path.GetFileName(entry.File)), text, new UTF8Encoding(false));
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(entry.File, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        /// "3 小时前", for the menu.
        public static string Ago(long milliseconds)
        {
            double minutes = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - milliseconds) / 60000.0;
            if (minutes < 1) return "刚刚";
            if (minutes < 60) return (int)minutes + " 分钟前";
            if (minutes < 1440) return (int)(minutes / 60) + " 小时前";
            return (int)(minutes / 1440) + " 天前";
        }
    }

    // Every Claude window carries the same taskbar identity, so the rotation window would sit
    // under the main window's button. Its windows get an identity of their own, with the relaunch command
    // and name a pinned button uses; the Start menu shortcut carries the same identity, which is
    // where the taskbar takes that button's icon from.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey { public Guid Format; public uint Id; public PropertyKey(Guid f, uint i) { Format = f; Id = i; } }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        int GetCount(out uint count); int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, IntPtr value); int SetValue(ref PropertyKey key, IntPtr value); int Commit();
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        int GetClassID(out Guid id); int IsDirty();
        int Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        int Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file); int GetCurFile(out IntPtr file);
    }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList { int HrInit(); int AddTab(IntPtr h); int DeleteTab(IntPtr h); int ActivateTab(IntPtr h); int SetActiveAlt(IntPtr h); }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] public class TaskbarList { }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] public class ShellLink { }

    public static class Taskbar
    {
        static readonly Guid AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        const uint Relaunch = 2, Icon = 3, Name = 4, Id = 5;

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("shell32.dll", PreserveSig = false)]
        static extern void SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr value);

        /// Top-level Electron windows of a process that can have a taskbar button, hidden ones included.
        public static IntPtr[] WindowsOf(uint pid)
        {
            var found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint owner; GetWindowThreadProcessId(h, out owner);
                var name = new StringBuilder(64); GetClassName(h, name, name.Capacity);
                bool toolWindow = (GetWindowLongPtr(h, -20).ToInt64() & 0x80L) != 0;
                if (owner == pid && GetWindow(h, 4) == IntPtr.Zero && !toolWindow && name.ToString() == "Chrome_WidgetWin_1") found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }

        // PROPVARIANT by hand: VT_LPWSTR at offset 0, the string pointer at offset 8.
        static void Set(IPropertyStore store, uint id, string value)
        {
            var key = new PropertyKey(AppUserModel, id);
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
            try { Marshal.WriteInt16(pv, 0, 31); Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value)); Marshal.ThrowExceptionForHR(store.SetValue(ref key, pv)); }
            finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
        }

        static string Get(IPropertyStore store, uint id)
        {
            var key = new PropertyKey(AppUserModel, id);
            IntPtr pv = Marshal.AllocCoTaskMem(32);
            for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
            try { return store.GetValue(ref key, pv) == 0 && Marshal.ReadInt16(pv, 0) == 31 ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) : null; }
            finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
        }

        /// Moves a window to its own button: identity, relaunch command, name and icon. A window
        /// already on screen has its button rebuilt so the move takes effect.
        public static void Tag(IntPtr h, string id, string relaunch, string name, string iconResource, IntPtr icon)
        {
            Guid iid = typeof(IPropertyStore).GUID; IPropertyStore store;
            SHGetPropertyStoreForWindow(h, ref iid, out store);
            bool moved = false;
            try
            {
                if (Get(store, Id) != id || Get(store, Relaunch) != relaunch)
                {
                    moved = Get(store, Id) != id;
                    Set(store, Id, id); Set(store, Relaunch, relaunch); Set(store, Name, name); Set(store, Icon, iconResource);
                    Marshal.ThrowExceptionForHR(store.Commit());
                }
            }
            finally { Marshal.ReleaseComObject(store); }
            if (SendMessage(h, 0x7F, (IntPtr)1, IntPtr.Zero) != icon) { SendMessage(h, 0x80, (IntPtr)0, icon); SendMessage(h, 0x80, (IntPtr)1, icon); }
            // A button added for a window stays on the taskbar after Claude hides that window, as a blank
            // entry that does not open, so a hidden window has its button taken away once.
            bool visible = IsWindowVisible(h);
            if (visible && moved) Button(h, true);
            else if (!visible && cleared.Add(h)) Button(h, false);
            if (visible) cleared.Remove(h);
        }

        static readonly HashSet<IntPtr> cleared = new HashSet<IntPtr>();

        static void Button(IntPtr h, bool add)
        {
            var taskbar = (ITaskbarList)new TaskbarList();
            try
            {
                taskbar.HrInit();
                taskbar.DeleteTab(h);
                if (add) taskbar.AddTab(h);
            }
            finally { Marshal.ReleaseComObject(taskbar); }
        }

        public static void TagShortcut(string path, string id)
        {
            object link = new ShellLink();
            try
            {
                Marshal.ThrowExceptionForHR(((IPersistFile)link).Load(path, 2));
                var store = (IPropertyStore)link;
                Set(store, Id, id);
                Marshal.ThrowExceptionForHR(store.Commit());
                Marshal.ThrowExceptionForHR(((IPersistFile)link).Save(path, true));
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }

    // ------------------------------------------------------------------ quota --

    static class Quota
    {
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }

        /// Share left and reset time of a quota window as the proxy last saw it; one past its
        /// reset is full again and not started (reset 0).
        public static double Window(object seen, string window, out long reset)
        {
            var pair = Json.Get(seen, window) as object[];
            reset = pair != null ? Convert.ToInt64(pair[1]) : 0;
            if (pair == null || reset <= Now) { reset = 0; return 1; }
            return 1 - Convert.ToDouble(pair[0]);
        }

        /// Time until then: "3天4小时", "2小时15分", "40分钟", "不到1分钟".
        public static string Until(long at)
        {
            long minutes = (at - Now) / 60;
            if (minutes < 1) return "不到1分钟";
            if (minutes < 60) return minutes + "分钟";
            if (minutes < 1440) return minutes / 60 + "小时" + (minutes % 60 > 0 ? minutes % 60 + "分" : "");
            return minutes / 1440 + "天" + (minutes % 1440 / 60 > 0 ? minutes % 1440 / 60 + "小时" : "");
        }

        static string Age(double seconds)
        {
            if (seconds < 60) return "刚刚更新";
            if (seconds < 3600) return (int)(seconds / 60) + " 分钟前更新";
            if (seconds < 86400) return (int)(seconds / 3600) + " 小时前更新";
            return (int)(seconds / 86400) + " 天前更新";
        }

        static string Percent(double left) { return Math.Round(left * 100) + "%"; }

        /// "5小时剩 73%，2小时15分后刷新 · 本周剩 5%，3天4小时后刷新", for the menu.
        public static string Summary(object seen)
        {
            if (seen == null) return "还没用过";
            var parts = new List<string>();
            foreach (var window in new[] { new[] { "5h", "5小时" }, new[] { "7d", "本周" } })
            {
                long reset;
                double left = Window(seen, window[0], out reset);
                parts.Add(window[1] + "剩 " + Percent(left) + (reset > 0 ? "，" + Until(reset) + "后刷新" : ""));
            }
            return string.Join(" · ", parts) + (Json.Number(seen, "blocked_until") > Now ? "（暂停中）" : "");
        }

        public static Row[] Rows(object config, object state)
        {
            var rows = new List<Row>();
            foreach (string name in Json.Names(config))
            {
                object seen = Json.Get(Json.Get(state, "accounts"), name);
                var row = new Row { Name = name };
                var tags = new List<string>();
                if (name == Json.Text(state, "current")) tags.Add("正在用");
                if (name == Json.Text(config, "pinned")) tags.Add("已固定");
                if (Json.Number(seen, "blocked_until") > Now) { tags = new List<string> { Until((long)Json.Number(seen, "blocked_until")) + "后恢复" }; row.Alert = true; }
                if (tags.Count > 0) row.Tag = string.Join(" · ", tags);
                if (seen == null) row.Note = "还没用过，额度未知";
                else
                {
                    row.Age = Age(Now - Json.Number(seen, "seen"));
                    string[] windows = { "5h", "7d" };
                    for (int k = 0; k < 2; k++)
                    {
                        long reset;
                        row.Left[k] = Window(seen, windows[k], out reset);
                        row.Reset[k] = reset > 0 ? Until(reset) + "后刷新" : "未开始计时";
                    }
                }
                rows.Add(row);
            }
            return rows.ToArray();
        }

        public static bool DarkTaskbar()
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) as int? == 0;
        }
    }

    public class Row
    {
        public string Name, Tag, Age, Note;     // Note stands in for the bars of an account never used
        public bool Alert;                      // the tag is a warning
        public double[] Left = { 1, 1 };        // share left of the 5-hour and weekly windows
        public string[] Reset = new string[2];
    }

    public static class Look
    {
        public static Color Level(double left, bool dark)
        {
            if (left < 0.15) return Color.FromArgb(220, 68, 56);
            if (left < 0.40) return Color.FromArgb(224, 146, 36);
            return dark ? Color.FromArgb(92, 156, 238) : Color.FromArgb(66, 127, 213);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var path = Rounded(r, radius)) using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
        }
    }

    public static class Badge
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        static IntPtr previous, current;

        /// A number on a rounded square. The icon before the one on screen is freed: the tray
        /// still draws with the current one until it is replaced.
        public static Icon Make(string text, Color back, int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Look.Fill(g, new RectangleF(0, 0, size, size), size * 0.22f, back);
                    float em = size * (text.Length > 1 ? 0.6f : 0.7f);
                    using (var font = new Font(char.IsDigit(text[0]) ? "Segoe UI" : "Microsoft YaHei UI", em, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, font, Brushes.White, new RectangleF(0, size * 0.04f, size, size), format);
                }
                if (previous != IntPtr.Zero) DestroyIcon(previous);
                previous = current;
                current = bmp.GetHicon();
                return Icon.FromHandle(current);
            }
        }
    }

    public static class Panel
    {
        const float Width = 360, Pad = 16, Margin = 16;

        public static Bitmap Draw(Row[] rows, string title, string status, string footer, bool dark, float scale)
        {
            Color back = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
            Color edge = dark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(220, 220, 220);
            Color ink = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
            Color muted = dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(100, 100, 100);
            Color track = dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(226, 226, 226);
            Color accent = Look.Level(1, dark), alert = Look.Level(0, dark);

            int n = rows.Length;
            float height = Pad + 22 + 10 + Math.Max(0, n - 1) * 13 + (n > 0 ? 10 : 0) + 18 + Pad;
            foreach (Row row in rows) height += row.Note != null ? 42 : 62;
            var bmp = new Bitmap((int)Math.Ceiling((Width + 2 * Margin) * scale), (int)Math.Ceiling((height + 2 * Margin) * scale), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var heading = new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var body = new Font("Microsoft YaHei UI", 12, GraphicsUnit.Pixel))
            using (var strong = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font("Microsoft YaHei UI", 11, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(scale, scale);

                var card = new RectangleF(Margin, Margin, Width, height);
                for (int i = 12; i >= 1; i--)
                    Look.Fill(g, new RectangleF(card.X - i, card.Y - i + 3, card.Width + 2 * i, card.Height + 2 * i), 8 + i, Color.FromArgb(dark ? 7 : 4, 0, 0, 0));
                Look.Fill(g, card, 8, back);
                using (var path = Look.Rounded(card, 8)) using (var pen = new Pen(edge, 1 / scale)) g.DrawPath(pen, path);

                float x0 = card.X + Pad, x1 = card.Right - Pad, y = card.Y + Pad;
                Text(g, title, heading, ink, x0, y, 22, false);
                Text(g, status, body, muted, x1, y, 22, true);
                y += 32;
                for (int i = 0; i < n; i++)
                {
                    Row row = rows[i];
                    if (i > 0)
                    {
                        using (var pen = new Pen(edge, 1 / scale)) g.DrawLine(pen, x0, y + 6, x1, y + 6);
                        y += 13;
                    }
                    float nameWidth = Text(g, row.Name, heading, ink, x0, y, 22, false);
                    if (row.Tag != null)
                    {
                        Color color = row.Alert ? alert : accent;
                        SizeF size = g.MeasureString(row.Tag, small, PointF.Empty, StringFormat.GenericTypographic);
                        var chip = new RectangleF(x0 + nameWidth + 8, y + 3, size.Width + 12, 16);
                        Look.Fill(g, chip, 8, Color.FromArgb(dark ? 64 : 40, color));
                        Text(g, row.Tag, small, color, chip.X + 6, chip.Y, chip.Height, false);
                    }
                    if (row.Age != null) Text(g, row.Age, small, muted, x1, y, 22, true);
                    y += 22;
                    if (row.Note != null)
                    {
                        Text(g, row.Note, body, muted, x0, y, 20, false);
                        y += 20;
                        continue;
                    }
                    for (int k = 0; k < 2; k++)
                    {
                        Text(g, k == 0 ? "5小时" : "本周", body, muted, x0, y, 20, false);
                        var bar = new RectangleF(x0 + 44, y + 7, 108, 6);
                        Look.Fill(g, bar, 3, track);
                        float filled = (float)Math.Max(0, Math.Min(1, row.Left[k])) * bar.Width;
                        if (filled > 0) Look.Fill(g, new RectangleF(bar.X, bar.Y, Math.Max(filled, bar.Height), bar.Height), 3, Look.Level(row.Left[k], dark));
                        Text(g, Math.Round(row.Left[k] * 100) + "%", strong, ink, x0 + 196, y, 20, true);
                        Text(g, row.Reset[k], body, muted, x1, y, 20, true);
                        y += 20;
                    }
                }
                if (n > 0) y += 10;
                Text(g, footer, small, muted, x0, y, 18, false);
            }
            return bmp;
        }

        /// The floating bar: "work  5小时 ▬ 69%  本周 ▬ 87%", or a name and a note.
        public static Bitmap DrawBar(string name, double shortLeft, double weekLeft, string note, bool dark, float scale)
        {
            Color back = dark ? Color.FromArgb(240, 38, 38, 38) : Color.FromArgb(240, 250, 250, 250);
            Color edge = dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(212, 212, 212);
            Color ink = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
            Color muted = dark ? Color.FromArgb(165, 165, 165) : Color.FromArgb(100, 100, 100);
            Color track = dark ? Color.FromArgb(76, 76, 76) : Color.FromArgb(222, 222, 222);
            const float Height = 28, Side = 10, BarWidth = 30;
            using (var bold = new Font("Microsoft YaHei UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var body = new Font("Microsoft YaHei UI", 12, GraphicsUnit.Pixel))
            {
                string[] labels = { "5小时", "本周" };
                double[] left = { shortLeft, weekLeft };
                float width;
                using (var probe = new Bitmap(1, 1)) using (var g = Graphics.FromImage(probe))
                {
                    width = Side + Measure(g, name, bold) + Side;
                    if (note != null) width += Measure(g, note, body);
                    else for (int k = 0; k < 2; k++)
                        width += (k > 0 ? 12 : 0) + Measure(g, labels[k], body) + 5 + BarWidth + 5 + Measure(g, "100%", bold);
                    width += Side;
                }
                var bmp = new Bitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(Height * scale), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.ScaleTransform(scale, scale);
                    var card = new RectangleF(0.5f / scale, 0.5f / scale, width - 1 / scale, Height - 1 / scale);
                    Look.Fill(g, card, 8, back);
                    using (var path = Look.Rounded(card, 8)) using (var pen = new Pen(edge, 1 / scale)) g.DrawPath(pen, path);
                    float x = Side + Text(g, name, bold, ink, Side, 0, Height, false) + Side;
                    if (note != null) Text(g, note, body, muted, x, 0, Height, false);
                    else for (int k = 0; k < 2; k++)
                    {
                        if (k > 0) x += 12;
                        x += Text(g, labels[k], body, muted, x, 0, Height, false) + 5;
                        var bar = new RectangleF(x, Height / 2 - 2, BarWidth, 4);
                        Look.Fill(g, bar, 2, track);
                        float filled = (float)Math.Max(0, Math.Min(1, left[k])) * BarWidth;
                        if (filled > 0) Look.Fill(g, new RectangleF(x, bar.Y, Math.Max(filled, 4), 4), 2, Look.Level(left[k], dark));
                        x += BarWidth + 5;
                        float slot = Measure(g, "100%", bold);
                        Text(g, Math.Round(left[k] * 100) + "%", bold, ink, x + slot, 0, Height, true);
                        x += slot;
                    }
                }
                return bmp;
            }
        }

        static float Measure(Graphics g, string text, Font font)
        {
            return g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        // One line of text centred in a band of the given height, from x or ending at x; returns its width.
        static float Text(Graphics g, string text, Font font, Color color, float x, float y, float band, bool right)
        {
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, right ? x - size.Width : x, y + (band - size.Height) / 2, StringFormat.GenericTypographic);
            return size.Width;
        }
    }

    /// Per-monitor-DPI layered popups in a process that is not DPI aware: they are created and
    /// placed with the thread switched to per-monitor awareness, so every coordinate is a real
    /// pixel and the bitmaps are drawn at the monitor's real DPI instead of being stretched.
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Cx, Cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr screen, ref POINT pos, ref SIZE size, IntPtr source, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int kind, IntPtr buffer, int length, out int needed);

        /// Another process's command line, or null when it cannot be read.
        public static string CommandLine(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);  // PROCESS_QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) return null;
            try
            {
                int needed;
                NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out needed);  // ProcessCommandLineInformation
                if (needed <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(process, 60, buffer, needed, out needed) != 0) return null;
                    var text = (UNICODE_STRING)Marshal.PtrToStructure(buffer, typeof(UNICODE_STRING));
                    return Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(process); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

        public static bool Packaged()
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700;  // APPMODEL_ERROR_NO_PACKAGE
        }

        /// Starts a program through the desktop's own shell window, so it runs as the desktop's child
        /// rather than this process's; false when the desktop could not be reached.
        public static bool RunOutside(string file, string arguments)
        {
            try
            {
                dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                object location = 0, root = null;  // CSIDL_DESKTOP
                int hwnd;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, 8, out hwnd, 1);  // SWC_DESKTOP, SWFO_NEEDDISPATCH
                desktop.Document.Application.ShellExecute(file, arguments, Path.GetDirectoryName(file), "open", 1);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// These windows' class has no cursor, so without this the pointer keeps whatever shape
        /// it had, often the busy one left from starting the tray.
        public static bool Arrow(ref Message m)
        {
            if (m.Msg != 0x0020) return false;  // WM_SETCURSOR
            SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32512));
            m.Result = (IntPtr)1;
            return true;
        }

        public static void Aware(Action action)
        {
            IntPtr old = SetThreadDpiAwarenessContext((IntPtr)(-4));
            try { action(); } finally { SetThreadDpiAwarenessContext(old); }
        }

        public static IntPtr Popup(NativeWindow window, int extraStyle)
        {
            Aware(delegate { window.CreateHandle(new CreateParams { Style = unchecked((int)0x80000000), ExStyle = 0x80000 | 0x80 | 0x8 | extraStyle }); });
            return window.Handle;
        }

        /// The whole monitor, the part of it not under the taskbar, and its scale.
        public static float Screen(IntPtr monitor, out RECT whole, out RECT work)
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(monitor, ref info);
            uint dpi, unused; GetDpiForMonitor(monitor, 0, out dpi, out unused);
            whole = info.Monitor; work = info.Work;
            return dpi / 96f;
        }

        public static void Paint(IntPtr h, Bitmap bmp, int x, int y)
        {
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen);
            IntPtr bitmap = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memory, bitmap);
            try
            {
                var pos = new POINT { X = x, Y = y };
                var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
                var from = new POINT();
                var blend = new BLEND { Alpha = 255, Format = 1 };
                UpdateLayeredWindow(h, screen, ref pos, ref size, memory, ref from, 0, ref blend, 2);
            }
            finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
        }
    }

    public class Flyout : NativeWindow
    {
        readonly Timer watch = new Timer { Interval = 200 };
        IntPtr foreground;
        int hiddenAt = -10000;

        public bool Visible { get; private set; }
        /// Clicking the tray icon while the panel is open first takes the focus away and hides it;
        /// that click should not open it again.
        public bool JustHidden { get { return Environment.TickCount - hiddenAt < 500; } }

        public Flyout()
        {
            Native.Popup(this, 0);
            // Gone as soon as anything else is brought forward.
            watch.Tick += delegate { if (Native.GetForegroundWindow() != foreground) Hide(); };
        }

        /// Opens above the pointer, or below it when there is no room above.
        public void Show(Row[] rows, string title, string status, string footer, bool dark)
        {
            Native.Aware(delegate
            {
                Native.POINT cursor; Native.GetCursorPos(out cursor);
                Native.RECT whole, work;
                float scale = Native.Screen(Native.MonitorFromPoint(cursor, 2), out whole, out work);
                using (Bitmap bmp = Panel.Draw(rows, title, status, footer, dark, scale))
                {
                    int x = Math.Max(work.Left, Math.Min(cursor.X - bmp.Width / 2, work.Right - bmp.Width));
                    int y = cursor.Y - bmp.Height >= work.Top ? cursor.Y - bmp.Height : cursor.Y;
                    Native.Paint(Handle, bmp, x, Math.Max(work.Top, Math.Min(y, work.Bottom - bmp.Height)));
                }
                Native.ShowWindow(Handle, 5);
                Native.SetForegroundWindow(Handle);
            });
            foreground = Native.GetForegroundWindow();
            Visible = true;
            watch.Start();
        }

        public void Hide()
        {
            if (!Visible) return;
            Native.ShowWindow(Handle, 0);
            Visible = false;
            watch.Stop();
            hiddenAt = Environment.TickCount;
        }

        protected override void WndProc(ref Message m)
        {
            if (Native.Arrow(ref m)) return;
            if (m.Msg == 0x0202 || (m.Msg == 0x0100 && (int)m.WParam == 0x1B)) Hide();  // click, Esc
            base.WndProc(ref m);
        }
    }

    /// The floating bar: always on top, never takes the focus, dragged anywhere and snapped to
    /// the screen's edges, the taskbar's edge, or the middle of the taskbar. Steps aside while
    /// another window is full screen.
    public class Widget : NativeWindow
    {
        // Mouse events are handed on through a window made outside per-monitor awareness, so
        // whatever the handlers open (the menu, message boxes) is not created in that mode.
        class Relay : NativeWindow
        {
            readonly Widget owner;
            public Relay(Widget owner) { this.owner = owner; CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x8001) owner.Raise((int)m.WParam);
                else base.WndProc(ref m);
            }
        }

        static readonly IntPtr Topmost = (IntPtr)(-1);
        const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
        readonly Relay relay;
        readonly Timer guard = new Timer { Interval = 1000 };
        string name, note, drawn;
        double shortLeft, weekLeft;
        bool dark, started, wanted = true, down, dragged;
        int width, height;
        Native.POINT grab, origin;

        public int X { get; private set; }
        public int Y { get; private set; }
        public event EventHandler Clicked, RightClicked, Moved;

        public Widget()
        {
            relay = new Relay(this);
            Native.Popup(this, 0x08000000);
            guard.Tick += delegate { Native.Aware(Guard); };
        }

        public bool Wanted
        {
            get { return wanted; }
            set { wanted = value; if (started) Native.ShowWindow(Handle, value ? 4 : 0); }
        }

        public void Render(string name, double shortLeft, double weekLeft, string note, bool dark)
        {
            this.name = name; this.shortLeft = shortLeft; this.weekLeft = weekLeft; this.note = note; this.dark = dark;
            if (started) Native.Aware(Redraw);
        }

        /// Shows the bar where it was left; the first time, in a bottom taskbar just left of the
        /// notification area, or else just above the taskbar's right end.
        public void Start(bool restore, int x, int y)
        {
            Native.Aware(delegate
            {
                var at = new Native.POINT { X = x, Y = y };
                if (!restore || Native.MonitorFromPoint(at, 0) == IntPtr.Zero)
                {
                    Native.RECT whole, work, bar, tray;
                    float scale = Native.Screen(Native.MonitorFromPoint(new Native.POINT(), 1), out whole, out work);
                    IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
                    using (Bitmap bmp = Draw(scale))
                    {
                        if (Native.GetWindowRect(taskbar, out bar) && bar.Top >= work.Bottom
                            && Native.GetWindowRect(Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null), out tray))
                        {
                            x = tray.Left - bmp.Width - (int)(8 * scale);
                            y = bar.Top + (bar.Bottom - bar.Top - bmp.Height) / 2;
                        }
                        else
                        {
                            x = work.Right - bmp.Width - (int)(12 * scale);
                            y = work.Bottom - bmp.Height - (int)(8 * scale);
                        }
                    }
                }
                X = x; Y = y; started = true;
                Redraw();
            });
            if (wanted) Native.ShowWindow(Handle, 4);
            guard.Start();
        }

        Bitmap Draw(float scale) { return Panel.DrawBar(name ?? "Claude", shortLeft, weekLeft, note, dark, scale); }

        void Redraw()
        {
            Native.RECT whole, work;
            float scale = Native.Screen(Native.MonitorFromPoint(new Native.POINT { X = X, Y = Y }, 2), out whole, out work);
            string key = name + "|" + shortLeft + "|" + weekLeft + "|" + note + "|" + dark + "|" + scale;
            if (key == drawn) return;
            drawn = key;
            using (Bitmap bmp = Draw(scale))
            {
                // A bar on the right half of its screen keeps its right end in place as it widens.
                if (width > 0 && X + width / 2 > (whole.Left + whole.Right) / 2) X += width - bmp.Width;
                width = bmp.Width; height = bmp.Height;
                Native.Paint(Handle, bmp, X, Y);
            }
        }

        void Guard()
        {
            IntPtr front = Native.GetForegroundWindow();
            bool hide = FullScreen(front);
            if (wanted && hide == Native.IsWindowVisible(Handle)) Native.ShowWindow(Handle, hide ? 0 : 4);
            // The taskbar comes forward whenever it is clicked; the bar goes back on top of it.
            if (wanted && !hide && !down) Native.SetWindowPos(Handle, Topmost, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
        }

        bool FullScreen(IntPtr h)
        {
            if (h == IntPtr.Zero || h == Handle) return false;
            var cls = new StringBuilder(64);
            Native.GetClassName(h, cls, cls.Capacity);
            string name = cls.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r, whole, work;
            Native.GetWindowRect(h, out r);
            Native.Screen(Native.MonitorFromWindow(h, 2), out whole, out work);
            return r.Left <= whole.Left && r.Top <= whole.Top && r.Right >= whole.Right && r.Bottom >= whole.Bottom;
        }

        // Window messages arrive with the thread already in the window's own DPI mode.
        protected override void WndProc(ref Message m)
        {
            if (Native.Arrow(ref m)) return;
            switch (m.Msg)
            {
                case 0x0021: m.Result = (IntPtr)3; return;  // WM_MOUSEACTIVATE: never take the focus
                case 0x0201:  // left button down
                    down = true; dragged = false;
                    Native.GetCursorPos(out grab);
                    origin = new Native.POINT { X = X, Y = Y };
                    Native.SetCapture(Handle);
                    return;
                case 0x0200:  // mouse move
                    if (!down) break;
                    Native.POINT p; Native.GetCursorPos(out p);
                    int dx = p.X - grab.X, dy = p.Y - grab.Y;
                    if (!dragged && Math.Abs(dx) + Math.Abs(dy) < 5) return;
                    dragged = true;
                    Snap(origin.X + dx, origin.Y + dy, p);
                    Native.SetWindowPos(Handle, Topmost, X, Y, 0, 0, NoSize | NoActivate);
                    return;
                case 0x0202:  // left button up
                    if (!down) break;
                    down = false;
                    Native.ReleaseCapture();
                    Native.PostMessage(relay.Handle, 0x8001, (IntPtr)(dragged ? 2 : 0), IntPtr.Zero);
                    if (dragged) { drawn = null; Redraw(); }  // a screen with another scale
                    return;
                case 0x0205: Native.PostMessage(relay.Handle, 0x8001, (IntPtr)1, IntPtr.Zero); return;  // right button up
            }
            base.WndProc(ref m);
        }

        void Snap(int x, int y, Native.POINT pointer)
        {
            Native.RECT whole, work;
            float scale = Native.Screen(Native.MonitorFromPoint(pointer, 2), out whole, out work);
            int reach = (int)(14 * scale);
            x = Nearest(x, reach, whole.Left, whole.Right - width, work.Left, work.Right - width);
            y = Nearest(y, reach, whole.Top, whole.Bottom - height, work.Top, work.Bottom - height,
                        work.Bottom + (whole.Bottom - work.Bottom - height) / 2,  // in a bottom taskbar
                        whole.Top + (work.Top - whole.Top - height) / 2);       // in a top one
            X = Math.Max(whole.Left, Math.Min(x, whole.Right - width));
            Y = Math.Max(whole.Top, Math.Min(y, whole.Bottom - height));
        }

        static int Nearest(int value, int reach, params int[] edges)
        {
            int best = value, distance = reach + 1;
            foreach (int edge in edges)
                if (Math.Abs(edge - value) < distance) { best = edge; distance = Math.Abs(edge - value); }
            return best;
        }

        void Raise(int what)
        {
            EventHandler handler = what == 0 ? Clicked : what == 1 ? RightClicked : Moved;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    // -------------------------------------------------------------------- tray --

    class Tray : ApplicationContext
    {
        readonly NotifyIcon notify = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Icon plainIcon = new Icon(Paths.Icon, 32, 32);
        readonly Flyout flyout = new Flyout();
        readonly Widget widget = new Widget();
        readonly Timer timer = new Timer { Interval = 5000 };
        readonly Timer tagging = new Timer { Interval = 200 };
        readonly int iconSize;
        DateTime taggingUntil;
        string badgeKey;
        object lastSwitch;
        bool proxyDown;

        public Tray()
        {
            object dpi = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "AppliedDPI", 96);
            iconSize = 16 * (dpi as int? ?? 96) / 96;

            notify.Icon = plainIcon;
            notify.Text = Program.Title;
            menu.Font = new Font("Microsoft YaHei UI", 10);
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            notify.ContextMenuStrip = menu;
            notify.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (flyout.Visible || flyout.JustHidden) flyout.Hide(); else Program.Safely(ShowQuota);
            };

            widget.Clicked += (s, e) => { if (flyout.Visible) flyout.Hide(); else Program.Safely(ShowQuota); };
            widget.RightClicked += (s, e) => menu.Show(Cursor.Position);
            widget.Moved += (s, e) => SaveWidget();

            tagging.Tick += (s, e) => { if (Rotation.Tag() || DateTime.Now > taggingUntil) tagging.Stop(); };
            timer.Tick += (s, e) => Tick();

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            };
            foreach (string root in roots.Where(Directory.Exists))
                foreach (string shortcut in new[] { root }.Concat(Directory.GetDirectories(root)).Select(d => Path.Combine(d, "Claude 轮换.lnk")).Where(File.Exists))
                    try { Taskbar.TagShortcut(shortcut, Rotation.Id); } catch (Exception) { }
            var state = Json.Read(Paths.PoolState);
            lastSwitch = Json.Number(Json.Get(state, "switch"), "at");
            UpdateQuota(Json.Read(Paths.PoolConfig), state);
            var saved = Json.Read(Paths.WidgetFile);
            widget.Wanted = !Json.Flag(saved, "hidden");
            widget.Start(saved.Count > 0, (int)Json.Number(saved, "x"), (int)Json.Number(saved, "y"));
            notify.Visible = true;
            KeepProxy();
            timer.Start();
        }

        /// Starts the proxy when it is not running. A failure is shown once, and again only after
        /// the proxy ran in between; the reason is in pool.log.
        void KeepProxy()
        {
            bool up = Pool.Start();
            if (!up && !proxyDown)
                notify.ShowBalloonTip(8000, "本机代理没能启动", "轮换窗口暂时连不上，原因记在 " + Path.Combine(Paths.Data, "pool.log"), ToolTipIcon.Warning);
            proxyDown = !up;
        }

        // Every few seconds: the proxy is alive, the rotation window is on its own button, the
        // bar and icon show what the account in use has left, and each switch is announced once.
        void Tick()
        {
            KeepProxy();
            // Claude can recreate its window, and an icon handle given by a launcher that has since
            // exited goes stale; both are put right here.
            Rotation.Tag();
            var state = Json.Read(Paths.PoolState);
            UpdateQuota(Json.Read(Paths.PoolConfig), state);
            var change = Json.Get(state, "switch");
            double at = Json.Number(change, "at");
            if (at != 0 && !at.Equals(lastSwitch))
            {
                lastSwitch = at;
                string from = Json.Text(change, "from");
                if (from != null)
                    notify.ShowBalloonTip(4000, "已切换到「" + Json.Text(change, "to") + "」", "「" + from + "」" + Json.Text(change, "reason") + "，同一个会话接着用新账号。", ToolTipIcon.Info);
            }
        }

        /// The floating bar shows both windows of the account in use; the tray icon the smaller.
        void UpdateQuota(object config, object state)
        {
            bool dark = Quota.DarkTaskbar();
            string current = Json.Text(state, "current");
            object seen = Json.Flag(config, "paused") || current == null ? null : Json.Get(Json.Get(state, "accounts"), current);
            if (seen == null)
            {
                string note = Json.Flag(config, "paused") ? "轮换已暂停" : Json.Names(config).Length == 0 ? "还没有轮换账号" : "还没开始用";
                widget.Render("Claude 轮换", 1, 1, note, dark);
                if (badgeKey != null) { notify.Icon = plainIcon; badgeKey = null; }
                notify.Text = Program.Title + " · " + note;
                return;
            }
            long reset;
            double shortLeft = Quota.Window(seen, "5h", out reset), weekLeft = Quota.Window(seen, "7d", out reset);
            widget.Render(current, shortLeft, weekLeft, null, dark);
            double left = Math.Min(shortLeft, weekLeft);
            double percent = Math.Round(left * 100);
            string text = percent >= 100 ? "满" : percent.ToString();
            if (badgeKey != text + "|" + dark)
            {
                notify.Icon = Badge.Make(text, Look.Level(left, dark), iconSize);
                badgeKey = text + "|" + dark;
            }
            // The tray refuses tooltips over 63 characters, and account names are whatever was typed.
            string tip = current + " · 5小时剩 " + Math.Round(shortLeft * 100) + "% · 本周剩 " + Math.Round(weekLeft * 100) + "%\n左键查看全部账号";
            notify.Text = tip.Substring(0, Math.Min(63, tip.Length));
        }

        void ShowQuota()
        {
            var config = Json.Read(Paths.PoolConfig);
            var state = Json.Read(Paths.PoolState);
            Row[] rows = Quota.Rows(config, state);
            string pinned = Json.Text(config, "pinned"), current = Json.Text(state, "current");
            string status = Json.Flag(config, "paused") ? "轮换已暂停" : pinned != null ? "固定用「" + pinned + "」" : current != null ? "正在用「" + current + "」" : "";
            string footer = rows.Length > 0 ? "额度取自各账号最近一次使用 · 右键管理账号" : "还没有轮换账号，右键添加";
            flyout.Show(rows, "Claude 轮换额度", status, footer, Quota.DarkTaskbar());
        }

        void SaveWidget()
        {
            Json.Write(Paths.WidgetFile, new Dictionary<string, object> { { "x", widget.X }, { "y", widget.Y }, { "hidden", !widget.Wanted } });
        }

        void OpenRotationWindow()
        {
            Rotation.Open();
            taggingUntil = DateTime.Now.AddSeconds(25);
            tagging.Start();
        }

        void RestartRotationWindow()
        {
            int id = Rotation.ProcessId();
            if (id != 0) using (var main = Process.GetProcessById(id)) { main.Kill(); main.WaitForExit(10000); }
            // Its helper processes hold the data folder until they are gone too.
            var until = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < until && Rotation.Running()) Thread.Sleep(200);
            OpenRotationWindow();
        }

        void ImportSession(Sessions.Entry entry)
        {
            if (Sessions.Replying(entry, Sessions.Projects))
            {
                MessageBox.Show("「" + entry.Title + "」正在主账号窗口里回复，等它停下来再导入。", Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string target = Sessions.RotationStore;
            if (target == null) throw new InvalidOperationException("没找到轮换窗口的会话目录，先打开一次轮换窗口的编程页。");
            Sessions.Move(entry, target);
            const string Leftover = "主账号窗口里它还会显示到那个窗口下次重启，别在那边接着聊。";
            if (Rotation.ProcessId() == 0)
            {
                MessageBox.Show("已把「" + entry.Title + "」移到轮换窗口，打开轮换窗口就能接着聊。\r\n\r\n" + Leftover, Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("已把「" + entry.Title + "」移到轮换窗口。\r\n\r\n轮换窗口要重启一次才会显示它。现在重启吗？正在回复的会话会被打断，重启后可以接着聊。\r\n\r\n" + Leftover,
                                Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RestartRotationWindow();
        }

        ToolStripMenuItem Add(ToolStripItemCollection items, string text, Action click = null, bool check = false, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled };
            if (click != null) item.Click += (s, e) => click();
            items.Add(item);
            return item;
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var config = Json.Read(Paths.PoolConfig);
            var state = Json.Read(Paths.PoolState);
            string[] names = Json.Names(config);
            string current = Json.Text(state, "current"), pinned = Json.Text(config, "pinned");
            bool paused = Json.Flag(config, "paused");
            var items = menu.Items;

            Add(items, names.Length == 0 ? "还没有轮换账号" : paused ? "轮换已暂停" : current != null ? "轮换中 · 正在用「" + current + "」" : "轮换中", enabled: false);
            Add(items, "打开轮换窗口", () => Program.Safely(OpenRotationWindow));
            // Read only when opened: the main window's index files run to megabytes.
            var sessions = Add(items, "从主账号导入会话");
            Add(sessions.DropDownItems, "读取中…", enabled: false);
            sessions.DropDownOpening += (s, e) =>
            {
                sessions.DropDownItems.Clear();
                string mainStore = Sessions.MainStore;
                Sessions.Entry[] recent = mainStore == null ? new Sessions.Entry[0] : Sessions.Recent(mainStore, 15);
                if (recent.Length == 0) Add(sessions.DropDownItems, "主账号里没有可导入的会话", enabled: false);
                foreach (var entry in recent)
                {
                    var picked = entry;
                    var item = Add(sessions.DropDownItems, entry.Title.Length > 28 ? entry.Title.Substring(0, 27) + "…" : entry.Title, () => Program.Safely(() => ImportSession(picked)));
                    item.ShortcutKeyDisplayString = Sessions.Ago(entry.Active);
                }
            };
            items.Add(new ToolStripSeparator());
            if (names.Length > 0)
            {
                Add(items, "自动选择账号", () => Program.Safely(() => Pool.Invoke(null, "pin", "auto")), check: pinned == null);
                foreach (string name in names)
                {
                    string picked = name;
                    var item = Add(items, name + (name == current ? "（正在用）" : ""), () => Program.Safely(() => Pool.Invoke(null, "pin", picked)), check: name == pinned);
                    item.ShortcutKeyDisplayString = Quota.Summary(Json.Get(Json.Get(state, "accounts"), name));
                }
                items.Add(new ToolStripSeparator());
            }
            var import = Add(items, "从已登录的窗口导入");
            Add(import.DropDownItems, "默认窗口", () => ImportLogin(Paths.MainProfile));
            Add(import.DropDownItems, "轮换窗口", () => ImportLogin(Paths.RotationProfile));
            Add(import.DropDownItems, "其他 Claude 数据目录…", () =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "选一个用 --user-data-dir 打开过、已登录的 Claude 数据目录" })
                    if (dialog.ShowDialog() == DialogResult.OK) ImportLogin(dialog.SelectedPath);
            });
            Add(items, "用新登录添加账号…", AddAccount);
            if (names.Length > 0)
            {
                var remove = Add(items, "移除账号");
                foreach (string name in names)
                {
                    string picked = name;
                    Add(remove.DropDownItems, name, () =>
                    {
                        if (MessageBox.Show("从轮换里移除「" + picked + "」？", Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                            Program.Safely(() => Pool.Invoke(null, "remove", picked));
                    });
                }
            }
            items.Add(new ToolStripSeparator());
            if (paused) Add(items, "恢复轮换", () => Program.Safely(() => Pool.Invoke(null, "resume")));
            // Paused, the rotation window keeps working on its own login.
            else if (names.Length > 0) Add(items, "暂停轮换", () => Program.Safely(() => Pool.Invoke(null, "pause")));
            Add(items, "显示悬浮条", () => { widget.Wanted = !widget.Wanted; SaveWidget(); }, check: widget.Wanted);
            Add(items, "开机启动", ToggleStartup, check: File.Exists(Paths.Startup));
            Add(items, "退出", () => { notify.Visible = false; ExitThread(); });
        }

        static void ImportLogin(string folder)
        {
            string given = Microsoft.VisualBasic.Interaction.InputBox("给这个账号起个名字（" + folder + "）", "从已登录的窗口导入").Trim();
            if (given.Length > 0) Program.Safely(() => Pool.Invoke(null, "import", given, folder));
        }

        static void ToggleStartup()
        {
            if (File.Exists(Paths.Startup)) { File.Delete(Paths.Startup); return; }
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Paths.Startup });
            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Paths.Exe });
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Paths.Icon + ",0" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }

        void AddAccount()
        {
            Color background = Color.FromArgb(250, 249, 245), ink = Color.FromArgb(38, 38, 36);
            Color muted = Color.FromArgb(120, 118, 112), accent = Color.FromArgb(203, 123, 93);
            using (var dialog = new Form
            {
                Text = "添加账号", Size = new Size(520, 330), StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowIcon = false,
                BackColor = background, Font = new Font("Microsoft YaHei UI", 10), TopMost = true,
            })
            {
                var hint = new Label { Text = "令牌只存在这台电脑上，用 Windows 加密保存。", ForeColor = muted, Location = new Point(18, 214), Size = new Size(470, 40) };
                var name = new TextBox { Location = new Point(70, 140), Size = new Size(418, 26) };
                var token = new TextBox { Location = new Point(70, 176), Size = new Size(418, 26), UseSystemPasswordChar = true };
                var login = new Button { Text = "打开登录", Location = new Point(18, 94), Size = new Size(110, 32) };
                login.Click += (s, e) =>
                {
                    string cli = FindClaudeCli();
                    if (cli == null) { hint.ForeColor = accent; hint.Text = "没找到 Claude 桌面端自带的 claude.exe，先打开一次桌面端的编程页。"; return; }
                    Process.Start("cmd.exe", "/k \"\"" + cli + "\" setup-token\"").Dispose();
                };
                var ok = new Button { Text = "添加", FlatStyle = FlatStyle.Flat, BackColor = accent, ForeColor = Color.White, Location = new Point(300, 248), Size = new Size(90, 32) };
                ok.FlatAppearance.BorderSize = 0;
                ok.Click += (s, e) =>
                {
                    if (name.Text.Trim().Length == 0) { hint.ForeColor = accent; hint.Text = "先起个名字。"; return; }
                    try
                    {
                        Pool.Invoke(token.Text, "add", name.Text.Trim());
                        dialog.DialogResult = DialogResult.OK;
                    }
                    catch (Exception error) { hint.ForeColor = accent; hint.Text = error.Message; }
                };
                var cancel = new Button { Text = "取消", Location = new Point(398, 248), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[]
                {
                    new Label
                    {
                        Text = "1. 点「打开登录」，在浏览器里登录要加入的 Claude 账号。\r\n2. 登录完成后，命令行窗口会显示一串 sk-ant- 开头的令牌，复制它。\r\n3. 起个名字，把令牌粘贴到下面。",
                        ForeColor = ink, Location = new Point(18, 16), Size = new Size(470, 72),
                    },
                    login,
                    new Label { Text = "名字", ForeColor = ink, Location = new Point(18, 144), Size = new Size(48, 24) }, name,
                    new Label { Text = "令牌", ForeColor = ink, Location = new Point(18, 180), Size = new Size(48, 24) }, token,
                    hint, ok, cancel,
                });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog() == DialogResult.OK)
                    notify.ShowBalloonTip(4000, "已添加账号", "「" + name.Text.Trim() + "」已加入，额度不够时会自动换到它。", ToolTipIcon.Info);
            }
        }

        static string FindClaudeCli()
        {
            var roots = new[] { Paths.MainProfile, Paths.RotationProfile }.Select(d => Path.Combine(d, "claude-code")).Where(Directory.Exists);
            return roots.SelectMany(r => Directory.GetFiles(r, "claude.exe", SearchOption.AllDirectories))
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
    }
}
