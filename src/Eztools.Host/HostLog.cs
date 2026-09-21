using System.Text;

namespace Eztools.Host;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 统一日志（设计方案 §6.4）。
///
/// 职责：宿主日志与工具日志分流到 <c>%LOCALAPPDATA%\Eztools\logs\</c>；
/// 工具的 stdout/stderr 自动重定向入库。
/// P0 只需要"能定位问题"，轮转与诊断包导出留到 P2。
/// </summary>
public sealed class HostLog : IDisposable
{
    private readonly object _gate = new();
    private readonly bool _echoToConsole;
    private readonly bool _verbose;
    private readonly string? _hostName;
    private Writer? _hostWriter;

    public HostLog(
        EztoolsPaths paths,
        bool verbose = false,
        bool echoToConsole = true,
        string? hostName = null)
    {
        Paths = paths;
        _verbose = verbose;
        _echoToConsole = echoToConsole;
        _hostName = hostName;
    }

    public EztoolsPaths Paths { get; }

    public bool Verbose => _verbose;

    /// <summary>创建一个带作用域的日志器（工具日志会落到各自的目录与文件）。</summary>
    public ScopedLog Scope(string scope, string? toolId = null) => new(this, scope, toolId);

    public void Debug(string message, string? scope = null) => Write(LogLevel.Debug, message, scope, null);

    public void Info(string message, string? scope = null) => Write(LogLevel.Info, message, scope, null);

    public void Warn(string message, string? scope = null) => Write(LogLevel.Warn, message, scope, null);

    public void Error(string message, string? scope = null) => Write(LogLevel.Error, message, scope, null);

    /// <summary>写一条诊断记录（清单问题、资源冲突等）。Error 级同时写入日志。</summary>
    public void Diagnostic(Contracts.ToolDiagnostic diagnostic, string? scope = null)
    {
        var level = diagnostic.Severity switch
        {
            Contracts.DiagnosticSeverity.Error => LogLevel.Error,
            Contracts.DiagnosticSeverity.Warning => LogLevel.Warn,
            _ => LogLevel.Info,
        };

        Write(level, diagnostic.ToString(), scope, null);
    }

    internal void Write(LogLevel level, string message, string? scope, string? toolId)
    {
        if (level == LogLevel.Debug && !_verbose)
        {
            return;
        }

        var scopeText = string.IsNullOrEmpty(scope) ? "" : $" [{scope}]";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Tag(level)}{scopeText} {message}";

        if (_echoToConsole)
        {
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Debug => ConsoleColor.DarkGray,
                    _ => previous,
                };
                Console.WriteLine(line);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }

        var target = toolId is not null
            ? Paths.ToolLogFile(toolId)
            : _hostName is null
                ? Paths.HostLogFile
                : Paths.HostLogFileFor(_hostName);
        var writer = GetWriter(target, toolId);
        writer?.Append(line);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };

    private Writer? GetWriter(string path, string? toolId)
    {
        lock (_gate)
        {
            if (toolId is not null)
            {
                // 工具日志量小，直接开写即可
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return new Writer(path);
                }
                catch
                {
                    return null;
                }
            }

            if (_hostWriter is not null)
            {
                return _hostWriter;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _hostWriter = new Writer(path);
            }
            catch
            {
                // 日志不可写绝不能影响主流程
                _hostWriter = null;
            }

            return _hostWriter;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _hostWriter?.Dispose();
            _hostWriter = null;
        }
    }

    /// <summary>按需打开、写一行、关闭。稳优先于快——日志不是热路径。</summary>
    private sealed class Writer : IDisposable
    {
        private readonly string _path;

        public Writer(string path)
        {
            _path = path;
        }

        public void Append(string line)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
                writer.Write('\n');
            }
            catch
            {
                // 忽略：日志失败不得影响业务
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>带作用域的日志器视图。</summary>
public readonly struct ScopedLog
{
    private readonly HostLog _log;
    private readonly string _scope;
    private readonly string? _toolId;

    internal ScopedLog(HostLog log, string scope, string? toolId)
    {
        _log = log;
        _scope = scope;
        _toolId = toolId;
    }

    public void Debug(string message) => _log.Write(LogLevel.Debug, message, _scope, _toolId);

    public void Info(string message) => _log.Write(LogLevel.Info, message, _scope, _toolId);

    public void Warn(string message) => _log.Write(LogLevel.Warn, message, _scope, _toolId);

    public void Error(string message) => _log.Write(LogLevel.Error, message, _scope, _toolId);
}
