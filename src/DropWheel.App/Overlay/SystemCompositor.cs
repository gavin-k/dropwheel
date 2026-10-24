using System.Runtime.InteropServices;

namespace DropWheel.App.Overlay;

/// <summary>
/// A Windows.UI.Composition compositor for the UI thread. ICompositionSupportsSystemBackdrop takes a
/// <em>system</em> CompositionBrush, and that compositor needs a Windows.System dispatcher queue, which a
/// WinUI 3 thread does not have (it only has the Microsoft.UI one).
/// </summary>
internal static partial class SystemCompositor
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static nint _controller;

    public static Windows.UI.Composition.Compositor Instance
    {
        get
        {
            if (_compositor is null)
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
                {
                    var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2 /* current */, ApartmentType = 2 /* STA */ };
                    Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _controller));
                }

                _compositor = new Windows.UI.Composition.Compositor();
            }

            return _compositor;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint dispatcherQueueController);
}
