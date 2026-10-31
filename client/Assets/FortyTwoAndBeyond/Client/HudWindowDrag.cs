using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Give immediate-mode subwindows a consistent, visible upper-left drag grip.</summary>
    public sealed class HudWindowDrag
    {
        private bool moved, dragging;
        private int activeControl;
        private Vector2 position, offset;

        /// <returns>Rect: Current window bounds, clamped to keep its grip reachable.</returns>
        public Rect Bounds(Rect suggested, float canvasWidth, float canvasHeight)
        {
            if (moved) suggested.position = position;
            suggested.x = Mathf.Clamp(suggested.x, 4, Mathf.Max(4, canvasWidth - suggested.width - 4));
            suggested.y = Mathf.Clamp(suggested.y, 4, Mathf.Max(4, canvasHeight - suggested.height - 4));
            return suggested;
        }

        /// <returns>Rect: Updated window bounds after a grip drag, without consuming content clicks.</returns>
        public Rect Handle(Rect suggested, float canvasWidth, float canvasHeight)
        {
            Rect bounds = Bounds(suggested, canvasWidth, canvasHeight);
            int control = GUIUtility.GetControlID(FocusType.Passive);
            Event input = Event.current;
            if (GUI.enabled && input.type == EventType.MouseDown && input.button == 0 && Grip(bounds).Contains(input.mousePosition))
            {
                dragging = moved = true; position = bounds.position;
                offset = input.mousePosition - position; activeControl = control; GUIUtility.hotControl = control; input.Use();
            }
            if (dragging && input.type == EventType.MouseDrag)
            {
                position = input.mousePosition - offset;
                bounds = Bounds(suggested, canvasWidth, canvasHeight);
                position = bounds.position; input.Use();
            }
            if (dragging && (input.rawType == EventType.MouseUp || input.rawType == EventType.Ignore))
            {
                dragging = false;
                if (GUIUtility.hotControl == activeControl) GUIUtility.hotControl = 0;
                activeControl = 0;
                input.Use();
            }
            return bounds;
        }

        /// <summary>Draw a high-contrast six-dot grip with a runtime hover hint.</summary>
        public static void DrawGrip(Rect window)
        {
            Rect grip = Grip(window);
            Color previous = GUI.color;
            GUI.color = new Color(.10f, .24f, .29f, .95f);
            GUI.DrawTexture(grip, Texture2D.whiteTexture);
            GUI.color = new Color(.55f, .90f, 1f);
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 2; column++)
                    GUI.DrawTexture(new Rect(grip.x + 6 + column * 6, grip.y + 5 + row * 6, 3, 3), Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(grip, new GUIContent("", "Drag to move this window"));
        }

        /// <returns>Rect: The same upper-left hit target used by every immediate-mode window grip.</returns>
        private static Rect Grip(Rect window) => new Rect(window.x + 4, window.y + 5, 24, 26);
    }
}
