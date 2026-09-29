using FusionRpg.Core.Commanders;

namespace FusionRpg.Injector.Commanders;

/// <summary>Injector entry for synchronous snapshot build at <c>board.start</c>.</summary>
public static class MatchCommanderSnapshotSource
{
    public static MatchCommanderSnapshot BuildFromSessionCache() =>
        MatchCommanderSessionCache.BuildFromSessionCache();

    public static bool LastBuildUsedFallback => MatchCommanderSessionCache.LastBuildUsedFallback;
}
