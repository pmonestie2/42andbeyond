using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class SpaceMissionChecks
{
    /// <summary>Poll until the server publishes one requested mission state.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed.TotalSeconds > 12)
                throw new TimeoutException(failure + " Last status: " + session.Status);
            session.Poll();
        }
    }

    /// <summary>Choose a native 0x54 mission branch on a selected space navpoint.</summary>
    public static void Live(string address)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(address, "rookiepp", "rookiepp");
            Wait(session, () => session.Ready && session.World.Station == null &&
                session.World.Missions.Any(mission => mission?.Name == "Waste Disposal" &&
                    mission.Stage == 1), "PP stage-one mission did not load in space.");
            session.SelectTarget(103059);
            Wait(session, () => session.World.Dialogue?.Choices.Any(choice => choice.Node == 2) == true,
                "Selected Waste Disposal Station did not open its native space dialogue.");
            if (session.World.Station != null)
                throw new Exception("Space mission dialogue unexpectedly required a station.");
            session.ChooseNpc(2);
            Wait(session, () => session.World.Missions.Any(mission => mission?.Name ==
                "Waste Disposal" && mission.Stage == 2),
                "Native space-dialogue choice did not advance the durable mission.");
        }
        Console.WriteLine("PASS: selected space nav opened native dialogue and advanced PP mission 48");
    }
}
