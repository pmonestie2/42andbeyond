namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Match native warp destinations without treating visible pilots as waypoints.</summary>
    public static class WarpTargets
    {
        /// <summary>
        /// Returns:
        ///     bool: Whether a visible world object has a server warp route.
        /// </summary>
        public static bool WorldObject(MapObject item)
        { return item != null && (item.Navigable || item.Kind == 25 || item.Kind == 37 || item.Kind == 38); }

        /// <summary>
        /// Returns:
        ///     bool: Whether a visible actor is a server warp target.
        /// </summary>
        public static bool Actor(ShipActor actor)
        { return actor != null && !actor.IsPlayer && actor.HasPosition; }

        /// <summary>
        /// Returns:
        ///     bool: Whether the current worker world contains a native warp destination.
        /// </summary>
        public static bool Available(World world, uint target)
        {
            if (target == 0) return true;
            if (world.Objects.TryGetValue(target, out MapObject item))
                return WorldObject(item) && (item.Kind != 38 ||
                    item.Position.Distance(world.Position) <= 20000);
            return world.Mobs.TryGetValue(target, out ShipActor actor) && Actor(actor);
        }
    }
}
