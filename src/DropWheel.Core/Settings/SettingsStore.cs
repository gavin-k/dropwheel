using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DropWheel.Core.Settings;

/// <summary>
/// settings.json: loaded once, saved on every change. A file that cannot be read is moved aside to
/// settings.json.bad and the defaults are used, so a bad file never stops the app from starting.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        var (loaded, problem) = JsonFile.Load(path, SettingsJson.Default.AppSettings, AppSettings.Default);
        Current = Migrate(Repair(loaded));
        LoadProblem = problem;
    }

    /// <summary>Raised after a change has been saved, on the thread that made it.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Current { get; private set; }

    /// <summary>Why the file on disk was not used, or null if it was (or did not exist yet).</summary>
    public string? LoadProblem { get; }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(Current);
            JsonFile.Save(_path, next, SettingsJson.Default.AppSettings);
            Current = next;
        }

        Changed?.Invoke(next);
        return next;
    }

    /// <summary>A hand-edited file can leave out or null whole sections; those fall back to their defaults.</summary>
    private static AppSettings Repair(AppSettings s) => s with
    {
        Keys = s.Keys?.Bindings is null ? KeyBindings.Default : s.Keys,
        Output = s.Output ?? new(),
        Actions = s.Actions?.Enabled is null ? FileActionSettings.Default : s.Actions,
        Privacy = s.Privacy ?? new(),
        Tools = s.Tools?.Trusted is null ? UserToolSettings.Default : s.Tools,
    };

    /// <summary>
    /// Version 1 had no file actions in the settings window, so its files hold that version's default,
    /// copy path and move to only. Those get today's default; a hand-edited set is kept.
    /// </summary>
    private static AppSettings Migrate(AppSettings s) => s.Version >= 2 ? s : s with
    {
        Version = AppSettings.CurrentVersion,
        Actions = s.Actions.Enabled.SetEquals([FileAction.CopyPath, FileAction.MoveTo]) ? FileActionSettings.Default : s.Actions,
    };
}

/// <summary>Small JSON documents written atomically: a temp file next to the target, then a replace.</summary>
public static class JsonFile
{
    public static (T Value, string? Problem) Load<T>(string path, JsonTypeInfo<T> type, T fallback)
    {
        if (!File.Exists(path))
        {
            return (fallback, null);
        }

        try
        {
            using var stream = File.OpenRead(path);
            return (JsonSerializer.Deserialize(stream, type) ?? fallback, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var aside = path + ".bad";
            File.Move(path, aside, overwrite: true);
            return (fallback, $"{Path.GetFileName(path)} could not be read ({ex.Message}); moved to {aside} and using defaults");
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, type);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class SettingsJson : JsonSerializerContext;
