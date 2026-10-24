using System;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Worker-owned native buff slot and matching object effect.</summary>
    public sealed class TimedDeviceBuff
    {
        public static TimedDeviceBuff Empty { get; } = new TimedDeviceBuff("", 0, 0, 0);
        public readonly string Name;
        public readonly uint RemovalTick, VisualEffectId;
        public readonly ushort VisualDescriptor;
        public bool Active => Name.Length > 0 && RemovalTick != 0;

        /// <summary>Keep native buff and visual fields in one immutable revision.</summary>
        public TimedDeviceBuff(string name, uint removalTick, uint visualEffectId, ushort visualDescriptor)
        {
            Name = name ?? "";
            RemovalTick = removalTick;
            VisualEffectId = visualEffectId;
            VisualDescriptor = visualDescriptor;
        }

        /// Returns:
        ///     TimedDeviceBuff: A new buff revision with its server-published visual.
        public TimedDeviceBuff WithVisual(uint effectId, ushort descriptor)
        { return new TimedDeviceBuff(Name, RemovalTick, effectId, descriptor); }

        /// Returns:
        ///     float: Seconds remaining on the wrapping server clock, or minus one before clock sync.
        public float RemainingSeconds(uint? serverTick)
        {
            if (!Active) return 0;
            if (!serverTick.HasValue) return -1;
            int remaining = unchecked((int)(RemovalTick - serverTick.Value));
            return Math.Max(0, remaining / 1000f);
        }
    }

    /// <summary>Stable native buff presentation for the Unity main thread.</summary>
    public sealed class DeviceBuffView
    {
        public readonly string Name;
        public readonly float RemainingSeconds;
        public readonly uint VisualEffectId;
        public readonly ushort VisualDescriptor;
        public bool VisualActive => VisualDescriptor != 0;

        /// <summary>Freeze one worker buff revision and its display countdown.</summary>
        public DeviceBuffView(TimedDeviceBuff buff, uint? serverTick)
        {
            Name = buff.Name;
            RemainingSeconds = buff.RemainingSeconds(serverTick);
            VisualEffectId = buff.VisualEffectId;
            VisualDescriptor = buff.VisualDescriptor;
        }
    }

    /// <summary>Native ObjectEffect identity and descriptor from opcode 0x09.</summary>
    public sealed class NativeObjectEffect
    {
        public readonly uint OwnerId, EffectId, Tick;
        public readonly ushort Descriptor;

        private NativeObjectEffect(uint ownerId, uint effectId, ushort descriptor, uint tick)
        { OwnerId = ownerId; EffectId = effectId; Descriptor = descriptor; Tick = tick; }

        /// Returns:
        ///     NativeObjectEffect: A supported identity-and-tick effect, or null for another native mask.
        /// Raises:
        ///     InvalidDataException: An identity-and-tick packet is truncated or has extra bytes.
        public static NativeObjectEffect Decode(byte[] body)
        {
            Wire.Require(body, 0, 1);
            if (body[0] != 3) return null;
            if (body.Length != 15) throw new InvalidDataException("Invalid native object effect length.");
            return new NativeObjectEffect(Wire.U32(body, 1), Wire.U32(body, 7),
                Wire.U16(body, 5), Wire.U32(body, 11));
        }
    }
}
