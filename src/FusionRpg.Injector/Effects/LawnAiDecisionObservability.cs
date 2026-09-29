using FusionRpg.Contracts;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Combat;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.5, spec-decision-inspector.md §4–§5): the lawn's
/// adapter over the Core ring — the recording sink the lawn decision host hands its records to, plus
/// the per-actor key derivation and the membership edge the ring's live index is withdrawn on.
///
/// <para><b>The ring's LOGIC is Core</b> (<see cref="AiDecisionRing"/>, tested by CI); this class holds
/// only what needs the live injector: the switch read, the ptr→actor-key derivation, and the death
/// edge. That split is the program's own rule 4 — <i>"Logic lives in Core, because CI never builds the
/// injector"</i>.</para>
///
/// <para><b>The membership edge is <see cref="InjectorEntityRegistry.Remove"/>/<see cref="Clear"/>,
/// not <c>MatchHost.Runtime.MembershipChanged</c>, and the reason is measurable.</b> The spec suggested
/// copying the <c>MembershipChanged</c> consumer, but that event's <c>Cleared</c> transition is raised
/// by <c>UniqueBindings.ClearInstance</c> — a <b>unique binding</b> lifecycle edge, not a general
/// death edge, so it never fires for the general creatures the inspector exists to explain. The
/// registry is the lawn's real per-actor death edge and already covers both sides:
/// <c>GameHooks</c>' plant death postfix calls <c>Remove</c>, and <c>NoteZombieDead</c> calls it on
/// every death-animation frame (its own comment records why: a board resync re-adds a dying zombie).
/// <see cref="InjectorEntityRegistry.Clear"/> is the match-end edge. Hanging the ring off the registry
/// therefore reaches every edge the spec enumerates through ONE already-live mechanism instead of
/// adding a second subscription to a different concept.</para>
///
/// <para><b>Spawn is deliberately NOT an edge here.</b> <see cref="InjectorEntityRegistry.Add"/> is not
/// a spawn signal: <c>Resync</c> clears the registry and re-<c>Add</c>s every live actor every
/// <c>ResyncFrames</c>, so treating it as one would wipe a long-lived actor's last decision roughly
/// every four seconds — the opposite of the property the last-per-actor index exists for. A genuinely
/// new ptr has no entry to clear, and a REUSED ptr's entry was already cleared by its own death
/// (<c>Remove</c> runs before the ptr can be handed out again), which is the order-independence the
/// Core ring's own tests pin.</para>
///
/// <para><b>It reads real decisions and never fabricates one.</b> <see cref="Record"/> stores the
/// record it is handed and nothing else; the read accessors return what is stored, and
/// <see cref="LastFor"/> returns <c>null</c> for an unknown actor rather than a synthesised
/// "what it would do". No read triggers a decision. That is the Game Injector Debug scope rule
/// (<c>live-probe-standard.md</c>): this ring proves what the injector's AI DECIDED, never that a cast
/// resolved.</para>
/// </summary>
public sealed class LawnAiDecisionObservability : IAiDecisionSink
{
    /// <summary>The one instance a lawn host should hold. Stateless: the ring is static, because the
    /// registry's death edge is static and both must address the same store.</summary>
    public static readonly LawnAiDecisionObservability Sink = new();

    static readonly AiDecisionRing Ring = new();

    LawnAiDecisionObservability() { }

    /// <summary>
    /// Records one lawn decision, or does nothing at all when the switch is off. The gate lives HERE,
    /// not at the call site: the lawn host holds a sink unconditionally, so "hidden by default" and
    /// "turning it off mid-match stops new records while existing entries stay readable" (spec §4's
    /// trigger table) are both this method's behaviour. Off costs one boolean.
    /// </summary>
    public void Record(in AiDecisionRecord record)
    {
        if (!AiInspectFeature.Enabled) return;
        Ring.Record(record);
    }

    /// <summary>Recent lawn decisions, oldest first — a copy, so a reader can neither evict from the
    /// ring nor reorder it.</summary>
    public static IReadOnlyList<AiDecisionRecord> Recent() => Ring.Recent();

    /// <summary>The last decision indexed for one actor, or <c>null</c> — never a synthesised record.
    /// The key is <see cref="ActorKeyOf(IntPtr)"/>'s.</summary>
    public static AiDecisionRecord? LastFor(string actorKey) => Ring.LastFor(actorKey);

    /// <summary>The membership edge (death, and match end via <see cref="Clear"/>). Both edges reach
    /// the same state, which is why the ring's own method takes no "which edge" argument.</summary>
    public static void ForgetActor(string actorKey) => Ring.ForgetActor(actorKey);

    /// <summary>Match ends / board reset: the ring and its index together.</summary>
    public static void Clear() => Ring.Clear();

    /// <summary>
    /// The ONE ptr→actor-key derivation, so the lawn host that feeds this sink and the registry that
    /// withdraws it cannot drift apart — a key built two ways would leave the index entry stranded
    /// under a key nobody removes. Same shape every other per-actor key in the injector uses
    /// (<c>entity:{normalized-ptr}</c>), because the ring's records are keyed by the same actor
    /// identity the effect layer grants against.
    /// </summary>
    public static string ActorKeyOf(IntPtr ptr) =>
        ptr == IntPtr.Zero ? "" : EffectOwnerKeys.Entity(CombatPtr.Normalize(ptr.ToString("X")));

    /// <inheritdoc cref="ActorKeyOf(IntPtr)"/>
    public static string ActorKeyOf(string? ptrHex) =>
        string.IsNullOrWhiteSpace(ptrHex) ? "" : EffectOwnerKeys.Entity(CombatPtr.Normalize(ptrHex));
}
