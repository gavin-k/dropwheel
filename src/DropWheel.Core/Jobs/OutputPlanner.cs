using DropWheel.Core.Conversion;

namespace DropWheel.Core.Jobs;

/// <summary>Turns a request's naming template and conflict policy into the path an engine should write.</summary>
public static class OutputPlanner
{
    /// <summary>
    /// Path for the output of <paramref name="sourcePath"/>, or <c>null</c> when the conflict policy says
    /// not to write it (Skip, and Ask until there is a UI for it). Engines write to a temporary file and
    /// move it here without overwriting, so a file that appears in between is never clobbered.
    /// </summary>
    /// <param name="folder">The output is a folder of files (the pages of a PDF), named without an extension.</param>
    public static string? PathFor(
        ConversionRequest request,
        string sourcePath,
        DateTimeOffset now,
        Func<string, bool> exists,
        int? sequence = null,
        int? width = null,
        int? height = null,
        bool folder = false)
    {
        var target = FormatRegistry.FromExtension(request.TargetFormatId)
            ?? throw new ArgumentException($"Unknown target format '{request.TargetFormatId}'.", nameof(request));
        var directory = string.IsNullOrEmpty(request.OutputDirectory)
            ? Path.GetDirectoryName(Path.GetFullPath(sourcePath))!
            : request.OutputDirectory;
        var context = new NamingContext(FormatRegistry.StemOf(sourcePath), target.Id, target.Id, now, sequence, width, height);
        var baseName = new NamingTemplate(request.Naming).Expand(context);
        return NamingTemplate.ResolveConflict(directory, baseName, folder ? "" : target.Id, request.Conflict, exists);
    }
}
