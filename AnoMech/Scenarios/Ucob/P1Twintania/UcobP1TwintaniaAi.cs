using System.Numerics;
using AnoMech.Core.Game.Ai;
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
        ai.Move(12.50f, () => AiMove.Create(
            new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
            new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
            new Vector2(-6f, 0f), new Vector2(6f, 0f),
            new Vector2(-15f, 11f), new Vector2(13f, 5f)
        ).NaturalOrder());
        // Begin gathering after the Twister resolves at ~13.0s; allow travel time before Fireball at ~16.75s.\n        // Six non-tank stack seats allow five bots to stack even when a player-controlled seat stays out.
        ai.Move(13.05f, () => AiMove.Create(
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
                new Vector2(-13f, 11f), new Vector2(11f, 5f)
            ).NaturalOrder());
            // Second Twister: leave the baited ground after snapshot.
            ai.Move(69.50f, () => AiMove.Create(
                new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
                new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
                new Vector2(-6f, 0f), new Vector2(6f, 0f),
                new Vector2(-15f, 11f), new Vector2(13f, 5f)
            ).NaturalOrder());
            ai.Move(75f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-7f, -2.5f), new Vector2(7f, -2.5f),
                new Vector2(-4f, 0f), new Vector2(4f, 0f),
                new Vector2(-13f, 11f), new Vector2(11f, 5f)
            ).NaturalOrder());
            // Second observed Fireball at t=99.525.
            ai.Move(96f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-0.8f, 1.8f), new Vector2(0f, 1f),
                new Vector2(-1.2f, 1f), new Vector2(1.2f, 1f),
                new Vector2(0f, 2f), new Vector2(0f, 0f)
            ).NaturalOrder());
            ai.Move(109f, () => AiMove.Create(
                new Vector2(-1.5f, -13f), new Vector2(-4f, -11f),
                new Vector2(-7f, -2.5f), new Vector2(7f, -2.5f),
                new Vector2(-4f, 0f), new Vector2(4f, 0f),
                new Vector2(-13f, 11f), new Vector2(11f, 5f)
            ).NaturalOrder());
            // Third Twister t=120.067–122.023.
            ai.Move(121.55f, () => AiMove.Create(
                new Vector2(-3.5f, -13f), new Vector2(-6f, -11f),
                new Vector2(-9f, -2.5f), new Vector2(9f, -2.5f),
                new Vector2(-6f, 0f), new Vector2(6f, 0f),
                new Vector2(-15f, 11f), new Vector2(13f, 5f)
            ).NaturalOrder());
        }
    }
}
