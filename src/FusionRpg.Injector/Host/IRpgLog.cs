namespace FusionRpg.Injector.Host;

/// <summary>Host-agnostic logger — BepInEx ManualLogSource or MelonLogger behind this.</summary>
public interface IRpgLog
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
