namespace FusionRpg.Injector.Bridges;

/// <summary>Profile-scoped CreateZombie factory (3.8.1: Bep 4-arg / Melon 5-arg with isIdle).</summary>
public static class CreateZombieSpawn
{
    public static Zombie? Set(int row, ZombieType type, float x, bool mindControl)
    {
        var inst = CreateZombie.Instance;
        if (inst == null) return null;
#if FUSIONRPG_MELON
        return inst.SetZombie(row, type, x, false, mindControl);
#else
        return inst.SetZombie(row, type, x, mindControl);
#endif
    }

    public static Zombie? SetMindControl(int row, ZombieType type, float x, bool withEffect)
    {
        var inst = CreateZombie.Instance;
        return inst == null ? null : inst.SetZombieWithMindControl(row, type, x, withEffect);
    }
}
