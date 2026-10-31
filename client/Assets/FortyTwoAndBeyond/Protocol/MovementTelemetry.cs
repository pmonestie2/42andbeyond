using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Optional motion fields present in captured production 0x3e revisions.</summary>
    public sealed class MovementTelemetry
    {
        public readonly float? Speed, TargetSpeed, Acceleration, AngularY, AngularZ;

        /// <summary>Keep one validated optional-field revision immutable.</summary>
        private MovementTelemetry(float?[] fields)
        { Speed = fields[0]; TargetSpeed = fields[1]; Acceleration = fields[2]; AngularY = fields[3]; AngularZ = fields[5]; }

        /// Returns:
        ///     MovementTelemetry: Validated optional values; omitted values remain absent.
        /// Raises:
        ///     InvalidDataException: The mask, packet size or floating-point fields are invalid.
        public static MovementTelemetry Decode(byte[] body)
        {
            Wire.Require(body, 0, 38);
            ushort flags = Wire.U16(body, 0);
            if (flags == 0 && body.Length == 38) return new MovementTelemetry(new float?[7]);
            Wire.Require(body, 0, 42);
            if ((flags & ~0x1ff) != 0) throw new InvalidDataException("Unknown movement field mask.");
            int expected = 42;
            for (int field = 0; field < 7; field++) if ((flags & (1 << field)) != 0) expected += 4;
            if ((flags & 0x80) != 0) expected += 24;
            if ((flags & 0x100) != 0) expected += 4;
            if (body.Length != expected) throw new InvalidDataException("Movement mask and length disagree.");
            var fields = new float?[7]; int offset = 42;
            for (int field = 0; field < fields.Length; field++)
                if ((flags & (1 << field)) != 0) { fields[field] = Wire.Float(body, offset); offset += 4; }
            if ((flags & 0x80) != 0)
                for (int field = 0; field < 6; field++) { Wire.Float(body, offset); offset += 4; }
            return new MovementTelemetry(fields);
        }
    }
}
