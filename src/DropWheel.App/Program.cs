using System.Runtime.InteropServices;
using System.Text;
using DropWheel.Core.Tools;
using DropWheel.Engines.Tools;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.System.Console;

namespace DropWheel.App;

/// <summary>
/// The entry point XAML would generate, with one thing in front of it: the tool command line
/// (<see cref="ToolCommandLine"/>), which prints JSON to the console DropWheel was started from and exits before
/// anything of WinUI or <see cref="SingleInstance"/> starts, so an agent can check a tool it just wrote while
/// DropWheel is running.
/// </summary>
internal static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        if (ToolCommandLine.Handles(args))
        {
            return RunTools(args);
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(static _ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }

    /// <summary>
    /// A windowed process starts without a console. When its output was piped or redirected the handle is there
    /// already; otherwise the console it was started from is attached, so the JSON shows up in that window.
    /// </summary>
    private static int RunTools(string[] args)
    {
        IntPtr handle = PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
        }

        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        var code = ToolCommandLine.Run(args, Path.Combine(App.DataDir(args), "tools"), new SidecarProcessRunner(), CommandResolver.FromEnvironment(), output);
        output.WriteLine();
        return code;
    }
}
