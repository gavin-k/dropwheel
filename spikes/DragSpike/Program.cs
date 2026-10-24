using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DragSpike;

internal static class Program
{
    private const string Usage = """
        DragSpike — M0 drag-detection spike

          DragSpike                      manual mode: hooks on, drag anything from Explorer, tap Shift,
                                         drop on a sector. Runs for --duration seconds (default 300).
          DragSpike --selftest           automated: WinForms drag source + SendInput driver, exit 0 on pass
          DragSpike --selftest --explorer  same, but the drag starts from a real File Explorer window
          DragSpike --uia-dump             diagnostic: what the .NET managed UIA client sees in open Explorer windows
          DragSpike --dragsource <file> --at X,Y   (internal) the drag source window used by --selftest
          DragSpike --locate <file>                (internal) hook-free helper that opens Explorer and finds the item via UIA
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = new UTF8Encoding(false);

        var selfTest = args.Contains("--selftest");
        var explorer = args.Contains("--explorer");
        var duration = ArgValue(args, "--duration") is { } d ? int.Parse(d) : 300;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Contains("--uia-dump"))
        {
            Log.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheelSpike"));
            LogEnvironment();
            ExplorerSource.Dump();
            return 0;
        }

        if (ArgValue(args, "--locate") is { } locate)
        {
            Log.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheelSpike"), "locate");
            return ExplorerSource.LocateMain(locate);
        }

        if (ArgValue(args, "--dragsource") is { } file)
        {
            var at = (ArgValue(args, "--at") ?? "100,100").Split(',');
            Application.Run(new DragSourceForm(file, new Point(int.Parse(at[0]), int.Parse(at[1]))));
            return 0;
        }

        Log.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheelSpike"));
        LogEnvironment();

        using var hooks = new HookThread();
        hooks.Start();

        var wheel = new WheelForm();
        _ = wheel.Handle; // create the hidden window so BeginInvoke works from the hook thread
        hooks.ShiftTapped += (pt, _) =>
        {
            Log.Info($"TAP accepted → show wheel at {pt.X},{pt.Y}");
            wheel.BeginInvoke(() => wheel.ShowAt(pt, "1 file"));
        };
        wheel.Dropped += (paths, sector) => Log.Info($"DROPPED {paths.Count} file(s) on sector {sector}");

        var exitCode = 0;
        if (selfTest)
        {
            var test = new SelfTest(hooks, wheel, explorer);
            var driver = new Thread(() =>
            {
                exitCode = test.Run();
                wheel.BeginInvoke(Application.Exit);
            }) { IsBackground = true, Name = "driver" };
            driver.SetApartmentState(ApartmentState.STA); // UIA lookups return nothing from an MTA thread
            driver.Start();
        }
        else
        {
            Log.Info($"manual mode for {duration}s: drag a file from Explorer, tap Shift while dragging, drop it on a sector.");
            var timer = new System.Windows.Forms.Timer { Interval = duration * 1000 };
            timer.Tick += (_, _) => Application.Exit();
            timer.Start();
        }

        Application.Run();
        Log.Info($"exit code {exitCode}; log file: {Log.FilePath}");
        return exitCode;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void LogEnvironment()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild");
        var ubr = key?.GetValue("UBR");
        var display = key?.GetValue("DisplayVersion");
        var vs = SystemInformation.VirtualScreen;
        Log.Info($"DragSpike on Windows {display} build {build}.{ubr}, {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        Log.Info($"virtual screen {vs} (physical px), primary dpi {new Form().DeviceDpi}, drag threshold {SystemInformation.DragSize}");
    }
}
