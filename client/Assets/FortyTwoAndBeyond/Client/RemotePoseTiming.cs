using System;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Bound visual MOB pose interpolation without changing authoritative positions.</summary>
    public static class RemotePoseTiming
    {
        /// Returns:
        ///     float: Duration of one visual transition from the latest server sample.
        public static float Duration(float sampleInterval) =>
            Math.Max(.25f, Math.Min(.8f, sampleInterval));

        /// Returns:
        ///     float: Linear blend fraction capped at the latest authoritative pose.
        public static float Fraction(float age, float duration) =>
            duration <= 0 ? 1 : Math.Max(0, Math.Min(1, age / duration));

        /// Returns:
        ///     bool: Whether a large discontinuity must snap rather than draw a false travel path.
        public static bool Teleport(float worldDistance, float sampleInterval) =>
            worldDistance > Math.Max(30f, Math.Min(180f, sampleInterval * 60f));
    }
}
