// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using Eztools.Host;

namespace Eztools.Cli;

/// <summary>极简参数解析。刻意不引 System.CommandLine——P0 的命令面很小，多一个依赖不值得。</summary>
internal sealed class CliArgs
{
    private readonly List<string> _positionals = new();
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;

            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            _flags[name] = value;
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Command => _positionals.Count > 0 ? _positionals[0] : null;

    public IReadOnlyList<string> Rest => _positionals.Count > 1 ? _positionals.Skip(1).ToList() : Array.Empty<string>();

    public bool Has(string name) => _flags.ContainsKey(name);

    public string? Get(string name) => _flags.TryGetValue(name, out var value) ? value : null;

    public string? Get(params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (_flags.TryGetValue(alias, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public bool GetBool(string name) => _flags.ContainsKey(name);

    /// <summary>返回参数（<c>--k v</c> 形式）组成的 JSON 对象；<c>--json</c> 优先。</summary>
    public System.Text.Json.Nodes.JsonObject? BuildParams()
    {
        var json = Get("json", "params");
        if (!string.IsNullOrWhiteSpace(json))
        {
            var text = json;
            if (text.StartsWith('@'))
            {
                var path = text[1..];
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"参数文件不存在: {path}");
                }

                text = File.ReadAllText(path);
            }
            else if (text == "-")
            {
                text = Console.In.ReadToEnd();
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            return node as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject { ["value"] = node };
        }

        var result = new System.Text.Json.Nodes.JsonObject();

        var text2 = Get("text");
        if (text2 is not null)
        {
            result["text"] = text2;
        }

        var path2 = Get("path", "file");
        if (path2 is not null)
        {
            // ⚠️ 必须绝对化：工具进程的工作目录是**工具自己的目录**（不是调用方的 cwd），
            //    直接把相对路径交给工具，它会相对工具目录去解析 → "文件不存在"。
            //    哪一层最清楚调用方的 cwd？CLI。所以由 CLI 承担这一步，而不是让每个工具各自猜。
            //    （Explorer 右键动作传进来的本来就是绝对路径，所以这条只影响命令行用法。）
            result["path"] = PathInput.NormalizeToFullPath(path2);
        }

        foreach (var positional in Rest)
        {
            // 支持 key=value 位置参数
            var equals = positional.IndexOf('=');
            if (equals > 0)
            {
                result[positional[..equals]] = positional[(equals + 1)..];
            }
        }

        return result.Count > 0 ? result : null;
    }
}
