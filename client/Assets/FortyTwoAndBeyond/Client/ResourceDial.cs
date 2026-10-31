using System;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    public enum ResourceDialKind { Reactor, Shield, Hull, Defense }

    /// <summary>A reusable textured instrument; its values and animated artwork are drawn live by Unity.</summary>
    public sealed class ResourceDial : IDisposable
    {
        private readonly Material material;
        private readonly RenderTexture surface;
        private readonly GUIStyle title, percentage, capacity, status;
        private readonly ResourceDialKind kind;
        private readonly Color color;

        /// <summary>Allocate one bounded render surface and retain the shared housing texture.</summary>
        public ResourceDial(ResourceDialKind kind, int resolution = 512, bool modern = false)
        {
            this.kind = kind;
            color = kind == ResourceDialKind.Reactor ? new Color(1,.52f,.055f) :
                kind == ResourceDialKind.Shield || kind == ResourceDialKind.Defense ? new Color(.05f,.77f,1) : new Color(1,.12f,.08f);
            Shader shader = Resources.Load<Shader>("UI/ResourceDials/Instrument");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Resource instrument shader is unavailable.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetTexture("_Bezel", Resources.Load<Texture2D>("UI/ResourceDials/ArmoredBezel"));
            material.SetColor("_Tint",color); material.SetFloat("_Kind",(int)kind);
            material.SetFloat("_Modern", modern ? 1 : 0);
            surface = new RenderTexture(Mathf.Clamp(resolution,128,2048),Mathf.Clamp(resolution,128,2048),0,RenderTextureFormat.ARGB32)
                { name = kind+" instrument", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            surface.Create();
            Font font = Resources.Load<Font>("Fonts/IBMPlexSans-SemiBold");
            title = Style(font,color); percentage = Style(font,Color.Lerp(color,Color.white,.7f));
            capacity = Style(font,Color.Lerp(color,Color.white,.65f)); status = Style(font,color);
        }

        /// Returns:
        ///     RenderTexture: The live instrument face, reusable by retained UI without an IMGUI pass.
        public RenderTexture Render(float current, float maximum, float seconds, bool compact = false)
        {
            material.SetFloat("_Fraction",maximum>0?Mathf.Clamp01(current/maximum):0);
            material.SetFloat("_Clock",seconds);
            material.SetFloat("_Compact",compact?1:0);
            RenderTexture previous=RenderTexture.active;
            try { Graphics.Blit(Texture2D.blackTexture,surface,material); }
            finally { RenderTexture.active=previous; }
            return surface;
        }

        /// Returns:
        ///     RenderTexture: One face with independent blue shield and red hull rings.
        public RenderTexture RenderDefense(float shield,float maxShield,float hull,float maxHull,bool hasHull,float seconds)
        {
            material.SetFloat("_HasHull",hasHull?1:0);
            material.SetFloat("_HullFraction",maxHull>0?Mathf.Clamp01(hull/maxHull):-1);
            return Render(shield,maxShield,seconds,compact:true);
        }

        /// <summary>Render a live resource level, energy core and labels at any square HUD size.</summary>
        public void Draw(Rect bounds, float current, float maximum, float seconds)
        {
            float fraction = maximum > 0 ? Mathf.Clamp01(current/maximum) : 0;
            if (Event.current.type == EventType.Repaint)
            {
                Render(current,maximum,seconds);
                GUI.DrawTexture(bounds,surface,ScaleMode.StretchToFill,true);
            }
            title.fontSize = Mathf.Max(8,Mathf.RoundToInt(bounds.width*.044f));
            percentage.fontSize = Mathf.Max(13,Mathf.RoundToInt(bounds.width*.073f));
            capacity.fontSize = Mathf.Max(8,Mathf.RoundToInt(bounds.width*.033f));
            status.fontSize = Mathf.Max(7,Mathf.RoundToInt(bounds.width*.024f));
            GUI.Label(Within(bounds,.26f,.26f,.48f,.05f),kind == ResourceDialKind.Hull ? "HULL INTEGRITY" : kind.ToString().ToUpperInvariant(),title);
            GUI.Label(Within(bounds,.28f,.303f,.44f,.075f),maximum >= 0 ? (fraction*100).ToString("0.#")+"%" : "—",percentage);
            GUI.Label(Within(bounds,.22f,.702f,.56f,.038f),maximum >= 0 ? $"{current:N0} / {maximum:N0}" : "NO TELEMETRY",capacity);
            string state = fraction < .25f ? "RESERVE CRITICAL" : kind == ResourceDialKind.Reactor ? "REACTOR RESERVE" :
                kind == ResourceDialKind.Shield || kind == ResourceDialKind.Defense ? "SHIELD STABLE" : "STRUCTURE NOMINAL";
            GUI.Label(Within(bounds,.24f,.746f,.52f,.03f),state,status);
        }

        /// Returns:
        ///     Rect: A proportional label box within one dial, independent of display resolution.
        private static Rect Within(Rect outer,float x,float y,float width,float height)
        { return new Rect(outer.x+x*outer.width,outer.y+y*outer.height,width*outer.width,height*outer.height); }

        /// Returns:
        ///     GUIStyle: A reusable transparent, centered instrument label.
        private static GUIStyle Style(Font font,Color color)
        { return new GUIStyle { font=font,alignment=TextAnchor.MiddleCenter,normal={textColor=color},padding=new RectOffset(),margin=new RectOffset(),clipping=TextClipping.Clip }; }

        /// <summary>Release only this instrument's material and render target.</summary>
        public void Dispose()
        { surface.Release(); UnityEngine.Object.Destroy(surface); UnityEngine.Object.Destroy(material); }
    }
}
