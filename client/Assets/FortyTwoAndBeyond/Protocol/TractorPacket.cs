using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>The common native ore and wreck tractor body.</summary>
    internal sealed class TractorPacket
    {
        internal readonly uint ArticleId, EffectId;
        internal readonly string ItemName;
        internal readonly float DurationSeconds;
        internal readonly Position Position;

        /// <summary>Keep one validated native tractor event together.</summary>
        private TractorPacket(uint articleId, uint effectId, string itemName,
            float durationSeconds, Position position)
        {
            ArticleId = articleId; EffectId = effectId; ItemName = itemName;
            DurationSeconds = durationSeconds; Position = position;
        }

        /// <summary>
        /// Returns:
        ///     TractorPacket: A validated own-player tractor event, or null for another pilot.
        /// Raises:
        ///     InvalidDataException: The native event body is malformed.
        /// </summary>
        internal static TractorPacket Decode(byte[] body, uint player)
        {
            Wire.Require(body, 0, 40);
            if (Wire.U32(body, 0) != player) return null;
            int length = Wire.U16(body, 18);
            if (length > 256 || body.Length != 40 + length)
                throw new InvalidDataException("Invalid native tractor name length.");
            uint article = Wire.U32(body, 4), effect = Wire.U32(body, 8);
            uint duration = Wire.U32(body, 20 + length);
            float speed = Wire.Float(body, 24 + length);
            Position position = new Position(Wire.Float(body, 28 + length),
                Wire.Float(body, 32 + length), Wire.Float(body, 36 + length));
            if (article == 0 || effect == 0 || duration == 0 || duration > 600000 || speed <= 0 ||
                System.Math.Abs(position.X) >= 1e9 || System.Math.Abs(position.Y) >= 1e9 ||
                System.Math.Abs(position.Z) >= 1e9)
                throw new InvalidDataException("Invalid native tractor details.");
            return new TractorPacket(article, effect, Wire.Text(body, 20, length),
                duration / 1000f, position);
        }
    }
}
