namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Classify native abilities whose target is the server's selected actor.</summary>
    public static class AbilityTargets
    {
        /// <returns>bool: Whether this ability needs a confirmed selected recipient.</returns>
        public static bool Directed(uint id)
        { return id == 31 || id == 32 || id == 89 || id == 95 || id == 125 || id == 44 || id == 45 ||
            id == 48 || id == 68 || id == 128; }

        /// <returns>bool: Whether a peer recipient must belong to the pilot's group.</returns>
        public static bool CrewOnly(uint id)
        { return id == 125 || id == 44 || id == 45 || id == 48 || id == 68 || id == 128; }

        /// <returns>bool: Whether self is an invalid recipient.</returns>
        public static bool RequiresPeer(uint id) { return id == 125; }

        /// <returns>bool: Whether the ability requires an acknowledged visible MOB.</returns>
        public static bool Enemy(uint id)
        { return id == 33 || id == 52 || id == 53 || id == 106 || id == 108 ||
            id == 109 || id == 124; }

        /// <returns>string: A concise name for worker status feedback.</returns>
        public static string Name(uint id)
        {
            if (id == 89) return "Recharge Shields";
            if (id == 95) return "Repair Hull";
            if (id == 31) return "Combat Hull Repair";
            if (id == 32) return "Combat Shield Recharge";
            if (id == 125) return "Teleport Friend";
            if (id == 44) return "Environment Barrier";
            if (id == 48) return "Greater Environment Shield";
            if (id == 68) return "Lesser Environment Shield";
            if (id == 128) return "Ultra Environment Shield";
            if (id == 33) return "Compulsory Contemplation";
            if (id == 52) return "Group Leech";
            if (id == 53) return "Group Leeching Sphere";
            if (id == 106) return "Shield Drain";
            if (id == 108) return "Shield Leech";
            if (id == 109) return "Shield Leeching Sphere";
            if (id == 124) return "Teleport Enemy";
            return "Environment Shield";
        }
    }
}
