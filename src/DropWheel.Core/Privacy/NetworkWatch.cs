using System.Diagnostics.Tracing;

namespace DropWheel.Core.Privacy;

/// <summary>
/// Counts the network requests this process makes through .NET, from the runtime's own events: every
/// connection a socket opens and every host name looked up, so Settings can show a number instead of a
/// promise. Native code that opened sockets of its own would not be seen.
/// </summary>
public sealed class NetworkWatch : EventListener
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Raised on the thread that made the request, with what it was, such as "ResolutionStart example.com".</summary>
    public event Action<string>? Seen;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // The base constructor calls this for sources that already exist, before this class's fields are set.
        if (eventSource.Name is "System.Net.Sockets" or "System.Net.NameResolution")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is "ConnectStart" or "ResolutionStart")
        {
            Interlocked.Increment(ref _count);
            Seen?.Invoke($"{eventData.EventName} {eventData.Payload?.FirstOrDefault()}");
        }
    }
}
