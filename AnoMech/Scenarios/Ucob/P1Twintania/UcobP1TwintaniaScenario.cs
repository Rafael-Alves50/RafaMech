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
        var options = new[] { "100–75%: opener", "75–45%: one Neurolink", "45–0%: two Neurolinks" };
        if (ImGui.Combo("Practice section", ref section, options, options.Length))
            settings.Section = section;
        ImGui.TextWrapped("Training build: cast timings, boss model and mechanic resolution are provisional.");
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
        AnoMech.Core.Native.Interfaces.Natives.UserActions.Acted -= OnPlayerAction;
        AnoMech.Core.Native.Interfaces.Natives.UserActions.Acted += OnPlayerAction;
        if (selectedAi is { } ai && ai == 0)
            new UcobP1TwintaniaAi().Run(this, world);
        world.Events.Add(0.15f, SpawnTwintania);
        // P1 timing reference: UCOB retail timeline (seconds from pull) published
        // on the Lodestone. HP-dependent section timing remains approximate.
        // The opener follows the documented retail order: Twister, Fireball, Death Sentence.
        (float At, string Mechanic)[][] tracks =
        [
            [(8f, "Plummet"), (11f, "Twister"), (16f, "Fireball"),
             (19f, "Death Sentence"), (28f, "Plummet"), (30f, "Twister"),
             (35f, "Fireball")],
            [(8f, "Liquid Hell"), (13f, "Generate"), (20f, "Liquid Hell"),
             (25f, "Death Sentence"), (33f, "Generate"), (37f, "Twister"),
             (46f, "Plummet")],
            [(8f, "Liquid Hell"), (13f, "Generate"), (20f, "Liquid Hell + Fireball"),
             (26f, "Death Sentence"), (34f, "Plummet"), (37f, "Generate"),
             (41f, "Twister"), (50f, "Plummet")]
        ];
        var section = Math.Clamp(settings.Section, 0, 2);
        foreach (var item in tracks[section])
        {
            var at = item.At;
            var mechanic = item.Mechanic;
            world.Events.Add(at, () => Execute(mechanic, section, at));
        }
        var endAt = tracks[section].Max(x => x.At) + 12f;
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
        if (settings.Section == 1)
            twin?.SetPosition(new Placement(new Vector3(-12f, 0f, 5f), -MathF.PI / 2f));
        else if (settings.Section == 2)
            twin?.SetPosition(new Placement(new Vector3(0f, 0f, 0f), MathF.PI));
    }

    private void Execute(string mechanic, int section, float at)
    {
        mechanicAt = at;
        if (section == 2 && mechanic == "Plummet" && activeTank == PartyRole.MainTank)
            twin?.MoveTo(new Vector3(12f, 0f, 5f), 4f, MathF.PI / 2f);
        if (mechanic.Contains("Liquid Hell"))
            ScheduleLiquidHells(section == 2 && mechanic.Contains("+"));
        if (mechanic.Contains("Fireball"))
            ScheduleFireball();
        if (mechanic == "Generate")
            ScheduleHatch(section);
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
                    world.Events.Add(at + 2.8f, BotSwapToOffTank);
                else
                    world.Events.Add(at + 2.8f, BotSwapToOffTank);
            }
            else if (settings.Role == PartyRole.MainTank)
            {
                awaitingPlayerProvoke = true;
            }
            else
            {
                // Non-MT practice currently assumes scripted bot swap to the MT.
                world.Events.Add(at + 2.8f, () => { activeTank = PartyRole.MainTank; swapComplete = true; twin?.SetTarget(Get(activeTank)); });
            }
            world.Events.Add(at + 4.6f, () =>
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
            twin?.NativeCast(actionId, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, 0f, 4f,
                false, targetId: tank.GameObjectId);
            world.Events.Add(mechanicAt + 4.1f, () =>
                new DamageSolver(world.Party).ApplyDamage(tank, 0.95f, DeathSentence, "Death Sentence: tank swap", lethal: false));
        }
    }

    private void ScheduleTwisters()
    {
        // The real Twister is a 2s Twintania cast; resolve at the end of the castbar.
        twin?.NativeCast(Twister, ActionType.Action, 0f, 2.0f, false);
        var selection = roster.OrderBy(_ => Guid.NewGuid()).Take(4).ToList();
        if (settings.TwisterAlwaysOnMe && !selection.Contains(settings.Role))
            selection[0] = settings.Role;
        // The actual Twister hazard is an EObj voidzone, not an action omen.
        // The 9899 action has no client omen file: SpawnActionOmen silently draws nothing.
        var initial = selection.Select(Get).Where(p => p != null).ToList();
        var snapshots = new List<Vector3>();
        world.Events.Add(mechanicAt + 2.0f, () =>
        {
            snapshots.Clear();
            snapshots.AddRange(initial.Select(p => p!.Position));
            twisterSpots.AddRange(snapshots);
            foreach (var spot in snapshots)
                world.SpawnEventObject(new EventObjectSpawnConfig
                {
                    EObjId = TwisterVoidzoneEObj,
                    Placement = new Placement(spot, 0f),
                    Radius = TwisterRadius,
                    Lifetime = 4f,
                    TargetableStatus = 1,
                });
        });
        // Collision checks occur at creation: the hazard is active immediately as the cast ends.
        // No delayed unconditional AOE blast (which previously made it seem tied to the tankbuster).
        world.Events.Add(mechanicAt + 2.0f, () =>
        {
            foreach (var point in snapshots)
                foreach (var victim in world.Party.Find.InsideCircle(point, TwisterRadius).ToList())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, TwisterAOE, "Twister contact at spawn", lethal: true);
        });
        world.Events.Add(mechanicAt + 6.5f, () => twisterSpots.Clear());
    }

    private void ScheduleFireball()
    {
        var target = Pick(roster.Skip(2).ToArray());
        if (target == null) return;
        // Fireball is instant/no castbar in retail. Play its action effect on impact.
        var fireballAt = mechanicAt;
        world.Events.Add(fireballAt, () =>
            twin?.NativeActionEffect(Fireball, 0.6f, 0, 0, ActionType.Action, 0,
                animationTargetId: target.GameObjectId, actionTargetId: target.GameObjectId));
        world.Events.Add(mechanicAt + 3.8f, () =>
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
        hellSpots.Clear();
        for (var n = 0; n < 5; n++)
        {
            world.Events.Add(mechanicAt + n * 1.15f, () =>
            {
                var point = target.Position;
                hellSpots.Add(point);
                twin?.NativeCast(LiquidHell, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action,
                    0f, 1f, false, position: point);
            });
        }
        world.Events.Add(mechanicAt + 5.8f, () =>
        {
            foreach (var point in hellSpots)
                foreach (var victim in world.Party.Find.InsideCircle(point, LiquidHellRadius).ToList())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, LiquidHell,
                        "Liquid Hell puddle", lethal: true);
            hellSpots.Clear();
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
                0f, 2f, false, targetId: character.GameObjectId);
            world.Events.Add(mechanicAt + 6f, () =>
            {
                if (Vector3.Distance(character.Position, link) > 2.5f)
                    new DamageSolver(world.Party).ApplyDamage(character, 1f, Generate,
                        "Hatch: intercept inside Neurolink", lethal: true);
            });
        }
    }
}
