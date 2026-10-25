using System.Text;

namespace DropWheel.Engines.Subtitle;

/// <summary>
/// Reads subtitle files whatever their encoding and writes them as UTF-8. Many Chinese SRT files are GBK
/// without a BOM, so the order is: BOM, then strict UTF-8, then GB18030 (a superset of GBK).
/// </summary>
internal static class SubtitleText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 with a BOM: older Windows players and TVs guess ANSI without one.</summary>
    public static readonly Encoding Output = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    static SubtitleText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(54936).GetString(bytes);
        }
    }
}
