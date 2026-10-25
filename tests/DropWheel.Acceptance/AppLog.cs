using System.Text;

namespace DropWheel.Acceptance;

/// <summary>
/// Tails the app's own log (%LOCALAPPDATA%\DropWheel\logs\dropwheel-*.log) from the moment this object is
/// created, so a scenario can wait for lines the app writes. Every new line is echoed as "app> ...".
/// </summary>
internal sealed class AppLog
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheel", "logs");
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lines = [];
    private int _mark;

    public AppLog()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory, "dropwheel-*.log"))
            {
                _offsets[file] = new FileInfo(file).Length;
            }
        }
    }

    /// <summary>Later <see cref="WaitFor"/> calls only look at lines written after this point.</summary>
    public void Mark()
    {
        Poll();
        _mark = _lines.Count;
    }

    /// <summary>First line since the last <see cref="Mark"/> that contains every fragment, or null on timeout.</summary>
    public string? WaitFor(int timeoutMs, params string[] fragments)
    {
        string? found = null;
        Checks.Wait(() => (found = Find(fragments)) is not null, timeoutMs);
        return found;
    }

    /// <summary>Same as <see cref="WaitFor"/> without waiting.</summary>
    public string? Find(params string[] fragments)
    {
        Poll();
        for (var i = _mark; i < _lines.Count; i++)
        {
            if (fragments.All(f => _lines[i].Contains(f, StringComparison.Ordinal)))
            {
                return _lines[i];
            }
        }

        return null;
    }

    /// <summary>How many lines since the last <see cref="Mark"/> contain every fragment.</summary>
    public int Count(params string[] fragments)
    {
        Poll();
        return _lines.Skip(_mark).Count(line => fragments.All(f => line.Contains(f, StringComparison.Ordinal)));
    }

    /// <summary>First line since this object was created that contains every fragment, whatever <see cref="Mark"/> said.</summary>
    public string? FindSinceStart(params string[] fragments)
    {
        Poll();
        return _lines.FirstOrDefault(line => fragments.All(f => line.Contains(f, StringComparison.Ordinal)));
    }

    public void Poll()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_directory, "dropwheel-*.log").Order(StringComparer.OrdinalIgnoreCase))
        {
            var offset = _offsets.GetValueOrDefault(file);
            try
            {
                // ReadWrite sharing so the app's appends never fail while this reads.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < offset)
                {
                    offset = 0; // the app cleared its logs and started this one afresh
                }

                if (stream.Length <= offset)
                {
                    continue;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                var bytes = new byte[stream.Length - offset];
                stream.ReadExactly(bytes);
                var text = Encoding.UTF8.GetString(bytes);
                var complete = text.LastIndexOf('\n');
                if (complete < 0)
                {
                    continue;
                }

                foreach (var line in text[..complete].Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    _lines.Add(trimmed);
                    Log.Info($"app> {trimmed}");
                }

                _offsets[file] = offset + Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
            }
            catch (IOException)
            {
                // Next poll.
            }
        }
    }
}
