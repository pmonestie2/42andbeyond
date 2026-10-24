using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Server-owned warp drive phase for the local ship.</summary>
    public enum WarpPhase { Idle, Charging, Traveling, Recovering }

    /// <summary>One immutable native warp revision.</summary>
    public sealed class WarpState
    {
        public readonly WarpPhase Phase;
        public readonly bool Arrived;

        /// <summary>Keep the phase and arrival reason in one snapshot revision.</summary>
        private WarpState(WarpPhase phase, bool arrived)
        { Phase = phase; Arrived = arrived; }

        /// <summary>
        /// Returns:
        ///     WarpState: The phase encoded by the native private and global ship fields.
        /// Raises:
        ///     InvalidDataException: The server fields disagree or describe an unknown phase.
        /// </summary>
        public static WarpState Decode(uint privateState, uint globalState)
        {
            if (privateState == 0 && globalState == 0) return new WarpState(WarpPhase.Idle, false);
            if (privateState == 1 && globalState == 1) return new WarpState(WarpPhase.Charging, false);
            if (privateState == 2 && globalState == 2) return new WarpState(WarpPhase.Traveling, false);
            if ((privateState == 3 || privateState == 8) && globalState == 3)
                return new WarpState(WarpPhase.Recovering, privateState == 8);
            throw new InvalidDataException("Invalid native warp phase.");
        }

        /// <summary>
        /// Returns:
        ///     byte[]: Native route request for one target, or no waypoints for free flight and cancellation.
        /// </summary>
        public static byte[] RouteRequest(uint owner, uint target)
        {
            return Wire.Body(writer =>
            {
                writer.Write(owner & 0xffffff);
                writer.Write((ushort)(target == 0 ? 0 : 1));
                if (target != 0) writer.Write(target);
            });
        }
    }
}
