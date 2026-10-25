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

    /// <summary>The switch in Settings › 文件动作 that turns this item on; null when it is always on.</summary>
    FileAction? Action => null;

    /// <summary>Whether it belongs on the wheel for items of this kind; <see cref="IJobRunner.Check"/> then says whether it can run.</summary>
    bool AppliesTo(MediaKind kind) => true;
}
