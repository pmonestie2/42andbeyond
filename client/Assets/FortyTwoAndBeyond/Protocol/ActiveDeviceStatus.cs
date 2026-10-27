using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One server-owned equipped activation in the full device snapshot.</summary>
    public sealed class ActiveDeviceStatus
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public readonly byte MountSlot;
        public readonly uint TemplateId, RemovalTick;
        public readonly string Name;

        private ActiveDeviceStatus(byte mountSlot, uint templateId, uint removalTick, string name)
        { MountSlot = mountSlot; TemplateId = templateId; RemovalTick = removalTick; Name = name; }

        /// Returns:
        ///     ActiveDeviceStatus[]: The complete version-one device set, including an empty set.
        /// Raises:
        ///     InvalidDataException: The server snapshot is malformed or exceeds mount limits.
        public static ActiveDeviceStatus[] DecodeSnapshot(byte[] body)
        {
            Wire.Require(body, 0, 2);
            if (body[0] != 1 || body[1] > 21)
                throw new InvalidDataException("Invalid active-device snapshot header.");
            var devices = new ActiveDeviceStatus[body[1]];
            var mounts = new HashSet<byte>();
            int offset = 2;
            for (int index = 0; index < devices.Length; index++)
            {
                Wire.Require(body, offset, 10);
                byte mount = body[offset];
                uint template = Wire.U32(body, offset + 1);
                uint removalTick = Wire.U32(body, offset + 5);
                int length = body[offset + 9];
                offset += 10;
                if (mount >= 20 || !mounts.Add(mount) || template == 0 || removalTick == 0 ||
                    length == 0 || length > 64)
                    throw new InvalidDataException("Invalid active-device entry.");
                Wire.Require(body, offset, length);
                string name;
                try { name = StrictUtf8.GetString(body, offset, length); }
                catch (DecoderFallbackException error)
                { throw new InvalidDataException("Invalid active-device label.", error); }
                if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0)
                    throw new InvalidDataException("Invalid active-device label.");
                devices[index] = new ActiveDeviceStatus(mount, template, removalTick, name);
                offset += length;
            }
            if (offset != body.Length)
                throw new InvalidDataException("Trailing active-device snapshot bytes.");
            return devices;
        }

        /// Returns:
        ///     ActiveDeviceView: The worker-owned device lease frozen for one main-thread frame.
        public ActiveDeviceView View(uint? serverTick)
        {
            float remaining = new TimedDeviceBuff(Name, RemovalTick, 0, 0)
                .RemainingSeconds(serverTick);
            return new ActiveDeviceView(MountSlot, TemplateId, Name, remaining);
        }
    }

    /// <summary>Immutable main-thread presentation of one activated item.</summary>
    public sealed class ActiveDeviceView
    {
        public readonly byte MountSlot;
        public readonly uint TemplateId;
        public readonly string Name;
        public readonly float RemainingSeconds;

        /// <summary>Freeze the server label and remaining time for one mounted device.</summary>
        public ActiveDeviceView(byte mountSlot, uint templateId, string name, float remainingSeconds)
        { MountSlot = mountSlot; TemplateId = templateId; Name = name; RemainingSeconds = remainingSeconds; }
    }
}
