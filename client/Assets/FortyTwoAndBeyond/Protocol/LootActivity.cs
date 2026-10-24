using System;
using System.Diagnostics;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One server-started wreck tractor transfer with a display timer.</summary>
    public sealed class LootActivity
    {
        public readonly uint CorpseId, ArticleId, EffectId;
        public readonly string ItemName;
        public readonly Position Position;
        public readonly float DurationSeconds;
        public readonly long StartedAt;

        /// <summary>Keep the native tractor identity, destination and duration together.</summary>
        private LootActivity(uint corpseId, TractorPacket tractor, long startedAt)
        {
            CorpseId = corpseId; ArticleId = tractor.ArticleId; EffectId = tractor.EffectId;
            ItemName = tractor.ItemName; Position = tractor.Position;
            DurationSeconds = tractor.DurationSeconds; StartedAt = startedAt;
        }

        /// <summary>
        /// Returns:
        ///     LootActivity: A validated own-player wreck tractor, or null for another pilot.
        /// Raises:
        ///     System.IO.InvalidDataException: The native tractor body is malformed.
        /// </summary>
        public static LootActivity FromTractor(byte[] body, uint player, uint corpseId)
        {
            TractorPacket tractor = TractorPacket.Decode(body, player);
            return tractor == null ? null : new LootActivity(corpseId, tractor,
                Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Returns:
        ///     float: The display fraction elapsed within the server's tractor duration.
        /// </summary>
        public float Progress(long now)
        { return Math.Max(0, Math.Min(1, (float)((now - StartedAt) / (double)Stopwatch.Frequency / DurationSeconds))); }
    }
}
