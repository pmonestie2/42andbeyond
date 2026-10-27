using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show native ship damage deflection values without predicting hit outcomes.</summary>
    public sealed class DefenseHud
    {
        private static readonly string[] Channels =
            { "IMPACT", "EXPLOSIVE", "PLASMA", "ENERGY", "EMP", "CHEMICAL", "PSIONIC" };
        private readonly FlightHud flightHud;
        private bool open;

        /// <summary>Share the flight HUD's canvas scale and panel styles.</summary>
        public DefenseHud(FlightHud flightHud) { this.flightHud = flightHud; }

        /// <summary>Toggle this detail panel from the retained flight controls.</summary>
        internal void Toggle() { open = !open; }

        /// <summary>Dismiss this panel when its owning flight context closes.</summary>
        public void Close() { open = false; }

        /// <summary>
        /// Returns:
        ///     bool: Whether the pointer is over the defense control or its open panel.
        /// </summary>
        public bool ContainsPointer(Snapshot snapshot)
        {
            if (!Available(snapshot)) return false;
            Vector2 pointer = new Vector2(Input.mousePosition.x / flightHud.Scale,
                (Screen.height - Input.mousePosition.y) / flightHud.Scale);
            Rect button = ButtonRect(snapshot);
            return button.Contains(pointer) || open && PanelRect(button).Contains(pointer);
        }

        /// <summary>Draw a compact entry beside ship systems and a seven-channel detail panel.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (!Available(snapshot))
            { open = false; return; }
            Rect button = ButtonRect(snapshot);
            if (!flightHud.RetainedVisible && GUI.Button(button, "DEFENSE", flightHud.ButtonStyle)) open = !open;
            if (!open) return;
            Rect panel = PanelRect(button);
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 14, panel.y + 10, 385, 22),
                "DAMAGE DEFLECTION", flightHud.SectionStyle);
            if (!snapshot.Defense.Known)
            {
                GUI.Label(new Rect(panel.x + 14, panel.y + 49, 385, 23),
                    "Waiting for server ship stats", flightHud.SmallStyle);
                return;
            }
            for (int channel = 0; channel < Channels.Length; channel++)
            {
                int column = channel % 2, row = channel / 2;
                int value = snapshot.Defense.Deflects[channel];
                GUI.Label(new Rect(panel.x + 14 + column * 202, panel.y + 41 + row * 27, 190, 23),
                    Channels[channel] + "   " + value + "%",
                    value != 0 ? flightHud.SectionStyle : flightHud.SmallStyle);
            }
            GUI.Label(new Rect(panel.x + 14, panel.yMax - 19, 385, 17),
                "Server stats · equipment and abilities", flightHud.SmallStyle);
        }

        /// <summary>
        /// Returns:
        ///     bool: Whether this pilot can view the space combat defense panel.
        /// </summary>
        private static bool Available(Snapshot snapshot)
        { return snapshot?.Ready == true && snapshot.Station == null &&
            !snapshot.ResourceWindowOpen && snapshot.MiningActivity == null; }

        /// <summary>
        /// Returns:
        ///     Rect: The button aligned with the current ship systems height.
        /// </summary>
        private Rect ButtonRect(Snapshot snapshot)
        {
            float canvasHeight = Screen.height / flightHud.Scale;
            float systemHeight = 87 + Mathf.Min(FlightHud.ActiveEffectCount(snapshot), 5) * 30;
            return new Rect(Screen.width / flightHud.Scale - 430, 62, 110, 25);
        }

        /// <summary>
        /// Returns:
        ///     Rect: The open detail panel directly above ship systems.
        /// </summary>
        private static Rect PanelRect(Rect button)
        { return new Rect(button.x - 310, button.y + 32, 420, 169); }
    }
}
