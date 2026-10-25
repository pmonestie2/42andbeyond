using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class CharacterRosterChecks
{
    /// <summary>Poll the disposable session until the server publishes the requested state.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            if (clock.Elapsed.TotalSeconds > 12)
                throw new TimeoutException(failure + " Last client status: " + session.Status);
            session.Poll();
        }
    }

    /// <summary>Verify hosted class starts through picker, launch, and return docking.</summary>
    public static void Live(string address)
    {
        string[] accounts = { "rookie", "rookieje", "rookieps" };
        string[] names = { "Rookie", "RookieJE", "RookiePS" };
        string[] classes = { "Terran Scout", "Jenquai Explorer", "Progen Sentinel" };
        uint[] stationIds = { 10201, 10551, 10361 };
        string[] stations = { "Loki Station", "Ashanti Maru", "Arx Forgus" };
        for (int i = 0; i < accounts.Length; i++)
        {
            using (var picker = new CharacterPickerWorker(address, accounts[i], "rookie"))
            {
                var clock = Stopwatch.StartNew();
                while (picker.Characters == null && picker.Error == null &&
                    clock.Elapsed.TotalSeconds < 12) Thread.Sleep(20);
                if (picker.Error != null) throw new Exception(picker.Error);
                CharacterInfo[] pilots = picker.Characters;
                if (pilots == null || pilots.Length != 1 || pilots[0].Name != names[i] ||
                    pilots[0].ClassName != classes[i] || pilots[0].CombatLevel != 0 ||
                    pilots[0].TradeLevel != 0 || pilots[0].ExploreLevel != 0 ||
                    pilots[0].SectorId != stationIds[i] || pilots[0].SectorName != stations[i])
                    throw new Exception(classes[i] + " roster did not advertise its hosted start.");
            }
            using (var session = new LocalSession(CancellationToken.None))
            {
                session.Connect(address, accounts[i], "rookie");
                Wait(session, () => session.Ready && session.World.Station != null,
                    classes[i] + " did not receive its starting-station snapshot.");
                if (session.World.Station.Id != stationIds[i])
                    throw new Exception(classes[i] + " spawned in the wrong hosted station.");
                session.Launch();
                Wait(session, () => session.Ready && session.World.Station == null &&
                    session.World.HasPosition,
                    classes[i] + " did not launch into the home sector.");
                Wait(session, () => session.World.Objects.Values.Any(item => item.Kind == 12 &&
                    item.Name == stations[i]), "Home station was not dockable after launch.");
                MapObject home = session.World.Objects.Values.First(item => item.Kind == 12 &&
                    item.Name == stations[i]);
                if (session.World.Position.Distance(home.Position) > 5000)
                    throw new Exception("Home station was outside docking range after launch.");
                Console.WriteLine(classes[i] + " returning to " + home.Name + " at " +
                    session.World.Position.Distance(home.Position).ToString("0") + "m");
                session.Dock(home.Id);
                Wait(session, () => session.World.Station?.Id == stationIds[i],
                    classes[i] + " could not dock back at its starting station.");
            }
        }
        Console.WriteLine("PASS: three class starts, native rosters, launch, and return docking");
    }
}
