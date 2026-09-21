using System.Text.Json.Nodes;

namespace Eztools.Contracts;

/// <summary>宿主与工具之间的协议方法名。两侧必须只有一份定义——写错方法名会导致"调用超时"这类难查症状。</summary>
public static class ProtocolMethods
{
    // ── 宿主 → 工具 ──
    public const string ToolInitialize = "tool.initialize";
    public const string ToolInvoke = "tool.invoke";
    public const string ToolOnConfigChanged = "tool.onConfigChanged";
    public const string ToolRecover = "tool.recover";
    public const string ToolStop = "tool.stop";

    /// <summary>
    /// 拉取面板数据（P4 Wave 2c）。宿主 → 工具，工具必须回复 <c>{nodes:[...]}</c>。
    ///
    /// **方向注意**：这是**宿主发起**的方法，不是"工具调用的宿主 API"，
    /// 所以它**不走 <c>EnsureApiAllowed</c> 白名单**（那条闸门管的是工具→宿主方向）；
    /// 档位检查在**发起侧**（仅 <c>weight: full</c>）。见 `docs/P4-Wave2c-面板协议.md` §6.2。
    /// </summary>
    public const string ToolPanelData = "tool.panel.data";

    /// <summary>工具 → 宿主 的握手（工具已就绪，可接收调用）。</summary>
    public const string HostReady = "host.ready";

    // ── 工具 → 宿主 ──
    public const string HostLog = "host.log";
    public const string HostNotify = "host.notify";
    public const string HostProgress = "host.progress";
    public const string HostStorageGet = "host.storage.get";
    public const string HostStorageSet = "host.storage.set";
    public const string HostStorageRemove = "host.storage.remove";
    public const string HostPrimitiveCall = "host.primitive.call";
    public const string HostInvokeTool = "host.invokeTool";
}

/// <summary>JSON-RPC 2.0 标准错误码 + 本项目的扩展码（-32000 ~ -32099 为服务端错误保留区间）。</summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // ── 本项目扩展 ──
    public const int NotSupported = -32001;
    public const int UnknownCommand = -32002;
    public const int UnknownHandler = -32003;

    /// <summary>
    /// 工具间调用成环（含自调用）。见 P4 实施方案 §2.2：
    /// 顶层调用被全局串行，故"链上无重复工具"就是无死锁的充分条件——成环必须拒绝而不是等待。
    /// </summary>
    public const int InvokeCycle = -32004;

    /// <summary>
    /// 当前 <c>weight</c> 档位不提供该 API（如 <c>script</c> 档的 storage / primitive / invokeTool）。
    /// 与 <see cref="NotSupported"/> 的区别：NotSupported 是"这个功能还没实现"，
    /// 本码是"功能有，但你这一档拿不到"——文案必须指出换哪一档。
    /// </summary>
    public const int WeightNotPermitted = -32005;

    public const int ToolCrashed = -32010;
    public const int ToolTimeout = -32011;
    public const int ToolDisabled = -32012;
}

/// <summary>协议层抛出的异常基类。</summary>
public class ToolProtocolException : Exception
{
    public ToolProtocolException(string message, int? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>对应的 JSON-RPC 错误码（可空）。</summary>
    public int? Code { get; }
}

/// <summary>工具返回了 JSON-RPC error。</summary>
public sealed class ToolRpcException : ToolProtocolException
{
    public ToolRpcException(int rpcCode, string message, string? rpcData = null)
        : base(message, rpcCode)
    {
        RpcCode = rpcCode;
        RpcData = rpcData;
    }

    public int RpcCode { get; }

    public string? RpcData { get; }
}

/// <summary>调用超时（进程已被回收）。</summary>
public sealed class ToolTimeoutException : ToolProtocolException
{
    public ToolTimeoutException(string message)
        : base(message, RpcErrorCodes.ToolTimeout)
    {
    }
}

/// <summary>工具进程已退出（崩溃或被 kill）。</summary>
public sealed class ToolCrashedException : ToolProtocolException
{
    public ToolCrashedException(string message, int exitCode)
        : base(message, RpcErrorCodes.ToolCrashed)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

/// <summary>工具被熔断禁用。</summary>
public sealed class ToolDisabledException : ToolProtocolException
{
    public ToolDisabledException(string message)
        : base(message, RpcErrorCodes.ToolDisabled)
    {
    }
}

/// <summary>JSON 节点助手。.NET 7 没有 <c>JsonNode.DeepClone()</c>（那是 .NET 8 才补上的），这里补一个。</summary>
public static class JsonNodeExtensions
{
    /// <summary>深拷贝（切断与父节点的归属关系，可安全挂到别的树上）。</summary>
    public static JsonNode? Clone(this JsonNode? node) =>
        node is null ? null : JsonNode.Parse(node.ToJsonString());

    /// <summary>取字符串值；类型不符或缺失时返回 null，不抛异常。</summary>
    public static string? GetString(this JsonNode? node, string property)
    {
        if (node is not JsonObject obj || obj[property] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : null;
    }
}
