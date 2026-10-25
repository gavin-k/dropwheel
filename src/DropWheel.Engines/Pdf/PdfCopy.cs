using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace DropWheel.Engines.Pdf;

/// <summary>
/// Copies pages from PDF to PDF with PdfPig, as they are: the content, fonts and images a page uses and the
/// annotations on it come along. Bookmarks, form fields and whatever else belongs to the document rather than
/// to a page do not. A password-protected PDF is refused, even one that opens without a password, so a copy
/// never sheds the restrictions its author set.
/// </summary>
internal static class PdfCopy
{
    /// <exception cref="InvalidDataException">It cannot be read or is password-protected; the message names it.</exception>
    public static PdfDocument Open(string path)
    {
        var name = Path.GetFileName(path);
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (Exception ex) when (PdfConverter.Unreadable(ex, name) is { } message)
        {
            throw new InvalidDataException(message, ex);
        }

        if (document.IsEncrypted)
        {
            document.Dispose();
            throw new InvalidDataException(PdfConverter.Encrypted(name));
        }

        return document;
    }

    /// <summary>Writes the given pages, numbered from 1, to a new PDF at <paramref name="output"/>, calling <paramref name="copied"/> after each.</summary>
    /// <exception cref="InvalidDataException">A page could not be read; the message names its file.</exception>
    public static void Write(string output, IEnumerable<(PdfDocument Document, string Source, int Number)> pages, Action copied, CancellationToken cancellationToken)
    {
        using var stream = File.Create(output);
        using var builder = new PdfDocumentBuilder(stream);
        foreach (var (document, source, number) in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                builder.AddPage(document, number);
            }
            catch (Exception ex) when (PdfConverter.Unreadable(ex, Path.GetFileName(source)) is { } message)
            {
                throw new InvalidDataException(message, ex);
            }

            copied();
        }

        builder.Build();
    }
}
