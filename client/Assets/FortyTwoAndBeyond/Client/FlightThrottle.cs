namespace FortyTwoAndBeyond.Client
{
    /// <summary>Turn held flight keys into one server throttle request per change.</summary>
    public sealed class FlightThrottle
    {
        private byte previousHeld;

        /// <summary>
        /// Returns:
        ///     byte?: A native thrust command when the held control changes.
        /// </summary>
        public byte? Sample(bool enabled, bool forwardHeld, bool reverseHeld, bool brakeHeld)
        {
            if (!enabled) { previousHeld = 0; return null; }
            byte held = brakeHeld ? (byte)4 : reverseHeld ? (byte)3 : forwardHeld ? (byte)2 : (byte)0;
            if (held == previousHeld) return null;
            previousHeld = held;
            return held == 0 ? (byte?)null : held;
        }

        /// <summary>Require a fresh request if a key remains held after lost focus.</summary>
        public void Reset() { previousHeld = 0; }
    }
}
