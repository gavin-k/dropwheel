using System.Text;

namespace DropWheel.Acceptance;

internal static class Program
{
    private const string Usage = """
        DropWheel.Acceptance: drives the M0 flow with real mouse and keyboard input.

          DropWheel.Acceptance host [--explorer]   DropTargetHost alone: drop on a sector, off the wheel,
                                                   on the centre, on a disabled sector
          DropWheel.Acceptance hooks [--explorer]  DragMonitor: drag phases, Shift / Ctrl+Alt / Win taps, long hold
          DropWheel.Acceptance fileops             ShellFileOperations: recycle, move with a collision
          DropWheel.Acceptance app --exe <DropWheel.exe> [--root <folder>] [--explorer]
                                                   the M0 flow against the real app: JPG to PNG, Undo,
                                                   then a release off the wheel. --root is the package's
                                                   install folder when --exe is the MSIX execution alias
          DropWheel.Acceptance --dragsource <file> --at X,Y   (internal) WinForms drag source
          DropWheel.Acceptance --locate <file>                (internal) hook-free Explorer item locator
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = new UTF8Encoding(false);

        if (ArgValue(args, "--dragsource") is { } file)
        {
            var at = (ArgValue(args, "--at") ?? "100,100").Split(',');
            Application.Run(new DragSourceForm(file, new Point(int.Parse(at[0]), int.Parse(at[1]))));
            return 0;
        }

        if (ArgValue(args, "--locate") is { } locate)
        {
            Log.Open("locate");
            return ExplorerSource.LocateMain(locate);
        }

        var explorer = args.Contains("--explorer");
        switch (args.FirstOrDefault())
        {
            case "host":
                Log.Open("host");
                return HostScenario.Run(explorer);
            case "hooks":
                Log.Open("hooks");
                return HooksScenario.Run(explorer);
            case "fileops":
                Log.Open("fileops");
                return FileOpsScenario.Run();
            case "app" when ArgValue(args, "--exe") is { } exe:
                Log.Open("app");
                return AppScenario.Run(Path.GetFullPath(exe), explorer, ArgValue(args, "--root"));
            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
