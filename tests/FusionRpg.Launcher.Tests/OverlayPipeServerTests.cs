using System.IO.Pipes;
using System.Text;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

[Trait("VerificationId", "launcher.overlay-pipe")]
public class OverlayPipeServerTests
{
    [Theory]
    [InlineData("toggle", OverlayPipeCommand.Toggle)]
    [InlineData("ping", OverlayPipeCommand.Ping)]
    [InlineData("hide", OverlayPipeCommand.Hide)]
    public void ParseCommand_accepts_every_shipped_verb(string line, OverlayPipeCommand expected)
    {
        Assert.Equal(expected, OverlayPipeServer.ParseCommand(line));
    }

    [Theory]
    [InlineData("TOGGLE")]
    [InlineData("Toggle")]
    [InlineData("  toggle  ")]
    [InlineData("toggle\r")]
    [InlineData("toggle\n")]
    public void ParseCommand_is_case_and_whitespace_tolerant(string line)
    {
        Assert.Equal(OverlayPipeCommand.Toggle, OverlayPipeServer.ParseCommand(line));
    }

    [Theory]
    [InlineData("HIDE")]
    [InlineData("Hide")]
    [InlineData("  hide  ")]
    public void ParseCommand_is_case_and_whitespace_tolerant_for_hide(string line)
    {
        Assert.Equal(OverlayPipeCommand.Hide, OverlayPipeServer.ParseCommand(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wat")]
    [InlineData("toggle now")]
    [InlineData("toggle;ping")]
    [InlineData("show")]
    [InlineData("../../etc/passwd")]
    public void ParseCommand_ignores_junk(string? line)
    {
        Assert.Equal(OverlayPipeCommand.None, OverlayPipeServer.ParseCommand(line));
    }

    [Fact]
    public void ParseCommand_rejects_an_oversize_line()
    {
        var line = new string('a', OverlayPipeServer.MaxLineLength + 1);
        Assert.Equal(OverlayPipeCommand.None, OverlayPipeServer.ParseCommand(line));
    }

    [Fact]
    public void ParseCommand_rejects_non_ascii()
    {
        Assert.Equal(OverlayPipeCommand.None, OverlayPipeServer.ParseCommand("tögglé"));
    }

    [Fact]
    public void ParseCommand_never_throws()
    {
        var record = Record.Exception(() =>
        {
            OverlayPipeServer.ParseCommand("\0\0\0");
            OverlayPipeServer.ParseCommand("\u0000toggle");
            OverlayPipeServer.ParseCommand(new string('\n', 200));
        });
        Assert.Null(record);
    }

    [Fact]
    public void PipeName_is_the_documented_local_name()
    {
        // Spec: \\.\pipe\FusionRpg.Overlay — the server registers the bare name.
        Assert.Equal("FusionRpg.Overlay", OverlayPipeServer.PipeName);
    }
}

/// <summary>
/// Real pipe I/O, not just parsing — a command written by a client has to reach the handler.
/// Each test gets its own pipe name so a launcher running on the dev machine can't collide.
/// </summary>
public class OverlayPipeServerRoundTripTests
{
    static string UniquePipe() => "FusionRpg.Overlay.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Waits until the listener has actually claimed the name, so ownership races are gone.</summary>
    static bool WaitForPipe(string pipeName, int timeoutMs = 3000)
    {
        var path = @"\\.\pipe\" + pipeName;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static bool Send(string pipeName, string line, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            var bytes = Encoding.ASCII.GetBytes(line + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static OverlayPipeCommand? Await(Func<OverlayPipeCommand?> read, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var v = read();
            if (v.HasValue) return v;
            Thread.Sleep(10);
        }
        return null;
    }

    [Fact]
    public void A_toggle_written_by_a_client_reaches_the_handler()
    {
        var pipeName = UniquePipe();
        OverlayPipeCommand? seen = null;
        using var server = new OverlayPipeServer(c => seen = c, _ => { }, pipeName);
        server.Start();

        Assert.True(Send(pipeName, "toggle"), "client could not connect to the listener");
        Assert.Equal(OverlayPipeCommand.Toggle, Await(() => seen));
    }

    [Fact]
    public void The_listener_survives_junk_and_still_serves_the_next_command()
    {
        var pipeName = UniquePipe();
        var commands = new List<OverlayPipeCommand>();
        using var server = new OverlayPipeServer(c => { lock (commands) commands.Add(c); }, _ => { }, pipeName);
        server.Start();

        Assert.True(Send(pipeName, "not-a-verb"));
        Assert.True(Send(pipeName, "toggle"));

        var got = Await(() =>
        {
            lock (commands) return commands.Count > 0 ? commands[0] : (OverlayPipeCommand?)null;
        });
        Assert.Equal(OverlayPipeCommand.Toggle, got);
        lock (commands) Assert.Single(commands); // the junk line dispatched nothing
    }

    [Fact]
    public void Dispose_stops_the_listener()
    {
        var pipeName = UniquePipe();
        var server = new OverlayPipeServer(_ => { }, _ => { }, pipeName);
        server.Start();
        Assert.True(Send(pipeName, "ping"), "listener should accept before dispose");

        server.Dispose();
        Thread.Sleep(200);
        Assert.False(Send(pipeName, "toggle", timeoutMs: 300), "listener should be gone after dispose");
    }

    // ---- Prove-It: a second launcher used to spin the accept loop and log twice a second ----

    [Fact]
    public void A_second_listener_on_the_same_pipe_does_not_spam_the_log()
    {
        var pipeName = UniquePipe();
        using var owner = new OverlayPipeServer(_ => { }, _ => { }, pipeName);
        owner.Start();
        Assert.True(WaitForPipe(pipeName), "the first listener never claimed the pipe");

        var logs = new List<string>();
        using var loser = new OverlayPipeServer(_ => { }, m => { lock (logs) logs.Add(m); }, pipeName);
        loser.Start();
        Thread.Sleep(1200); // long enough for several retries at the old 500 ms cadence

        lock (logs)
            Assert.True(logs.Count <= 1,
                $"a launcher that cannot own the pipe must say so once, not once per retry (got {logs.Count})");
    }

    [Fact]
    public void A_waiting_listener_takes_over_when_the_owner_stops()
    {
        var pipeName = UniquePipe();
        var owner = new OverlayPipeServer(_ => { }, _ => { }, pipeName);
        owner.Start();
        Assert.True(WaitForPipe(pipeName), "the first listener never claimed the pipe");

        OverlayPipeCommand? seen = null;
        using var waiter = new OverlayPipeServer(c => seen = c, _ => { }, pipeName, busyRetryMs: 50);
        waiter.Start();
        Thread.Sleep(150); // let it discover the pipe is taken

        owner.Dispose();

        // The survivor should claim the name and serve the button, not stay parked forever.
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (DateTime.UtcNow < deadline && seen == null)
        {
            Send(pipeName, "toggle", timeoutMs: 200);
            Thread.Sleep(50);
        }
        Assert.Equal(OverlayPipeCommand.Toggle, seen);
    }

    // ---- Prove-It: a silent client used to park the only listener for the whole session ----

    [Fact]
    public void A_client_that_connects_without_sending_does_not_block_the_next_command()
    {
        var pipeName = UniquePipe();
        OverlayPipeCommand? seen = null;
        using var server = new OverlayPipeServer(c => seen = c, _ => { }, pipeName, readTimeoutMs: 200);
        server.Start();
        Assert.True(WaitForPipe(pipeName));

        // Connect and hold without ever writing a line.
        var squatter = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        squatter.Connect(2000);

        // The listener must time the squatter out and come back for the real button press.
        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (DateTime.UtcNow < deadline && seen == null)
        {
            Send(pipeName, "toggle", timeoutMs: 200);
            Thread.Sleep(50);
        }
        squatter.Dispose();

        Assert.Equal(OverlayPipeCommand.Toggle, seen);
    }

    [Fact]
    public void Start_is_idempotent()
    {
        var pipeName = UniquePipe();
        var logs = new List<string>();
        using var server = new OverlayPipeServer(_ => { }, m => { lock (logs) logs.Add(m); }, pipeName);
        server.Start();
        server.Start(); // a second accept loop would fight its own listener for the name
        Thread.Sleep(400);

        lock (logs)
            Assert.True(logs.Count == 0,
                "starting twice must not spawn a rival listener: " + string.Join(" | ", logs));
    }
}
