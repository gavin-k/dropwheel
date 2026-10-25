using System.Net;
using System.Net.Sockets;
using DropWheel.Core.Privacy;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class NetworkWatchTests
{
    [Fact]
    public void A_connection_is_counted()
    {
        using var watch = new NetworkWatch();
        var seen = new List<string>();
        watch.Seen += what =>
        {
            lock (seen)
            {
                seen.Add(what);
            }
        };
        var before = watch.Count;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }

        watch.Count.Should().BeGreaterThan(before);
        lock (seen)
        {
            seen.Should().Contain(s => s.StartsWith("ConnectStart", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_host_name_lookup_is_counted_with_the_name()
    {
        using var watch = new NetworkWatch();
        var seen = new List<string>();
        watch.Seen += what =>
        {
            lock (seen)
            {
                seen.Add(what);
            }
        };
        Dns.GetHostAddresses("localhost");

        lock (seen)
        {
            seen.Should().Contain("ResolutionStart localhost");
        }
    }
}
