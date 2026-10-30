using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>A wheel item that came from a tool folder: a <see cref="ScriptTool"/> or a <see cref="RecipeTool"/>.</summary>
public interface IUserTool : IWheelOperation
{
    ToolManifest Manifest { get; }

    /// <summary>The tool's folder, holding tool.json and whatever else it needs.</summary>
    string Folder { get; }
}
