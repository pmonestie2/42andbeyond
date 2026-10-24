using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Offer native Turbo Weapons activation with server-acknowledged status.</summary>
    public sealed class TurboHud
    {
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private float CanvasWidth => Screen.width / flightHud.Scale;

        /// <summary>Reuse the flight HUD's button and status styling.</summary>
        public TurboHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Draw the equipped Turbo Weapons device control during space flight.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Station != null) return;
            ItemView device = snapshot.Mounted.Skip(9).Take((int)snapshot.DeviceMounts)
                .FirstOrDefault(item => item?.Template == 2363 || item?.Template == 2412 ||
                    item?.Template == 2265);
            if (device == null) return;
            Rect panel = new Rect((CanvasWidth - 208) * .5f - 45, 22, 208, 76);
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 12, panel.y + 8, 184, 19),
                "WEAPON TURBO  ·  " + device.Name, flightHud.SmallStyle);
            GUI.enabled = client.Ready && !client.Incapacitated && !snapshot.TurboWeaponsActive;
            if (GUI.Button(new Rect(panel.x + 12, panel.y + 31, 184, 32),
                snapshot.TurboWeaponsActive ? "TURBO WEAPONS ACTIVE" :
                "TURBO WEAPONS  ·  " + client.Bindings.Display(ControlAction.TurboWeapons),
                flightHud.ButtonStyle))
                client.ActivateTurboWeapons();
            GUI.enabled = true;
        }
    }
}
