using System;
using FortyTwoAndBeyond.Protocol;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Bound local station-avatar input before publishing native room poses.</summary>
    public sealed class StationWalkState
    {
        public uint Room { get; private set; } = uint.MaxValue;
        public float X { get; private set; }
        public float Z { get; private set; }
        public float Orientation { get; private set; }
        public bool Moving { get; private set; }

        /// <summary>Place the local avatar near the visible entrance of a new room.</summary>
        public void EnterRoom(uint room)
        {
            Room = room; X = 0; Z = LocalSession.StationEntryZ;
            Orientation = 0; Moving = false;
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether position or facing changed this frame.
        /// Raises:
        ///     ArgumentOutOfRangeException: The frame duration is not finite or is negative.
        /// </summary>
        public bool Advance(float deltaSeconds, bool forward, bool reverse, bool turnLeft, bool turnRight)
        {
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            if (Room == uint.MaxValue) return false;
            float delta = Math.Min(deltaSeconds, .1f);
            float oldX = X, oldZ = Z, oldOrientation = Orientation;
            int turn = (turnRight ? 1 : 0) - (turnLeft ? 1 : 0);
            int walk = (forward ? 1 : 0) - (reverse ? 1 : 0);
            Orientation += turn * 2f * delta;
            if (Orientation > Math.PI) Orientation -= (float)(2 * Math.PI);
            if (Orientation < -Math.PI) Orientation += (float)(2 * Math.PI);
            X = Math.Max(-30f, Math.Min(30f, X + (float)Math.Sin(Orientation) * walk * 8f * delta));
            Z = Math.Max(-40f, Math.Min(40f, Z + (float)Math.Cos(Orientation) * walk * 8f * delta));
            Moving = turn != 0 || walk != 0;
            return X != oldX || Z != oldZ || Orientation != oldOrientation;
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether an idle pose should be published after movement.
        /// </summary>
        public bool Stop()
        { bool wasMoving = Moving; Moving = false; return wasMoving; }
    }
}
