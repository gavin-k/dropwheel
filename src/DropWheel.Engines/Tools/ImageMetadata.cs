using System.Buffers.Binary;

namespace DropWheel.Engines.Tools;

/// <summary>
/// Takes the metadata out of a JPEG, PNG or WebP by dropping the segments or chunks that hold it, so the pixels are
/// never decoded and encoded again. What stays is what changes how the image looks: a JPEG's ICC profile, Adobe
/// colour transform and orientation; a PNG's colour chunks and pixel density; a WebP's ICC profile and animation.
/// </summary>
internal static class ImageMetadata
{
    public static readonly string[] Formats = ["jpg", "png", "webp"];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <returns>The file without its metadata, or null when it has none.</returns>
    /// <exception cref="InvalidDataException">The file is not laid out the way its format says.</exception>
    public static byte[]? Strip(byte[] file, string formatId)
    {
        var stripped = formatId switch
        {
            "jpg" => Jpeg(file),
            "png" => Png(file),
            "webp" => WebP(file),
            _ => throw new ArgumentOutOfRangeException(nameof(formatId), formatId, null),
        };
        return stripped.AsSpan().SequenceEqual(file) ? null : stripped;
    }

    /// <summary>
    /// Drops APP1 (EXIF, XMP), APP13 (IPTC), comments and every other APPn but JFIF, ICC and Adobe, and whatever
    /// follows the end of the image: phones put a second image there (MPF), with EXIF of its own. A photo stored
    /// turned keeps a minimal EXIF holding only its orientation, so it still shows the right way up.
    /// </summary>
    private static byte[] Jpeg(byte[] jpeg)
    {
        if (jpeg is not [0xFF, 0xD8, ..])
        {
            throw NotA("JPEG");
        }

        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var at = 2;
        var turned = false;
        while (true)
        {
            if (at + 4 > jpeg.Length || jpeg[at] != 0xFF)
            {
                throw NotA("JPEG");
            }

            var marker = jpeg[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }

            if (marker == 0xDA)
            {
                output.Write(jpeg, at, EndOfImage(jpeg, at) - at);
                return output.ToArray();
            }

            var end = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            if (end > jpeg.Length)
            {
                throw NotA("JPEG");
            }

            var body = jpeg.AsSpan(at + 4, end - at - 4);
            if (Keep(marker, body))
            {
                output.Write(jpeg, at, end - at);
            }
            else if (!turned && marker == 0xE1 && Orientation(body) is { } orientation and not 1)
            {
                output.Write(OrientationOnly(orientation));
                turned = true;
            }

            at = end;
        }
    }

    private static bool Keep(byte marker, ReadOnlySpan<byte> body) => marker switch
    {
        0xE0 => body.StartsWith("JFIF\0"u8),
        0xE2 => body.StartsWith("ICC_PROFILE\0"u8),
        0xEE => body.StartsWith("Adobe"u8),
        >= 0xE0 and <= 0xEF or 0xFE => false,
        _ => true,
    };

    /// <summary>
    /// Just past the end-of-image marker, found by walking the scans from the first one and skipping the segments
    /// between them, since a marker's bytes can turn up inside a Huffman table. The file's length when it has none.
    /// </summary>
    private static int EndOfImage(byte[] jpeg, int at)
    {
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            if (marker == 0xD9)
            {
                return at + 2;
            }

            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            if (marker != 0xDA)
            {
                continue;
            }

            // Entropy-coded data: 0xFF is followed by 0x00 (a stuffed byte) or a restart marker until the next marker.
            while (at + 1 < jpeg.Length && !(jpeg[at] == 0xFF && jpeg[at + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7)))
            {
                at++;
            }
        }

        return jpeg.Length;
    }

    /// <summary>The EXIF orientation tag, 1 to 8, or null when this APP1 has none.</summary>
    private static int? Orientation(ReadOnlySpan<byte> app1)
    {
        if (!app1.StartsWith("Exif\0\0"u8) || app1.Length < 14)
        {
            return null;
        }

        var tiff = app1[6..];
        bool little;
        if (tiff.StartsWith("II"u8))
        {
            little = true;
        }
        else if (tiff.StartsWith("MM"u8))
        {
            little = false;
        }
        else
        {
            return null;
        }

        var ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
        if (ifd > (uint)tiff.Length - 2)
        {
            return null;
        }

        var count = U16(tiff, (int)ifd, little);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            // Tag 0x0112, type SHORT.
            if (U16(tiff, entry, little) == 0x0112 && U16(tiff, entry + 2, little) == 3)
            {
                return U16(tiff, entry + 8, little) is var value and >= 1 and <= 8 ? value : null;
            }
        }

        return null;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    /// <summary>An APP1 whose EXIF holds one tag, the orientation.</summary>
    private static byte[] OrientationOnly(int orientation) =>
    [
        0xFF, 0xE1, 0x00, 0x22,
        (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
        (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>Drops the text, EXIF and time chunks and anything after the end chunk.</summary>
    private static byte[] Png(byte[] png)
    {
        if (!png.AsSpan().StartsWith(PngSignature))
        {
            throw NotA("PNG");
        }

        using var output = new MemoryStream(png.Length);
        output.Write(PngSignature);
        var at = PngSignature.Length;
        while (true)
        {
            if (at + 12 > png.Length)
            {
                throw NotA("PNG");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (length > (uint)(png.Length - at - 12))
            {
                throw NotA("PNG");
            }

            var end = at + 12 + (int)length;
            var type = png.AsSpan(at + 4, 4);
            if (!(type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)
                || type.SequenceEqual("eXIf"u8) || type.SequenceEqual("tIME"u8)))
            {
                output.Write(png, at, end - at);
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return output.ToArray();
            }

            at = end;
        }
    }

    /// <summary>
    /// Drops the EXIF and XMP chunks and clears their flags in the extended header. A simple WebP, one without
    /// that header, has nowhere to keep metadata.
    /// </summary>
    private static byte[] WebP(byte[] webp)
    {
        if (webp.Length < 12 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw NotA("WebP");
        }

        var riffEnd = (int)Math.Min(webp.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)));
        using var output = new MemoryStream(webp.Length);
        output.Write(webp, 0, 12);
        var at = 12;
        while (at + 8 <= riffEnd)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            var end = at + 8 + (long)size + (size & 1);
            if (at + 8 + (long)size > riffEnd)
            {
                throw NotA("WebP");
            }

            // The last chunk's padding byte is sometimes missing.
            end = Math.Min(end, riffEnd);
            var fourCc = webp.AsSpan(at, 4);
            if (!fourCc.SequenceEqual("EXIF"u8) && !fourCc.SequenceEqual("XMP "u8))
            {
                var start = (int)output.Position;
                output.Write(webp, at, (int)end - at);
                if (fourCc.SequenceEqual("VP8X"u8) && size >= 1)
                {
                    output.GetBuffer()[start + 8] &= unchecked((byte)~0x0C); // the EXIF and XMP flags
                }
            }

            at = (int)end;
        }

        var stripped = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(stripped.AsSpan(4), (uint)(stripped.Length - 8));
        return stripped;
    }

    private static InvalidDataException NotA(string format) => new($"文件内容不是有效的 {format}");
}
