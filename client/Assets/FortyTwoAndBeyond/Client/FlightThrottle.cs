namespace FortyTwoAndBeyond.Client
{
    /// <summary>Turn held flight keys into one server throttle request per change.</summary>
    public sealed class FlightThrottle
    {
        private byte previousHeld;
        private byte previousDirections;
        private bool brakingPress;

        /// <summary>
        /// Returns:
        ///     byte?: A native thrust command when the held control changes.
        /// </summary>
        public byte? Sample(bool enabled, bool forwardHeld, bool reverseHeld, bool brakeHeld, float currentSpeed = 0, float targetSpeed = 0)
        {
            if (!enabled) { Reset(); return null; }
            byte directions = (byte)((forwardHeld ? 1 : 0) | (reverseHeld ? 2 : 0));
            if (directions != previousDirections)
            {
                // Keep an opposing press as a brake through rest. A new press can
                // reverse; holding the brake must not silently accelerate backward.
                float motion = System.Math.Abs(currentSpeed) > .5f ? currentSpeed : targetSpeed;
                brakingPress = directions == 1 && motion < -.5f || directions == 2 && motion > .5f;
                previousDirections = directions;
            }
            byte held = brakeHeld || forwardHeld && reverseHeld || brakingPress ? (byte)4 :
                reverseHeld ? (byte)3 : forwardHeld ? (byte)2 : (byte)0;
            if (held == previousHeld) return null;
            previousHeld = held;
            return held == 0 ? (byte?)null : held;
        }

        /// <summary>Require a fresh request if a key remains held after lost focus.</summary>
        public void Reset() { previousHeld = previousDirections = 0; brakingPress = false; }
    }
}
