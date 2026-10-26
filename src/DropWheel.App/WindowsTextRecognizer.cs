using System.Runtime.InteropServices.WindowsRuntime;
using DropWheel.App.Diagnostics;
using DropWheel.Engines.Ocr;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace DropWheel.App;

/// <summary>
/// Windows' own OCR (Windows.Media.Ocr) for 识别文字, in the first of the user's languages it can read, else
/// in any language it can. Made on first use, which <see cref="App"/> starts in the background at startup.
/// </summary>
internal sealed class WindowsTextRecognizer : ITextRecognizer
{
    private readonly Lazy<(OcrEngine? Engine, string? Reason)> _engine = new(Create);

    public string? Unavailable => _engine.Value.Reason;

    public int MaxSide => (int)OcrEngine.MaxImageDimension;

    public string Language => _engine.Value.Engine?.RecognizerLanguage.DisplayName ?? "";

    public async Task<IReadOnlyList<IReadOnlyList<string>>> ReadAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken)
    {
        var engine = _engine.Value.Engine ?? throw new InvalidOperationException(Unavailable);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return [.. result.Lines.Select(line => (IReadOnlyList<string>)[.. line.Words.Select(word => word.Text)])];
    }

    private static (OcrEngine?, string?) Create()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null);
            if (engine is null)
            {
                FileLog.Info("ocr: no recognizer language");
                return (null, L.T("Windows 里没有装文字识别语言，可以在「设置」的「时间和语言」里给语言装上「光学字符识别」"));
            }

            FileLog.Info($"ocr: {engine.RecognizerLanguage.LanguageTag}, of {string.Join(" ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag))}, up to {OcrEngine.MaxImageDimension} px");
            return (engine, null);
        }
        catch (Exception ex)
        {
            FileLog.Error("ocr: Windows OCR could not start", ex);
            return (null, L.T("Windows 的文字识别启动不了，详情在日志里"));
        }
    }
}
