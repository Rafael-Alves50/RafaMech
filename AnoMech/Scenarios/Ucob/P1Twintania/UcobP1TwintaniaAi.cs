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
        // Five-man Fireball after Twister, tanks + one healer out, ranged returns to bait.
        ai.Move(16.0f, () => AiMove.Create(
            new Vector2(-1.5f, -13f),
            new Vector2(-4f, -11f),
            new Vector2(-7f, -2.5f),
            new Vector2(0f, 1.0f),
            new Vector2(-1.2f, 1.0f),
            new Vector2(1.2f, 1.0f),
            new Vector2(0f, 2.0f),
            new Vector2(0f, 0.0f)
        ).NaturalOrder());
        ai.Move(33f, () => AiMove.Create(
            new Vector2(-1.5f, -13f),
            new Vector2(-4f, -11f),
            new Vector2(-7f, -2.5f),
            new Vector2(7f, -2.5f),
            new Vector2(-4f, 0f),
            new Vector2(4f, 0f),
            new Vector2(-13f, 11f),
            new Vector2(11f, 5f)
        ).NaturalOrder());
    }
}
