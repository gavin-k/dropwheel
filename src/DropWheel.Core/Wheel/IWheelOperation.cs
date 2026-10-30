using DropWheel.Core.Conversion;
using DropWheel.Core.Settings;

namespace DropWheel.Core.Wheel;

/// <summary>
/// A Tools or Actions wheel item. Convert items are target formats that the <see cref="ConversionGraph"/>
/// resolves; these work on the dragged items themselves, so they run as their own job.
/// The request they get has <see cref="Id"/> as its TargetFormatId.
/// </summary>
public interface IWheelOperation : IJobRunner
{
    /// <summary>Stable id, such as "action.zip". Never a format id.</summary>
    string Id { get; }

    WheelMode Mode { get; }

    /// <summary>The sector's label, a few characters.</summary>
    string Label { get; }

    /// <summary>The line under the label while the item is available.</summary>
    string Hint { get; }

    /// <summary>A Segoe Fluent Icons code point drawn above the label, as a one-character string; null for none.</summary>
    string? Glyph => null;

    /// <summary>The switch in Settings › 文件动作 that turns this item on; null when it is always on.</summary>
    FileAction? Action => null;

    /// <summary>Whether it belongs on the wheel for items of this kind; <see cref="IJobRunner.Check"/> then says whether it can run.</summary>
    bool AppliesTo(MediaKind kind) => true;

    /// <summary>
    /// For an item that runs code the user did not get from DropWheel: what Settings › 我的工具 must have recorded
    /// as trusted before it runs, which changes when the code it runs does. Null for a built-in item.
    /// </summary>
    string? TrustKey => null;
}
