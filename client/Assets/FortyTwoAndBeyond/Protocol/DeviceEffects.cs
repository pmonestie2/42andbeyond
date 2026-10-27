using System;
using System.Collections.Generic;
using System.Linq;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Public catalog names for server-supported single-effect devices.</summary>
    public static class DeviceEffects
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Turbo Weapons (Activated)"] = "Turbo Weapons",
            ["Scan Boost (Activated)"] = "Scan Boost",
            ["Increase Speed (Activated)"] = "Increase Speed",
            ["Reactor Boost (Activated)"] = "Reactor Boost",
            ["Turbo Warp (Activated)"] = "Turbo Warp",
            ["Improved Beam Handling (Activated)"] = "Improved Beam Handling",
            ["Improved Projectile Handling (Activated)"] = "Improved Projectile Handling",
            ["Shunt to Reactor (Activated)"] = "Shunt to Reactor",
            ["Turbo (Activated)"] = "Impulse Turbo",
            ["Increase Maneuverability (Activated)"] = "Increase Maneuverability",
            ["Maximize Shields (Activated)"] = "Maximize Shields",
            ["Reroute Shield Energy (Instant)"] = "Reroute Shield Energy",
            ["Transfer Reactor Energy (Instant)"] = "Transfer Reactor Energy",
            ["Recharge Reactor (Activated)"] = "Recharge Reactor",
            ["Recharge Shields (Activated)"] = "Recharge Shields",
            ["Recharge Shields - BOOST (Activated)"] = "Recharge Shields",
            ["Reduce Ship Signature (Activated)"] = "Reduce Ship Signature",
            ["Deflect Impact (Activated)"] = "Deflect Impact",
            ["Deflect Explosive (Activated)"] = "Deflect Explosive",
            ["Deflect Plasma (Activated)"] = "Deflect Plasma",
            ["Deflect Energy (Activated)"] = "Deflect Energy",
            ["Deflect EMP (Activated)"] = "Deflect EMP",
            ["Deflect Chemical (Activated)"] = "Deflect Chemical",
            ["Resist Psionic (Activated)"] = "Resist Psionic"
        };

        internal static readonly string[] Labels = Names.Values.Distinct().ToArray();

        /// <summary>
        /// Returns:
        ///     string: The server effect label, or empty text for an unsupported description.
        /// </summary>
        public static string LabelFor(string description)
        { return Names.TryGetValue(description, out string label) ? label : ""; }

        /// <returns>bool: Whether activation completes immediately without a timed buff.</returns>
        public static bool IsInstant(string label)
        { return label == "Reroute Shield Energy" || label == "Transfer Reactor Energy"; }

        /// <returns>bool: Whether this effect has no self-target activation path.</returns>
        public static bool RequiresPeer(string label, bool targetsPeer = false)
        { return label == "Transfer Reactor Energy"; }

        /// <returns>bool: Whether the catalog effect may be activated from this unlocked mount.</returns>
        public static bool MountedSlot(string label, uint type, int slot, uint deviceMounts)
        {
            if (label.Length == 0) return false;
            if (type == 11) return slot >= 9 && slot < 9 + deviceMounts && slot < 20;
            if (type == 2 && slot == 0 &&
                (label == "Increase Maneuverability" || label == "Recharge Shields" ||
                    label == "Turbo Weapons" || label == "Deflect Impact" ||
                    label == "Deflect Explosive" || label == "Shunt to Reactor")) return true;
            if (label == "Increase Maneuverability" && type == 6 && slot == 2) return true;
            return type == 7 && slot == 1 &&
                (label == "Impulse Turbo" || label == "Increase Maneuverability" ||
                    label == "Scan Boost" || label == "Recharge Reactor" ||
                    label == "Recharge Shields" ||
                    label == "Reroute Shield Energy");
        }
    }
}
