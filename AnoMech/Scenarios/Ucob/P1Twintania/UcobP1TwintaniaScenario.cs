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
    private readonly List<(Vector3 Position, float Expires)> activeTwisters = [];
    private readonly List<Vector3> hellSpots = [];
    private readonly List<(Vector3 Position, float Expires)> activeLiquidHells = [];
    private PartyRole? liquidHellBaiter;
    public bool IsLiquidHellBaiter(PartyRole role) => liquidHellBaiter == role;
    private readonly List<Vector3> neurolinkPositions = [];
    private readonly Dictionary<PartyRole, Vector3> pendingHatches = [];
    private const uint NeurolinkEObj = 0x1E88FF;
    private const uint LiquidHellEObj = 0x1E88FE;
    public Vector2 DodgeForHatch(PartyRole role, Vector2 normal)
        => pendingHatches.TryGetValue(role, out var link) ? new Vector2(link.X + 3.6f, link.Z + 3f) : normal;
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
    private SimCharacter? fireballMarked;
    private const uint LiquidHell = 9901;
    private const uint Generate = 9902;
    private const float TwisterRadius = 1.25f; // retail proximity verified in bossmod
    private const float LiquidHellRadius = 6.0f;

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
        activeTwisters.Clear();
        hellSpots.Clear();
        activeLiquidHells.Clear();
        liquidHellBaiter = null;
        neurolinkPositions.Clear();
        pendingHatches.Clear();
        fireballMarked = null;
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
        // NA neurolink triangle; one link is left when Twintania crosses 75%,
        // a second at 45%, and the third on death. The times in a recorded
        // pull are proxies for those HP transitions, not live HP readings.
        if (settings.Section == 0)
        {
            world.Events.Add(31.0f, () => SpawnNeurolink(new Vector3(0, 0, -8)));
            world.Events.Add(32.0f, () => twin?.MoveTo(new Vector3(-8, 0, 5), 4f, -MathF.PI / 2f));
            world.Events.Add(76.0f, () => SpawnNeurolink(new Vector3(-8, 0, 5)));
            world.Events.Add(76.2f, () => twin?.MoveTo(new Vector3(8, 0, 5), 4f, MathF.PI / 2f));
            world.Events.Add(139.0f, () => SpawnNeurolink(new Vector3(8, 0, 5)));
        }
        else if (settings.Section == 2)
            world.Events.Add(0.3f, () => SpawnNeurolink(new Vector3(0, 0, -8)));
        else if (settings.Section == 3)
        {
            world.Events.Add(0.3f, () => SpawnNeurolink(new Vector3(0, 0, -8)));
            world.Events.Add(0.4f, () => SpawnNeurolink(new Vector3(-8, 0, 5)));
        }
        // Headmarker timestamps are independently recorded in FF Logs fight 11.
        if (settings.Section is 0 or 1)
            world.Events.Add(10.946f, () => MarkFireball(false));
        if (settings.Section == 0)
            world.Events.Add(92.373f, () => MarkFireball(true));
        if (settings.Section == 3)
            world.Events.Add(11.914f, () => MarkFireball(true));
        var section = Math.Clamp(settings.Section, 0, 3);
        var events = UcobP1Timeline.ForDrill(section);
        foreach (var item in events)
        {
            var at = item.Time;
            var kind = item.Kind;
            world.Events.Add(at, () => Execute(kind switch
            {
                UcobP1Timeline.Mechanic.DeathSentence => "Death Sentence",
                UcobP1Timeline.Mechanic.LiquidHell => "Liquid Hell",
                _ => kind.ToString()
            }, section, at));
        }
        var endAt = events.Max(x => x.Time) + 12f;
        if (settings.Section == 0)
            world.Events.Add(139.330f, () => twin?.Despawn());
        world.Events.Add(endAt, () =>
        {
            Natives.UserActions.Acted -= OnPlayerAction;
            twin?.Despawn();
        });
    }

    // Retail Neurolink status (344) applies only while standing in a link.
    public void Tick(float delta, float elapsed)
    {
        currentElapsed = elapsed;
        activeTwisters.RemoveAll(t => elapsed >= t.Expires);
        activeLiquidHells.RemoveAll(h => elapsed >= h.Expires);
        foreach (var puddle in activeLiquidHells)
            foreach (var victim in world.Party.Find.InsideCircle(puddle.Position, LiquidHellRadius).ToList())
                if (victim.IsAlive())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, LiquidHell,
                        "Liquid Hell: stood in active flame", lethal: true);
        foreach (var hazard in activeTwisters)
            foreach (var victim in world.Party.Find.InsideCircle(hazard.Position, TwisterRadius).ToList())
                new DamageSolver(world.Party).ApplyDamage(victim, 1f, TwisterAOE,
                    "Twister: stepped into lingering tornado", lethal: true);
        foreach (var role in roster)
        {
            var character = Get(role);
            if (character == null) continue;
            var inside = neurolinkPositions.Any(link => Vector3.DistanceSquared(character.Position, link) <= 4f);
            if (inside && !character.HasStatus(344)) character.AddStatus(344);
            else if (!inside && character.HasStatus(344)) character.RemoveStatus(344);
        }
    }

    private void OnPlayerAction(PlayerAction action)
    {
        if (!awaitingPlayerProvoke || action.Step != PlayerActionStep.Resolved || action.ActionId != ProvokeAction)
            return;
        if (settings.Role != PartyRole.MainTank || twin == null || action.TargetId != twin.GameObjectId)
            return;
        activeTank = PartyRole.MainTank;
        swapComplete = true;
        awaitingPlayerProvoke = false;
        twin.SetTarget(Get(activeTank), follow: false);
    }

    private void BotSwapToOffTank()
    {
        activeTank = PartyRole.OffTank;
        otHasTakenFirstSwap = true;
        swapComplete = true;
        awaitingPlayerProvoke = false;
        twin?.SetTarget(Get(activeTank), follow: false);
    }

    private SimCharacter? Get(PartyRole role) => world.Party.Get(role);
    private SimCharacter? Pick(IReadOnlyList<PartyRole> options)
    {
        var candidates = options.Select(Get).Where(p => p != null).ToArray();
        return candidates.Length == 0 ? null : candidates[world.Stream("ucob-pick").Next(candidates.Length)];
    }

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
        twin?.SetTarget(Get(activeTank), follow: false);
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
            ScheduleLiquidHells(section == 0 ? at > 90f && at < 100f : section == 3 && at > 10f && at < 15f);
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
                world.Events.Add(2.8f, () => { activeTank = PartyRole.MainTank; swapComplete = true; twin?.SetTarget(Get(activeTank), follow: false); });
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
        twin?.NativeCast(Twister, ActionType.Action, 0f, 1.96f, false);
        var targets = world.Stream("ucob-twister-targets").Shuffle(roster).Take(4).ToList();
        if (settings.TwisterAlwaysOnMe && !targets.Contains(settings.Role))
            targets[0] = settings.Role;
        var selected = targets.Select(Get).Where(p => p != null).ToList();
        var baitPositions = new List<Vector3>();
        world.Events.Add(1.40f, () =>
        {
            baitPositions.Clear();
            baitPositions.AddRange(selected.Select(p => p!.Position));
        });
        // Training bots must dodge AFTER bait snapshot, irrespective of competing
        // AI/Hatch commands. Move away from the actual sampled position, not
        // from a pre-scripted waypoint that may already be occupied.
        if (trainingBotsEnabled)
            world.Events.Add(1.43f, () =>
            {
                foreach (var role in roster)
                {
                    if (role == settings.Role) continue;
                    var bot = Get(role);
                    if (bot == null || !bot.IsAlive()) continue;
                    var current = bot.Position;
                    var direction = new Vector2(current.X, current.Z);
                    if (direction.LengthSquared() < 0.01f)
                        direction = new Vector2(1f, 0f);
                    direction = Vector2.Normalize(direction);
                    var destination = current + new Vector3(direction.X * 3.3f, 0f, direction.Y * 3.3f);
                    bot.MoveTo(destination, speed: 8.3f);
                }
            });
        world.Events.Add(1.96f, () =>
        {
            twisterSpots.AddRange(baitPositions);
            activeTwisters.AddRange(baitPositions.Select(p => (Position: p, Expires: currentElapsed + 4f)));
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

    private void MarkFireball(bool overlappingLiquidHells)
    {
        // Ranged baiter is left out of the later Fireball stack in this
        // default NA training strategy while Liquid Hells are still dropping.
        var possible = roster.Skip(2)
            .Where(r => !overlappingLiquidHells || r != PartyRole.PhysRangedDps)
            .Select(Get).Where(p => p != null).ToList();
        if (possible.Count == 0) return;
        fireballMarked = possible[world.Stream("ucob-fireball-marker").Next(possible.Count)];
        fireballMarked?.AttachLockonVfx(117);
    }

    private void ScheduleFireball()
    {
        // FF Logs action timestamp is the hit, not the headmarker application.
        var target = fireballMarked ?? Pick(roster.Skip(2).ToArray());
        if (target == null) return;
        fireballMarked = null;
        twin?.NativeActionEffect(Fireball, 0.6f, 0, 0, ActionType.Action, 0,
            animationTargetId: target.GameObjectId, actionTargetId: target.GameObjectId);
        var members = world.Party.Find.InsideCircle(target.Position, 4f).ToList();
        if (members.Count == 0) return;
        if (members.Count < 5)
            world.Fail($"Fireball: only {members.Count} in stack (need five).");
        var sharedDamage = 0.9f / members.Count;
        foreach (var member in members)
            new DamageSolver(world.Party).ApplyDamage(member,
                sharedDamage, Fireball, "Fireball: shared damage", lethal: false);
    }

    private void ScheduleLiquidHells(bool randomTarget)
    {
        // Retail: one target is chosen for all five puddles. The initial sets
        // use the farthest player (NA: physical ranged); the 45%-HP set randomizes.
        var options = randomTarget
            ? roster.Where(r => r != activeTank && r != settings.Role && Get(r) != fireballMarked).ToArray()
            : [PartyRole.PhysRangedDps];
        var target = Pick(options);
        if (target == null) return;
        var baitRole = roster.First(r => ReferenceEquals(Get(r), target));
        liquidHellBaiter = baitRole;
        var start = target.Position;
        var angle = MathF.Atan2(start.Z, start.X);

        for (var n = 0; n < 5; n++)
        {
            var index = n;
            world.Events.Add(index * 1.155f, () =>
            {
                var point = target.Position;
                twin?.NativeCast(LiquidHell, ActionType.Action, 0f, 0.1f, false, position: point);
                // The damaging persistent voidzone appears after the impact,
                // not at the time the target position is sampled.
                world.Events.Add(1.8f, () =>
                {
                    world.SpawnEventObject(new EventObjectSpawnConfig
                    {
                        EObjId = LiquidHellEObj,
                        Placement = new Placement(point, 0f),
                        Radius = LiquidHellRadius,
                        Lifetime = 6f,
                        TargetableStatus = 1,
                    });
                    activeLiquidHells.Add((point, currentElapsed + 6f));
                });
                if (randomTarget && trainingBotsEnabled && baitRole != settings.Role)
                {
                    var nextAngle = angle + (index + 1) * 0.5f;
                    var destination = new Vector3(16f * MathF.Cos(nextAngle), 0f, 16f * MathF.Sin(nextAngle));
                    world.Events.Add(0.12f, () => target.MoveTo(destination, speed: 8.3f));
                }
            });
        }
        world.Events.Add(7.6f, () =>
        {
            if (liquidHellBaiter == baitRole) liquidHellBaiter = null;
        });
    }

    private void SpawnNeurolink(Vector3 position)
    {
        neurolinkPositions.Add(position);
        world.SpawnEventObject(new EventObjectSpawnConfig
        {
            EObjId = NeurolinkEObj,
            Placement = new Placement(position, 0f),
            Radius = 2f,
            Lifetime = 300f,
            TargetableStatus = 1,
        });
    }

    private void ScheduleHatch(int section)
    {
        if (neurolinkPositions.Count == 0) return;
        var targets = world.Stream("ucob-hatch-targets").Shuffle(roster.Skip(4))
            .Take(Math.Min(neurolinkPositions.Count, 2)).ToArray();
        var assignedRoles = new HashSet<PartyRole>();
        var recordedAt = mechanicAt + (settings.Section switch { 2 => 35.810f, 3 => 80.459f, _ => 0f });
        twin?.NativeCast(Generate, ActionType.Action, 0f, 2.98f, false);
        // The NA caster intercepts the physical ranged player's orb while D3 baits.
        for (var i = 0; i < targets.Length; ++i)
        {
            var assigned = targets[i] == PartyRole.PhysRangedDps ? PartyRole.CasterDps : targets[i];
            if (!assignedRoles.Add(assigned))
            {
                assigned = new[] { PartyRole.MeleeDpsA, PartyRole.MeleeDpsB, PartyRole.CasterDps }
                    .First(role => !assignedRoles.Contains(role));
                assignedRoles.Add(assigned);
            }
            var character = Get(assigned);
            if (character == null) continue;
            var link = neurolinkPositions[i];
            pendingHatches[assigned] = link;
            character.AttachLockonVfx(118);
            SimEnemy? oviform = null;
            world.Events.Add(2.98f, () =>
            {
                oviform = world.SpawnEnemy(new EnemySpawnConfig(
                    BNpcBaseId: 0x1FE0, // real UCOB Hatch orb (Oviform)
                    NameId: 0, Level: UcobConstants.Level,
                    Targetable: false, EnemyList: EnemyListMode.Never,
                    Visibility: SpawnVisibility.Visible,
                    Placement: new Placement(twin?.Position ?? Vector3.Zero, 0)));
            });
            world.Events.Add(3.15f, () => oviform?.Follow(character, speed: 5f));
            if (trainingBotsEnabled && assigned != settings.Role)
                character.MoveTo(link, speed: 6.5f);
            // Recorded hatch hits (from FF Logs): 47.684, 71.611,
            // 92.283/93.172, 122.999/123.445. Last cast precedes boss death.
            float[] delays = recordedAt switch
            {
                > 130f => [],
                > 110f => [7.070f, 7.516f],
                > 80f => [6.003f, 6.892f],
                > 60f => [7.687f],
                _ => [6.050f]
            };
            if (i >= delays.Length) continue;
            var hitDelay = delays[i];
            if (recordedAt is > 60f and < 70f or > 110f and < 120f)
            {
                // Leave the Neurolink BEFORE Twister snapshots at 1.4s into
                // its cast, then dodge sideways and return via a safe waypoint.
                world.Events.Add(4.8f, () =>
                {
                    if (trainingBotsEnabled && assigned != settings.Role)
                        character.MoveTo(link + new Vector3(2.8f, 0, 0), speed: 6.5f);
                });
                world.Events.Add(6.15f, () =>
                {
                    if (trainingBotsEnabled && assigned != settings.Role)
                        character.MoveTo(link + new Vector3(0, 0, 3f), speed: 8.3f);
                });
                world.Events.Add(6.66f, () =>
                {
                    if (trainingBotsEnabled && assigned != settings.Role)
                        character.MoveTo(link, speed: 8.3f);
                });
            }
            world.Events.Add(hitDelay, () =>
            {
                oviform?.NativeActionEffect(9903, 0.6f, 0, 0, ActionType.Action, 0,
                    animationTargetId: character.GameObjectId, actionTargetId: character.GameObjectId);
                oviform?.Despawn();
                pendingHatches.Remove(assigned);
                if (Vector3.Distance(character.Position, link) > 2.5f || character.HasStatus(1434))
                    new DamageSolver(world.Party).ApplyDamage(character, 1f, Generate,
                        "Hatch: orb not intercepted in Neurolink or Mana Hypersensitivity active", lethal: true);
                else
                    character.AddStatus(1434, duration: 16f);
            });
        }
    }

}
