using System;
using System.Collections.Generic;
using System.Linq;

namespace AnoMech.Scenarios.Ucob.P1Twintania;

// Event start times extracted from the supplied FF Logs report, fight 11.
// HP transitions are NOT observable as a cast: these are time slices for one pull.
public static class UcobP1Timeline
{
    public readonly record struct Event(float Time, Mechanic Kind);
    public enum Mechanic { Plummet, Twister, Fireball, DeathSentence, LiquidHell, Generate }

    private static readonly Event[] Recorded =
    [
        new(6.894f, Mechanic.Plummet), new(11.035f, Mechanic.Twister),
        new(16.250f, Mechanic.Fireball), new(20.351f, Mechanic.DeathSentence),
        new(35.810f, Mechanic.LiquidHell), new(41.634f, Mechanic.Generate),
        new(47.996f, Mechanic.LiquidHell), new(55.823f, Mechanic.DeathSentence),
        new(63.924f, Mechanic.Generate), new(68.056f, Mechanic.Twister),
        new(80.459f, Mechanic.LiquidHell), new(86.280f, Mechanic.Generate),
        new(92.551f, Mechanic.LiquidHell), new(99.525f, Mechanic.Fireball),
        new(104.640f, Mechanic.DeathSentence), new(111.797f, Mechanic.Plummet),
        new(115.929f, Mechanic.Generate), new(120.067f, Mechanic.Twister),
        new(127.183f, Mechanic.Plummet), new(129.455f, Mechanic.LiquidHell),
        new(135.277f, Mechanic.Generate)
    ];
    public static IReadOnlyList<Event> ForDrill(int drill) => drill switch
    {
        0 => Recorded,
        1 => Recorded.Where(e => e.Time < 35.810f).ToArray(),
        2 => Recorded.Where(e => e.Time >= 35.810f && e.Time < 80.459f)
                     .Select(e => e with { Time = e.Time - 35.810f }).ToArray(),
        3 => Recorded.Where(e => e.Time >= 80.459f)
                     .Select(e => e with { Time = e.Time - 80.459f }).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(drill))
    };
    public static int NeurolinksAt(float recordedTime) =>
        recordedTime < 35.810f ? 0 : recordedTime < 80.459f ? 1 : 2;
}
