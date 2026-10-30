using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO.Compression;
using System.Text;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropWheel.Acceptance;

internal static partial class AppScenario
{
    private static readonly VIRTUAL_KEY[] CtrlAlt = [VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_LMENU];

    /// <summary>
    /// The Tools wheel, opened by a Ctrl+Alt tap mid-drag: 缩小 halves a JPG into a copy named by its new
    /// size, 解压 unpacks a zip into a folder named after it, which 撤销 then moves to the Recycle Bin,
    /// 拆分 PDF puts each page of a PDF in a folder, 识别文字 saves the text of a PNG in a TXT with Windows'
    /// own OCR, and 压缩视频 makes a smaller H.264 MP4 with the bundled ffmpeg.
    /// </summary>
    private static void Tools(Checks checks, AppLog log, string exe, bool explorer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DropWheelAcceptance", "tools");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir))
        {
            File.Delete(old);
        }

        foreach (var old in Directory.GetDirectories(dir))
        {
            Directory.Delete(old, recursive: true);
        }

        var stamp = DateTime.Now.ToString("HHmmss");
        HalfSize(checks, log, Path.Combine(dir, $"half-{stamp}.jpg"), explorer);
        Extract(checks, log, Path.Combine(dir, $"unzip-{stamp}.zip"), explorer);
        SplitPdf(checks, log, Path.Combine(dir, $"split-{stamp}.pdf"), explorer);
        ReadText(checks, log, Path.Combine(dir, $"ocr-{stamp}.png"), explorer);
        CompressVideo(checks, log, FfmpegDir(exe), Path.Combine(dir, $"video-{stamp}.mp4"), explorer);
    }

    private static void HalfSize(Checks checks, AppLog log, string jpg, bool explorer)
    {
        WriteJpeg(jpg, 640, 400);

        // Alt going up can open a menu bar in the window the drag came from; the drop must still land.
        var owner = "not looked at";
        if (DropOnWheel(checks, log, jpg, "tool.half-size", explorer, CtrlAlt, beforeRelease: () => owner = ForegroundOwner()) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(jpg)!, $"{Path.GetFileNameWithoutExtension(jpg)}-320x200.jpg");
        var size = "no output";
        if (output is not null && File.Exists(output))
        {
            using var image = Image.FromFile(output);
            size = $"{image.Width}x{image.Height}";
        }

        checks.Check("缩小 halves the JPG into a copy named by its new size",
            string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && size == "320x200" && File.Exists(jpg),
            $"{done ?? "no queue outcome within 30 s"}; {size}; foreground before the drop {owner}");
    }

    /// <summary>Two lines of large black text on white; the TXT has to hold both, spaces aside.</summary>
    private static void ReadText(Checks checks, AppLog log, string png, bool explorer)
    {
        using (var bitmap = new Bitmap(640, 200))
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 48, GraphicsUnit.Pixel))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString("DropWheel 2026", font, Brushes.Black, 24, 20);
            g.DrawString("Hello OCR", font, Brushes.Black, 24, 110);
            bitmap.Save(png, ImageFormat.Png);
        }

        if (DropOnWheel(checks, log, png, "tool.ocr", explorer, CtrlAlt) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(png)!, $"{Path.GetFileNameWithoutExtension(png)}.txt");
        var text = output is not null && File.Exists(output) ? File.ReadAllText(output) : "";
        // Segoe UI draws l and I alike, so "Hello" may come back as "HeIIo"; fold one into the other before comparing.
        var squeezed = new string([.. text.Where(c => !char.IsWhiteSpace(c)).Select(c => c == 'I' ? 'l' : c)]).ToUpperInvariant();
        checks.Check("识别文字 saves both lines of the PNG's text in a TXT next to it",
            string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && squeezed.Contains("DROPWHEEL2026", StringComparison.Ordinal) && squeezed.Contains("HELLOOCR", StringComparison.Ordinal),
            $"{log.FindSinceStart("ocr: ") ?? "no 'ocr:' line"}; {done ?? "no queue outcome within 30 s"}; text [{text.ReplaceLineEndings(" / ").Trim()}]");
    }

    private static void Extract(Checks checks, AppLog log, string zip, bool explorer)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("说明.txt", "说明"), ("图/a.txt", "a") })
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        if (DropOnWheel(checks, log, zip, "tool.extract", explorer, CtrlAlt) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip));
        var files = output is not null && Directory.Exists(output)
            ? string.Join(", ", Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                .Select(f => $"{Path.GetRelativePath(output, f).Replace('\\', '/')}={File.ReadAllText(f)}").Order(StringComparer.Ordinal))
            : "";
        var unpacked = string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && files == "图/a.txt=a, 说明.txt=说明";
        checks.Check("解压 unpacks the zip into a folder named after it", unpacked, $"{done ?? "no queue outcome within 30 s"}; files [{files}]");
        if (!unpacked)
        {
            return;
        }

        var pressed = PressUndo(label, out var how);
        var undone = pressed ? log.WaitFor(10000, $"queue: undid {label}") : null;
        Checks.Wait(() => !Directory.Exists(expected), 3000);
        var binned = FileOpsScenario.InRecycleBin(Path.GetDirectoryName(zip)!);
        checks.Check("撤销 moves the unpacked folder to the Recycle Bin and keeps the zip",
            undone is not null && !Directory.Exists(expected) && binned.Contains(Path.GetFileName(expected)) && File.Exists(zip),
            $"{how}; {undone ?? "no 'queue: undid' line within 10 s"}; folder exists={Directory.Exists(expected)}; Recycle Bin items from the folder: [{string.Join(", ", binned)}]");
    }

    private static void SplitPdf(Checks checks, AppLog log, string pdf, bool explorer)
    {
        WritePdf(pdf, "one", "two");
        if (DropOnWheel(checks, log, pdf, "tool.split-pdf", explorer, CtrlAlt) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(30000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var stem = Path.GetFileNameWithoutExtension(pdf);
        var expected = Path.Combine(Path.GetDirectoryName(pdf)!, stem);
        var pages = output is not null && Directory.Exists(output)
            ? Directory.GetFiles(output).Select(f => $"{Path.GetFileName(f)}{(File.ReadAllText(f, Encoding.Latin1).StartsWith("%PDF-", StringComparison.Ordinal) ? "" : " (not a PDF)")}")
                .Order(StringComparer.Ordinal).ToList()
            : [];
        checks.Check("拆分 PDF puts each page in a PDF of its own in a folder named after it",
            string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && pages.SequenceEqual([$"{stem}-01.pdf", $"{stem}-02.pdf"]),
            $"{done ?? "no queue outcome within 30 s"}; [{string.Join(", ", pages)}]");
    }

    private static void CompressVideo(Checks checks, AppLog log, string sidecar, string mp4, bool explorer)
    {
        const string Name = "压缩视频 makes a smaller H.264 MP4 with the bundled ffmpeg";
        Run(Path.Combine(sidecar, "ffmpeg.exe"),
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4", "-f", "lavfi", "-i", "sine=duration=4",
             "-c:v", "mpeg4", "-q:v", "2", "-c:a", "aac", "-y", mp4]);
        if (!File.Exists(mp4))
        {
            checks.Check(Name, false, $"could not make the test video with {sidecar}");
            return;
        }

        if (DropOnWheel(checks, log, mp4, "tool.compress-video", explorer, CtrlAlt) is not { } label)
        {
            return;
        }

        var done = log.WaitFor(120000, $"queue: {label} ");
        var output = done is null ? null : OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        var expected = Path.Combine(Path.GetDirectoryName(mp4)!, $"{Path.GetFileNameWithoutExtension(mp4)}-压缩.mp4");
        var found = output is not null && File.Exists(output);
        var stream = found ? Run(Path.Combine(sidecar, "ffprobe.exe"),
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "csv=p=0", output!]) : "";
        var sizes = found ? $"{new FileInfo(mp4).Length} → {new FileInfo(output!).Length} bytes" : "no output";
        checks.Check(Name,
            found && string.Equals(output, expected, StringComparison.OrdinalIgnoreCase) && new FileInfo(output!).Length < new FileInfo(mp4).Length && stream == "h264,1280,720",
            $"{done ?? "no queue outcome within 120 s"}; {sizes}; video stream [{stream}]");
    }

    /// <summary>Runs a sidecar tool for at most a minute and returns what it printed, trimmed.</summary>
    private static string Run(string tool, string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (process is null)
        {
            return "";
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill();
        }

        return output.Wait(5000) ? output.Result.Trim() : "";
    }
}
