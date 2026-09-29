using System.Collections.Concurrent;
using System.Text.Json;
using FusionRpg.CheatCore;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Overlay;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Vfx;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Fx;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Hud;
using FusionRpg.Injector.Stats;
using FusionRpg.Core.Time;

namespace FusionRpg.Injector;

/// <summary>Main-thread drain for SignalR/HTTP cheat commands from the web/server.</summary>
public static class CheatCommandRunner
{
    static readonly ConcurrentQueue<CommandDto> Pending = new();
    static readonly ConcurrentDictionary<string, byte> SeenIds = new();
    // Structural (tunables-ssot.md T2) — dedupe-cache memory bound, not balance.
    const int SeenCap = 512;

    public static void Enqueue(CommandDto cmd)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Name)) return;
        if (!string.IsNullOrWhiteSpace(cmd.Id))
        {
            if (!SeenIds.TryAdd(cmd.Id, 0)) return;
            if (SeenIds.Count > SeenCap)
            {
                // Bound memory: drop oldest-ish by clearing when oversized.
                SeenIds.Clear();
                SeenIds.TryAdd(cmd.Id, 0);
            }
        }
        Pending.Enqueue(cmd);
        try { RpgHost.Log.Info("[cheat-cmd] queued " + cmd.Name + (string.IsNullOrEmpty(cmd.Id) ? "" : " id=" + cmd.Id)); }
        catch { }
    }

    public static void Drain()
    {
        while (Pending.TryDequeue(out var cmd))
        {
            try { Run(cmd); }
            catch (Exception ex) { CheatState.Error("cmd " + cmd.Name + ": " + ex.Message); }
        }
    }

    static void Run(CommandDto cmd)
    {
        var name = cmd.Name.Trim().ToLowerInvariant();
        if (name is "reload-stats" or "stats.reload")
        {
            if (RpgHost.Client != null)
                CheatState.PullFromServer(RpgHost.Client.Stats);
            return;
        }
        if (name is "pvz.stats.reload")
        {
            if (RpgHost.Client != null)
                _ = RpgHost.Client.RefreshPvzStatsAsync();
            return;
        }
        if (name is "aptitudes.allocation.reload")
        {
            if (RpgHost.Client != null)
            {
                _ = RpgHost.Client.RefreshCommanderAllocationAsync();
                // unique-lawn-wire (AS-1.1): a Bound specimen's own AptitudesUpdated broadcast (scope
                // "unique") rides the SAME command as commander/species -- one reload signal, three
                // caches, never a fourth SignalR event.
                _ = RpgHost.Client.RefreshUniqueAptitudesAsync();
                _ = RpgHost.Client.RefreshCommanderSnapshotCacheAsync();
            }
            return;
        }
        if (name is "commander.snapshot.reload")
        {
            if (RpgHost.Client != null)
                _ = RpgHost.Client.RefreshCommanderSnapshotCacheAsync();
            return;
        }
        if (name is "passive-tree.bound-atoms.reload")
        {
            if (RpgHost.Client != null)
                _ = RpgHost.Client.RefreshTreeBoundAtomsAsync();
            return;
        }
        if (name is "lawn-deploy.roster.reload")
        {
            if (RpgHost.Client != null)
                _ = RpgHost.Client.RefreshLawnDeployRosterCacheAsync();
            return;
        }
        if (name is "power.index.reload")
        {
            if (RpgHost.Client != null)
                _ = RpgHost.Client.RefreshPowerIndexAsync();
            return;
        }
        // rift-gate overlay-hide: the web FE's Leave control closes the overlay window. This rides the
        // same drain as the refresh commands above, which is why the vocabulary lives in
        // OverlayCommandNames rather than as a bare literal. It is story-neutral: nothing here (or in
        // OverlaySwitch.RequestHide) touches the onboarding/story ledger, so closing the window can
        // never be recorded as finishing or skipping the prologue.
        if (name is OverlayCommandNames.Hide)
        {
            OverlaySwitch.RequestHide();
            return;
        }
        if (name is "pvz.spawn.extra")
        {
            var p = PayloadJson(cmd.Payload);
            var typeId = IntProp(p, "typeId", CheatState.ManualTypeId);
            int? row = null;
            int? col = null;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("row", out var rowEl) && rowEl.ValueKind == JsonValueKind.Number && rowEl.TryGetInt32(out var rv))
                row = rv;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("col", out var colEl) && colEl.ValueKind == JsonValueKind.Number && colEl.TryGetInt32(out var cv))
                col = cv;
            var reason = Str(p, "reason");
            var corr = Str(p, "correlationId");
            var side = Str(p, "side") ?? "zombie";
            var instanceId = Str(p, "instanceId");
            var loadoutJson = LoadoutJsonFromPayload(p);
            var empireId = Str(p, "empireId");
            var controller = Str(p, "controller");
            CheatActions.SpawnExtra(side, typeId, col, row, reason, corr, instanceId, loadoutJson, empireId, controller);
            return;
        }
        if (name is "unique.binding.clear")
        {
            var p = PayloadJson(cmd.Payload);
            Match.MatchHost.TryClearUniquePending(Str(p, "instanceId"), Str(p, "correlationId"));
            return;
        }
        if (name is "effects.reload")
        {
            Effects.EffectRuntime.ReplaceCatalog(FusionRpg.Core.Effects.EffectAtomCatalog.CreateAll());
            return;
        }
        if (name is "effects.grants.apply")
        {
            RunEffectsGrantsApply(PayloadJson(cmd.Payload));
            return;
        }
        if (name is "cheat.set" or "cheat.apply-snapshot")
        {
            var json = PayloadJson(cmd.Payload);
            TakeProbeContext(json, "web");
            if (json.ValueKind != JsonValueKind.Undefined && json.ValueKind != JsonValueKind.Null)
                CheatState.ApplySnapshot(json);
            CheatState.Note("web: applied cheat snapshot");
            CheatState.EmitInject("web", "action", action: "apply-snapshot");
            return;
        }
        if (name is "cheat.probe-begin")
        {
            var p = PayloadJson(cmd.Payload);
            var probeId = Str(p, "probeId") ?? Guid.NewGuid().ToString("N");
            var packId = Str(p, "packId");
            CheatState.BeginProbe(probeId, packId);
            CheatState.EmitInject("pack", "pack-step", action: "probe-begin", id: packId);
            return;
        }
        if (name is "cheat.probe-end")
        {
            CheatState.EndProbe(Str(PayloadJson(cmd.Payload), "reason") ?? "web");
            return;
        }
        if (name is "cheat.probe-run")
        {
            RunLocalPack(PayloadJson(cmd.Payload));
            return;
        }
        // solid-remediation 2026-09-17 (vfx.tick, owner ruling: "check it can improve or else. Else we
        // will disable a default and add user setting on the web FE"). The world HUD walk is the
        // dominant vfx.tick cost -- ActorHudPool's own measurements put it at 1,899 ms per five-second
        // window unlimited, 483 ms at the shipped budget of 60, on a ~290-entity board. It now ships
        // OFF by default and this is how the player turns it back on.
        //
        // A plain runtime flag, not a cheat and not tuning: it is a per-machine capability choice the
        // player makes for themselves, so it has no place in the cheat registry (which the injector
        // publishes) or in actor-hud.v{n}.json (which is balance). The FE persists the choice in
        // the server and re-sends it on connect. That makes the preference follow its player rather
        // than a particular browser or game launch.
        if (name is "hud.world")
        {
            var p = PayloadJson(cmd.Payload);
            var on = p.TryGetProperty("enabled", out var he) && he.GetBoolean();
            ActorHudPool.WorldHudEnabled = on;
            return;
        }
        if (name is "vfx.enabled")
        {
            var p = PayloadJson(cmd.Payload);
            var on = p.TryGetProperty("enabled", out var ve) && ve.GetBoolean();
            VfxDirector.SetVisualEffectsEnabled(on);
            return;
        }
        if (name is "cheat.toggle")
        {
            var p = PayloadJson(cmd.Payload);
            TakeProbeContext(p, "web");
            var id = Str(p, "id") ?? "";
            var on = p.TryGetProperty("enabled", out var en) && en.GetBoolean();
            var src = Str(p, "source") ?? (string.IsNullOrEmpty(CheatState.ActivePackId) ? "web" : "pack");
            if (!string.IsNullOrEmpty(id)) CheatState.SetToggle(id, on, src);
            return;
        }
        if (name is "cheat.clear-field")
        {
            var p = PayloadJson(cmd.Payload);
            TakeProbeContext(p, "web");
            var id = Str(p, "id") ?? "";
            var src = Str(p, "source") ?? "web";
            if (!string.IsNullOrEmpty(id))
            {
                CheatState.ClearField(id, src);
                if (id.StartsWith("A-", StringComparison.Ordinal))
                    CheatActions.PushScalesNow();
            }
            return;
        }
        if (name is "cheat.set-float")
        {
            var p = PayloadJson(cmd.Payload);
            TakeProbeContext(p, "web");
            var id = Str(p, "id") ?? "";
            var v = p.TryGetProperty("value", out var fv) ? fv.GetDouble() : 0d;
            var src = Str(p, "source") ?? (string.IsNullOrEmpty(CheatState.ActivePackId) ? "web" : "pack");
            if (!string.IsNullOrEmpty(id))
            {
                CheatState.SetFloat(id, v, src);
                // Tab A floats must affect living entities — set-float alone only updated registry.
                if (id.StartsWith("A-", StringComparison.Ordinal))
                    CheatActions.PushScalesNow();
            }
            return;
        }
        if (name is "cheat.action")
        {
            var p = PayloadJson(cmd.Payload);
            TakeProbeContext(p, "web");
            var action = Str(p, "action") ?? "";
            var src = Str(p, "source") ?? (string.IsNullOrEmpty(CheatState.ActivePackId) ? "web" : "pack");
            if (!string.IsNullOrEmpty(action))
                CheatState.EmitActionInject(action, src);
            RunAction(action, p);
            return;
        }
        if (name is LawnOrderHost.CommandName)
        {
            // combat-ai `commander-direct-orders` (CAI4.9): the Server relayed a player order. The verb is
            // a named constant rather than a bare literal, the same reason `OverlayCommandNames.Hide`
            // exists. Admission is the host's; this dispatch only hands it the payload.
            var p = PayloadJson(cmd.Payload);
            LawnOrderHost.Admit(
                Str(p, "matchKey"), Str(p, "actorKey"), Str(p, "actionId"), Str(p, "targetKey"));
            return;
        }
        if (name.StartsWith("debug.", StringComparison.Ordinal))
        {
            RunDebug(name, PayloadJson(cmd.Payload));
            return;
        }
        if (name.StartsWith("cheat.", StringComparison.Ordinal))
            RunAction(name["cheat.".Length..], PayloadJson(cmd.Payload));
    }

    static void RunDebug(string name, JsonElement p)
    {
        switch (name)
        {
            case "debug.run-steps":
                RunDebugSteps(p);
                break;
            case "debug.session":
                if (string.Equals(Str(p, "op"), "end", StringComparison.OrdinalIgnoreCase))
                    DebugRuntime.EndSession();
                else
                    DebugRuntime.StartSession(Str(p, "scenarioId"));
                break;
            case "debug.spawn-plant":
                DebugActions.SpawnPlant(p);
                break;
            case "debug.spawn-zombie":
                DebugActions.SpawnZombie(p);
                break;
            case "debug.spawn-bullet":
                DebugActions.SpawnBullet(p);
                break;
            case "debug.reset-mods":
                DebugActions.ResetMods();
                break;
            case "debug.set-mods":
                DebugActions.SetMods(p);
                break;
            case "debug.reapply":
                CheatActions.ReapplyAllLiving();
                break;
            case "debug.apply-status":
                DebugActions.ApplyStatus(p, method: true);
                break;
            case "debug.apply-status-float":
                DebugActions.ApplyStatus(p, method: false);
                break;
            case "debug.clear-status":
                DebugActions.ClearStatus(p);
                break;
            case "debug.arm":
                DebugRuntime.ApplyPayloadArm(p);
                DebugRuntime.Emit("debug.arm", new Dictionary<string, object>
                {
                    ["kind"] = Str(p, "kind") ?? ""
                });
                break;
            case "debug.disarm":
                DebugRuntime.DisarmAll();
                DebugRuntime.Emit("debug.disarm", new Dictionary<string, object>());
                break;
            case "debug.kill":
                DebugActions.Kill(p, plants: false);
                break;
            case "debug.kill-plant":
                DebugActions.Kill(p, plants: true);
                break;
            case "debug.wave-freeze":
                DebugActions.WaveFreeze(p.TryGetProperty("enabled", out var en) && en.ValueKind != JsonValueKind.False);
                break;
            case "debug.stress-fill":
                DebugActions.StressFill(p);
                break;
            case "debug.stress-clear":
                DebugActions.StressClear(p);
                break;
            case "debug.ensure-sun":
                DebugActions.EnsureSun(p.TryGetProperty("value", out var sv) && sv.TryGetSingle(out var sf) ? sf : 9999f);
                break;
            case "debug.enter-level":
                DebugActions.EnterLevel(p);
                break;
            case "debug.skip-setup":
                DebugActions.SkipSetup(p);
                break;
            case "debug.select":
                DebugActions.Select(p);
                break;
            case "debug.spawn-cell":
                if (p.TryGetProperty("col", out var c) && c.TryGetInt32(out var col)) CheatState.SpawnCol = col;
                if (p.TryGetProperty("row", out var r) && r.TryGetInt32(out var row)) CheatState.SpawnRow = row;
                break;
            case "debug.reset-board":
                CheatActions.DeleteAllPlants();
                CheatActions.DeleteAllZombies(); // also clears DeadZombies
                break;
            case "debug.clear-plants":
                CheatActions.DeleteAllPlants();
                break;
            case "debug.clear-zombies":
                CheatActions.DeleteAllZombies();
                break;
            case "debug.snapshot":
                DebugRuntime.Emit("debug.snapshot", DebugRuntime.Snapshot());
                break;
            case "debug.screenshot":
                ScreenshotCapture.TryArm(p);
                break;
            case "debug.inspect":
                ControlInspect.Run(p);
                break;
            case "debug.census":
                ControlInspect.RunCensus();
                break;
            case "debug.scan":
                ControlInspect.RunScan(p);
                break;
            case "debug.dump-all":
                ControlInspect.RunDumpAll(p);
                break;
            case "debug.evaluate-search":
                ControlInspect.RunEvaluateSearch(p);
                break;
            case "debug.evaluate-methods":
                ControlInspect.RunEvaluateMethods(p);
                break;
            case "debug.evaluate-call":
                ControlInspect.RunEvaluateCall(p);
                break;
            case "debug.evaluate-text":
                ControlInspect.RunEvaluateText(p);
                break;
            case "debug.click":
                ControlClick.Run(p);
                break;
            case "debug.act":
                ControlAct.Run(p);
                break;
            case "debug.cursor":
                ControlCursor.Run(p);
                break;
            case "debug.game-state":
                DebugActions.GameState();
                break;
            case "debug.leave-board":
                DebugLeaveBoard.Start();
                break;
            case "debug.ui-nav":
                DebugActions.UiNav(p);
                break;
            case "debug.board-stats":
            {
                var payload = DebugRuntime.BoardEntityStats();
                if (p.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                {
                    var tag = tagEl.GetString();
                    if (!string.IsNullOrEmpty(tag))
                        payload["tag"] = tag!;
                }

                DebugRuntime.Emit("debug.board-stats", payload);
                break;
            }
            case "debug.economy":
                DebugActions.Economy(p);
                break;
            case "debug.board-config":
                DebugActions.BoardConfigApply(p);
                break;
            case "debug.board-action":
                DebugActions.BoardAction(p);
                break;
            case "debug.spawn-grid":
                DebugActions.SpawnGrid(p);
                break;
            case "debug.clear-grid":
                DebugActions.ClearGrid(p);
                break;
            case "debug.set-box":
                DebugActions.SetBox(p);
                break;
            case "debug.grid-query":
                DebugActions.GridQuery(p);
                break;
            case "debug.ice-road":
                DebugActions.IceRoad(p);
                break;
            case "debug.effect.grant":
                RunEffectGrant(p);
                break;
            case "debug.effect.withdraw":
                Effects.EffectRuntime.Withdraw(Str(p, "grantId") ?? "");
                break;
            case "debug.effect.clear":
                Effects.EffectRuntime.ClearAll("debug");
                break;
            case "debug.effect.list":
            {
                var snap = Effects.EffectRuntime.Snapshot();
                DebugRuntime.Emit("debug.effect.list", new Dictionary<string, object>
                {
                    ["contractVersion"] = snap.ContractVersion,
                    ["defs"] = snap.Defs.Count,
                    ["grants"] = snap.Grants.Count,
                    ["grantIds"] = snap.Grants.Select(g => g.GrantId).ToList()
                });
                break;
            }
            case "debug.effect.fire-synthetic":
            {
                if (!TryResolveSyntheticEvent(p, out var ev))
                    break;
                var plan = Effects.EffectRuntime.FireSynthetic(ev);
                DebugRuntime.Emit("debug.effect.synthetic", new Dictionary<string, object>
                {
                    ["trigger"] = plan.Trigger,
                    ["actorPtr"] = ev.ActorPtr ?? "",
                    ["targetPtr"] = ev.TargetPtr ?? "",
                    ["actions"] = plan.Actions.Count,
                    ["skipped"] = plan.Skipped
                });
                break;
            }
            case "debug.effect.enqueue-delta":
                RunEnqueueDelta(p);
                break;
            case "debug.scope.start-own-side":
                RunScopeStartOwnSide(p);
                break;
            case "debug.scope.stop-own-side":
                FusionRpg.Injector.Effects.DebugScopeRuntime.StopOwnSide();
                break;
            case "debug.shield.grant":
                RunShieldGrant(p);
                break;
            case "debug.shield.clear":
                RunShieldClear(p);
                break;
            case "debug.shield.demo":
                RunShieldDemo(p);
                break;
            case "debug.shield.demo-all":
                RunShieldDemoAll(p);
                break;
            case "debug.shield.snapshot":
                RunShieldSnapshot(p);
                break;
            case "debug.shield.bar-status":
                RunShieldBarStatus(p);
                break;
            case "debug.effect.board-snapshot":
                EmitBoardSnapshot();
                break;
            case "debug.effect.dots":
                EmitStatus(includeLegacyDots: true);
                break;
            case "debug.effect.counters":
                EmitStatus(includeLegacyCounters: true);
                break;
            case "debug.status":
                EmitStatus();
                break;
            case "debug.status.apply":
                ApplyStatusL2(p);
                break;
            case "debug.actor-derived":
                HandleActorDerived(p);
                break;
            case "debug.combat.pin-element":
                HandleCombatPinElement(p);
                break;
            case "debug.combat.silence-vanilla":
                DebugCombatActions.SilenceVanilla(p);
                break;
            case "debug.combat.probe":
                DebugCombatActions.Probe(p);
                break;
            case "debug.combat.snapshot":
                DebugCombatActions.Snapshot(p);
                break;
            case "debug.fx.probe-shaders":
                RunShaderProbe();
                break;
            case "debug.fx.world-flash":
                RunWorldFlash(p);
                break;
            case "debug.fx.play":
                RunFxPlay(p);
                break;
            case "debug.fx.list":
                RunFxList();
                break;
            case "debug.fx.mute":
                RunFxMute(p, mute: true);
                break;
            case "debug.fx.unmute":
                RunFxMute(p, mute: false);
                break;
            case "debug.fx.state":
                RunFxState();
                break;
            default:
                CheatState.Error("unknown debug cmd: " + name);
                break;
        }
    }

    /// <summary>Execute scenario steps sequentially on the Unity main-thread drain (no same-frame fan-out race).</summary>
    static void RunDebugSteps(JsonElement p)
    {
        if (!p.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            CheatState.Error("debug.run-steps: missing steps[]");
            return;
        }

        var scenarioId = Str(p, "scenarioId");
        if (!string.IsNullOrEmpty(scenarioId) && !DebugRuntime.SessionActive)
            DebugRuntime.StartSession(scenarioId);

        var n = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var name = Str(step, "name") ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, "debug.run-steps", StringComparison.Ordinal))
            {
                CheatState.Error("debug.run-steps: nested run-steps ignored");
                continue;
            }

            var payload = step.TryGetProperty("payload", out var pl) ? pl : default;
            try
            {
                if (string.Equals(name, "pvz.spawn.extra", StringComparison.Ordinal))
                {
                    var typeId = payload.TryGetProperty("typeId", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
                    int? row = payload.TryGetProperty("row", out var r) && r.TryGetInt32(out var rv) ? rv : null;
                    int? col = payload.TryGetProperty("col", out var c) && c.TryGetInt32(out var cv) ? cv : null;
                    var reason = Str(payload, "reason") ?? "debug";
                    var corr = Str(payload, "correlationId") ?? Guid.NewGuid().ToString("N");
                    var side = Str(payload, "side") ?? "zombie";
                    var instanceId = Str(payload, "instanceId");
                    var loadoutJson = LoadoutJsonFromPayload(payload);
                    var empireId = Str(payload, "empireId");
                    var controller = Str(payload, "controller");
                    CheatActions.SpawnExtra(side, typeId, col, row, reason, corr, instanceId, loadoutJson, empireId, controller);
                }
                else if (name.StartsWith("debug.", StringComparison.Ordinal))
                    RunDebug(name, payload);
                else
                    CheatState.Error("debug.run-steps: unknown step " + name);
                n++;
            }
            catch (Exception ex)
            {
                CheatState.Error("debug.run-steps step " + name + ": " + ex.Message);
            }
        }

        DebugRuntime.Emit("debug.run-steps.done", new Dictionary<string, object>
        {
            ["scenarioId"] = scenarioId ?? DebugRuntime.ScenarioId ?? "",
            ["steps"] = n
        });
    }

    /// <summary>F8 / local: apply a named pack without server fan-out.</summary>
    public static void RunPackLocal(string packId)
    {
        var pack = ProbePacks.Get(packId);
        if (pack == null)
        {
            CheatState.Error("unknown pack " + packId);
            return;
        }
        var probeId = Guid.NewGuid().ToString("N");
        CheatState.BeginProbe(probeId, pack.Id);
        CheatState.EmitInject("pack", "pack-step", action: "probe-begin", id: pack.Id);
        foreach (var step in pack.Steps)
            ApplyStep(step, "pack");
        CheatState.Note($"pack {pack.Id} applied probeId={probeId}");
    }

    static void RunLocalPack(JsonElement p)
    {
        var packId = Str(p, "packId") ?? "";
        var probeId = Str(p, "probeId") ?? Guid.NewGuid().ToString("N");
        var pack = ProbePacks.Get(packId);
        if (pack == null)
        {
            CheatState.Error("unknown pack " + packId);
            return;
        }
        CheatState.BeginProbe(probeId, pack.Id);
        CheatState.EmitInject("pack", "pack-step", action: "probe-begin", id: pack.Id);
        foreach (var step in pack.Steps)
            ApplyStep(step, "pack");
    }

    static void ApplyStep(ProbeStep step, string source)
    {
        switch (step.Op.ToLowerInvariant())
        {
            case "toggle":
                if (!string.IsNullOrEmpty(step.Id))
                    CheatState.SetToggle(step.Id!, step.Enabled ?? true, source);
                break;
            case "set-float":
                if (!string.IsNullOrEmpty(step.Id))
                    CheatState.SetFloat(step.Id!, step.Value ?? 0, source);
                break;
            case "action":
                if (!string.IsNullOrEmpty(step.Action))
                {
                    CheatState.EmitActionInject(step.Action!, source);
                    var dict = new Dictionary<string, object?> { ["action"] = step.Action };
                    if (step.Which != null) dict["which"] = step.Which;
                    if (step.Value is { } v) dict["value"] = v;
                    if (step.Add is { } a) dict["add"] = a;
                    var json = JsonSerializer.Serialize(dict);
                    using var doc = JsonDocument.Parse(json);
                    RunAction(step.Action!, doc.RootElement.Clone());
                }
                break;
        }
    }

    static void TakeProbeContext(JsonElement p, string defaultSource)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        var probeId = Str(p, "probeId");
        if (!string.IsNullOrEmpty(probeId))
        {
            var packId = Str(p, "packId") ?? CheatState.ActivePackId;
            if (!string.Equals(CheatState.ActiveProbeId, probeId, StringComparison.Ordinal))
                CheatState.BeginProbe(probeId!, packId);
            else
            {
                CheatState.ActiveProbeUtc = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(packId)) CheatState.ActivePackId = packId;
            }
        }
        if (p.TryGetProperty("correlationId", out var c) && c.ValueKind == JsonValueKind.String)
            CheatState.ActiveCorrelationId = c.GetString();
        _ = defaultSource;
    }

    static void RunAction(string action, JsonElement p)
    {
        switch (action.ToLowerInvariant())
        {
            case "reapply":
            case "reapply-living":
                CheatActions.ReapplyAllLiving();
                break;
            case "push-now":
            case "push-scales":
                CheatActions.PushScalesNow();
                break;
            case "apply-plants":
                CheatActions.ApplyAbsolutesToSelectedOrAll("plant");
                break;
            case "apply-zombies":
                CheatActions.ApplyAbsolutesToSelectedOrAll("zombie");
                break;
            case "spawn-plant":
                CheatActions.SpawnPlant(IntProp(p, "type", CheatState.ManualTypeId));
                break;
            case "spawn-zombie":
                CheatActions.SpawnZombie(IntProp(p, "type", CheatState.ManualTypeId), BoolProp(p, "mindControl", false));
                break;
            case "delete-plants":
                CheatActions.DeleteAllPlants();
                break;
            case "delete-zombies":
            case "kill-zombies":
                CheatActions.KillAllZombies();
                break;
            case "hypno-all":
                CheatActions.HypnoAll();
                break;
            case "oneshot":
                CheatActions.OneShotSelected();
                break;
            case "economy":
                CheatActions.SetEconomy(
                    p.TryGetProperty("which", out var w) ? w.GetString() ?? "sun" : "sun",
                    p.TryGetProperty("value", out var v) ? (float)v.GetDouble() : 0,
                    BoolProp(p, "add", false));
                break;
            case "board-config":
                CheatActions.ApplyBoardConfig();
                break;
            case "load-board-config":
                CheatActions.LoadBoardConfigIntoCheats();
                break;
            case "summon":
                CheatActions.SummonWave(IntProp(p, "wave", 1));
                break;
            case "huge-wave":
                CheatActions.HugeWave();
                break;
            case "wave-timer":
                CheatActions.SetWaveTimer(p.TryGetProperty("value", out var wt) ? (float)wt.GetDouble() : 5f);
                break;
            case "recipes":
                CheatActions.DumpRecipes();
                break;
            case "almanac-dump-all":
                CheatActions.DumpAlmanacAll();
                break;
            case "basestats":
                CheatActions.DumpBaseStats();
                break;
            case "reinforce":
                CheatActions.ReinforceSelected();
                break;
            case "set-zombie-hp":
                CheatActions.SetSelectedZombieHealth(IntProp(p, "hp", 27000));
                break;
            case "spawn-pet":
                CheatActions.SpawnPet(IntProp(p, "type", 0));
                break;
            case "spawn-grid":
                CheatActions.SpawnGrid(IntProp(p, "type", 0));
                break;
            case "spawn-bucket":
                CheatActions.SpawnBucket(IntProp(p, "type", 0));
                break;
            case "present":
                CheatActions.TriggerPresent();
                break;
            case "travel-buff":
                CheatActions.TravelBuffStub();
                break;
            case "clear-selection":
                CheatState.ClearSelection();
                break;
            case "clear-failed":
                SpawnCatalog.ClearFailed();
                break;
            case "clear-field":
            {
                var cid = Str(p, "id") ?? "";
                CheatState.ClearField(cid, Str(p, "source") ?? "web");
                if (cid.StartsWith("A-", StringComparison.Ordinal))
                    CheatActions.PushScalesNow();
                break;
            }
            case "reset-all":
                CheatState.ResetAll();
                CheatActions.PushScalesNow();
                break;
            case "reset-group":
                CheatState.ResetGroup(p.TryGetProperty("prefix", out var pref) ? pref.GetString() ?? "A-" : "A-");
                CheatActions.PushScalesNow();
                break;
            case "push-stats":
                CheatActions.PushStatsToServer();
                break;
            case "pull-stats":
                CheatActions.PullStatsFromServer();
                break;
            case "set-spawn-cell":
                if (p.TryGetProperty("col", out var col)) CheatState.SpawnCol = col.GetInt32();
                if (p.TryGetProperty("row", out var row)) CheatState.SpawnRow = row.GetInt32();
                break;
            case "save-persist":
                CheatState.PersistEnabled = true;
                CheatState.MaybeSave();
                break;
            case "reload-persist":
                CheatState.TryLoad();
                break;
            case "run-pack":
                RunPackLocal(Str(p, "packId") ?? "");
                break;
            default:
                if (!CheatActionNames.IsKnown(action))
                    CheatState.Error("unknown action " + action);
                else
                    CheatState.Error("unhandled known action " + action);
                break;
        }
    }

    static JsonElement PayloadJson(object? payload)
    {
        if (payload is JsonElement el) return el;
        if (payload == null) return default;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    /// <summary>
    /// The atom half of <c>effects.grants.apply</c> (E19). Absent on the legacy payload, which
    /// carries <c>grants[]</c> and nothing else — so an older server keeps working unchanged.
    /// </summary>
    static int InstallAtomPush(JsonElement p)
    {
        var isAtomPush =
            p.TryGetProperty("runnerBindings", out _) ||
            p.TryGetProperty("defs", out _) ||
            p.TryGetProperty("upToDate", out _);
        if (!isAtomPush) return 0;

        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<AtomPushDto>(p.GetRawText());
            if (payload == null) return 0;
            return Effects.AtomPushReceiver.Install(payload);
        }
        catch (Exception ex)
        {
            CheatState.Error("effects.grants.apply (atoms): " + ex.Message);
            return 0;
        }
    }

    static void RunEffectsGrantsApply(JsonElement p)
    {
        try
        {
            if (p.ValueKind != JsonValueKind.Object ||
                !p.TryGetProperty("grants", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                CheatState.Error("effects.grants.apply: missing grants[]");
                return;
            }

            var applied = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                RunEffectGrant(item);
                applied++;
            }

            // E19: the same command now also carries compiled defs and runner bindings. The grant
            // loop above is untouched — it does the owner-key work (entity:selected, instance
            // refusal) that the receiver must not duplicate or skip.
            var runnerBindings = InstallAtomPush(p);

            DebugRuntime.Emit("debug.effect.rehydrated", new Dictionary<string, object>
            {
                ["count"] = applied,
                ["grants"] = Effects.EffectRuntime.Snapshot().Grants.Count,
                ["runnerBindings"] = runnerBindings
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("effects.grants.apply: " + ex.Message);
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// debug.shield.grant — owner decision 10: grant a shield to the selected/target ptr
    /// (base, element, durationTicks, priority default skill 20). Same command pattern as
    /// enqueue-delta; goes through ShieldRuntime.Apply, never a Funnel write.
    /// </summary>
    static void RunShieldGrant(JsonElement p)
    {
        var ptr = ResolveDeltaTargetPtr(p);
        if (string.IsNullOrWhiteSpace(ptr))
        {
            CheatState.Error("debug.shield.grant: missing target");
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
            {
                ["error"] = "missing-target",
                ["command"] = "debug.shield.grant"
            });
            return;
        }

        var amount = LongProp(p, "amount", 0);
        if (amount <= 0)
        {
            CheatState.Error("debug.shield.grant: amount must be > 0");
            return;
        }

        FusionRpg.Core.Stats.Derived.ElementTypeId? element = null;
        var elementStr = Str(p, "element");
        if (!string.IsNullOrWhiteSpace(elementStr))
        {
            if (!FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(elementStr, out var parsedEl))
            {
                CheatState.Error("debug.shield.grant: unknown element " + elementStr);
                return;
            }

            element = parsedEl;
        }

        Effects.EffectRuntime.Ensure();
        Effects.EffectRuntime.FreezeBoard();
        var gate = Effects.EffectRuntime.Bag.ShieldGate;
        if (gate == null)
        {
            CheatState.Error("debug.shield.grant: shield gate missing");
            return;
        }

        var duration = LongProp(p, "durationTicks", 0);
        var sourceId = Str(p, "sourceId");
        var result = gate.ApplyGrant(CombatPtr.Normalize(ptr!), new FusionRpg.Core.Combat.Shield.ShieldGrant
        {
            SourceId = "debug.shield:" + (string.IsNullOrWhiteSpace(sourceId) ? "manual" : sourceId),
            Element = element,
            BaseHp = amount,
            Priority = (int)LongProp(p, "priority", FusionRpg.Core.Combat.Shield.ShieldPolicy.PrioritySkill),
            DurationTicks = duration > 0 ? duration : null,
            RefillOnMerge = true
        });

        DebugRuntime.Emit("debug.shield.granted", new Dictionary<string, object>
        {
            ["targetPtr"] = ptr!,
            ["outcome"] = result.Outcome.ToString(),
            ["shieldId"] = result.Instance?.ShieldId ?? "",
            ["hp"] = result.Instance?.Hp ?? 0,
            ["maxHp"] = result.Instance?.MaxHp ?? 0,
            ["evicted"] = result.Evicted?.ShieldId ?? ""
        });
        try { Hud.ActorHudCache.MarkDirty(ptr); } catch { }
    }

    /// <summary>debug.shield.clear — RemoveAll on target/selected (no Funnel write).</summary>
    static void RunShieldClear(JsonElement p)
    {
        var ptr = ResolveDeltaTargetPtr(p);
        if (string.IsNullOrWhiteSpace(ptr))
        {
            CheatState.Error("debug.shield.clear: missing target");
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
            {
                ["error"] = "missing-target",
                ["command"] = "debug.shield.clear"
            });
            return;
        }

        Effects.EffectRuntime.Ensure();
        var runtime = Effects.EffectRuntime.Bag.ShieldGate?.Runtime;
        if (runtime == null)
        {
            CheatState.Error("debug.shield.clear: shield gate missing");
            return;
        }

        var ownerKey = EffectOwnerKeys.Entity(CombatPtr.Normalize(ptr!));
        var before = runtime.GetShields(ownerKey).Count;
        runtime.RemoveAll(ownerKey);
        DebugRuntime.Emit("debug.shield.cleared", new Dictionary<string, object>
        {
            ["targetPtr"] = ptr!,
            ["removed"] = before
        });
        try { Hud.ActorHudCache.MarkDirty(ptr); } catch { }
    }

    /// <summary>
    /// debug.shield.demo — grant up to 3 stacks with distinct sourceIds (default fire/ice/earth)
    /// so the hybrid multi-stop bar is visible in one call.
    /// </summary>
    static void RunShieldDemo(JsonElement p)
    {
        var ptr = ResolveDeltaTargetPtr(p);
        if (string.IsNullOrWhiteSpace(ptr))
        {
            CheatState.Error("debug.shield.demo: missing target");
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
            {
                ["error"] = "missing-target",
                ["command"] = "debug.shield.demo"
            });
            return;
        }

        var amount = LongProp(p, "amount", 80);
        if (amount <= 0)
        {
            CheatState.Error("debug.shield.demo: amount must be > 0");
            return;
        }

        var elements = ParseShieldDemoElements(p);
        Effects.EffectRuntime.Ensure();
        Effects.EffectRuntime.FreezeBoard();
        var gate = Effects.EffectRuntime.Bag.ShieldGate;
        if (gate == null)
        {
            CheatState.Error("debug.shield.demo: shield gate missing");
            return;
        }

        var normPtr = CombatPtr.Normalize(ptr!);
        var granted = new List<object>();
        for (var i = 0; i < elements.Count; i++)
        {
            var elName = elements[i];
            FusionRpg.Core.Stats.Derived.ElementTypeId? element = null;
            if (!string.IsNullOrWhiteSpace(elName)
                && !string.Equals(elName, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (!FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(elName, out var parsedEl))
                {
                    CheatState.Error("debug.shield.demo: unknown element " + elName);
                    return;
                }

                element = parsedEl;
            }

            var tag = string.IsNullOrWhiteSpace(elName) ? "none" : elName.Trim().ToLowerInvariant();
            var result = gate.ApplyGrant(normPtr, new FusionRpg.Core.Combat.Shield.ShieldGrant
            {
                SourceId = "demo:" + tag,
                Element = element,
                BaseHp = amount,
                Priority = FusionRpg.Core.Combat.Shield.ShieldPolicy.PrioritySkill,
                RefillOnMerge = true
            });
            granted.Add(new Dictionary<string, object>
            {
                ["element"] = tag,
                ["outcome"] = result.Outcome.ToString(),
                ["shieldId"] = result.Instance?.ShieldId ?? "",
                ["hp"] = result.Instance?.Hp ?? 0,
                ["maxHp"] = result.Instance?.MaxHp ?? 0,
                ["evicted"] = result.Evicted?.ShieldId ?? ""
            });
        }

        DebugRuntime.Emit("debug.shield.demo", new Dictionary<string, object>
        {
            ["targetPtr"] = ptr!,
            ["amount"] = amount,
            ["count"] = granted.Count,
            ["grants"] = granted
        });
    }

    /// <summary>
    /// debug.shield.demo-all — 3-stack demo on every living plant and zombie (lab setup).
    /// Does not require select / targetPtr.
    /// </summary>
    static void RunShieldDemoAll(JsonElement p)
    {
        var amount = LongProp(p, "amount", 80);
        if (amount <= 0)
        {
            CheatState.Error("debug.shield.demo-all: amount must be > 0");
            return;
        }

        var elements = ParseShieldDemoElements(p);
        Effects.EffectRuntime.Ensure();
        Effects.EffectRuntime.FreezeBoard();
        var gate = Effects.EffectRuntime.Bag.ShieldGate;
        if (gate == null)
        {
            CheatState.Error("debug.shield.demo-all: shield gate missing");
            return;
        }

        var targets = new List<string>();
        try
        {
            // Spawn hooks can lag one step behind run-steps — force a full FindObjects resync
            // so pea + zombie both receive the 3-stack demo in lab-shield-bar.
            Effects.InjectorEntityRegistry.Resync(UnityEngine.Time.frameCount);
            Effects.InjectorEntityRegistry.VisitPlants(plant =>
            {
                try
                {
                    if (plant == null || plant.Pointer == IntPtr.Zero) return;
                    targets.Add(CombatPtr.Normalize(plant.Pointer.ToString("X")));
                }
                catch { }
            });
            Effects.InjectorEntityRegistry.VisitZombies(zombie =>
            {
                try
                {
                    if (zombie == null || zombie.Pointer == IntPtr.Zero) return;
                    targets.Add(CombatPtr.Normalize(zombie.Pointer.ToString("X")));
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.shield.demo-all: " + ex.Message);
            return;
        }

        var results = new List<object>();
        foreach (var ptr in targets)
        {
            if (string.IsNullOrEmpty(ptr)) continue;
            gate.Runtime.RemoveAll(EffectOwnerKeys.Entity(ptr));
            var granted = new List<object>();
            for (var i = 0; i < elements.Count; i++)
            {
                var elName = elements[i];
                FusionRpg.Core.Stats.Derived.ElementTypeId? element = null;
                if (!string.IsNullOrWhiteSpace(elName)
                    && !string.Equals(elName, "none", StringComparison.OrdinalIgnoreCase))
                {
                    if (!FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(elName, out var parsedEl))
                    {
                        CheatState.Error("debug.shield.demo-all: unknown element " + elName);
                        return;
                    }

                    element = parsedEl;
                }

                var tag = string.IsNullOrWhiteSpace(elName) ? "none" : elName.Trim().ToLowerInvariant();
                var result = gate.ApplyGrant(ptr, new FusionRpg.Core.Combat.Shield.ShieldGrant
                {
                    SourceId = "demo:" + tag,
                    Element = element,
                    BaseHp = amount,
                    Priority = FusionRpg.Core.Combat.Shield.ShieldPolicy.PrioritySkill,
                    RefillOnMerge = true
                });
                granted.Add(new Dictionary<string, object>
                {
                    ["element"] = tag,
                    ["outcome"] = result.Outcome.ToString(),
                    ["shieldId"] = result.Instance?.ShieldId ?? "",
                    ["hp"] = result.Instance?.Hp ?? 0,
                    ["maxHp"] = result.Instance?.MaxHp ?? 0
                });
            }

            results.Add(new Dictionary<string, object>
            {
                ["targetPtr"] = ptr,
                ["count"] = granted.Count,
                ["grants"] = granted
            });
        }

        DebugRuntime.Emit("debug.shield.demo-all", new Dictionary<string, object>
        {
            ["amount"] = amount,
            ["targetCount"] = results.Count,
            ["targets"] = results
        });
    }

    static List<string> ParseShieldDemoElements(JsonElement p)
    {
        var list = new List<string>();
        if (p.TryGetProperty("elements", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) continue;
                var s = e.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                list.Add(s.Trim());
                if (list.Count >= FusionRpg.Core.Combat.Shield.ShieldPolicy.MaxShieldsPerActor)
                    break;
            }
        }

        if (list.Count == 0)
        {
            list.Add("fire");
            list.Add("ice");
            list.Add("earth");
        }

        return list;
    }

    /// <summary>
    /// debug.shield.snapshot — emit per-stack lines for target, or all live owners with shields.
    /// </summary>
    static void RunShieldSnapshot(JsonElement p)
    {
        Effects.EffectRuntime.Ensure();
        var runtime = Effects.EffectRuntime.Bag.ShieldGate?.Runtime;
        if (runtime == null)
        {
            CheatState.Error("debug.shield.snapshot: shield gate missing");
            return;
        }

        var owners = new List<object>();
        var explicitPtr = Str(p, "targetPtr");
        if (!string.IsNullOrWhiteSpace(explicitPtr)
            && !string.Equals(explicitPtr, "selected", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(explicitPtr, "entity:selected", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(explicitPtr, "entity:select", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(explicitPtr, "all", StringComparison.OrdinalIgnoreCase))
        {
            AppendShieldOwnerSnapshot(owners, runtime, CombatPtr.Normalize(explicitPtr!));
        }
        else if (string.Equals(explicitPtr, "all", StringComparison.OrdinalIgnoreCase)
                 || string.IsNullOrWhiteSpace(explicitPtr))
        {
            // Authoritative: every shield owner in runtime (not registry-only — registry can lag).
            runtime.VisitOwners((ownerKey, _) =>
            {
                var hex = CombatPtr.Normalize(ownerKey);
                if (string.IsNullOrEmpty(hex)) return;
                AppendShieldOwnerSnapshot(owners, runtime, hex);
            });
        }
        else
        {
            var ptr = ResolveDeltaTargetPtr(p);
            if (string.IsNullOrWhiteSpace(ptr))
            {
                CheatState.Error("debug.shield.snapshot: missing target");
                return;
            }

            AppendShieldOwnerSnapshot(owners, runtime, CombatPtr.Normalize(ptr!));
        }

        DebugRuntime.Emit("debug.shield.snapshot", new Dictionary<string, object>
        {
            ["owners"] = owners,
            ["ownerCount"] = owners.Count
        });
    }

    /// <summary>
    /// debug.shield.bar-status — one-shot pipeline audit: shield data + body resolve + last HUD draw diag.
    /// Prefer this over polling many events for HUD proof.
    /// </summary>
    static void RunShieldBarStatus(JsonElement p)
    {
        _ = p;
        try
        {
            OverlaySettings.ShieldBarEnabled = true;
        }
        catch { }

        var status = ShieldBarOverlay.CaptureStatus();
        DebugRuntime.Emit("debug.shield.bar-status", status);
        var early = "?";
        var drawn = 0;
        if (status.TryGetValue("lastDraw", out var ld) && ld is Dictionary<string, object> d)
        {
            if (d.TryGetValue("early", out var e)) early = e?.ToString() ?? "?";
            if (d.TryGetValue("drawnOwners", out var dr) && dr is int di) drawn = di;
        }
        CheatState.Note($"shield.bar-status data={status["dataOwners"]} drawn={drawn} early={early}");
    }

    static void AppendShieldOwnerSnapshot(
        List<object> into,
        FusionRpg.Core.Combat.Shield.ShieldRuntime runtime,
        string ptrHex)
    {
        if (string.IsNullOrEmpty(ptrHex)) return;
        var ownerKey = EffectOwnerKeys.Entity(ptrHex);
        var shields = runtime.GetShields(ownerKey);
        if (shields.Count == 0) return;
        var totals = runtime.Totals(ownerKey);
        var stacks = new List<object>(shields.Count);
        for (var i = 0; i < shields.Count; i++)
        {
            var s = shields[i];
            stacks.Add(new Dictionary<string, object>
            {
                ["shieldId"] = s.ShieldId,
                ["element"] = s.Element is { } el
                    ? el.ToElementId()
                    : "none",
                ["hp"] = s.Hp,
                ["maxHp"] = s.MaxHp,
                ["priority"] = s.Priority,
                ["sourceId"] = s.SourceId,
                ["innate"] = s.IsInnate
            });
        }

        into.Add(new Dictionary<string, object>
        {
            ["ptr"] = ptrHex,
            ["ownerKey"] = ownerKey,
            ["hp"] = totals.Hp,
            ["maxHp"] = totals.MaxHp,
            ["stackCount"] = stacks.Count,
            ["stacks"] = stacks
        });
    }

    static void RunEnqueueDelta(JsonElement p)
    {
        try
        {
            if (HasAbsoluteHpKeys(p))
            {
                CheatState.Error("debug.effect.enqueue-delta: absolute HP rejected");
                DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
                {
                    ["error"] = "absolute-hp",
                    ["command"] = "debug.effect.enqueue-delta"
                });
                return;
            }

            var mode = Str(p, "mode");
            if (!string.IsNullOrWhiteSpace(mode) &&
                !string.Equals(mode, "add", StringComparison.OrdinalIgnoreCase))
            {
                CheatState.Error("debug.effect.enqueue-delta: mode-set rejected");
                DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
                {
                    ["error"] = "mode-set",
                    ["command"] = "debug.effect.enqueue-delta"
                });
                return;
            }

            var ptr = ResolveDeltaTargetPtr(p);
            if (string.IsNullOrWhiteSpace(ptr) && !HasCellAnchor(p))
            {
                CheatState.Error("debug.effect.enqueue-delta: missing target");
                DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
                {
                    ["error"] = "missing-target",
                    ["command"] = "debug.effect.enqueue-delta"
                });
                return;
            }

            var amount = LongProp(p, "amount", 0);
            DamageFxTag? tag = null;
            var tagRaw = Str(p, "tag");
            if (!string.IsNullOrWhiteSpace(tagRaw) &&
                Enum.TryParse<DamageFxTag>(tagRaw, ignoreCase: true, out var parsed))
                tag = parsed;

            Effects.EffectRuntime.Ensure();
            Effects.EffectRuntime.FreezeBoard();
            var funnel = Effects.EffectRuntime.Bag.Funnel;
            if (funnel == null)
            {
                CheatState.Error("debug.effect.enqueue-delta: funnel missing");
                return;
            }

            var hasTargetSpec = p.TryGetProperty("target", out var tEl) && tEl.ValueKind == JsonValueKind.Object;
            var useCombatDispatch = hasTargetSpec || HasElementPayload(p) || HasCellAnchor(p);
            var ok = true;
            var resolved = 0;
            if (useCombatDispatch)
            {
                var overlay = JsonElementToOverlay(p);
                var packet = DamagePacketBuilder.FromOverlay(overlay, new EffectEventDto
                {
                    TargetPtr = ptr,
                    ActorPtr = ptr,
                    Tick = Effects.EffectRuntime.NextTick()
                }, grantId: "debug.enqueue-delta", pluginId: "debug");
                Effects.EffectRuntime.BindSelectedTarget(packet);
                resolved = CombatDamageDispatcher.DispatchInstant(
                    packet,
                    Effects.EffectRuntime.Bag.BoardSnapshot,
                    new EffectEventDto { TargetPtr = ptr, ActorPtr = ptr },
                    funnel,
                    Effects.EffectRuntime.Bag.CombatPolicy,
                    Effects.EffectRuntime.Bag.CombatRng,
                    Effects.EffectRuntime.Bag.CombatMath,
                    skipped: null,
                    shieldGate: Effects.EffectRuntime.Bag.ShieldGate,
                    actorResolve: Effects.EffectRuntime.Bag.ActorResolve);
            }
            else if (amount != 0)
            {
                ok &= funnel.EnqueueMutation("entity:" + ptr, amount, pluginId: "debug");
                resolved = ok ? 1 : 0;
            }

            if (tag.HasValue)
            {
                ok &= funnel.EnqueuePresent(new DamageFxDto
                {
                    TargetPtr = ptr,
                    Amount = amount,
                    Tag = tag.Value,
                    Fx = Str(p, "fx") ?? "float"
                });
            }

            funnel.Flush();
            DebugRuntime.Emit("debug.effect.enqueue-delta", new Dictionary<string, object>
            {
                ["ptr"] = ptr,
                ["amount"] = amount,
                ["tag"] = tag?.ToString() ?? "",
                ["ok"] = ok,
                ["resolved"] = resolved
            });
            if (useCombatDispatch)
            {
                DebugRuntime.Emit("debug.combat.packet", new Dictionary<string, object>
                {
                    ["fa10"] = resolved,
                    ["source"] = "enqueue-delta"
                });
            }
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.effect.enqueue-delta: " + ex.Message);
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["command"] = "debug.effect.enqueue-delta"
            });
        }
    }

    static bool HasAbsoluteHpKeys(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in p.EnumerateObject())
        {
            if (prop.Name.Equals("setHp", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("absoluteHp", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("hp", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("EntityFinal.Hp", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("entityFinalHp", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static bool HasCellAnchor(JsonElement p)
    {
        if (!p.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object)
            return false;
        if (!t.TryGetProperty("anchor", out var a) || a.ValueKind != JsonValueKind.Object)
            return false;
        return a.TryGetProperty("row", out _) && a.TryGetProperty("col", out _);
    }

    static bool HasElementPayload(JsonElement p)
    {
        if (!p.TryGetProperty("elementPayload", out var el) || el.ValueKind != JsonValueKind.Array)
            return false;
        return el.GetArrayLength() > 0;
    }

    static void EmitBoardSnapshot()
    {
        Effects.EffectRuntime.FreezeBoard();
        var ents = Effects.EffectRuntime.Bag.BoardSnapshot.Entities
            .Select(e => new Dictionary<string, object>
            {
                ["ptr"] = e.Ptr,
                ["side"] = e.Side,
                ["typeId"] = e.TypeId,
                ["col"] = e.Col,
                ["row"] = e.Row,
                ["living"] = e.Living,
                ["mindControlled"] = e.MindControlled
            })
            .ToList();
        DebugRuntime.Emit("debug.effect.board-snapshot", new Dictionary<string, object>
        {
            ["count"] = ents.Count,
            ["entities"] = ents,
            ["selectedPtr"] = Effects.EffectRuntime.Bag.SelectedPtr ?? ""
        });
    }

    static void HandleActorDerived(JsonElement p)
    {
        Effects.EffectRuntime.Ensure();
        var ptr = Str(p, "ptr");
        if (string.IsNullOrWhiteSpace(ptr) && CheatState.SelectedPtr != IntPtr.Zero)
            ptr = CheatState.SelectedPtr.ToString("X");

        var profile = Str(p, "profile") ?? Str(p, "derivedProfile");
        Dictionary<string, double>? overlay = null;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Object)
            overlay = ReadDoubleMap(ch);
        else if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("derived", out var d) && d.ValueKind == JsonValueKind.Object)
            overlay = ReadDoubleMap(d);

        if (!string.IsNullOrWhiteSpace(profile) || (overlay != null && overlay.Count > 0))
        {
            if (string.IsNullOrWhiteSpace(ptr))
            {
                CheatState.Error("debug.actor-derived: ptr required to pin");
                return;
            }

            try
            {
                InjectorDerivedOverride.PinProfile(ptr, profile, overlay);
            }
            catch (Exception ex)
            {
                CheatState.Error("debug.actor-derived pin: " + ex.Message);
                return;
            }
        }

        EmitActorDerived(ptr);
    }

    static Dictionary<string, double> ReadDoubleMap(JsonElement obj)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.TryGetDouble(out var v))
                map[prop.Name] = v;
            else if (prop.Value.TryGetInt64(out var l))
                map[prop.Name] = l;
        }

        return map;
    }

    static void ApplyStatusL2(JsonElement p)
    {
        Effects.EffectRuntime.Ensure();
        Effects.EffectRuntime.FreezeBoard();
        var statusId = Str(p, "statusId") ?? Str(p, "status");
        if (string.IsNullOrWhiteSpace(statusId))
        {
            CheatState.Error("debug.status.apply: statusId required");
            return;
        }

        var hostPtr = Str(p, "hostPtr") ?? Str(p, "targetPtr") ?? Str(p, "ptr");
        if (string.IsNullOrWhiteSpace(hostPtr) && CheatState.SelectedPtr != IntPtr.Zero)
            hostPtr = CheatState.SelectedPtr.ToString("X");
        if (string.IsNullOrWhiteSpace(hostPtr))
        {
            CheatState.Error("debug.status.apply: hostPtr required");
            return;
        }

        var attackerPtr = Str(p, "attackerPtr");
        var durationMs = ResolveStatusApplyDurationMs(p);
        var amount = LongProp(p, "amount", 0);
        var now = ServerClock.UtcNow;
        var runtime = Effects.EffectRuntime.Status;
        StatusDef def;
        try
        {
            def = runtime.Catalog.GetRequired(statusId);
        }
        catch (UnknownStatusIdException ex)
        {
            CheatState.Error("debug.status.apply: " + ex.Message);
            return;
        }

        var input = new StatusApplyInput(
            statusId,
            hostPtr,
            attackerPtr,
            GrantId: "debug-status-apply",
            BaseMagnitude: amount,
            BaseDuration: durationMs,
            PeriodMs: IntProp(p, "periodMs", 1000),
            DurationMs: durationMs,
            TickBudget: IntProp(p, "tickBudget", 1),
            GrantChance: 1.0,
            AttackerLess: string.IsNullOrWhiteSpace(attackerPtr));
        var outcome = runtime.Apply(input, Effects.EffectRuntime.Bag.StatusRng, now);
        if (outcome.Applied
            && def.PayloadKinds.Contains(StatusPayloadKind.UnityCc))
        {
            var durationSec = (float)Math.Max(0.1, (outcome.Instance?.EffectiveDuration ?? durationMs) / 1000.0);
            foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
            {
                if (z == null) continue;
                if (!string.Equals(GameDumps.Ptr(z), hostPtr, StringComparison.OrdinalIgnoreCase)) continue;
                DebugActions.ApplyStatusToZombie(z, statusId, durationSec, 1, method: true);
                break;
            }
        }

        EmitStatus();
    }

    static void EmitActorDerived(string? ptr)
    {
        Effects.EffectRuntime.Ensure();
        if (string.IsNullOrWhiteSpace(ptr))
        {
            CheatState.Error("debug.actor-derived: ptr required");
            return;
        }

        var key = ptr.Trim();
        var (derived, contributions) = InjectorStatusBridge.ResolveDerivedWithContributions(key, attackerLess: false);
        var channels = derived.Channels
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(
                kv => kv.Key,
                kv => (object)new Dictionary<string, object>
                {
                    ["value"] = kv.Value,
                    ["contributions"] = contributions.ContributionsFor(kv.Key)
                        .Select(c => new Dictionary<string, object>
                        {
                            ["sourceId"] = c.SourceId,
                            ["label"] = ContributionSourceIds.FictionLabel(c.SourceId),
                            ["op"] = c.Op.ToString(),
                            ["value"] = c.Value
                        })
                        .ToList()
                },
                StringComparer.Ordinal);
        DebugRuntime.Emit("debug.actor-derived", new Dictionary<string, object>
        {
            ["ptr"] = key,
            ["tierPower"] = derived.TierPower,
            ["channels"] = channels
        });
    }

    static void HandleCombatPinElement(JsonElement p)
    {
        Effects.EffectRuntime.Ensure();
        var ptr = Str(p, "ptr");
        if (string.IsNullOrWhiteSpace(ptr) && CheatState.SelectedPtr != IntPtr.Zero)
            ptr = CheatState.SelectedPtr.ToString("X");
        if (string.IsNullOrWhiteSpace(ptr))
        {
            CheatState.Error("debug.combat.pin-element: ptr required");
            return;
        }

        var primary = Str(p, "elementPrimary") ?? Str(p, "primary");
        var secondary = Str(p, "elementSecondary") ?? Str(p, "secondary");
        try
        {
            InjectorElementOverride.PinParse(ptr, primary, secondary);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.combat.pin-element: " + ex.Message);
            return;
        }

        // L-N6: the basic-attack grant baked its element at spawn; rebind it so the pin also reaches this actor's attacks.
        // A rebind is an idempotent upsert (deterministic grant id), and the binder skips it while the feature is off.
        Effects.LawnBasicAttackGrantBinder.QueueSpawn(ptr.Trim());

        DebugRuntime.Emit("debug.combat.pin-element", new Dictionary<string, object>
        {
            ["ptr"] = ptr.Trim(),
            ["elementPrimary"] = primary ?? "",
            ["elementSecondary"] = secondary ?? ""
        });
    }

    static void EmitStatus(bool includeLegacyDots = false, bool includeLegacyCounters = false)
    {
        Effects.EffectRuntime.Ensure();
        var now = ServerClock.UtcNow;
        var status = Effects.EffectRuntime.Status;
        var instances = status.AllInstances()
            .Select(i => new Dictionary<string, object>
            {
                ["instanceId"] = i.InstanceId,
                ["statusId"] = i.StatusId,
                ["hostPtr"] = i.HostPtr,
                ["attackerPtr"] = i.AttackerPtr ?? "",
                ["grantId"] = i.GrantId,
                ["kind"] = i.Kind.ToString(),
                ["effectiveMagnitude"] = i.EffectiveMagnitude,
                ["periodMs"] = i.PeriodMs,
                ["nextMs"] = (long)Math.Max(0, (i.NextPulse - now).TotalMilliseconds),
                ["endMs"] = (long)Math.Max(0, (i.ExpiresAt - now).TotalMilliseconds),
                ["pulsesFired"] = i.PulsesFired,
                ["tickBudget"] = i.TickBudget,
                ["hopDepth"] = i.HopDepth,
                ["spreadChance"] = i.SpreadChance
            })
            .ToList();
        var resisted = status.ResistedEvents
            .Select(ev => new Dictionary<string, object>
            {
                ["statusId"] = ev.StatusId,
                ["hostPtr"] = ev.HostPtr,
                ["attackerPtr"] = ev.AttackerPtr ?? "",
                ["grantId"] = ev.GrantId,
                ["reason"] = ev.Reason.ToString(),
                ["delta"] = ev.Delta,
                ["at"] = ev.At.ToString("o")
            })
            .ToList();
        DebugRuntime.Emit("debug.status", new Dictionary<string, object>
        {
            ["count"] = instances.Count,
            ["instances"] = instances,
            ["resistedCount"] = resisted.Count,
            ["resisted"] = resisted
        });

        if (includeLegacyDots)
            EmitDotsLegacy(now);
        if (includeLegacyCounters)
            EmitCountersLegacy();
    }

    static void EmitDotsLegacy(DateTimeOffset now)
    {
        _ = now;
        DebugRuntime.Emit("debug.effect.dots", new Dictionary<string, object>
        {
            ["count"] = 0,
            ["items"] = new List<Dictionary<string, object>>(),
            ["note"] = "legacy scheduler removed; see debug.status"
        });
    }

    static void EmitCountersLegacy()
    {
        var meters = Effects.EffectRuntime.Status.CounterSnapshot()
            .Select(kv => new Dictionary<string, object> { ["key"] = kv.Key, ["hits"] = kv.Value })
            .ToList();
        DebugRuntime.Emit("debug.effect.counters", new Dictionary<string, object>
        {
            ["count"] = meters.Count,
            ["meters"] = meters,
            ["note"] = meters.Count == 0 ? "legacy CounterProcState removed; bond meters on StatusRuntime" : ""
        });
    }

    static void EmitDots()
    {
        EmitStatus(includeLegacyDots: true);
    }

    static void EmitCounters()
    {
        EmitStatus(includeLegacyCounters: true);
    }

    static void RunShaderProbe()
    {
        var payload = OverlayShaderProbe.ToEventPayload(force: true);
        DebugRuntime.Emit("debug.fx.shader-probe", payload);
        var found = payload.TryGetValue("foundCount", out var n) ? n : 0;
        var draw = payload.TryGetValue("drawShader", out var s) ? s : "";
        CheatState.Note("shader probe found=" + found + " draw=" + draw);
    }

    static void RunWorldFlash(JsonElement p)
    {
        // Legacy scenario-step name — locked alias for fx.play debug.probe (vfx-ssot.md §11).
        var col = IntProp(p, "col", CheatState.SpawnCol);
        var row = IntProp(p, "row", CheatState.SpawnRow);
        VfxDirector.Play(new VfxCueDto { CueId = VfxCueIds.DebugProbe, Col = col, Row = row });
        CheatState.Note("world flash cell=" + col + "," + row);
    }

    static void RunFxPlay(JsonElement p)
    {
        var cueId = Str(p, "cueId");
        var cue = new VfxCueDto
        {
            CueId = string.IsNullOrWhiteSpace(cueId) ? VfxCueIds.DebugProbe : cueId!,
            Amount = LongProp(p, "amount", 0)
        };
        var tagRaw = Str(p, "tag");
        if (!string.IsNullOrWhiteSpace(tagRaw) &&
            Enum.TryParse<DamageFxTag>(tagRaw, ignoreCase: true, out var tag))
            cue.Tag = tag;
        cue.Elements = ParseFxElements(p);
        cue.SourcePtr = Str(p, "sourcePtr");
        var ptr = Str(p, "ptr");
        if (!string.IsNullOrWhiteSpace(ptr))
        {
            cue.TargetPtr = ptr;
        }
        else
        {
            cue.Col = IntProp(p, "col", CheatState.SpawnCol);
            cue.Row = IntProp(p, "row", CheatState.SpawnRow);
        }

        VfxDirector.Play(cue);
        CheatState.Note("fx.play " + cue.CueId +
                        (cue.TargetPtr != null ? " ptr=" + cue.TargetPtr : " cell=" + cue.Col + "," + cue.Row));
    }

    static void RunFxState()
    {
        var sets = VfxDirector.SustainedSnapshot();
        DebugRuntime.Emit("debug.fx.state", new Dictionary<string, object>
        {
            ["count"] = sets.Count,
            ["sets"] = sets
        });
        CheatState.Note("fx sustained: " + sets.Count);
    }

    static void RunFxList()
    {
        DebugRuntime.Emit("debug.fx.list", new Dictionary<string, object>
        {
            ["cues"] = VfxDirector.CueIds.ToList(),
            ["muted"] = VfxDirector.MutedIds.ToList()
        });
        CheatState.Note("fx cues: " + string.Join(",", VfxDirector.CueIds));
    }

    static void RunFxMute(JsonElement p, bool mute)
    {
        var cueId = Str(p, "cueId");
        if (string.IsNullOrWhiteSpace(cueId))
        {
            CheatState.Error("debug.fx." + (mute ? "mute" : "unmute") + ": missing cueId");
            return;
        }

        VfxDirector.SetMuted(cueId!, mute);
        DebugRuntime.Emit("debug.fx.mute", new Dictionary<string, object>
        {
            ["cueId"] = cueId!,
            ["muted"] = mute
        });
        CheatState.Note("fx " + (mute ? "muted " : "unmuted ") + cueId);
    }

    static List<ElementPayloadComponentDto>? ParseFxElements(JsonElement p)
    {
        if (!p.TryGetProperty("elements", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<ElementPayloadComponentDto>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var element = e.TryGetProperty("element", out var ev) ? ev.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(element)) continue;
            var weight = e.TryGetProperty("weight", out var wv) && wv.ValueKind == JsonValueKind.Number
                ? wv.GetDouble()
                : 1.0;
            list.Add(new ElementPayloadComponentDto { Element = element, Weight = weight });
        }

        return list.Count > 0 ? list : null;
    }

    static string? ResolveDeltaTargetPtr(JsonElement p)
    {
        var raw = Str(p, "targetPtr");
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "selected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "entity:selected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "entity:select", StringComparison.OrdinalIgnoreCase))
        {
            if (CheatState.SelectedPtr == IntPtr.Zero) return null;
            return CheatState.SelectedPtr.ToString("X");
        }

        raw = CombatPtr.Normalize(raw);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>
    /// T11 test harness (buff-debuff-scope-todo.md): wires a live `BattlefieldOwnSideReactor` to this
    /// match's real `EffectBag`/`MembershipChanged`. Params: `effectId` (required), `pluginId` (default
    /// "debug-scope"), `relation` ("ally"/"enemy", default "ally"), `atomKindId` (default
    /// "resource.delta" — the normal per-entity-grant case), `channel` (optional), `host` ("sim"/"live",
    /// default "live" — this command only makes sense against a real running match). A G8-shaped
    /// kind/channel/host combination throws `ScopeUnsupportedException` here, live — the same refusal
    /// T10's own tests already proved against `BattleEffectHost`, now reachable from a real match.
    /// </summary>
    static void RunScopeStartOwnSide(JsonElement p)
    {
        try
        {
            var effectId = Str(p, "effectId");
            if (string.IsNullOrWhiteSpace(effectId))
            {
                CheatState.Error("debug.scope.start-own-side: effectId required");
                return;
            }

            var pluginId = Str(p, "pluginId") ?? "debug-scope";
            var atomKindId = Str(p, "atomKindId") ?? "resource.delta";
            var channel = Str(p, "channel");

            var hostText = Str(p, "host") ?? "live";
            if (!FusionRpg.Core.Scope.ScopeHosts.TryParse(hostText, out var host))
            {
                CheatState.Error("debug.scope.start-own-side: unknown host '" + hostText + "' (want sim|live)");
                return;
            }

            var relationText = Str(p, "relation") ?? "ally";
            if (!FusionRpg.Contracts.RelationKinds.TryParse(relationText, out var relation))
            {
                CheatState.Error("debug.scope.start-own-side: unknown relation '" + relationText + "' (want ally|enemy)");
                return;
            }

            FusionRpg.Injector.Effects.DebugScopeRuntime.StartOwnSide(effectId!, pluginId, atomKindId, host, channel, relation);
            DebugRuntime.Emit("debug.scope.started", new Dictionary<string, object>
            {
                ["effectId"] = effectId!,
                ["pluginId"] = pluginId,
                ["atomKindId"] = atomKindId,
                ["host"] = hostText,
                ["relation"] = relationText
            });
        }
        catch (FusionRpg.Core.Scope.ScopeUnsupportedException ex)
        {
            // The G8 refusal, live: proves T11's own criterion 4 the same way T10's tests already
            // proved it against BattleEffectHost — this kind/host/channel combination is legal only
            // as the side-wide-constant shape, so this command refuses rather than issuing a grant.
            CheatState.Error("debug.scope.start-own-side: ScopeUnsupported — " + ex.Message);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.scope.start-own-side: " + ex.Message);
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }

    static void RunEffectGrant(JsonElement p)
    {
        try
        {
            Dictionary<string, object?>? overlay = null;
            if (p.TryGetProperty("overlay", out var ov) && ov.ValueKind == JsonValueKind.Object)
            {
                overlay = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in ov.EnumerateObject())
                    overlay[prop.Name] = UnwrapJson(prop.Value);
            }

            var ownerKey = Str(p, "ownerKey") ?? EffectOwnerKeys.Match;
            if (string.Equals(ownerKey, "entity:selected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ownerKey, "entity:select", StringComparison.OrdinalIgnoreCase))
            {
                if (CheatState.SelectedPtr == IntPtr.Zero)
                {
                    CheatState.Error("debug.effect.grant: entity:selected but no SelectedPtr");
                    return;
                }

                ownerKey = EffectOwnerKeys.Entity(CheatState.SelectedPtr.ToString("X"));
            }

            ownerKey = StatApplyScope.Normalize(ownerKey);
            if (StatApplyScope.IsInstanceOwnerKey(ownerKey))
            {
                CheatState.Error("debug.effect.grant: instance: forbidden in Hot; bind to entity:{ptr}");
                return;
            }

            var ownerKind = Str(p, "ownerKind");
            if (string.IsNullOrWhiteSpace(ownerKind))
            {
                if (ownerKey.StartsWith("entity:", StringComparison.Ordinal)) ownerKind = "entity";
                else if (ownerKey.StartsWith("plant:", StringComparison.Ordinal)) ownerKind = "plant";
                else if (ownerKey.StartsWith("zombie:", StringComparison.Ordinal)) ownerKind = "zombie";
                else if (ownerKey.StartsWith("player:", StringComparison.Ordinal)) ownerKind = "player";
                else ownerKind = "match";
            }

            var grantId = Str(p, "grantId");
            if (!FusionRpg.Core.Effects.EffectGrantSessionRecorder.TryValidateHotGrantId(grantId, out var grantIdErr))
            {
                CheatState.Error("debug.effect.grant: " + grantIdErr);
                return;
            }

            var dto = new EffectGrantDto
            {
                GrantId = grantId!,
                EffectId = Str(p, "effectId") ?? "",
                OwnerKind = ownerKind,
                OwnerKey = ownerKey,
                PluginId = Str(p, "pluginId") ?? "debug",
                Priority = p.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var pi) ? pi : 0,
                Overlay = overlay
            };
            if (string.IsNullOrWhiteSpace(dto.EffectId))
            {
                CheatState.Error("debug.effect.grant: effectId required");
                return;
            }

            Effects.EffectRuntime.Grant(dto);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.effect.grant: " + ex.Message);
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }

    static Dictionary<string, object?> JsonElementToOverlay(JsonElement p)
    {
        var overlay = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (p.ValueKind != JsonValueKind.Object) return overlay;
        foreach (var prop in p.EnumerateObject())
            overlay[prop.Name] = UnwrapJson(prop.Value);
        return overlay;
    }

    static object? UnwrapJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(x => x.Name, x => UnwrapJson(x.Value), StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => el.EnumerateArray().Select(UnwrapJson).ToList(),
        _ => el.GetRawText()
    };

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    static string? LoadoutJsonFromPayload(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (p.TryGetProperty("loadoutJson", out var s) && s.ValueKind == JsonValueKind.String)
            return s.GetString();
        if (p.TryGetProperty("loadout", out var obj) && obj.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            return obj.ValueKind == JsonValueKind.String ? obj.GetString() : obj.GetRawText();
        return null;
    }

    static bool TryResolveSyntheticEvent(JsonElement p, out EffectEventDto ev)
    {
        ev = null!;
        var selected = CheatState.SelectedPtr == IntPtr.Zero
            ? null
            : CheatState.SelectedPtr.ToString("X");
        var side = Str(p, "side") ?? "plant";
        var plantSide = string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase);

        var actor = Str(p, "actorPtr");
        int? actorTypeId = p.TryGetProperty("typeId", out var tid) && tid.TryGetInt32(out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(actor))
        {
            if (plantSide)
            {
                var col = IntProp(p, "actorCol", IntProp(p, "col", 2));
                var row = IntProp(p, "actorRow", IntProp(p, "row", 2));
                actor = DebugActions.TryResolvePlantPtr(col, row, out var plantType);
                if (string.IsNullOrWhiteSpace(actor))
                {
                    CheatState.Error($"debug.effect.fire-synthetic: no plant at col={col} row={row}");
                    return false;
                }

                actorTypeId ??= plantType;
            }
            else
                actor = selected;
        }

        var target = Str(p, "targetPtr");
        int? targetTypeId = p.TryGetProperty("targetTypeId", out var ttid) && ttid.TryGetInt32(out var tt) ? tt : null;
        if (string.IsNullOrWhiteSpace(target))
        {
            if (plantSide
                && selected != null
                && string.Equals(CheatState.SelectedSide, "zombie", StringComparison.OrdinalIgnoreCase))
            {
                target = selected;
            }
            else if (p.TryGetProperty("targetRow", out var tr) && tr.TryGetInt32(out var tRow))
            {
                float? tx = p.TryGetProperty("targetX", out var txEl) && txEl.TryGetSingle(out var txv) ? txv : null;
                target = DebugActions.TryResolveZombiePtr(tRow, tx, out var zombieType);
                targetTypeId ??= zombieType;
            }
            else
                target = selected;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            CheatState.Error("debug.effect.fire-synthetic: no target (select zombie or pass targetPtr/targetRow)");
            return false;
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            CheatState.Error("debug.effect.fire-synthetic: no actor");
            return false;
        }

        ev = new EffectEventDto
        {
            Trigger = Str(p, "trigger") ?? EffectTriggers.OnDamageDealt,
            MatchKey = GameHooks.MatchKey,
            Side = side,
            ActorPtr = actor,
            TargetPtr = target,
            TypeId = actorTypeId,
            TargetTypeId = targetTypeId,
            Tick = Effects.EffectRuntime.NextTick(),
            ScenarioId = DebugRuntime.ScenarioId
        };
        return true;
    }

    static int IntProp(JsonElement p, string name, int fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.TryGetInt32(out var i) ? i : fallback;

    /// <summary>debug.status.apply: durationMs wins; else duration (seconds, float) → ms; else 4000.</summary>
    static int ResolveStatusApplyDurationMs(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("durationMs", out var msEl) && msEl.TryGetInt32(out var ms))
            return ms;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("duration", out var secEl))
        {
            if (secEl.TryGetDouble(out var sec))
                return (int)Math.Max(1, Math.Round(sec * 1000.0));
            if (secEl.TryGetInt32(out var secInt))
                return Math.Max(1, secInt * 1000);
        }
        return 4000;
    }

    static long LongProp(JsonElement p, string name, long fallback)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var el)) return fallback;
        if (el.TryGetInt64(out var l)) return l;
        if (el.TryGetInt32(out var i)) return i;
        if (el.TryGetDouble(out var d)) return (long)d;
        return fallback;
    }

    static bool BoolProp(JsonElement p, string name, bool fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) ? el.GetBoolean() : fallback;
}
