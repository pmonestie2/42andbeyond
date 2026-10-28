using System;

namespace FortyTwoAndBeyond.Protocol
{
    public sealed partial class LocalSession
    {
        private readonly GalaxyPilot galaxyPilot = new GalaxyPilot();
        private readonly GalaxyNavigationCatalog navigation;
        private uint galaxyRequest;
        private int sectorGeneration;

        /// <summary>Search a bounded page of the client's known gate and station map.</summary>
        public void QueryGalaxyDestinations(string query, uint page, uint flags)
        {
            if (!Ready || flags < 1 || flags > 3) return;
            World.GalaxyCatalog = navigation.Query(++galaxyRequest, page, flags, query ?? "");
        }

        /// <summary>Plan launch, gate, and docking steps entirely on the network worker.</summary>
        public void PreviewGalaxyRoute(uint sector, uint target)
        {
            if (!Ready || sector == 0 || target == 0) return;
            World.GalaxyRoute = navigation.Plan(World.SectorId, sector, target, World.Objects);
        }

        /// <summary>Start the displayed known-map route without teleporting the ship.</summary>
        public void StartGalaxyTravel()
        {
            if (!Ready || World.GalaxyRoute == null) return;
            if (World.Dialogue != null) ChooseNpc(230);
            StopWeapons(); followTarget = 0;
            galaxyPilot.Start(World.GalaxyRoute, World.SectorId, clock.Elapsed.TotalSeconds);
            World.GalaxyTravel = galaxyPilot.View;
        }

        /// <summary>Cancel automatic navigation and optionally brake/cancel the active warp.</summary>
        public void CancelGalaxyTravel(bool stopMotion)
        {
            if (!galaxyPilot.View.Active) return;
            galaxyPilot.Stop("Automatic travel cancelled.");
            World.GalaxyTravel = galaxyPilot.View;
            if (!stopMotion || !Ready || gate != 0 || World.Station != null) return;
            if (World.Warp.Phase == WarpPhase.Charging || World.Warp.Phase == WarpPhase.Traveling) Warp(0);
            else Thrust(4);
        }

        /// <summary>Advance the itinerary on the network worker using authoritative movement and the local route.</summary>
        private void AdvanceGalaxyTravel(double now)
        {
            bool wasActive = galaxyPilot.View.Active;
            GalaxyFlightAction action = galaxyPilot.Step(World, Ready, sectorGeneration, gate != 0 || dock != 0, now);
            GalaxyRouteLeg leg = galaxyPilot.CurrentLeg;
            if (leg != null)
            {
                switch (action)
                {
                    case GalaxyFlightAction.Warp: Warp(leg.Object); break;
                    case GalaxyFlightAction.Gate: Gate(leg.Object); break;
                    case GalaxyFlightAction.Dock: Dock(leg.Object, radius: leg.Radius); break;
                    case GalaxyFlightAction.Launch: Launch(); break;
                }
            }
            World.GalaxyTravel = galaxyPilot.View;
            if (wasActive && !galaxyPilot.View.Active)
            {
                Status = galaxyPilot.View.Status;
                World.Messages.Enqueue(Status);
                while (World.Messages.Count > 100) World.Messages.Dequeue();
            }
        }
    }
}
