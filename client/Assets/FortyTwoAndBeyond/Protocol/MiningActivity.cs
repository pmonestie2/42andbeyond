using System;
using System.Diagnostics;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    public enum MiningPhase { Prospect, Tractor }

    /// <summary>One authoritative mining phase with a locally advancing display timer.</summary>
    public sealed class MiningActivity
    {
        public readonly MiningPhase Phase;
        public readonly uint TargetId, EffectId, ArticleId;
        public readonly string ItemName;
        public readonly float DurationSeconds;
        public readonly long StartedAt;

        /// <summary>Keep the server's phase identity and duration together.</summary>
        public MiningActivity(MiningPhase phase, uint targetId, uint effectId, uint articleId,
            string itemName, float durationSeconds, long startedAt)
        {
            Phase = phase; TargetId = targetId; EffectId = effectId; ArticleId = articleId;
            ItemName = itemName; DurationSeconds = durationSeconds; StartedAt = startedAt;
        }

        /// <summary>
        /// Returns:
        ///     MiningActivity: A validated own-player prospect phase, or null for another pilot.
        /// Raises:
        ///     InvalidDataException: The native phase body is malformed.
        /// </summary>
        public static MiningActivity FromProspect(byte[] body, uint player)
        {
            if (body.Length != 20) throw new InvalidDataException("Invalid prospect phase length.");
            if (Wire.U32(body, 0) != player) return null;
            uint target = Wire.U32(body, 4), effect = Wire.U32(body, 8);
            uint duration = Wire.U32(body, 16);
            if (target == 0 || effect == 0 || duration == 0 || duration > 100000)
                throw new InvalidDataException("Invalid prospect phase details.");
            return new MiningActivity(MiningPhase.Prospect, target, effect, 0, "",
                duration / 1000f, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Returns:
        ///     MiningActivity: A validated own-player tractor phase, or null for another pilot.
        /// Raises:
        ///     InvalidDataException: The native tractor body is malformed.
        /// </summary>
        public static MiningActivity FromTractor(byte[] body, uint player, MiningActivity previous,
            uint selectedResource)
        {
            Wire.Require(body, 0, 40);
            if (Wire.U32(body, 0) != player) return null;
            int length = Wire.U16(body, 18);
            if (length > 256 || body.Length != 40 + length)
                throw new InvalidDataException("Invalid ore tractor name length.");
            uint article = Wire.U32(body, 4), effect = Wire.U32(body, 8);
            uint duration = Wire.U32(body, 20 + length);
            float speed = Wire.Float(body, 24 + length);
            for (int offset = 28 + length; offset < body.Length; offset += 4) Wire.Float(body, offset);
            if (article == 0 || effect == 0 || duration == 0 || duration > 600000 || speed <= 0)
                throw new InvalidDataException("Invalid ore tractor phase details.");
            string itemName = Wire.Text(body, 20, length);
            return new MiningActivity(MiningPhase.Tractor, previous?.TargetId ?? selectedResource, effect,
                article, itemName, duration / 1000f, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Returns:
        ///     float: The visual fraction elapsed within the server's phase duration.
        /// </summary>
        public float Progress(long now)
        { return Math.Max(0, Math.Min(1, (float)((now - StartedAt) / (double)Stopwatch.Frequency / DurationSeconds))); }
    }
}
