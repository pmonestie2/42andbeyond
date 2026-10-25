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
        public readonly Position Position;
        public readonly float DurationSeconds;
        public readonly long StartedAt;

        /// <summary>Keep the server's phase identity and duration together.</summary>
        public MiningActivity(MiningPhase phase, uint targetId, uint effectId, uint articleId,
            string itemName, float durationSeconds, long startedAt, Position position = default)
        {
            Phase = phase; TargetId = targetId; EffectId = effectId; ArticleId = articleId;
            ItemName = itemName; DurationSeconds = durationSeconds; StartedAt = startedAt;
            Position = position;
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
            TractorPacket tractor = TractorPacket.Decode(body, player);
            return tractor == null ? null : new MiningActivity(MiningPhase.Tractor,
                previous?.TargetId ?? selectedResource, tractor.EffectId,
                tractor.ArticleId, tractor.ItemName, tractor.DurationSeconds,
                Stopwatch.GetTimestamp(), tractor.Position);
        }

        /// <summary>
        /// Returns:
        ///     float: The visual fraction elapsed within the server's phase duration.
        /// </summary>
        public float Progress(long now)
        { return Math.Max(0, Math.Min(1, (float)((now - StartedAt) / (double)Stopwatch.Frequency / DurationSeconds))); }
    }
}
