using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Ucob.P1Twintania;

// Training bots use the simulation's normal pathing (AiManager), not teleportation.
// Times match the P1 teaching timeline; fine timing will need a retail replay.
public sealed class UcobP1TwintaniaAi : IScenarioAi<UcobP1TwintaniaScenario>
{
    public string Name => "NAUR practice (training bots)";

    public void Run(UcobP1TwintaniaScenario scenario, SimWorld world)
    {
        var ai = new AiManager(world);
        ai.Move(0.6f, () => AiMove.Create(
            new Vector2(-1.5f, -13f), // MT facing boss northward
            new Vector2(-4f, -11f),
            new Vector2(-7f, -2.5f),
            new Vector2(7f, -2.5f),
            new Vector2(-4f, 0.0f),
            new Vector2(4f, 0.0f),
            new Vector2(-13f, 11f),
            new Vector2(11f, 5f)
        ).NaturalOrder());
        // Move at ~75% of cast (11s–13s); movement must start BEFORE the ground Twisters spawn.
        ai.Move(12.10f, () => AiMove.Create(
            new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
            new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
            new Vector2(-6f, 0f), new Vector2(6f, 0f),
            new Vector2(-15f, 11f), new Vector2(13f, 5f)
        ).NaturalOrder());
        // Wait until the Twister has resolved and bots have cleared their bait positions before converging.
        // Six non-tank stack seats allow five bots to stack even when a player-controlled seat stays out.
        ai.Move(14.25f, () => AiMove.Create(
            new Vector2(-1.5f, -13f),
            new Vector2(-4f, -11f),
            new Vector2(-0.8f, 1.8f),
            new Vector2(0f, 1.0f),
            new Vector2(-1.2f, 1.0f),
            new Vector2(1.2f, 1.0f),
            new Vector2(0f, 2.0f),
            new Vector2(0f, 0.0f)
        ).NaturalOrder());
        // Complete-pull mode: reset after the first stack and handle both later Twisters.
        if (scenario.FullPull)
        {
            // Move away from the first Fireball and make room for Liquid Hell baits.
            ai.Move(26f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-7f, -2.5f), new Vector2(7f, -2.5f),
                new Vector2(-4f, 0f), new Vector2(4f, 0f),
                new Vector2(-13f, 11f), new Vector2(11f, -5f)
            ).NaturalOrder());
            // Proximity Liquid Hell volleys: D3 follows the arena edge.
            // Reverse the second volley to avoid crossing the preceding voidzones.
            // The 92.551s volley is random and handled by the scenario.
            foreach (var (start, reverse) in new (float Start, bool Reverse)[]
            {
                (35.810f, false), (47.996f, true),
                (80.459f, false), (129.455f, false)
            })
            {
                for (var step = 0; step <= 5; step++)
                {
                    var at = start + (step == 0 ? -0.5f : (step - 1) * 1.155f + 0.12f);
                    var x = reverse
                        ? (step == 5 ? -15.6f : 15.6f - step * 4.4f)
                        : (step == 5 ? 15.6f : -13f + step * 4.4f);
                    world.Events.Add(at, () =>
                    {
                        var ranged = world.Party.Get(PartyRole.PhysRangedDps);
                        if (ranged != null && ranged.IsAlive())
                            ranged.MoveTo(new Vector3(x, 0f, 11f), speed: 6.5f);
                    });
                }
            }
            // Second Twister: leave the baited ground after snapshot.
            ai.Move(69.50f, () => AiMove.Create(
                new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
                new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
                scenario.DodgeForHatch(PartyRole.MeleeDpsA, new Vector2(-6f, 0f)),
                scenario.DodgeForHatch(PartyRole.MeleeDpsB, new Vector2(6f, 0f)),
                scenario.DodgeForHatch(PartyRole.PhysRangedDps, new Vector2(-15f, 11f)),
                scenario.DodgeForHatch(PartyRole.CasterDps, new Vector2(13f, 5f))
            ).NaturalOrder());
            ai.Move(75f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-7f, -2.5f), new Vector2(7f, -2.5f),
                new Vector2(-4f, 0f), new Vector2(4f, 0f),
                new Vector2(-13f, 11f), new Vector2(11f, -5f)
            ).NaturalOrder());
            // Second observed Fireball at t=99.525.
            ai.Move(96f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                scenario.IsLiquidHellBaiter(PartyRole.RegenHealer) ? (Vector2?)null : new Vector2(-0.8f, 1.8f),
                scenario.IsLiquidHellBaiter(PartyRole.ShieldHealer) ? (Vector2?)null : new Vector2(0f, 1f),
                scenario.IsLiquidHellBaiter(PartyRole.MeleeDpsA) ? (Vector2?)null : new Vector2(-1.2f, 1f),
                scenario.IsLiquidHellBaiter(PartyRole.MeleeDpsB) ? (Vector2?)null : new Vector2(1.2f, 1f),
                scenario.IsLiquidHellBaiter(PartyRole.PhysRangedDps) ? (Vector2?)null : new Vector2(0f, 2f),
                scenario.IsLiquidHellBaiter(PartyRole.CasterDps) ? (Vector2?)null : new Vector2(0f, 0f)
            ).NaturalOrder());
            ai.Move(109f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-7f, -2.5f), new Vector2(7f, -2.5f),
                new Vector2(-4f, 0f), new Vector2(4f, 0f),
                new Vector2(-13f, 11f), new Vector2(11f, -5f)
            ).NaturalOrder());
            // Third Twister t=120.067–122.023. Hatch baiters move
            // a short distance away from their Neurolink, then return.
            ai.Move(121.55f, () => AiMove.Create(
                new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
                new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
                scenario.DodgeForHatch(PartyRole.MeleeDpsA, new Vector2(-6f, 0f)),
                scenario.DodgeForHatch(PartyRole.MeleeDpsB, new Vector2(6f, 0f)),
                scenario.DodgeForHatch(PartyRole.PhysRangedDps, new Vector2(-15f, 11f)),
                scenario.DodgeForHatch(PartyRole.CasterDps, new Vector2(13f, 5f))
            ).NaturalOrder());
        }
    }
}
