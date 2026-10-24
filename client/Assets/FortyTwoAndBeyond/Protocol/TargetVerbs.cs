using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Native action availability for one server-selected world target.</summary>
    public sealed class TargetVerbs
    {
        private readonly Dictionary<ushort, ushort> attributes;
        public uint TargetId { get; }
        public static TargetVerbs Empty { get; } = new TargetVerbs(0, new Dictionary<ushort, ushort>());

        private TargetVerbs(uint targetId, Dictionary<ushort, ushort> attributes)
        { TargetId = targetId; this.attributes = attributes; }

        /// Returns:
        ///     bool?: Whether the server enabled this verb, or null when it did not advertise it for this target.
        public bool? Available(uint targetId, ushort verb)
        {
            if (targetId != TargetId || !attributes.TryGetValue(verb, out ushort attribute)) return null;
            return attribute == 0;
        }

        /// Returns:
        ///     TargetVerbs: An immutable revision of the server's current verb list.
        /// Raises:
        ///     InvalidDataException: The native lists are truncated or exceed the bounded verb count.
        public static TargetVerbs Decode(byte[] body)
        {
            Wire.Require(body, 0, 12);
            uint target = Wire.U32(body, 0, true);
            int offset = 4;
            Dictionary<ushort, ushort> current = null;
            for (int list = 0; list < 2; list++)
            {
                uint count = Wire.U32(body, offset, true);
                offset += 4;
                if (count > 64 || count > (body.Length - offset) / 4)
                    throw new InvalidDataException("Invalid native target verb count.");
                var values = new Dictionary<ushort, ushort>();
                for (int index = 0; index < count; index++)
                {
                    ushort attribute = Wire.U16(body, offset);
                    ushort verb = Wire.U16(body, offset + 2);
                    offset += 4;
                    if (!values.TryAdd(verb, attribute))
                        throw new InvalidDataException("Duplicate native target verb.");
                }
                if (list == 1) current = values;
            }
            if (offset != body.Length) throw new InvalidDataException("Trailing native target verb bytes.");
            return new TargetVerbs(target, current);
        }
    }
}
