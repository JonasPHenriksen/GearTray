using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GearTray.Util;

/// <summary>
/// Download and upload speed in MB/s (10⁶ bytes), from Windows' own counters of the network adapters that have a
/// default gateway (the ones that reach the internet; virtual and loopback adapters would count traffic twice).
/// Call from one thread at a time, about once a second.
/// </summary>
public static class NetworkSpeed
{
    private static long _received, _sent, _at;
    private static double? _down, _up;

    public static (double? Down, double? Up) Read()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch (NetworkInformationException) { return (_down, _up); }

        long now = Stopwatch.GetTimestamp();
        if (_at != 0)
        {
            double seconds = (now - _at) / (double)Stopwatch.Frequency;
            if (seconds < 0.5) return (_down, _up);
            _down = Math.Max(0, received - _received) / seconds / 1e6;
            _up = Math.Max(0, sent - _sent) / seconds / 1e6;
        }
        (_received, _sent, _at) = (received, sent, now);
        return (_down, _up);
    }
}
