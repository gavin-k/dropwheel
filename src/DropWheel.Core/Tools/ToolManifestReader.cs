using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>
/// Reads and checks a tool folder's <c>tool.json</c>. Every problem is listed, not just the first, so an agent can
/// fix them all in one go; a manifest is returned only when nothing is an error.
/// </summary>
public static partial class ToolManifestReader
{
    public const string FileName = "tool.json";

    public static ToolReadResult Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var problems = new List<ToolProblem>();
        if (!File.Exists(path))
        {
            problems.Add(new(ToolProblem.Json, L.T("文件夹里没有 tool.json")));
            return new(folder, null, problems);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            problems.Add(new(ToolProblem.Json, L.T("tool.json 不是有效的 JSON：{0}", ex.Message)));
            return new(folder, null, problems);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new(ToolProblem.Json, L.T("tool.json 的顶层必须是对象")));
                return new(folder, null, problems);
            }

            var manifest = Parse(root, problems);
            return new(folder, problems.Any(p => !p.IsWarning) ? null : manifest, problems);
        }
    }

    /// <summary>What the trust switch remembers: changing the command or its arguments asks for trust again.</summary>
    public static string CommandHash(ScriptSpec script)
    {
        var text = script.Command + '\0' + string.Join('\0', script.Args);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static ToolManifest Parse(JsonElement root, List<ToolProblem> problems)
    {
        var schema = Int(root, "schema", problems, ToolProblem.Schema, required: true) ?? 0;
        if (schema != 0 && schema != ToolManifest.CurrentSchema)
        {
            problems.Add(new(ToolProblem.Schema, L.T("schema 只能是 {0}", ToolManifest.CurrentSchema)));
        }

        var id = String(root, "id", problems, ToolProblem.Id, required: true) ?? "";
        if (id.Length > 0 && !IdPattern().IsMatch(id))
        {
            // The "user." prefix is also what keeps the id, which becomes the request's TargetFormatId, off every format id.
            problems.Add(new(ToolProblem.Id, L.T("id 必须是 user. 开头的小写字母、数字和连字符，例如 user.strip-exif")));
        }

        var name = Text(root, "name", problems, ToolProblem.Name, required: true) ?? new("");
        var hint = Text(root, "hint", problems, ToolProblem.Name, required: false) ?? new("");
        var icon = String(root, "icon", problems, ToolProblem.Icon, required: false);
        if (icon is { Length: > 0 } && Glyphs.Parse(icon) is null)
        {
            problems.Add(new(ToolProblem.Icon, L.T("icon 要是 Segoe Fluent Icons 的码点，例如 E7A7；这个先不显示图标"), IsWarning: true));
        }

        var wheelName = String(root, "wheel", problems, ToolProblem.Wheel, required: true);
        var wheel = wheelName switch
        {
            "tools" => WheelMode.Tools,
            "actions" => WheelMode.Actions,
            null => WheelMode.Tools,
            _ => Fail(problems, ToolProblem.Wheel, L.T("wheel 只能是 tools 或 actions"), WheelMode.Tools),
        };

        var appliesTo = Kinds(root, problems);

        var kind = String(root, "kind", problems, ToolProblem.Kind, required: false) ?? "script";
        if (kind != "script")
        {
            problems.Add(new(ToolProblem.Kind, L.T("kind 目前只支持 script")));
        }

        var script = Script(root, problems);
        var placement = Placement(root, problems);

        var undo = String(root, "undo", problems, ToolProblem.Undo, required: false) ?? "recycle";
        if (undo is not ("recycle" or "none"))
        {
            problems.Add(new(ToolProblem.Undo, L.T("undo 只能是 recycle 或 none")));
        }

        return new(schema, id, name, hint, icon, wheel, appliesTo, kind, script, placement, undo);
    }

    private static ScriptSpec Script(JsonElement root, List<ToolProblem> problems)
    {
        if (!root.TryGetProperty("script", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(ToolProblem.Command, L.T("缺少 script 对象")));
            return new("", [], true, OutputSpec.Default, ScriptSpec.DefaultTimeoutSeconds);
        }

        var command = String(element, "command", problems, ToolProblem.Command, required: true) ?? "";
        var args = new List<string>();
        if (element.TryGetProperty("args", out var argsElement))
        {
            if (argsElement.ValueKind != JsonValueKind.Array || argsElement.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
            {
                problems.Add(new(ToolProblem.Args, L.T("args 必须是字符串数组")));
            }
            else
            {
                args.AddRange(argsElement.EnumerateArray().Select(a => a.GetString()!));
            }
        }

        var perFile = Bool(element, "perFile", problems, ToolProblem.Args) ?? true;
        var output = Output(element, problems);
        var timeout = Int(element, "timeoutSeconds", problems, ToolProblem.Timeout, required: false) ?? ScriptSpec.DefaultTimeoutSeconds;
        if (timeout is < 1 or > 86400)
        {
            problems.Add(new(ToolProblem.Timeout, L.T("timeoutSeconds 必须在 1 到 86400 之间")));
        }

        foreach (var placeholder in args.SelectMany(a => PlaceholderPattern().Matches(a).Select(m => m.Groups[1].Value)).Distinct())
        {
            if (!ScriptSpec.Placeholders.Contains(placeholder))
            {
                problems.Add(new(ToolProblem.Args, L.T("不认识的占位符 {{{0}}}；可用的有 {1}", placeholder, "{input} {inputs} {output} {outputDir} {toolDir}")));
            }
            else if (placeholder is "output" or "outputDir" && !output.WritesFile)
            {
                problems.Add(new(ToolProblem.Output, L.T("output.mode 是 none 时不能用 {{{0}}}", placeholder)));
            }
            else if (placeholder == "input" && !perFile)
            {
                problems.Add(new(ToolProblem.Args, L.T("perFile 为 false 时请用 {inputs}，不是 {input}")));
            }
            else if (placeholder == "inputs" && perFile)
            {
                problems.Add(new(ToolProblem.Args, L.T("perFile 为 true 时请用 {input}，不是 {inputs}")));
            }
        }

        return new(command, args, perFile, output, timeout);
    }

    private static OutputSpec Output(JsonElement script, List<ToolProblem> problems)
    {
        if (!script.TryGetProperty("output", out var element))
        {
            return OutputSpec.Default;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(ToolProblem.Output, L.T("output 必须是对象")));
            return OutputSpec.Default;
        }

        var mode = String(element, "mode", problems, ToolProblem.Output, required: false) ?? OutputSpec.NewFile;
        if (mode is not (OutputSpec.NewFile or OutputSpec.None))
        {
            problems.Add(new(ToolProblem.Output, L.T("output.mode 只能是 new-file 或 none")));
        }

        var extension = (String(element, "extension", problems, ToolProblem.Output, required: false) ?? OutputSpec.Same).TrimStart('.');
        if (extension != OutputSpec.Same && !ExtensionPattern().IsMatch(extension))
        {
            problems.Add(new(ToolProblem.Output, L.T("output.extension 只能是字母和数字，或 same")));
        }

        var suffix = String(element, "suffix", problems, ToolProblem.Output, required: false) ?? "";
        if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problems.Add(new(ToolProblem.Output, L.T("output.suffix 含有文件名不能用的字符")));
        }

        return new(mode, extension, suffix);
    }

    private static PlacementHint? Placement(JsonElement root, List<ToolProblem> problems)
    {
        if (!root.TryGetProperty("placement", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(ToolProblem.Placement, L.T("placement 必须是对象")));
            return null;
        }

        var group = Text(element, "group", problems, ToolProblem.Placement, required: false);
        var index = Int(element, "index", problems, ToolProblem.Placement, required: false);
        if (index is < 0 or >= WheelModel.SlotCount)
        {
            problems.Add(new(ToolProblem.Placement, L.T("placement.index 必须在 0 到 {0} 之间", WheelModel.SlotCount - 1)));
        }

        return new(group, index);
    }

    private static IReadOnlyList<MediaKind>? Kinds(JsonElement root, List<ToolProblem> problems)
    {
        if (!root.TryGetProperty("appliesTo", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new(ToolProblem.AppliesTo, L.T("appliesTo 必须是数组")));
            return null;
        }

        var kinds = new List<MediaKind>();
        foreach (var item in element.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (text is not null && Enum.TryParse<MediaKind>(text, ignoreCase: true, out var kind) && kind != MediaKind.Unknown)
            {
                kinds.Add(kind);
            }
            else
            {
                problems.Add(new(ToolProblem.AppliesTo, L.T("appliesTo 里不认识 {0}；可用的有 {1}", item.ToString(), string.Join(' ', Enum.GetNames<MediaKind>().Skip(1).Select(n => n.ToLowerInvariant())))));
            }
        }

        return kinds.Count == 0 ? null : kinds;
    }

    private static LocalizedText? Text(JsonElement parent, string property, List<ToolProblem> problems, string code, bool required)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                problems.Add(new(code, L.T("缺少 {0}", property)));
            }

            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { Length: > 0 } text:
                return new(text);
            case JsonValueKind.Object:
                var zh = element.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null;
                var en = element.TryGetProperty("en", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (!string.IsNullOrEmpty(zh))
                {
                    return new(zh, en);
                }

                if (!string.IsNullOrEmpty(en))
                {
                    return new(en, en);
                }

                break;
        }

        problems.Add(new(code, L.T("{0} 必须是文字，或带 zh / en 的对象", property)));
        return null;
    }

    private static string? String(JsonElement parent, string property, List<ToolProblem> problems, string code, bool required)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                problems.Add(new(code, L.T("缺少 {0}", property)));
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text && (text.Length > 0 || !required))
        {
            return text;
        }

        problems.Add(new(code, L.T("{0} 必须是非空文字", property)));
        return null;
    }

    private static int? Int(JsonElement parent, string property, List<ToolProblem> problems, string code, bool required)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                problems.Add(new(code, L.T("缺少 {0}", property)));
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        problems.Add(new(code, L.T("{0} 必须是整数", property)));
        return null;
    }

    private static bool? Bool(JsonElement parent, string property, List<ToolProblem> problems, string code)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        problems.Add(new(code, L.T("{0} 必须是 true 或 false", property)));
        return null;
    }

    private static T Fail<T>(List<ToolProblem> problems, string code, string message, T fallback)
    {
        problems.Add(new(code, message));
        return fallback;
    }

    [GeneratedRegex(@"^user\.[a-z0-9-]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionPattern();
}
