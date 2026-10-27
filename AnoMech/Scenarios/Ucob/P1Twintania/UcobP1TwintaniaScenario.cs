using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Scenarios.Legacy;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
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
    private const uint TwintaniaNameId = 3210; // TEMP: adjust to correct UCOB Twintania NameId from replay
    private const uint Plummet = 9896;
    private const uint DeathSentence = 9897;
    private const uint Fireball = 9900;
    private const uint LiquidHell = 9901;
    private const uint Generate = 9902;
    private const float TwisterRadius = 2.2f;
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
        if (selectedAi is { } ai && ai == 0)
            new UcobP1TwintaniaAi().Run(this, world);
        world.Events.Add(0.15f, SpawnTwintania);
        // Each interval is a teaching timeline, not a claimed FFXIV retail timestamp.
        string[][] order = [
            ["Plummet", "Twister", "Fireball", "Death Sentence", "Plummet", "Twister", "Fireball"],
            ["Liquid Hell", "Generate", "Liquid Hell", "Death Sentence", "Generate", "Twister", "Plummet"],
            ["Liquid Hell", "Generate", "Liquid Hell + Fireball", "Death Sentence", "Plummet", "Generate", "Twister", "Plummet"]
        ];
        var section = Math.Clamp(settings.Section, 0, 2);
        for (var i = 0; i < order[section].Length; i++)
        {
            var stage = order[section][i];
            var at = 5f + i * 10f;
            world.Events.Add(at, () => Execute(stage, section, at));
        }
        world.Events.Add(5f + order[section].Length * 10f, () => twin?.Despawn());
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
    }

    private void Execute(string mechanic, int section, float at)
    {
        mechanicAt = at;
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
            activeTank = activeTank == PartyRole.MainTank ? PartyRole.OffTank : PartyRole.MainTank;
            twin?.SetTarget(Get(activeTank));
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
        var selection = roster.OrderBy(_ => Guid.NewGuid()).Take(4).ToList();
        if (settings.TwisterAlwaysOnMe && !selection.Contains(settings.Role))
            selection[0] = settings.Role;
        // Snapshot at the end of the provisional 3s cast, not at its beginning.
        var initial = selection.Select(Get).Where(p => p != null).ToList();
        var snapshots = new List<Vector3>();
        world.Events.Add(mechanicAt + 2.7f, () =>
        {
            snapshots.Clear();
            snapshots.AddRange(initial.Select(p => p!.Position));
            twisterSpots.AddRange(snapshots);
        });
        world.Events.Add(mechanicAt + 3.0f, () =>
        {
            foreach (var point in snapshots)
                foreach (var victim in world.Party.Find.InsideCircle(point, TwisterRadius).ToList())
                    new DamageSolver(world.Party).ApplyDamage(victim, 1f, 0, "Twister: move during cast", lethal: true);
        });
        world.Events.Add(mechanicAt + 6.5f, () => twisterSpots.Clear());
    }

    private void ScheduleFireball()
    {
        var target = Pick(roster.Skip(2).ToArray());
        if (target == null) return;
        twin?.NativeCast(Fireball, FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action,
            0f, 2f, false, targetId: target.GameObjectId);
        world.Events.Add(mechanicAt + 2.3f, () =>
        {
            var members = world.Party.Find.InsideCircle(target.Position, 5f).ToList();
            foreach (var member in members)
                new DamageSolver(world.Party).ApplyDamage(member,
                    members.Count >= 5 ? 0.2f : 0.85f, Fireball,
                    "Fireball: insufficient stack", lethal: false);
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
        // Caster intercepts first Hatch assigned to physical ranged during liquid bait.
        for (var i = 0; i < targets.Length; i++)
        {
            var role = targets[i] == PartyRole.PhysRangedDps ? PartyRole.CasterDps : targets[i];
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
