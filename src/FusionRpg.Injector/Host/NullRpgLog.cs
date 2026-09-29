namespace FusionRpg.Injector.Host;

/// <summary>No-op log used before host Initialize (should not happen in normal boot).</summary>
public sealed class NullRpgLog : IRpgLog
{
    public static readonly NullRpgLog Instance = new();
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
}
