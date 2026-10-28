using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class GalaxyTravelChecks
{
    private const uint Player = 1;

    /// <summary>Fail with a concrete navigation behavior description.</summary>
    private static void Require(bool valid, string reason) { if (!valid) throw new Exception(reason); }
    /// <summary>Require malformed local resource bytes to be rejected.</summary>
    private static void Reject(Action operation)
    { try { operation(); } catch (InvalidDataException) { return; } throw new Exception("Malformed local navigation resource was accepted."); }

    /// <summary>Returns:
    ///     string: The checked-in navigation resource found from repository or test-output directories.
    /// </summary>
    public static string ResourcePath()
    {
        foreach (string origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(origin); directory != null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, "client/Assets/FortyTwoAndBeyond/Resources/GalaxyNavigation.bytes");
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("Run the offline navigation resource generator first.");
    }

    /// <summary>Exercise local catalog integrity, deterministic pagination, real shortest paths and authoritative planner phases.</summary>
    public static void Check()
    {
        byte[] data = File.ReadAllBytes(ResourcePath());
        GalaxyNavigationCatalog catalog = GalaxyNavigationCatalog.Decode(data);
        Require(catalog.Count == 308, "Known public gate/station catalog is incomplete.");
        Require(GalaxyNavigationCatalog.Decode(null).Count == 0 && !GalaxyNavigationCatalog.Empty.Plan(10151, 1060, 103066).Success,
            "Missing local data invented a route.");
        var seen = new HashSet<ulong>(); GalaxyCatalogPage first = catalog.Query(1, 0, 3, "");
        for (uint page = 0; page < (first.Total + 63) / 64; page++)
        {
            GalaxyCatalogPage current = catalog.Query(page + 5, page, 3, "");
            Require(current.RequestId == page + 5 && current.Page == page && current.PageSize == 64 && current.Total == 308 && current.Entries.Count <= 64,
                "Local search paging changed its owned request or bounds.");
            foreach (GalaxyDestination entry in current.Entries)
                Require((entry.Kind == 11 || entry.Kind == 12) && seen.Add(((ulong)entry.Sector << 32) | entry.Id), "Catalog duplicated an object or exposed unrelated navpoints.");
        }
        Require(seen.Count == 308 && first.Entries[0].Id == catalog.Query(99, 0, 3, "").Entries[0].Id,
            "Repeated local queries changed prior immutable pages.");
        Require(catalog.Query(2, 0, 1, "").Total == 245 && catalog.Query(3, 0, 2, "").Total == 63, "Gate/station filters lost entries.");
        Require(catalog.Query(4, 0, 2, "EARTH STATION").Entries.Any(value => value.Id == 103066 && value.DestinationSector == 10601), "Local search missed Earth Station.");
        Require(catalog.Query(4, uint.MaxValue, 3, "unmatched fixture").Page == 0, "Empty search produced an invalid page.");
        GalaxyRoutePlan route = catalog.Plan(10151, 1060, 103066);
        Require(route.Success && route.ArrivalSector == 10601 && route.Legs.Count == 3 &&
            route.Legs.Select(leg => leg.Kind).SequenceEqual(new uint[] { 1, 0, 2 }) &&
            route.Legs[0].Object == 10151 && route.Legs[1].Object == 100533 && route.Legs[2].Object == 103066,
            "Luna-to-Earth route did not use its real launch, gate and dock.");
        GalaxyRoutePlan crossing = catalog.Plan(1060, 1015, 100533);
        Require(crossing.Success && crossing.Legs.Count == 2 && crossing.Legs.Last().Object == 100533 && crossing.ArrivalSector == 1060,
            "Selected gate was only approached instead of crossed.");
        Require(catalog.Plan(10601, 1060, 103066).Legs.Count == 0, "Already docked route unnecessarily launched the pilot.");
        Require(!catalog.Plan(999999, 1060, 103066).Success && !catalog.Plan(1015, 1015, 100530).Success, "Unknown graph or planet generated a route.");
        GalaxyDestination disabled = Enumerable.Range(0, 5).SelectMany(page => catalog.Query(0, (uint)page, 3, "").Entries).First(entry => entry.DestinationSector == 0);
        Require(!catalog.Plan(1015, disabled.Sector, disabled.Id).Success, "Unavailable destination was fabricated.");
        Require(!catalog.Plan(1015, 1060, 103066, new Dictionary<uint, MapObject>()).Success, "Missing current-sector gate was silently invented.");
        var actual = new Dictionary<uint, MapObject> { [100533] = new MapObject(Map(100533, 11, new Position(12345, 200, 300))) };
        GalaxyRoutePlan observed = catalog.Plan(1015, 1060, 103066, actual);
        Require(observed.Success && observed.Legs[0].Position.X == 12345, "Known current gate did not use its observed position.");
        byte[] badMagic = (byte[])data.Clone(); badMagic[0] = 0; Reject(() => GalaxyNavigationCatalog.Decode(badMagic));
        Reject(() => GalaxyNavigationCatalog.Decode(data.Take(data.Length - 1).ToArray()));
        Reject(() => GalaxyNavigationCatalog.Decode(data.Concat(new byte[] { 0 }).ToArray()));
        byte[] badKind = (byte[])data.Clone(); Array.Copy(BitConverter.GetBytes(37u), 0, badKind, 16, 4); Reject(() => GalaxyNavigationCatalog.Decode(badKind));
        byte[] badPosition = (byte[])data.Clone(); Array.Copy(BitConverter.GetBytes(float.NaN), 0, badPosition, 24, 4); Reject(() => GalaxyNavigationCatalog.Decode(badPosition));
        byte[] badCount = (byte[])data.Clone(); Array.Copy(BitConverter.GetBytes(10001u), 0, badCount, 4, 4); Reject(() => GalaxyNavigationCatalog.Decode(badCount));
        CheckPilot(route);
        Console.WriteLine("PASS: client-only GXY1 bounds, 308-entry paging, known-map BFS, observed gates, launch/warp/gate/dock, cancellation and loop-gate epochs");
    }

    /// <summary>Returns:
    ///     byte[]: A native static-object record used by authoritative world fixtures.
    /// </summary>
    private static byte[] Map(uint id, byte kind, Position position)
    {
        byte[] name = Encoding.UTF8.GetBytes("Known route object"); var body = new byte[60 + name.Length];
        Array.Copy(BitConverter.GetBytes(id), 0, body, 0, 4); body[4] = kind;
        Array.Copy(BitConverter.GetBytes(1f), 0, body, 7, 4);
        Array.Copy(BitConverter.GetBytes(position.X), 0, body, 25, 4); Array.Copy(BitConverter.GetBytes(position.Y), 0, body, 29, 4); Array.Copy(BitConverter.GetBytes(position.Z), 0, body, 33, 4);
        Array.Copy(BitConverter.GetBytes(1f), 0, body, 49, 4); body[57] = 0x91;
        Array.Copy(BitConverter.GetBytes((ushort)name.Length), 0, body, 58, 2); Array.Copy(name, 0, body, 60, name.Length); return body;
    }
    /// <summary>Apply a server-style pose without allowing the planner to write flight coordinates.</summary>
    private static void Pose(World world, Position position)
    {
        world.Apply(new Frame(0x3e, Wire.Body(writer => { writer.Write((ushort)0); writer.Write(Player); writer.Write(100u);
            writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); })), Player);
    }
    /// <summary>Apply the native private/global warp fields used by the game worker.</summary>
    private static void Phase(World world, uint local, uint global)
    {
        world.Apply(new Frame(0x1b, Wire.Body(writer => { writer.Write(Player); writer.Write((ushort)17); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0, 0, 0x18, 0, 0, 0 }); writer.Write(local); writer.Write(global); })), Player);
    }
    /// <summary>Returns:
    ///     World: A station or space fixture with authoritative native identity, pose and hull fields.
    /// </summary>
    private static World Scene(uint sector, bool station, GalaxyRouteLeg target, bool near)
    {
        var world = new World(sector); world.Equipment.Hull = 100;
        if (station) world.Apply(new Frame(0x4f, Wire.Body(writer => { writer.Write(sector); writer.Write((byte)0); writer.Write((byte)0); })), Player);
        if (target != null) world.Apply(new Frame(0x2018, Map(target.Object, target.Kind == 2 ? (byte)12 : (byte)11, target.Position)), Player);
        Position position = target == null ? new Position(0, 0, 0) : new Position(target.Position.X + target.Radius + (near ? 1000 : 50000), target.Position.Y, target.Position.Z);
        Pose(world, position); return world;
    }
    /// <summary>Require authoritative phase/sector progression and bounded cancellation/loop handling.</summary>
    private static void CheckPilot(GalaxyRoutePlan route)
    {
        var pilot = new GalaxyPilot(); var station = Scene(10151, true, null, false);
        pilot.Start(route, 10151, 0);
        Require(pilot.Step(station, true, 0, false, 0) == GalaxyFlightAction.Launch, "Pilot did not issue a normal station launch.");
        Require(pilot.Step(station, false, 0, false, 1) == GalaxyFlightAction.None, "Pilot issued a command during handoff.");
        World luna = Scene(1015, false, route.Legs[1], false); Position original = luna.Position;
        Require(pilot.Step(luna, true, 1, false, 2) == GalaxyFlightAction.Warp, "Pilot did not warp to its gate.");
        Require(luna.Position.Distance(original) == 0, "Pure planner mutated the authoritative pose.");
        Require(pilot.Step(luna, true, 1, false, 3) == GalaxyFlightAction.None, "Pilot duplicated an unacknowledged warp request.");
        Phase(luna, 1, 1); Require(pilot.Step(luna, true, 1, false, 4) == GalaxyFlightAction.None, "Pilot sent another command while charging.");
        Phase(luna, 2, 2); Require(pilot.Step(luna, true, 1, false, 5) == GalaxyFlightAction.None, "Pilot sent another command in warp.");
        Pose(luna, route.Legs[1].Position); Phase(luna, 8, 3);
        Require(pilot.Step(luna, true, 1, false, 6) == GalaxyFlightAction.None, "Pilot gated before warp recovery completed.");
        Phase(luna, 0, 0); Require(pilot.Step(luna, true, 1, false, 7) == GalaxyFlightAction.Gate, "Pilot did not activate its nearby gate.");
        Require(pilot.Step(luna, true, 1, true, 45) == GalaxyFlightAction.None, "Pilot restarted a busy gate animation.");
        World earth = Scene(1060, false, route.Legs[2], true);
        Require(pilot.Step(earth, true, 2, false, 50) == GalaxyFlightAction.Dock, "Pilot did not request docking after reaching Earth.");
        World arrived = Scene(10601, true, null, false);
        Require(pilot.Step(arrived, true, 3, false, 55) == GalaxyFlightAction.None && pilot.View.Completed && !pilot.View.Active,
            "Pilot did not complete on authoritative station arrival.");
        pilot.Start(route, 10151, 60); pilot.Stop("Cancelled fixture"); Require(pilot.Step(station, true, 0, false, 61) == GalaxyFlightAction.None && !pilot.View.Active, "Cancelled travel continued.");
        var loop = new GalaxyRoutePlan(1015, 1015, 7, 1015, true, "", new[] { new GalaxyRouteLeg(0, 1015, 7, 1015, new Position(0, 0, 0), 1, "Loop", "Loop") });
        World looping = Scene(1015, false, loop.Legs[0], true); pilot.Start(loop, 1015, 0);
        Require(pilot.Step(looping, true, 7, false, 0) == GalaxyFlightAction.Gate && !pilot.View.Completed, "Same-sector gate completed before crossing.");
        Require(pilot.Step(looping, true, 7, false, 1) == GalaxyFlightAction.None && !pilot.View.Completed, "Same-epoch loop completed without handoff.");
        Require(pilot.Step(looping, true, 8, false, 2) == GalaxyFlightAction.None && pilot.View.Completed, "Fresh loop-gate epoch did not complete.");
    }

    /// <summary>Wait on an owned LocalSession with a bounded deadline and useful last-state diagnostics.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, double seconds, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(failure + "; " + session.Status + "; " + session.World.GalaxyTravel.Status); session.Poll(); }
    }
    /// <summary>Exercise local browsing/plotting and ordinary launch, warp, gate and dock against an explicitly owned disposable server.</summary>
    public static void Live(string address)
    {
        using (var session = new LocalSession(CancellationToken.None, File.ReadAllBytes(ResourcePath())))
        {
            session.Connect(address, "local", "local");
            Wait(session, () => session.Ready && session.World.Station?.Id == 10151 && session.World.Equipment.Hull > 0, 20, "Luna station login did not complete");
            uint sector = session.World.SectorId; Position position = session.World.Position;
            session.QueryGalaxyDestinations("", 0, 3);
            Require(session.World.GalaxyCatalog?.Total == 308 && session.World.GalaxyCatalog.Entries.Count == 64, "Offline catalog page did not load.");
            session.QueryGalaxyDestinations("Earth Station", 0, 2);
            Require(session.World.GalaxyCatalog.Entries.Any(entry => entry.Id == 103066 && entry.DestinationSector == 10601), "Offline search lost Earth Station.");
            session.PreviewGalaxyRoute(1060, 103066);
            GalaxyRoutePlan route = session.World.GalaxyRoute;
            Require(route?.Success == true && route.Legs.Count == 3 && route.Legs.Select(leg => leg.Kind).SequenceEqual(new uint[] { 1, 0, 2 }), "Offline preview did not plan Luna launch/Earth crossing/dock.");
            Require(!session.World.GalaxyTravel.Active && session.World.SectorId == sector && session.World.Position.Distance(position) == 0, "Browsing/preview moved the pilot.");
            Console.WriteLine("PREVIEW: local catalog 308, page 64, route Luna Station -> Luna -> Earth -> Earth Station; browsing did not move");
            session.StartGalaxyTravel(); var timer = Stopwatch.StartNew(); string previous = "";
            while (!session.World.GalaxyTravel.Completed)
            {
                if (!session.World.GalaxyTravel.Active) throw new Exception("Automatic flight stopped: " + session.World.GalaxyTravel.Status + "; " + session.Status);
                if (timer.Elapsed.TotalSeconds > 180) throw new TimeoutException("Automatic journey exceeded 180 seconds: " + session.World.GalaxyTravel.Status);
                session.Poll();
                string state = session.World.SectorId + "/" + session.World.GalaxyTravel.LegIndex + "/" + session.World.Warp.Phase;
                if (state != previous) { Console.WriteLine("PROGRESS " + timer.Elapsed.TotalSeconds.ToString("0.0") + "s " + state + " " + session.World.GalaxyTravel.Status); previous = state; }
            }
            Require(session.Ready && session.World.SectorId == 10601 && session.World.Station?.Id == 10601, "Journey claimed completion outside Earth Station.");
            Console.WriteLine("PASS: client-only automatic normal-flight journey arrived at Earth Station 10601 in " + timer.Elapsed.TotalSeconds.ToString("0.0") + " seconds");
        }
    }
}
