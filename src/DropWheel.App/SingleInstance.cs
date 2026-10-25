using Windows.Win32;

namespace DropWheel.App;

/// <summary>
/// One DropWheel per session. A second launch hands over to the first and exits: a plain launch asks it
/// to show its task window, <c>--quit</c> asks it to exit. Named auto-reset events, so a signal sent
/// before the first instance starts listening is not lost.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\DropWheel.App";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show = new(false, EventResetMode.AutoReset, Name + ".Show");
    private readonly EventWaitHandle _quit = new(false, EventResetMode.AutoReset, Name + ".Quit");
    private readonly List<RegisteredWaitHandle> _waits = [];

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, Name, out var first);
        IsFirst = first;
    }

    public bool IsFirst { get; }

    /// <summary>Second instance: pass the request on. The first instance may bring a window forward, so it is allowed to.</summary>
    public void Signal(bool quit)
    {
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        (quit ? _quit : _show).Set();
    }

    /// <summary>First instance: <paramref name="show"/> and <paramref name="quit"/> run on a thread-pool thread.</summary>
    public void Listen(Action show, Action quit)
    {
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false));
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => quit(), null, Timeout.Infinite, executeOnlyOnce: false));
    }

    public void Dispose()
    {
        foreach (var wait in _waits)
        {
            wait.Unregister(null);
        }

        _show.Dispose();
        _quit.Dispose();
        _mutex.Dispose();
    }
}
