using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// The injector end of the compiled push (spec-compiled-push.md, E19).
///
/// <para><b>A shim on purpose.</b> Everything decidable — revision negotiation, keeping what you hold
/// on an up-to-date reply, reseeding on match start, dropping at <c>board.end</c> — lives in
/// <see cref="AtomPushInstaller"/> in Core, where tests can reach it. What is left here is what only
/// exists inside the game process: the static holder, the Funnel, the clock, and the error sink. The
/// injector cannot host a test project (its host needs the game's interop assemblies), so logic that
/// stays here is logic nothing can check.</para>
///
/// <para>It holds no content rows: predicates arrive as flat int ops, values as curve-scaled bounds,
/// status and element names already interned.</para>
/// </summary>
public static class AtomPushReceiver
{
    static readonly object Gate = new();

    static AtomPushInstaller? _installer;

    static AtomPushInstaller Installer
    {
        get
        {
            if (_installer != null) return _installer;
            // The Funnel is the only Secondary path to the bag. Resolved per dispatch rather than
            // captured, because the bag is rebuilt by ResetForTests.
            _installer = new AtomPushInstaller(
                EffectRuntime.NowMs,
                grant => EffectRuntime.Bag.Funnel?.EnqueueModifier(grant) ?? false);
            return _installer;
        }
    }

    /// <summary>Null until a push with bindings arrives and a match has started.</summary>
    public static AtomRunner? Runner
    {
        get { lock (Gate) return Installer.Runner; }
    }

    public static long CatalogRevision
    {
        get { lock (Gate) return Installer.CatalogRevision; }
    }

    /// <summary>What the injector currently holds, for the Hello handshake.</summary>
    public static AtomPushHelloDto Hello()
    {
        lock (Gate) return Installer.Hello();
    }

    /// <summary>
    /// Install the defs and runner bindings from a delivered payload.
    ///
    /// <para><b>Grants are deliberately not applied here.</b> The command runner's existing grant
    /// loop owns that, and it does injector-only work this class must not duplicate or skip —
    /// resolving <c>entity:selected</c>, normalising the owner key, and refusing an
    /// <c>instance:</c> owner on the Hot path.</para>
    /// </summary>
    public static int Install(AtomPushDto payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        lock (Gate)
        {
            // Defs before anything else: a grant naming an effectId the catalog has never heard of
            // throws inside a later flush, far from here.
            foreach (var def in payload.Defs)
            {
                try { EffectRuntime.Bag.Catalog.Upsert(AtomPushCodec.ToDef(def)); }
                catch (Exception ex) { CheatState.Error("atom push def " + def.EffectId + ": " + ex.Message); }
            }

            return Installer.Install(payload);
        }
    }

    /// <summary>
    /// Feed one event to the runner. Called <b>before</b> the bag sees it: <c>EffectBag.OnEvent</c>
    /// flushes the Funnel inside itself, so a dispatch enqueued afterwards would wait for the next
    /// event. Secondary enqueues; the bag drains.
    /// </summary>
    public static void OnEvent(EffectEventDto ev, Core.Combat.BoardSnapshot? board)
    {
        AtomRunner? runner;
        lock (Gate) runner = Installer.Runner;
        if (runner is null) return;

        try
        {
            runner.OnEvent(RunnerEventMapper.From(ev, board));
        }
        catch (Exception ex)
        {
            // A bad binding must not take down the drain for every other effect on the board.
            CheatState.Error("atom runner: " + ex.Message);
        }
    }

    /// <summary>Match start: the runner is rebuilt against this match's seed and fresh counters.</summary>
    public static void NotifyMatchStart(string matchKey)
    {
        lock (Gate) Installer.BeginMatch(matchKey);
    }

    /// <summary><c>board.end</c> — compiled output is match-scoped, like the grant session.</summary>
    public static void Clear()
    {
        lock (Gate) Installer.Clear();
    }
}
