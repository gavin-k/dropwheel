using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DropWheel.Engines;

/// <summary>
/// Reading the zip packages Word and Excel save, with the zip and XML readers the runtime has: opening one,
/// following its relationships and loading its parts, DTDs refused.
/// </summary>
internal static class OoxmlPackage
{
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The OLE compound file signature: Office saves a password-protected file in one, and the old binary formats are one too.</summary>
    private static readonly byte[] Compound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <param name="format">"DOCX", for a file that is not a zip.</param>
    /// <param name="legacy">".doc", the old binary format a renamed file may be.</param>
    public static ZipArchive Open(string path, string format, string legacy)
    {
        var name = Path.GetFileName(path);
        var stream = File.OpenRead(path);
        try
        {
            var head = new byte[Compound.Length];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.AsSpan().SequenceEqual(Compound))
            {
                throw new InvalidDataException($"{name} 有密码保护，或是改了扩展名的旧版 {legacy}，暂不支持");
            }

            stream.Position = 0;
            try
            {
                return new ZipArchive(stream, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException($"{name} 不是有效的 {format} 文件");
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Part names are not case sensitive in a package; zip entry names are.</summary>
    public static ZipArchiveEntry? Entry(ZipArchive zip, string path) =>
        zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

    public static XDocument? Load(ZipArchive zip, string path)
    {
        if (Entry(zip, path) is not { } entry)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe);
        return XDocument.Load(reader);
    }

    /// <summary>The relationships of the part at <paramref name="part"/>, or of the package for "", with targets resolved to entry names.</summary>
    public static List<(string Id, string Type, string Target, bool External)> Relationships(ZipArchive zip, string part)
    {
        var folder = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
        var name = part[(part.LastIndexOf('/') + 1)..];
        var document = Load(zip, (folder.Length > 0 ? folder + "/" : "") + "_rels/" + name + ".rels");
        var list = new List<(string, string, string, bool)>();
        foreach (var rel in document?.Root?.Elements(Rels + "Relationship") ?? [])
        {
            if (rel.Attribute("Id")?.Value is not { } id || rel.Attribute("Type")?.Value is not { } type || rel.Attribute("Target")?.Value is not { } target)
            {
                continue;
            }

            var external = rel.Attribute("TargetMode")?.Value == "External";
            list.Add((id, type, external ? target : Resolve(folder, target), external));
        }

        return list;
    }

    /// <summary>The package's main part, from its relationship, or <paramref name="fallback"/> when there is none.</summary>
    public static string Main(ZipArchive zip, string fallback) =>
        Relationships(zip, "").FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target ?? fallback;

    private static string Resolve(string folder, string target)
    {
        var parts = target.StartsWith('/') ? [] : folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in Uri.UnescapeDataString(target).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }
}
