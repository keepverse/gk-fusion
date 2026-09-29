namespace FusionRpg.Injector.Bridges;

/// <summary>Profile-scoped CreateZombie factory (3.9 Melon: 4-arg SetZombie).</summary>
public static class CreateZombieSpawn
{
    public static Zombie? Set(int row, ZombieType type, float x, bool mindControl)
    {
        var inst = CreateZombie.Instance;
        return inst == null ? null : inst.SetZombie(row, type, x, mindControl);
    }

    public static Zombie? SetMindControl(int row, ZombieType type, float x, bool withEffect)
    {
        var inst = CreateZombie.Instance;
        return inst == null ? null : inst.SetZombieWithMindControl(row, type, x, withEffect);
    }
}
