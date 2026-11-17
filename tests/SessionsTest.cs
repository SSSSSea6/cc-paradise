// 两个窗口之间移动会话的测试。和程序源码编译在一起，在 tests 目录里：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /main:T /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll SessionsTest.cs ..\ClaudeSwitcher.cs
//   SessionsTest.exe [正在回复的会话的对话编号]
// 给了对话编号（例如在 Claude Code 会话里跑时用它自己的）才测“正在回复”的正例。
using System; using System.IO; using System.Linq; using ClaudeSwitcher;
static class T {
    static int failed;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
    static int Main(string[] args) {
        Func<string, long> at = text => DateTimeOffset.Parse(text + "Z").ToUnixTimeSeconds();
        Check("订阅续期：下一个同日同时刻，和代理算的一样", Quota.Renews("2026-10-19T09:44:11.321544Z", at("2026-11-17T00:00:00")) == at("2026-11-19T09:44:11"));
        Check("订阅续期：过了这个月的就算下个月", Quota.Renews("2026-10-19T09:44:11Z", at("2026-11-19T09:44:12")) == at("2026-12-19T09:44:11"));
        Check("订阅续期：月份没有那天取月底", Quota.Renews("2026-01-31T00:00:00Z", at("2026-02-01T00:00:00")) == at("2026-02-28T00:00:00"));
        Check("订阅续期：不知道开始时间的不显示", Quota.Renews(null, 0) == 0);

        string root = Path.Combine(Path.GetTempPath(), "session-move-test-" + Guid.NewGuid().ToString("N"));
        string main = Path.Combine(root, "main"), rot = Path.Combine(root, "rot"), projects = Path.Combine(root, "projects", "C--work");
        Directory.CreateDirectory(main); Directory.CreateDirectory(rot); Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(main, "local_a.json"), "{\"sessionId\":\"local_a\",\"cliSessionId\":\"aaaa\",\"title\":\"旧的\",\"lastActivityAt\":1000,\"isArchived\":false}");
        File.WriteAllText(Path.Combine(main, "local_b.json"), "{\"sessionId\":\"local_b\",\"cliSessionId\":\"bbbb\",\"title\":\"新的\",\"lastActivityAt\":3000,\"isArchived\":false}");
        File.WriteAllText(Path.Combine(main, "local_c.json"), "{\"sessionId\":\"local_c\",\"cliSessionId\":\"cccc\",\"title\":\"归档了\",\"lastActivityAt\":5000,\"isArchived\":true}");
        File.WriteAllText(Path.Combine(main, "local_d.json"), "{\"sessionId\":\"local_d\",\"cliSessionId\":null,\"title\":\"没开始\",\"lastActivityAt\":9000,\"isArchived\":false}");
        var recent = Sessions.Recent(main, 15);
        Check("列出未归档、有对话的会话，最近的在前", string.Join(",", recent.Select(e => e.Title)) == "新的,旧的");
        Check("只取指定条数", Sessions.Recent(main, 1).Length == 1);

        File.WriteAllText(Path.Combine(projects, "bbbb.jsonl"), "{}");
        Check("聊天记录刚写过、但没有进程挂着它：不算正在回复", !Sessions.Replying(recent[0], Path.Combine(root, "projects")));
        Check("找不到聊天记录：不算正在回复", !Sessions.Replying(recent[1], Path.Combine(root, "projects")));
        if (args.Length > 0)
            Check("有进程挂着、聊天记录刚写过：算正在回复", Sessions.Replying(new Sessions.Entry { Conversation = args[0] }, Sessions.Projects));
        string stale = Path.Combine(projects, "eeee.jsonl"); File.WriteAllText(stale, "{}"); File.SetLastWriteTime(stale, DateTime.Now.AddMinutes(-10));
        Check("聊天记录十分钟没动：不算正在回复", !Sessions.Replying(new Sessions.Entry { Conversation = "eeee" }, Path.Combine(root, "projects")));

        Sessions.Move(recent[0], rot);
        string moved = Path.Combine(rot, "local_b.json");
        Check("移过去：轮换那边有了同名索引，内容原样", File.Exists(moved) && File.ReadAllText(moved).Contains("\"cliSessionId\":\"bbbb\""));
        Check("移过去：主账号那边的索引没了（进了回收站）", !File.Exists(Path.Combine(main, "local_b.json")));
        File.WriteAllText(Path.Combine(main, "local_x.json"), "{\"sessionId\":\"local_x\",\"cliSessionId\":\"xxxx\",\"isArchived\": true}");
        Sessions.Move(new Sessions.Entry { File = Path.Combine(main, "local_x.json") }, rot);
        Check("移过去的会话显示为未归档", File.ReadAllText(Path.Combine(rot, "local_x.json")).Contains("\"isArchived\":false"));

        // The other way: a rotation-window session in a scratch folder of its own, into the main window.
        // Conversation folders spell out the whole working directory, so this part needs a short root.
        Directory.Delete(root, true);
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string rotProfile = Path.Combine(root, "rot"), mainProfile = Path.Combine(root, "main"), allProjects = Path.Combine(root, "projects");
        string rotStore = Path.Combine(rotProfile, "claude-code-sessions", "RACCT", "RORG"), mainStore = Path.Combine(mainProfile, "claude-code-sessions", "MACCT", "MORG");
        Directory.CreateDirectory(rotStore); Directory.CreateDirectory(mainStore);
        File.WriteAllText(Path.Combine(mainStore, "local_m.json"), "{\"sessionId\":\"local_m\",\"remoteMcpServersConfig\":[{\"name\":\"主窗口的连接器\"}]}");
        string scratch = Path.Combine(rotProfile, "scratch-workspaces", "RACCT", "RORG", "scratch-2026-10-28-abc123");
        Directory.CreateDirectory(scratch); File.WriteAllText(Path.Combine(scratch, "notes.txt"), "工作文件");
        string oldFolder = Path.Combine(allProjects, Sessions.Folder(scratch));
        Directory.CreateDirectory(Path.Combine(oldFolder, "now", "subagents"));
        File.WriteAllText(Path.Combine(oldFolder, "now.jsonl"), "{}"); File.WriteAllText(Path.Combine(oldFolder, "before.jsonl"), "{}");
        File.WriteAllText(Path.Combine(oldFolder, "someone-else.jsonl"), "{}");
        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
        File.WriteAllText(Path.Combine(rotStore, "local_r.json"), json.Serialize(new System.Collections.Generic.Dictionary<string, object> {
            { "sessionId", "local_r" }, { "cliSessionId", "now" }, { "priorCliSessionIds", new[] { "before" } }, { "title", "轮换里的会话" },
            { "cwd", scratch }, { "originCwd", scratch }, { "gitAnchorsFolderRealpath", scratch }, { "isArchived", false },
            { "spawnedFrom", "local_parent" }, { "bridgeSessionIds", new[] { "b1" } }, { "chromeTabGroupId", 7 }, { "toolSurfaceSnapshot", new { h = 1 } },
            { "remoteMcpServersConfig", new[] { new { name = "轮换窗口的连接器" } } },
        }));
        Sessions.MoveToMain(new Sessions.Entry { File = Path.Combine(rotStore, "local_r.json"), Conversation = "now" }, rotProfile, rotStore, mainProfile, mainStore, allProjects);
        string landed = Path.Combine(mainStore, "local_r.json");
        var index = File.Exists(landed) ? (System.Collections.Generic.Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(landed)) : null;
        string newScratch = Path.Combine(mainProfile, "scratch-workspaces", "MACCT", "MORG", "scratch-2026-10-28-abc123");
        string newFolder = Path.Combine(allProjects, Sessions.Folder(newScratch));
        Check("移到主窗口：索引在主窗口的账号目录里，轮换窗口那份进了回收站", index != null && !File.Exists(Path.Combine(rotStore, "local_r.json")) && (string)index["title"] == "轮换里的会话");
        Check("移到主窗口：工作目录换成主窗口的临时目录，文件跟着过去", index != null && (string)index["cwd"] == newScratch && (string)index["originCwd"] == newScratch
              && (string)index["gitAnchorsFolderRealpath"] == newScratch && File.ReadAllText(Path.Combine(newScratch, "notes.txt")) == "工作文件" && !Directory.Exists(scratch));
        Check("移到主窗口：聊天记录（含压缩前的旧记录和子文件夹）在新目录对应的文件夹里，别的会话的记录不动",
              File.Exists(Path.Combine(newFolder, "now.jsonl")) && File.Exists(Path.Combine(newFolder, "before.jsonl")) && Directory.Exists(Path.Combine(newFolder, "now", "subagents"))
              && !File.Exists(Path.Combine(oldFolder, "now.jsonl")) && File.Exists(Path.Combine(oldFolder, "someone-else.jsonl")));
        Check("移到主窗口：只属于轮换窗口的字段清掉，连接器用主窗口的", index != null && !index.ContainsKey("spawnedFrom") && !index.ContainsKey("bridgeSessionIds")
              && !index.ContainsKey("chromeTabGroupId") && !index.ContainsKey("toolSurfaceSnapshot") && json.Serialize(index["remoteMcpServersConfig"]).Contains("主窗口的连接器"));

        string project = Path.Combine(root, "某个项目");
        Directory.CreateDirectory(project); Directory.CreateDirectory(Path.Combine(allProjects, Sessions.Folder(project)));
        File.WriteAllText(Path.Combine(allProjects, Sessions.Folder(project), "proj.jsonl"), "{}");
        File.WriteAllText(Path.Combine(rotStore, "local_p.json"), json.Serialize(new System.Collections.Generic.Dictionary<string, object> { { "sessionId", "local_p" }, { "cliSessionId", "proj" }, { "cwd", project } }));
        Sessions.MoveToMain(new Sessions.Entry { File = Path.Combine(rotStore, "local_p.json"), Conversation = "proj" }, rotProfile, rotStore, mainProfile, mainStore, allProjects);
        Check("移到主窗口：在普通项目目录里的会话，目录和聊天记录都原地不动",
              File.ReadAllText(Path.Combine(mainStore, "local_p.json")).Contains(json.Serialize(project).Trim('"')) && File.Exists(Path.Combine(allProjects, Sessions.Folder(project), "proj.jsonl")));
        Console.WriteLine("RESULT failed=" + failed);
        Directory.Delete(root, true);
        return failed;
    }
}