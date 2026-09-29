using System.Net;
using System.Net.Sockets;

namespace FusionRpg.Launcher.Services;

public class PortPicker
{
    public const int PreferredPort = 5088;
    // Structural (tunables-ssot.md T2) — network port range for the local server, not balance.
    public const int ScanStart = 5089;
    // Structural (tunables-ssot.md T2) — see ScanStart above.
    public const int ScanEnd = 5188;
    public const int SkipVitePort = 5173;

    static readonly HealthMonitor DefaultProbe = HealthMonitor.ForPortProbe();

    public sealed record Result(int Port, bool ReusedOurServer, string Url)
    {
        public static Result For(int port, bool reused) =>
            new(port, reused, $"http://127.0.0.1:{port}");
    }

    /// <summary>
    /// Prefer lastGood, then 5088; reuse if our server already owns it (GET /health ok);
    /// else scan 5089–5188 (skip 5173).
    /// </summary>
    public virtual Result Pick(int? lastGoodPort = null, Func<int, bool>? isPortFree = null, Func<int, bool>? isOwnedByOurServer = null)
    {
        isPortFree ??= IsTcpPortFree;
        isOwnedByOurServer ??= IsOwnedByFusionRpgServer;

        var candidates = new List<int>();
        if (lastGoodPort is >= 1 and <= 65535)
            candidates.Add(lastGoodPort.Value);
        if (!candidates.Contains(PreferredPort))
            candidates.Add(PreferredPort);
        for (var p = ScanStart; p <= ScanEnd; p++)
        {
            if (p == SkipVitePort) continue;
            if (!candidates.Contains(p))
                candidates.Add(p);
        }

        foreach (var port in candidates)
        {
            if (port == SkipVitePort) continue;
            if (isOwnedByOurServer(port))
                return Result.For(port, reused: true);
            if (isPortFree(port))
                return Result.For(port, reused: false);
        }

        throw new InvalidOperationException(
            $"No free TCP port on 127.0.0.1 in {PreferredPort} or {ScanStart}-{ScanEnd} (skipped {SkipVitePort}).");
    }

    public static bool IsTcpPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ours only when GET /health on that port returns ok=true.
    /// Avoids false reuse when a stranger holds the port while FusionRpg.Server listens elsewhere.
    /// </summary>
    public static bool IsOwnedByFusionRpgServer(int port) =>
        DefaultProbe.LooksLikeOurServer(port);
}
