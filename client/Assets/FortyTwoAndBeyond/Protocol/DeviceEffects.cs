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
            ["Recharge Reactor (Activated)"] = "Recharge Reactor",
            ["Recharge Shields (Activated)"] = "Recharge Shields",
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
    }
}
