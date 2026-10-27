// 从主账号导入会话的测试。和程序源码编译在一起，在 tests 目录里：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /main:T /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll SessionsTest.cs ..\ClaudeSwitcher.cs
//   SessionsTest.exe [正在回复的会话的对话编号]
// 给了对话编号（例如在 Claude Code 会话里跑时用它自己的）才测“正在回复”的正例。
using System; using System.IO; using System.Linq; using ClaudeSwitcher;
static class T {
    static int failed;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
    static int Main(string[] args) {
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
        Console.WriteLine("RESULT failed=" + failed);
        Directory.Delete(root, true);
        return failed;
    }
}