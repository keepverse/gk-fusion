using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FusionRpg.Launcher.Services;

/// <summary>
/// Verbs the in-game button may send. Anything else is ignored. Deliberately only what the
/// injector actually sends — a wire protocol with unreachable verbs is untested surface, and
/// wave 2 can add its own when something calls them.
/// </summary>
public enum OverlayPipeCommand
{
    None = 0,
    Toggle,
    /// <summary>Availability probe — answers "a host is listening" and must not move the overlay.</summary>
    Ping,
    /// <summary>
    /// Close the overlay window (rift-gate overlay-hide: the page's Leave control). One more route to
    /// the SAME hide the Esc key uses — it is not a second behaviour, and it never touches the story.
    /// </summary>
    Hide
}

/// <summary>
/// Listens on a local named pipe for the injector's overlay button. One message per connection;
/// the handler runs on the caller's thread pool, so the owner marshals to the UI thread and calls
/// the same toggle path as the hotkey — this class never shows or hides anything itself.
/// </summary>
public sealed class OverlayPipeServer : IDisposable
{
    /// <summary>Bare name; the full path is <c>\\.\pipe\FusionRpg.Overlay</c>.</summary>
    public const string PipeName = "FusionRpg.Overlay";

    /// <summary>Longest command line accepted. Anything longer is junk, not a verb.</summary>
    public const int MaxLineLength = 64;

    /// <summary>Wait between attempts while another launcher owns the pipe. Long: nobody is waiting on us.</summary>
    public const int DefaultBusyRetryMs = 5_000;

    /// <summary>
    /// How long a connected client gets to send its line. Only one server instance exists, so a
    /// client that connects and never writes would otherwise park the listener for the session.
    /// </summary>
    public const int DefaultReadTimeoutMs = 2_000;

    readonly Action<OverlayPipeCommand> _onCommand;
    readonly Action<string> _log;
    readonly string _pipeName;
    readonly int _busyRetryMs;
    readonly int _readTimeoutMs;
    int _started;
    readonly CancellationTokenSource _cts = new();
    bool _disposed;

    /// <param name="pipeName">Overridable so tests get their own pipe instead of the live one.</param>
    /// <param name="busyRetryMs">How long to wait between attempts when another launcher owns the pipe.</param>
    public OverlayPipeServer(
        Action<OverlayPipeCommand> onCommand,
        Action<string> log,
        string? pipeName = null,
        int busyRetryMs = DefaultBusyRetryMs,
        int readTimeoutMs = DefaultReadTimeoutMs)
    {
        _onCommand = onCommand;
        _log = log;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName!;
        _busyRetryMs = busyRetryMs > 0 ? busyRetryMs : DefaultBusyRetryMs;
        _readTimeoutMs = readTimeoutMs > 0 ? readTimeoutMs : DefaultReadTimeoutMs;
    }

    /// <summary>Maps one received line to a verb. Pure — junk returns <see cref="OverlayPipeCommand.None"/>.</summary>
    public static OverlayPipeCommand ParseCommand(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return OverlayPipeCommand.None;
        if (line.Length > MaxLineLength) return OverlayPipeCommand.None;

        var verb = line.Trim();
        foreach (var ch in verb)
        {
            if (ch > 0x7F || char.IsControl(ch)) return OverlayPipeCommand.None;
        }

        return verb.ToLowerInvariant() switch
        {
            "toggle" => OverlayPipeCommand.Toggle,
            "ping" => OverlayPipeCommand.Ping,
            "hide" => OverlayPipeCommand.Hide,
            _ => OverlayPipeCommand.None
        };
    }

    /// <summary>Starts the accept loop. Idempotent; failures are logged, never thrown.</summary>
    public void Start()
    {
        // A second loop would race its own listener for the name and report it as a rival launcher.
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    async Task AcceptLoopAsync(CancellationToken token)
    {
        // Claiming the name and serving a connection fail for different reasons and deserve
        // different handling: a second launcher can never claim the name while the first lives,
        // so retrying it on the connection cadence would log twice a second for the whole session.
        var busyLogged = false;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                if (!busyLogged)
                {
                    busyLogged = true;
                    _log($"Overlay pipe: another launcher already owns {_pipeName} " +
                         $"({ex.GetType().Name}) — its in-game button stays in charge until it exits.");
                }
                try { await Task.Delay(_busyRetryMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (busyLogged)
            {
                busyLogged = false;
                _log($"Overlay pipe: took over {_pipeName} — the in-game button reaches this launcher now.");
            }

            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(_readTimeoutMs);
                    var line = await ReadLineAsync(pipe, readCts.Token).ConfigureAwait(false);
                    var command = ParseCommand(line);
                    if (command == OverlayPipeCommand.None)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            _log($"Overlay pipe: ignored unknown command ({Describe(line)}).");
                        continue;
                    }
                    _onCommand(command);
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) return;
                    // Read timeout only — drop this client and get back to listening.
                    _log("Overlay pipe: a client connected without sending a command — dropped.");
                    continue;
                }
                catch (Exception ex)
                {
                    _log("Overlay pipe: " + ex.Message);
                    // A broken connection must not kill the listener; pause so a hard failure can't spin.
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }

    /// <summary>Reads at most one capped line. Never allocates on a hostile sender's terms.</summary>
    static async Task<string> ReadLineAsync(Stream pipe, CancellationToken token)
    {
        var buffer = new byte[MaxLineLength + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read), token).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                read = newline;
                break;
            }
        }
        return read <= 0 ? "" : Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>Keeps a junk line out of the log verbatim — length only, so a sender can't write our log.</summary>
    static string Describe(string line) => $"{line.Length} bytes";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        // Unblock WaitForConnectionAsync: cancellation alone can leave the listener parked on Windows.
        try
        {
            using var poke = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            poke.Connect(100);
        }
        catch { }
        try { _cts.Dispose(); } catch { }
    }
}
