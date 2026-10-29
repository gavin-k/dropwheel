using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DropWheel.Core.Jobs;

/// <summary>A fingerprint of an output and its whole tree; unreadable items and links are never owned.</summary>
internal sealed record OutputSnapshot(string Fingerprint)
{
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileIdInfo = 18;

    public static OutputSnapshot? Capture(string path)
    {
        try
        {
            using var hash = SHA256.Create();
            using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            using var writer = new BinaryWriter(sink);
            Write(path, writer);
            writer.Flush();
            sink.FlushFinalBlock();
            return new(Convert.ToHexString(hash.Hash!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Matches(string path) => Capture(path) == this;

    private static void Write(string path, BinaryWriter writer)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Cannot own a symbolic link or junction.");
        }

        var folder = (attributes & FileAttributes.Directory) != 0;
        writer.Write(folder);
        if (OperatingSystem.IsWindows())
        {
            // NTFS can tunnel creation times across replacement; the volume and file ID identify the item.
            using var handle = CreateFileW(path, 0, (uint)(FileShare.ReadWrite | FileShare.Delete), IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            {
                throw new IOException("Cannot verify file identity.");
            }

            writer.Write(identity.Volume);
            writer.Write(identity.Low);
            writer.Write(identity.High);
        }
        else
        {
            writer.Write(File.GetCreationTimeUtc(path).Ticks);
        }

        writer.Write(File.GetLastWriteTimeUtc(path).Ticks);
        if (folder)
        {
            var children = Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray();
            writer.Write(children.Length);
            foreach (var child in children)
            {
                writer.Write(Path.GetFileName(child));
                Write(child, writer);
            }
        }
        else
        {
            using var file = File.OpenRead(path);
            writer.Write(file.Length);
            writer.Write(SHA256.HashData(file));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity identity, uint size);
}
