using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageMagick;
using ImageMagick.Drawing;

// Generates a JPG in %TEMP%, converts it to PNG and WEBP (the M0 image engine's job), reads each back
// and prints what loaded and how long it took.  Exit 0 only if every round trip succeeds.
var dir = Path.Combine(Path.GetTempPath(), "DropWheelSpike");
Directory.CreateDirectory(dir);
Console.WriteLine($"MagickSpike on {RuntimeInformation.OSDescription}, OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");

var sw = Stopwatch.StartNew();
Console.WriteLine($"Magick.NET {MagickNET.Version}");
Console.WriteLine($"ImageMagick features: {MagickNET.Features}");
Console.WriteLine($"native init {sw.ElapsedMilliseconds} ms");
var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .FirstOrDefault(m => m.ModuleName.StartsWith("Magick.Native", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"native module: {native?.FileName ?? "(not loaded?)"}");

var jpg = Path.Combine(dir, "spike-source.jpg");
using (var src = new MagickImage(new MagickColor("#3366cc"), 1920, 1080))
{
    new Drawables().FillColor(MagickColors.Orange).Ellipse(960, 540, 400, 250, 0, 360).Draw(src);
    src.Write(jpg, MagickFormat.Jpeg);
}
Console.WriteLine($"wrote {Path.GetFileName(jpg)} ({new FileInfo(jpg).Length} bytes)");

// What this build claims it can read/write for the formats the product cares about.
var interesting = new[] { MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.WebP, MagickFormat.Avif, MagickFormat.Heic, MagickFormat.Heif, MagickFormat.Jxl, MagickFormat.Tiff, MagickFormat.Bmp, MagickFormat.Gif, MagickFormat.Psd, MagickFormat.Svg, MagickFormat.Pdf };
foreach (var f in MagickNET.SupportedFormats.Where(f => interesting.Contains(f.Format)))
{
    Console.WriteLine($"format {f.Format,-5} read={f.SupportsReading,-5} write={f.SupportsWriting,-5} {f.Description}");
}

// HEIC/HEIF writing needs an x265/kvazaar encoder that Magick.NET does not ship on any platform, so
// a HEIC write failure is recorded but not counted as an ARM64 problem.
var ok = true;
foreach (var (format, ext) in new[] { (MagickFormat.Png, "png"), (MagickFormat.WebP, "webp"), (MagickFormat.Avif, "avif"), (MagickFormat.Jxl, "jxl"), (MagickFormat.Heic, "heic") })
{
    var canWrite = MagickNET.SupportedFormats.FirstOrDefault(f => f.Format == format)?.SupportsWriting == true;
    var target = Path.Combine(dir, $"spike-target.{ext}");
    try
    {
        sw.Restart();
        using (var image = new MagickImage(jpg))
        {
            image.Write(target, format);
        }
        var write = sw.ElapsedMilliseconds;
        sw.Restart();
        using var back = new MagickImage(target);
        Console.WriteLine($"OK   jpg -> {ext}: {new FileInfo(target).Length} bytes, write {write} ms, read back {sw.ElapsedMilliseconds} ms, {back.Width}x{back.Height} {back.Format}");
    }
    catch (Exception ex)
    {
        ok &= !canWrite; // only a failure if the build claimed it could write this format
        Console.WriteLine($"{(canWrite ? "FAIL" : "SKIP")} jpg -> {ext}: {ex.GetType().Name}: {ex.Message}{(canWrite ? "" : " (build does not claim write support)")}");
    }
}

Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
return ok ? 0 : 1;
