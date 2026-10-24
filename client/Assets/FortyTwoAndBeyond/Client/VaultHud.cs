using System.Linq;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Show server-owned station storage and request full-stack moves.</summary>
    public sealed class VaultHud
    {
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
            Rect toggle = new Rect((CanvasWidth - 180) * .5f - 100, 27, 180, 30);
            if (GUI.Button(toggle, open ? "CLOSE VAULT" : "STATION VAULT", flightHud.ButtonStyle))
                open = !open;
            if (!open) return;
            Rect panel = new Rect((CanvasWidth - 720) * .5f, 96, 720, Mathf.Min(530, CanvasHeight - 180));
            flightHud.Panel(panel);
            GUI.Label(new Rect(panel.x + 18, panel.y + 14, 660, 28), "STATION VAULT", flightHud.BodyStyle);
            GUI.Label(new Rect(panel.x + 18, panel.y + 44, 660, 21),
                "The server owns each move. Select a stack to store or retrieve it.", flightHud.SmallStyle);
            float columnHeight = panel.height - 92;
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 74, 330, columnHeight));
            GUILayout.Label($"CARGO HOLD   {snapshot.Cargo.Count(item => item != null)}/{snapshot.Cargo.Length}",
                flightHud.BodyStyle);
            cargoScroll = GUILayout.BeginScrollView(cargoScroll);
            foreach (ItemView item in snapshot.Cargo.Where(item => item != null))
                if (GUILayout.Button($"STORE  {item.Slot:00}  {item.Name} ×{item.Quantity}",
                    flightHud.ButtonStyle, GUILayout.Height(29))) client.StoreCargo(item.Slot);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(panel.x + 372, panel.y + 74, 330, columnHeight));
            GUILayout.Label($"VAULT   {snapshot.Vault.Count(item => item != null)}/{snapshot.Vault.Length}",
                flightHud.BodyStyle);
            vaultScroll = GUILayout.BeginScrollView(vaultScroll);
            foreach (ItemView item in snapshot.Vault.Where(item => item != null))
                if (GUILayout.Button($"RETRIEVE  {item.Slot:00}  {item.Name} ×{item.Quantity}",
                    flightHud.ButtonStyle, GUILayout.Height(29))) client.RetrieveVault(item.Slot);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
    }
}
