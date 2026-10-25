namespace DropWheel.Engines.Ocr;

/// <summary>Reads the text in a picture. The app's is Windows' own OCR; the tests use a fake.</summary>
public interface ITextRecognizer
{
    /// <summary>Why it cannot read on this computer, such as no OCR language installed; null when it can.</summary>
    string? Unavailable { get; }

    /// <summary>The longest side of a picture it takes, in pixels; larger ones are scaled down first.</summary>
    int MaxSide { get; }

    /// <summary>The language it reads, for the task window, such as "中文(中华人民共和国)".</summary>
    string Language { get; }

    /// <summary>The lines of text in a picture, in reading order, each as its words.</summary>
    /// <param name="bgra">Opaque pixels, 4 bytes each in blue, green, red, alpha order, row after row with no padding.</param>
    Task<IReadOnlyList<IReadOnlyList<string>>> ReadAsync(byte[] bgra, int width, int height, CancellationToken cancellationToken);
}
