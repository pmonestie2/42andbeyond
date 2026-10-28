using System;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Normal flight command selected by the worker's journey planner.</summary>
    public enum GalaxyFlightAction { None, Warp, Gate, Dock, Launch }

    /// <summary>Follow locally planned gate and station legs without predicting or writing ship positions.</summary>
    public sealed class GalaxyPilot
    {
        private GalaxyRoutePlan route;
        private int leg, attempts, issuedGeneration = -1;
        private double nextAction, progressAt;
        private bool awaitingTransition;
        public GalaxyTravelView View { get; private set; } = new GalaxyTravelView(false, false, 0, "Choose a destination.");
        public GalaxyRouteLeg CurrentLeg => route != null && leg < route.Legs.Count ? route.Legs[leg] : null;

        /// <summary>Begin a validated route at its advertised start sector.</summary>
        public void Start(GalaxyRoutePlan plan, uint sector, double now)
        {
            if (plan == null || !plan.Success || plan.StartSector != sector)
            { Stop("Your location changed. Replot this route."); return; }
            route = plan; leg = attempts = 0; issuedGeneration = -1;
            awaitingTransition = false; nextAction = 0; progressAt = now;
            View = new GalaxyTravelView(true, false, 0, "Preparing journey…", route.TargetSector, route.TargetObject);
            if (route.Legs.Count == 0) Stop("Already at this destination.", completed: true);
        }

        /// <summary>Stop scheduling commands while preserving a readable outcome.</summary>
        public void Stop(string reason, bool completed = false)
        { View = new GalaxyTravelView(false, completed, leg, reason, route?.TargetSector ?? 0, route?.TargetObject ?? 0); }

        /// <summary>
        /// Returns:
        ///     GalaxyFlightAction: At most one normal flight command due for the current authoritative world.
        /// </summary>
        public GalaxyFlightAction Step(World world, bool ready, int generation, bool transitBusy, double now)
        {
            if (!View.Active) return GalaxyFlightAction.None;
            if (!ready) { Describe("Crossing sectors…"); return GalaxyFlightAction.None; }
            if (world.Equipment.Hull <= 0) { Stop("Travel stopped: the ship is incapacitated."); return GalaxyFlightAction.None; }
            if (world.Formation != null) { Stop("Travel stopped: leave formation to navigate independently."); return GalaxyFlightAction.None; }
            GalaxyRouteLeg current = CurrentLeg;
            if (current == null) { Stop("Arrived at destination.", completed: true); return GalaxyFlightAction.None; }
            if (world.SectorId == current.DestinationSector &&
                (current.SourceSector != current.DestinationSector || generation > issuedGeneration && issuedGeneration >= 0))
            {
                leg++; attempts = 0; nextAction = 0; progressAt = now; awaitingTransition = false; issuedGeneration = -1;
                if (CurrentLeg == null) { Stop("Arrived at " + current.DestinationName + ".", completed: true); return GalaxyFlightAction.None; }
                current = CurrentLeg;
            }
            if (world.SectorId != current.SourceSector)
            { Stop("Travel paused: unexpected sector. Replot from your current location."); return GalaxyFlightAction.None; }
            if (now - progressAt > 900)
            { Stop("Travel paused: this leg did not finish. Check your ship and replot."); return GalaxyFlightAction.None; }
            if (awaitingTransition)
            {
                Describe(current.Kind == 2 ? "Docking at " + current.DestinationName : "Crossing to " + current.DestinationName);
                if (transitBusy || now < nextAction) return GalaxyFlightAction.None;
                awaitingTransition = false;
                if (++attempts > 3) { Stop("Travel paused: the sector transition did not complete."); return GalaxyFlightAction.None; }
            }
            if (current.Kind == 1)
            {
                if (world.Station?.Id != current.SourceSector)
                { Stop("Travel paused: the departure station changed."); return GalaxyFlightAction.None; }
                return Transition(GalaxyFlightAction.Launch, generation, now);
            }
            if (world.Station != null || !world.HasPosition || !world.Objects.ContainsKey(current.Object))
            { Describe("Waiting for sector navigation data…"); return GalaxyFlightAction.None; }
            if (world.Warp.Phase == WarpPhase.Charging || world.Warp.Phase == WarpPhase.Traveling)
            { Describe("Warping toward " + current.DestinationName); attempts = 0; return GalaxyFlightAction.None; }
            if (world.Warp.Phase == WarpPhase.Recovering)
            { Describe("Warp drive recovering…"); return GalaxyFlightAction.None; }
            float range = Math.Max(0, world.Position.Distance(current.Position) - current.Radius);
            if (range <= 4500)
                return Transition(current.Kind == 2 ? GalaxyFlightAction.Dock : GalaxyFlightAction.Gate, generation, now);
            Describe("Approaching " + current.DestinationName + "  ·  " + (range / 1000).ToString("0.0") + " km");
            if (now < nextAction) return GalaxyFlightAction.None;
            if (++attempts > 8) { Stop("Travel paused: warp did not start. Check your engine and reactor."); return GalaxyFlightAction.None; }
            nextAction = now + 8;
            return GalaxyFlightAction.Warp;
        }

        /// <summary>
        /// Returns:
        ///     GalaxyFlightAction: The next crossing command, with a bounded acknowledgment deadline.
        /// </summary>
        private GalaxyFlightAction Transition(GalaxyFlightAction action, int generation, double now)
        {
            awaitingTransition = true; issuedGeneration = generation; nextAction = now + 30;
            Describe(action == GalaxyFlightAction.Launch ? "Launching from " + CurrentLeg.SourceName :
                action == GalaxyFlightAction.Dock ? "Docking at " + CurrentLeg.DestinationName : "Crossing to " + CurrentLeg.DestinationName);
            return action;
        }

        /// <summary>Publish a new progress value only when its displayed state changes.</summary>
        private void Describe(string status)
        { if (View.Status != status || View.LegIndex != leg) View = new GalaxyTravelView(true, false, leg, status, route.TargetSector, route.TargetObject); }
    }
}
