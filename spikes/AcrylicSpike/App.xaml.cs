using Microsoft.UI.Xaml;

namespace AcrylicSpike;

public partial class App : Application
{
    private Runner? _runner;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Info($"UNHANDLED {e.Exception}");
            e.Handled = true;
            Environment.Exit(3);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = Options.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Log.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheelSpike"), $"acrylic-{options.Variant.ToString().ToLowerInvariant()}{(options.DefaultConfig ? "-defaultconfig" : "")}");
        _runner = new Runner(options);
        _ = _runner.RunAsync();
    }
}
