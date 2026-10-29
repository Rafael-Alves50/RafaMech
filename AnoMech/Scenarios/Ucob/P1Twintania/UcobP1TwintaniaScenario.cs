using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Scenarios.Legacy;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Core.Native.Interfaces;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Scenarios.Ucob.P1Twintania;

// First native RafaMech integration. Timings are deliberately provisional pending a UCOB network log.
// The three sections follow NAUR's HP-based rotations; the scenario is not a retail-accurate replay.
public sealed class UcobP1TwintaniaScenario : IScenario
{
    public string Name => "Twintania (NAUR training)";
    public IPhase Phase => UcobZone.P1;
    public bool SupportsSolo => true;
    public IReadOnlyList<IScenarioAi> AiStrats => [new UcobP1TwintaniaAi()];
    public object SettingsOverrides => settings;
    private readonly Settings settings = new();
    public bool FullPull => settings.Section == 0;
    private sealed class Settings
    {
        public PartyRole Role { get; set; } = PartyRole.RegenHealer;
        public bool TwisterAlwaysOnMe { get; set; }
        public int Section { get; set; } = 0;
    }

    private SimWorld world = null!;
    private float mechanicAt;
    private const uint ProvokeAction = 7533;
    private bool awaitingPlayerProvoke;
    private bool swapComplete;
    private bool otHasTakenFirstSwap;
    private PartyRole exposedTank;
    private float currentElapsed;
    private bool trainingBotsEnabled;

    private SimEnemy? twin;
    private PartyRole activeTank = PartyRole.MainTank;
    private readonly List<Vector3> twisterSpots = [];
    private readonly List<Vector3> hellSpots = [];
    private readonly PartyRole[] roster = [
        PartyRole.MainTank, PartyRole.OffTank, PartyRole.RegenHealer,
        PartyRole.ShieldHealer, PartyRole.MeleeDpsA, PartyRole.MeleeDpsB,
        PartyRole.PhysRangedDps, PartyRole.CasterDps
    ];
    private const uint TwintaniaBaseId = 0x1FDF; // UCOB Twintania OID
    private static uint? cachedTwintaniaNameId;
    private static uint TwintaniaNameId
    {
        get
        {
            if (cachedTwintaniaNameId.HasValue) return cachedTwintaniaNameId.Value;
            for (uint id = 1; id < 15000; id++)
                if (string.Equals(Natives.Data.BNpcName(id), "Twintania", StringComparison.OrdinalIgnoreCase))
                    return (cachedTwintaniaNameId = id).Value;
            return (cachedTwintaniaNameId = 0).Value;
        }
    }
    private const uint Plummet = 9896;
    private const uint DeathSentence = 9897;
    private const uint Twister = 9898;
    private const uint TwisterAOE = 9899;
    private const uint TwisterVoidzoneEObj = 0x1E8910;
    private const uint Fireball = 9900;
    private const uint LiquidHell = 9901;
    private const uint Generate = 9902;
    private const float TwisterRadius = 0.8f; // contact core; larger knockback occurs only on contact
    private const float LiquidHellRadius = 3.0f;

    public void DrawSettings()
    {
        ImGui.TextUnformatted("NAUR UCOB P1 — Twintania");
        ImGui.TextWrapped("Choose the simulated party seat. Use a matching job for accurate role assignment.");
        var names = new[] { "MT", "OT", "H1", "H2", "M1", "M2", "Physical ranged", "Caster" };
        var roleIndex = Array.IndexOf(roster, settings.Role);
        if (roleIndex < 0) roleIndex = 0;
        if (ImGui.Combo("My role", ref roleIndex, names, names.Length))
            settings.Role = roster[roleIndex];
        var always = settings.TwisterAlwaysOnMe;
        if (ImGui.Checkbox("Twister always on me", ref always))
            settings.TwisterAlwaysOnMe = always;
        var section = settings.Section;
        var options = new[] { "Complete P1: recorded pull (0–139s)", "100–75%: opener", "75–45%: one Neurolink", "45–0%: two Neurolinks" };
        if (ImGui.Combo("Practice section", ref section, options, options.Length))
            settings.Section = section;
        ImGui.TextWrapped("Recorded-pull practice: a full observed P1 timeline, not an HP-driven retail replay. Neurolink visuals and hatch orbs are still under development.");
    }

    public void Run(SimWorld simWorld, int? selectedAi)
    {
        world = simWorld;
        twisterSpots.Clear();
        hellSpots.Clear();
        activeTank = PartyRole.MainTank;
        awaitingPlayerProvoke = false;
        swapComplete = false;
        otHasTakenFirstSwap = false;
        currentElapsed = 0f;
        trainingBotsEnabled = selectedAi == 0;
        AnoMech.Core.Native.Interfaces.Natives.UserActions.Acted -= OnPlayerAction;
        AnoMech.Core.Native.Interfaces.Natives.UserActions.Acted += OnPlayerAction;
        if (selectedAi is { } ai && ai == 0)
            new UcobP1TwintaniaAi().Run(this, world);
        world.Events.Add(0.15f, SpawnTwintania);
        var section = Math.Clamp(settings.Section, 0, 3);
        var events = UcobP1Timeline.ForDrill(section);
        foreach (var item in events)
        {
            var at = item.Time;
            var kind = item.Kind;
            world.Events.Add(at, () => Execute(kind.ToString(), section, at));
        }
        var endAt = events.Max(x => x.Time) + 12f;
        world.Events.Add(endAt, () =>
        {
            Natives.UserActions.Acted -= OnPlayerAction;
            twin?.Despawn();
        });
    }

    public void Tick(float delta, float elapsed) => currentElapsed = elapsed;

    private void OnPlayerAction(PlayerAction action)
    {
        if (!awaitingPlayerProvoke || action.Step != PlayerActionStep.Resolved || action.ActionId != ProvokeAction)
            return;
        if (settings.Role != PartyRole.MainTank || twin == null || action.TargetId != twin.GameObjectId)
            return;
        activeTank = PartyRole.MainTank;
        swapComplete = true;
        awaitingPlayerProvoke = false;
        twin.SetTarget(Get(activeTank));
    }

    private void BotSwapToOffTank()
    {
        activeTank = PartyRole.OffTank;
        otHasTakenFirstSwap = true;
        swapComplete = true;
        awaitingPlayerProvoke = false;
        twin?.SetTarget(Get(activeTank));
    }

    private SimCharacter? Get(PartyRole role) => world.Party.Get(role);
    private SimCharacter? Pick(IReadOnlyList<PartyRole> options)
        => options.Select(Get).Where(p => p != null).OrderBy(_ => Guid.NewGuid()).FirstOrDefault();

    private void SpawnTwintania()
    {
        twin = world.SpawnEnemy(new EnemySpawnConfig(
            BNpcBaseId: TwintaniaBaseId,
            NameId: TwintaniaNameId,
            Level: UcobConstants.Level,
            Targetable: true,
            EnemyList: EnemyListMode.Always,
            Visibility: SpawnVisibility.Visible,
            Placement: new Placement(new Vector3(0f, 0f, -8f), MathF.PI)));
        twin?.SetTarget(Get(activeTank));
        if (settings.Section == 2)
            twin?.SetPosition(new Placement(new Vector3(-12f, 0f, 5f), -MathF.PI / 2f));
        else if (settings.Section == 3)
            twin?.SetPosition(new Placement(new Vector3(0f, 0f, 0f), MathF.PI));
    }

    private void Execute(string mechanic, int section, float at)
    {
        mechanicAt = at;
        if (section == 3 && mechanic == "Plummet" && activeTank == PartyRole.MainTank)
            twin?.MoveTo(new Vector3(12f, 0f, 5f), 4f, MathF.PI / 2f);
        if (mechanic.Contains("Liquid Hell"))
            ScheduleLiquidHells(section == 0 ? at > 90f && at < 100f : section == 3 && mechanic.Contains("+"));
        if (mechanic.Contains("Fireball"))
            ScheduleFireball();
        if (mechanic == "Generate")
            ScheduleHatch(section == 0 ? (at < 80f ? 1 : 2) : section - 1);
        if (mechanic == "Twister")
            ScheduleTwisters();
        if (mechanic == "Plummet")
            StrikeTank(Plummet);
        if (mechanic == "Death Sentence")
        {
            StrikeTank(DeathSentence);
            // The outgoing tank retains Slashing Resistance Down; the other tank
            // must Provoke during the cast, before the following auto-attack.
            exposedTank = activeTank;
            swapComplete = false;
            if (activeTank == PartyRole.MainTank)
            {
                if (settings.Role == PartyRole.MainTank)
                    world.Events.Add(2.8f, BotSwapToOffTank);
                else
                    world.Events.Add(2.8f, BotSwapToOffTank);
            }
            else if (settings.Role == PartyRole.MainTank)
            {
                awaitingPlayerProvoke = true;
            }
            else
            {
                // Non-MT practice currently assumes scripted bot swap to the MT.
                world.Events.Add(2.8f, () => { activeTank = PartyRole.MainTank; swapComplete = true; twin?.SetTarget(Get(activeTank)); });
            }
            world.Events.Add(4.45f, () =>
            {
                if (!swapComplete || activeTank == exposedTank)
                {
                    var victim = Get(exposedTank);
                    if (victim != null)
                        new DamageSolver(world.Party).ApplyDamage(victim, 1f, DeathSentence, "Death Sentence: failed tank swap", lethal: true);
                    world.Fail("Tank swap failed: Provoke Twintania during Death Sentence before the next attack.");
                }
            });
        }
    }

    private void StrikeTank(uint actionId)
    {
        if (Get(activeTank) is not { } tank) return;
        if (actionId == Plummet)
        {
            // Plummet is an instant frontal cleave, not a cast-bar tankbuster.
            var direction = tank.Position - (twin?.Position ?? Vector3.Zero);
            var facing = MathF.Atan2(direction.X, direction.Z);
            if (twin is { } caster)
                foreach (var role in roster)
                {
                    var member = Get(role);
                    if (member == null) continue;
                    var delta = member.Position - caster.Position;
                    var distance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
                    var angle = MathF.Atan2(delta.X, delta.Z);
                    var difference = MathF.Atan2(MathF.Sin(angle - facing), MathF.Cos(angle - facing));
                    if (distance <= 12f && MathF.Abs(difference) <= MathF.PI / 6f)
                        new DamageSolver(world.Party).ApplyDamage(member, 0.65f, Plummet, "Plummet frontal cleave", lethal: false);
                }
        }
        else
        {
            twin?.NativeCast(actionId, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, 0f, 3.7f,
                false, targetId: tank.GameObjectId);
            world.Events.Add(3.96f, () =>
                new DamageSolver(world.Party).ApplyDamage(tank, 0.95f, DeathSentence, "Death Sentence: tank swap", lethal: false));
        }
    }

    private void ScheduleTwisters()
    {
        // The cast bar finishes at t+2.0. Snapshot bait locations shortly before the finish,
        // then materialize the ground hazard at the finish. Checking a position sampled
        // at the same timestamp as the hit falsely kills every targeted moving player.
        twin?.NativeCast(Twister, ActionType.Action, 0f, 1.7f, false);
        var targets = roster.OrderBy(_ => Guid.NewGuid()).Take(4).ToList();
        if (settings.TwisterAlwaysOnMe && !targets.Contains(settings.Role))
            targets[0] = settings.Role;
        var selected = targets.Select(Get).Where(p => p != null).ToList();
        var baitPositions = new List<Vector3>();
        world.Events.Add(1.40f, () =>
        {
            baitPositions.Clear();
            baitPositions.AddRange(selected.Select(p => p!.Position));
        });
        world.Events.Add(1.96f, () =>
        {
            twisterSpots.AddRange(baitPositions);
            foreach (var point in baitPositions)
            {
                world.SpawnEventObject(new EventObjectSpawnConfig
                {
                    EObjId = TwisterVoidzoneEObj,
                    Placement = new Placement(point, 0f),
                    Radius = TwisterRadius,
                    Lifetime = 4f,
                    TargetableStatus = 1,
                });
            }
            // One contact check at creation, against the earlier bait locations.
            // Moving away must be possible; no delayed invisible explosion.
            foreach (var point in baitPositions)
                foreach (var victim in world.Party.Find.InsideCircle(point, TwisterRadius).ToList())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, TwisterAOE,
                        "Twister: standing at baited location", lethal: true);
        });
        world.Events.Add(6.5f, () => twisterSpots.Clear());
    }

    private void ScheduleFireball()
    {
        // For a bot-practice run, pick a target already at the designated stack
        // anchor rather than an uncontrolled player outside the bot stack.
        // TODO: drive bot movement from a randomly chosen marked target for retail fidelity.
        var stackAnchor = new Vector3(0f, 0f, 1f);
        var candidates = roster.Skip(2).Select(Get)
            .Where(p => p != null && Vector3.DistanceSquared(p.Position, stackAnchor) <= 9f)
            .ToList();
        var target = candidates.Count > 0
            ? candidates[world.Stream("ucob-fireball").Next(candidates.Count)]
            : Pick(roster.Skip(2).ToArray());
        if (target == null) return;
        // Fireball is instant/no castbar in retail. Play its action effect on impact.
        world.Events.Add(0.5f, () =>
            twin?.NativeActionEffect(Fireball, 0.6f, 0, 0, ActionType.Action, 0,
                animationTargetId: target.GameObjectId, actionTargetId: target.GameObjectId));
        world.Events.Add(0.5f, () =>
        {
            var members = world.Party.Find.InsideCircle(target.Position, 5f).ToList();
            if (members.Count == 0) return;
            var sharedDamage = 0.9f / members.Count;
            if (members.Count < 5)
                world.Fail($"Fireball: only {members.Count} in stack (need five).");
            foreach (var member in members)
                new DamageSolver(world.Party).ApplyDamage(member,
                    sharedDamage, Fireball, "Fireball: shared damage", lethal: false);
        });
    }

    private void ScheduleLiquidHells(bool randomTarget)
    {
        var candidates = randomTarget ? roster.Where(r => r != activeTank).ToArray()
            : new[] { PartyRole.PhysRangedDps };
        var target = Pick(candidates);
        if (target == null) return;
        var localPuddles = new List<Vector3>();
        for (var n = 0; n < 5; n++)
        {
            world.Events.Add(n * 1.15f, () =>
            {
                var point = target.Position;
                localPuddles.Add(point);
                twin?.NativeCast(LiquidHell, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action,
                    0f, 1f, false, position: point);
            });
        }
        world.Events.Add(5.8f, () =>
        {
            foreach (var point in localPuddles)
                foreach (var victim in world.Party.Find.InsideCircle(point, LiquidHellRadius).ToList())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, LiquidHell,
                        "Liquid Hell puddle", lethal: true);
            localPuddles.Clear();
        });
    }

    private void ScheduleHatch(int section)
    {
        var targets = roster.Skip(4).OrderBy(_ => Guid.NewGuid()).Take(section == 2 ? 2 : 1).ToArray();
        // NAUR customization: M1 always intercepts a Hatch assigned to the physical ranged baiter.
        for (var i = 0; i < targets.Length; i++)
        {
            var role = targets[i] == PartyRole.PhysRangedDps ? PartyRole.MeleeDpsA : targets[i];
            var character = Get(role);
            if (character == null) continue;
            var link = i == 0 ? new Vector3(0f, 0f, -8f) : new Vector3(-8f, 0f, 5f);
            twin?.NativeCast(Generate, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action,
                0f, 2.7f, false, targetId: character.GameObjectId);
            // The selected Hatch target must intercept at the Neurolink. The
            // original AI never moved for Generate, so every bot target died.
            // Do not automatically move the human-controlled seat.
            if (trainingBotsEnabled && role != settings.Role)
                character.MoveTo(link, speed: 6.5f);
            world.Events.Add(2.7f, () =>
            {
                if (Vector3.Distance(character.Position, link) > 2.5f)
                    new DamageSolver(world.Party).ApplyDamage(character, 1f, Generate,
                        "Hatch: intercept inside Neurolink", lethal: true);
            });
        }
    }
}
