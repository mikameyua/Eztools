using Eztools.Core;

// ════════════════════════════════════════════════════════════════════════
// ezt-core —— Core 特权原语服务（P3）。
//
// 用法：
//   ezt-core --root <安装根> [--echo] [--from-task]
//
//   --root       安装根（core.json 与审计日志都落在这里；管道名由它派生）
//   --echo       审计记录同时回显到控制台（调试用）
//   --from-task  由计划任务启动（信息性标记，记录进启动日志）
//
// 退出码：0 正常退出 · 3 已有实例在运行 · 其他非零 = 启动失败。
// ════════════════════════════════════════════════════════════════════════

string? root = null;
var echo = false;
var fromTask = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root":
            root = i + 1 < args.Length ? args[++i] : null;
            break;
        case "--echo":
            echo = true;
            break;
        case "--from-task":
            fromTask = true;
            break;
        default:
            CoreConsole.Error($"ezt-core: 未知参数 {args[i]}（需要 --root <安装根>）");
            return 64;
    }
}

if (string.IsNullOrEmpty(root))
{
    CoreConsole.Error("ezt-core: 缺少 --root <安装根>。宿主经 `ezt core start` 启动，无需手工指定。");
    return 64;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;      // 优雅退出：让 AcceptLoop 走 finally（摘除 core.json）
    cts.Cancel();
};

var server = new CoreServer(root!, echo);
if (fromTask)
{
    CoreConsole.Say("ezt-core: 由计划任务启动。");
}

try
{
    return await server.RunAsync(cts.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
    // 顶层兜底：崩溃现场必须落 stderr（父进程会把它收进 host-ezt-core-*.log）
    CoreConsole.Error($"ezt-core 异常退出：{ex}");
    return 1;
}
