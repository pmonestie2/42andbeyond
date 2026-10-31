using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    internal sealed partial class AuxState
    {
        /// Returns:
        ///     byte[]: A canonical hulk envelope; production full snapshots can undercount their own body by two bytes.
        private static byte[] NativeCorpseEnvelope(byte[] body)
        {
            Wire.Require(body, 0, 7);
            if (body.Length >= 10 && body[6] == 1 && body[7] == 0x16 && body[8] == 0xec && body[9] == 0x0f &&
                Wire.U16(body, 4) + 8 == body.Length)
            {
                byte[] normalized = (byte[])body.Clone();
                ushort length = checked((ushort)(body.Length - 6));
                normalized[4] = (byte)length; normalized[5] = (byte)(length >> 8);
                return normalized;
            }
            return body;
        }

        /// Returns:
        ///     string: The native eight-field hulk's announced name, or null when omitted by this diff.
        public static string ReadCorpseName(byte[] body)
        {
            var reader = new AuxState(NativeCorpseEnvelope(body));
            Flags fields = reader.Mask(8);
            if (!fields.Present[0]) return null;
            string name = reader.Text();
            if (name.Length > 128) throw new InvalidDataException("Native corpse name exceeds its bound.");
            return name;
        }
    }
}
