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

    /// <summary>Finish the server-owned PP mission through its station NPC branch.</summary>
    public static void TurnIn(string address)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(address, "rookiepp", "rookiepp");
            Wait(session, () => session.Ready && session.World.Station?.Id == 10371 &&
                session.World.Missions.Any(mission => mission?.Name == "Waste Disposal" &&
                    mission.Stage == 2), "PP stage-two mission did not load at Arx Bursa.");
            StationContact contact = session.World.Station.Contacts.FirstOrDefault(item => item.Id == 371);
            if (contact == null) throw new Exception("Crones Christopher is not in the station contact list.");
            if (session.World.Station.CurrentRoom != contact.Room)
            {
                session.EnterStationRoom(contact.Room);
                Wait(session, () => session.World.Station?.CurrentRoom == contact.Room,
                    "Pilot could not reach Crones Christopher's room.");
            }
            ulong initialCredits = session.World.Credits;
            session.OpenNpc(371);
            Wait(session, () => session.World.Dialogue?.Choices.Any(choice => choice.Node == 2) == true,
                "Crones Christopher did not offer the native mission turn-in.");
            session.ChooseNpc(2);
            Wait(session, () => session.World.Missions.All(mission => mission?.Name != "Waste Disposal") &&
                session.World.Credits >= initialCredits + 200,
                "Mission turn-in did not clear the journal and award authoritative credits.");
        }
        Console.WriteLine("PASS: native PP mission 48 turn-in cleared journal and awarded credits");
    }
}
