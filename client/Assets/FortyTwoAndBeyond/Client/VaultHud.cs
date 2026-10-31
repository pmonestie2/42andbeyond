using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show server-owned station storage and bounded stack moves.</summary>
    public sealed class VaultHud
    {
        private readonly HudWindowDrag windowDrag = new HudWindowDrag();
        private readonly SpaceClient client;
        private readonly FlightHud flightHud;
        private Vector2 cargoScroll, vaultScroll;
        private bool open;
        public bool IsOpen => open;
        private float CanvasWidth => Screen.width / flightHud.Scale;
        private float CanvasHeight => Screen.height / flightHud.Scale;

        /// <summary>Bind the vault panel to the existing station HUD styles.</summary>
        public VaultHud(SpaceClient client, FlightHud flightHud)
        { this.client = client; this.flightHud = flightHud; }

        /// <summary>Close the storage overlay when another station service takes focus.</summary>
        public void Close() { open = false; }

        /// <summary>Offer storage only while docked and outside NPC dialogue.</summary>
        public void Draw(Snapshot snapshot)
        {
            if (snapshot?.Station == null) { open = false; return; }
            if (snapshot.Dialogue != null) { open = false; return; }
            Rect toggle = new Rect(CanvasWidth - 750, 37, 170, 34);
            if (GUI.Button(toggle, open ? "CLOSE VAULT" : "STATION VAULT", flightHud.ButtonStyle))
                open = !open;
            if (!open) return;
            Rect panel = windowDrag.Handle(new Rect((CanvasWidth - 720) * .5f, 96, 720, Mathf.Min(530, CanvasHeight - 180)), CanvasWidth, CanvasHeight);
            flightHud.Panel(panel); HudWindowDrag.DrawGrip(panel);
            GUI.Label(new Rect(panel.x + 36, panel.y + 14, 642, 28), "STATION VAULT", flightHud.BodyStyle);
            GUI.Label(new Rect(panel.x + 18, panel.y + 44, 660, 21),
                "Compatible stacks merge. A partial move leaves the rest to transfer again.", flightHud.SmallStyle);
            float columnHeight = panel.height - 92;
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 74, 330, columnHeight));
            GUILayout.Label($"CARGO HOLD · STORE   {snapshot.Cargo.Take((int)snapshot.CargoCapacity).Count(item => item != null)}/{snapshot.CargoCapacity}",
                flightHud.BodyStyle);
            cargoScroll = GUILayout.BeginScrollView(cargoScroll);
            foreach (ItemView item in snapshot.Cargo.Where(item => item != null))
                if (flightHud.ItemActionRow(item, $"STORE  ×{item.Quantity}  ·  Slot {item.Slot:00}",
                    330, client.Ready)) client.StoreCargo(item.Slot);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 372, panel.y + 74, 330, columnHeight));
            GUILayout.Label($"VAULT · RETRIEVE   {snapshot.Vault.Count(item => item != null)}/{snapshot.Vault.Length}",
                flightHud.BodyStyle);
            vaultScroll = GUILayout.BeginScrollView(vaultScroll);
            foreach (ItemView item in snapshot.Vault.Where(item => item != null))
                if (flightHud.ItemActionRow(item, $"RETRIEVE  ×{item.Quantity}  ·  Slot {item.Slot:00}",
                    330, client.Ready)) client.RetrieveVault(item.Slot);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
    }
}
