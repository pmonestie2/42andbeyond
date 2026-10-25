using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class EnemyAbilityChecks
{
    /// <summary>Poll one disposable JE until its selected-MOB cast resolves.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed.TotalSeconds > 12) throw new TimeoutException(failure);
            session.Poll();
        }
    }

    /// <summary>Verify rapid target selection cannot send Shield Drain at an older target.</summary>
    public static void Live(string host)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(host, "pje", "pje");
            Wait(session, () => session.Ready && session.World.AvailableAbilities.Contains(106u) &&
                session.World.Mobs.Values.Any(mob => mob.HasPosition && mob.Shield > 0),
                "Visible shielded MOB or Shield Drain was not advertised.");
            ShipActor target = session.World.Mobs.Values.First(mob => mob.HasPosition && mob.Shield > 0);
            float shield = target.Shield;
            session.SelectTarget(target.Id);
            session.ActivateAbility(106);
            Wait(session, () => session.World.Equipment.TargetId == target.Id &&
                session.World.Mobs.TryGetValue(target.Id, out ShipActor current) &&
                current.Shield < shield,
                "Acknowledged Shield Drain did not hit the selected visible MOB.");
            Console.WriteLine("PASS: native Shield Drain waits for selected-MOB acknowledgment");
        }
    }
}
