using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One server-authorized launcher preference and its compatible owned cargo slots.</summary>
    public sealed class LauncherAmmoOption
    {
        public readonly int Mount;
        public readonly bool AutoReload;
        public readonly uint PreviousTemplate;
        public readonly IReadOnlyList<int> CargoSlots;

        /// <summary>Copy the authoritative slot list before publishing it to Unity.</summary>
        public LauncherAmmoOption(int mount, bool autoReload, uint previousTemplate, int[] cargoSlots)
        {
            Mount = mount; AutoReload = autoReload; PreviousTemplate = previousTemplate;
            CargoSlots = Array.AsReadOnly((int[])cargoSlots.Clone());
        }
    }

    /// <summary>One complete native launcher compatibility and reload snapshot.</summary>
    public sealed class LauncherAmmoState
    {
        public static readonly LauncherAmmoState Unknown = new LauncherAmmoState(false, Array.Empty<LauncherAmmoOption>());
        public readonly bool Known;
        public readonly IReadOnlyList<LauncherAmmoOption> Launchers;

        /// <summary>Keep one complete server revision immutable for the scene thread.</summary>
        public LauncherAmmoState(bool known, LauncherAmmoOption[] launchers)
        {
            Known = known;
            Launchers = Array.AsReadOnly((LauncherAmmoOption[])launchers.Clone());
        }

        /// <returns>LauncherAmmoState: A validated complete server compatibility revision.</returns>
        public static LauncherAmmoState Decode(byte[] body, uint owner)
        {
            if (body == null || body.Length < 8 || Wire.U32(body, 0) != owner)
                throw new InvalidDataException("Invalid launcher preference owner.");
            uint count = Wire.U32(body, 4);
            if (count > 6) throw new InvalidDataException("Too many launcher preferences.");
            var options = new List<LauncherAmmoOption>();
            var seenMounts = new HashSet<uint>();
            int offset = 8;
            for (int index = 0; index < count; index++)
            {
                Wire.Require(body, offset, 16);
                uint mount = Wire.U32(body, offset), enabled = Wire.U32(body, offset + 4);
                uint previous = Wire.U32(body, offset + 8), slots = Wire.U32(body, offset + 12);
                offset += 16;
                if (mount < 3 || mount > 8 || !seenMounts.Add(mount) || enabled > 1 || slots > 40 ||
                    slots > (body.Length - offset) / 4)
                    throw new InvalidDataException("Invalid launcher preference entry.");
                var cargoSlots = new int[slots];
                var seenSlots = new HashSet<uint>();
                for (int slot = 0; slot < slots; slot++)
                {
                    uint cargo = Wire.U32(body, offset); offset += 4;
                    if (cargo >= 40 || !seenSlots.Add(cargo))
                        throw new InvalidDataException("Invalid compatible cargo slot.");
                    cargoSlots[slot] = (int)cargo;
                }
                options.Add(new LauncherAmmoOption((int)mount, enabled != 0, previous, cargoSlots));
            }
            if (offset != body.Length) throw new InvalidDataException("Launcher preference trailing bytes.");
            return new LauncherAmmoState(true, options.ToArray());
        }
    }
}
