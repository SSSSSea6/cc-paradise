# Claude 自动切号 · ClaudeSwitcher

[English](#english)

给 Windows 版 Claude 桌面端用的多账号轮换工具。开一个单独的“轮换窗口”，它的编程会话（Claude Code）会自动挑还有额度的账号去发请求：一个账号快用完时，同一个会话接着用下一个账号，不用手动切号，也不会断掉对话。其他 Claude 窗口不受影响，照常直连。

> **先看风险**：订阅账号的额度是按“普通的个人使用”给的。把几个账号放在一起轮流用，可能违反 Anthropic 的使用条款，账号有被限制或封禁的风险。是否使用、后果如何，由你自己判断和承担。

## 它做什么

- **轮换窗口**：用单独数据目录打开的 Claude 桌面端，有自己独立的任务栏按钮。只有这个窗口的请求经过本机代理。
- **自动选号**：账号一个一个用。会话一直用当前账号，快到上限（5 小时或本周额度用到 95%）才换。如果另一个账号的周额度快要作废（不用就作废的速度是当前账号的两倍以上，且还剩 20% 以上），就提前换过去先用它。被拒的请求会自动换号重发。
- **额度悬浮条**：常驻在屏幕下方，可以拖进任务栏，会自动吸附到边缘。显示当前账号 5 小时和本周的剩余额度。单击弹出全部账号的额度和刷新倒计时，右键是管理菜单。有窗口全屏时自动隐藏。
- **托盘**：图标上显示当前账号的剩余百分比，切号时弹提示。
- **从主账号导入会话**：把默认窗口里的某个编程会话移到轮换窗口接着聊。

## 工作原理

- 轮换窗口启动时带上 `HTTPS_PROXY=http://127.0.0.1:47630`，它的 Claude Code 会话都会继承这个设置。
- 本机代理（`claude_pool.py`）只解开发往 `api.anthropic.com` 的连接，并且只给 `/v1/messages` 请求换上选中账号的令牌，其余流量原样转发。
- 解开连接要用一张本机生成的证书。只有 Claude Code 通过 `~/.claude/settings.json` 里的 `NODE_EXTRA_CA_CERTS` 信任它，不会装进系统证书库。
- 各账号的用量取自 Anthropic 回复里的 `anthropic-ratelimit-unified-*` 响应头。代理自己不会额外发请求。
- 令牌用 Windows DPAPI 加密保存，只有当前 Windows 用户能解开。

## 环境要求

- Windows 10/11，已装 Claude 桌面端
- Python 3.10+，在 PATH 里，并装好 `cryptography`：`pip install cryptography`
- .NET Framework 4.8（Windows 自带，用来编译托盘程序）

## 安装

1. 运行 `build.cmd`，生成 `ClaudeSwitcher.exe`。可以就在这里运行，也可以把 `ClaudeSwitcher.exe`、`claude_pool.py` 和 `icons` 文件夹一起复制到固定的位置，比如 `%LOCALAPPDATA%\Programs\ClaudeSwitcher`。这样以后移动或删掉源码目录，也不影响使用。
2. 运行 `ClaudeSwitcher.exe`。托盘出现图标，屏幕下方出现悬浮条。
3. 右键 → **打开轮换窗口**，在里面登录一次（用哪个账号都行）。
4. 添加要轮换的账号，任选一种：
   - **从已登录的窗口导入**：默认窗口、轮换窗口，或者其他用 `--user-data-dir` 打开过、已经登录的 Claude 数据目录。
   - **用新登录添加账号**：程序会运行 `claude setup-token`，在浏览器里登录，再把显示的令牌粘贴回来。
5. 可选：右键 → **开机启动**。如果想要“直接打开轮换窗口”的快捷方式，目标填 `ClaudeSwitcher.exe --open-window`，命名为 `Claude 轮换`，放在桌面或开始菜单。程序会给它打上和轮换窗口相同的任务栏标识。

想继续用一个现成的 Claude 数据目录当轮换窗口，在 `%LOCALAPPDATA%\ClaudeSwitcher\pool.json` 里加上 `"rotation_profile": "那个目录的完整路径"`，然后重启托盘。

## 使用

- **悬浮条**：拖到哪里都行。靠近屏幕边缘、任务栏上沿时会贴上，拖进任务栏会居中卡住，位置会记住。单击看全部账号，右键打开菜单。
- **右键菜单**：
  - 固定只用某个账号，或交回自动选择
  - 暂停或恢复轮换（暂停后，轮换窗口用它自己登录的账号）
  - 导入、添加、移除账号
  - 显示或隐藏悬浮条、开机启动
- **从主账号导入会话**：列出默认窗口最近的 15 个会话，选一个就移到轮换窗口，默认窗口那边的索引移到回收站。正在回复的会话不能导入。Claude 只在启动时读取会话列表，所以导入后要重启一次轮换窗口，程序会问你要不要现在重启。

## 数据放在哪

所有私密数据都在 `%LOCALAPPDATA%\ClaudeSwitcher`，不会出现在代码目录里：

| 文件 | 内容 |
|---|---|
| `pool.json` | 账号列表和 DPAPI 加密的令牌、固定或暂停的设置 |
| `pool-state.json` | 各账号最近看到的额度、每个会话用的账号 |
| `pool.log` | 切号和网络错误日志 |
| `proxy\` | 本机证书和私钥 |
| `widget.json` | 悬浮条位置 |
| `rotation-profile\` | 轮换窗口的 Claude 数据（没配置 `rotation_profile` 时） |

## 卸载

1. 右键 → 退出，并关掉轮换窗口。
2. 从 `~/.claude/settings.json` 的 `env` 里删掉 `NODE_EXTRA_CA_CERTS`。
3. 删掉 `%LOCALAPPDATA%\ClaudeSwitcher`，以及你建过的快捷方式（开机启动项在“启动”文件夹里，名叫 `Claude 切号器`）。

## 测试

```bat
python tests\test_pool.py
```

`tests\SessionsTest.cs` 测会话导入，编译和运行方法写在文件开头。

## 许可证

MIT

---

## English

ClaudeSwitcher rotates several Claude subscription accounts behind the Claude desktop app on Windows. It opens a separate "rotation window" (Claude Desktop with its own `--user-data-dir`). The Claude Code sessions in that window send their requests through a local proxy. The proxy picks an account that still has quota, so a session simply carries on with the next account when the current one runs low. Other Claude windows are untouched.

> **Risk**: subscription quotas are meant for ordinary individual use. Pooling accounts may break Anthropic's terms and get accounts limited or banned. Use at your own risk.

**How it works**

- The rotation window is started with `HTTPS_PROXY=http://127.0.0.1:47630`.
- The proxy (`claude_pool.py`) terminates TLS only for `api.anthropic.com`, using a local CA. Claude Code trusts that CA through `NODE_EXTRA_CA_CERTS` in `~/.claude/settings.json`; it is never added to the system store.
- The proxy swaps the token only on `/v1/messages` and passes everything else through.
- Quota is read from the `anthropic-ratelimit-unified-*` response headers. The proxy never sends requests of its own.
- Tokens are stored encrypted with Windows DPAPI.

**Account selection**: accounts are used up one at a time. A session stays on its account until the account reaches 95% of its 5-hour or weekly limit. It moves early only when another account's weekly quota would go to waste at least twice as fast and has at least 20% left. A rejected request is retried on the next account.

**Requirements**: Windows 10/11 with Claude Desktop, Python 3.10+ on PATH with `pip install cryptography`, and .NET Framework 4.8 (built into Windows).

**Setup**

1. Run `build.cmd`, then run `ClaudeSwitcher.exe`.
2. Right-click the tray icon or the floating bar → *打开轮换窗口* (open rotation window), and sign in once.
3. Add accounts with *从已登录的窗口导入* (import a signed-in Claude data folder) or *用新登录添加账号* (sign in with `claude setup-token`).

**Data**: all private data lives in `%LOCALAPPDATA%\ClaudeSwitcher`: tokens, certificates, state, logs, and the rotation window's profile. Nothing private is kept in the code folder.

**Uninstall**: quit the tray, remove `NODE_EXTRA_CA_CERTS` from `~/.claude/settings.json`, and delete `%LOCALAPPDATA%\ClaudeSwitcher`.

The UI is in Chinese. Licensed under MIT.
