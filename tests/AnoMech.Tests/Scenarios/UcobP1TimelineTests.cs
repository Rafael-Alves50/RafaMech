using System.Linq;
using AnoMech.Scenarios.Ucob.P1Twintania;
using NUnit.Framework;

namespace AnoMech.Tests;

public class UcobP1TimelineTests
{
    [Test]
    public void CompleteRecordedPullHasAllExpectedMechanics()
    {
        var events = UcobP1Timeline.ForDrill(0);
        Assert.That(events.Count, Is.EqualTo(21));
        Assert.That(events.Count(e => e.Kind == UcobP1Timeline.Mechanic.Twister), Is.EqualTo(3));
        Assert.That(events.Count(e => e.Kind == UcobP1Timeline.Mechanic.Generate), Is.EqualTo(5));
        Assert.That(events.Count(e => e.Kind == UcobP1Timeline.Mechanic.LiquidHell), Is.EqualTo(5));
        Assert.That(events.Count(e => e.Kind == UcobP1Timeline.Mechanic.Fireball), Is.EqualTo(2));
        Assert.That(events.Select(e => e.Time), Is.Ordered);
        Assert.That(events.Last().Time, Is.EqualTo(135.277f));
    }

    [Test]
    public void SplitDrillsPreserveWholePullEventCount()
    {
        var whole = UcobP1Timeline.ForDrill(0);
        var total = Enumerable.Range(1, 3).Sum(i => UcobP1Timeline.ForDrill(i).Count);
        Assert.That(total, Is.EqualTo(whole.Count));
        foreach (var i in Enumerable.Range(1, 3))
            Assert.That(UcobP1Timeline.ForDrill(i).First().Time, Is.GreaterThanOrEqualTo(0f));
    }
}
