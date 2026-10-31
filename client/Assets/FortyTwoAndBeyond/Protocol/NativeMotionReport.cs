using System;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Encode the native MVAS position and heading report without changing World state.</summary>
    public static class NativeMotionReport
    {
        /// <summary>
        /// Returns:
        ///     float: The bounded speed after one prediction step without a fresh server pose.
        /// </summary>
        public static float AdvanceSpeed(float current, float target, float acceleration, float seconds)
        {
            if (!Finite(current) || !Finite(target) || !Finite(acceleration) || !Finite(seconds) ||
                seconds < 0) return current;
            float amount = Math.Max(0, acceleration) * seconds;
            return current < target ? Math.Min(target, current + amount) :
                Math.Max(target, current - amount);
        }

        /// <returns>float: The native throttle target within server-published impulse limits.</returns>
        public static float ControlTarget(byte command, float serverTarget,
            float maximumSpeed, float minimumSpeed)
        {
            float desired = command == 2 ? maximumSpeed : command == 3 ? minimumSpeed :
                command == 4 ? 0 : serverTarget;
            return Math.Max(minimumSpeed, Math.Min(maximumSpeed, desired));
        }

        /// <summary>
        /// Returns:
        ///     byte[]: The captured 28-byte 0x1004 body: position, unit heading, signed speed in metres per second.
        /// Raises:
        ///     InvalidDataException: A position, heading or speed is nonfinite, or the heading is zero.
        /// </summary>
        public static byte[] Encode(Position position, Position heading, float speedMetersPerSecond)
        {
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(position.Z) ||
                !Finite(heading.X) || !Finite(heading.Y) || !Finite(heading.Z) ||
                !Finite(speedMetersPerSecond) || Math.Abs(speedMetersPerSecond) > int.MaxValue)
                throw new InvalidDataException("Nonfinite native motion report.");
            float length = (float)Math.Sqrt(heading.X * heading.X +
                heading.Y * heading.Y + heading.Z * heading.Z);
            if (length < .0001f || !Finite(length))
                throw new InvalidDataException("Native motion heading is empty.");
            return Wire.Body(writer =>
            {
                writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z);
                writer.Write(heading.X / length); writer.Write(heading.Y / length);
                writer.Write(heading.Z / length);
                writer.Write((int)Math.Round(speedMetersPerSecond, MidpointRounding.AwayFromZero));
            });
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether a value can be used in a native movement report.
        /// </summary>
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
