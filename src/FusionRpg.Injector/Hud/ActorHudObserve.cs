namespace FusionRpg.Injector.Hud;

/// <summary>Attach nested <c>actorHud</c> to observe row dictionaries.</summary>
public static class ActorHudObserve
{
    public static void AttachRow(Dictionary<string, object> row, string ptrHex)
    {
        try
        {
            var snapshot = ActorHudCache.GetOrBuild(ptrHex);
            if (snapshot == null) return;
            row["actorHud"] = FusionRpg.Core.Hud.ActorHudWireSerializer.ToDictionary(snapshot);
        }
        catch { }
    }
}
