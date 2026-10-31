using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>A compact retained instrument with a restrained face shared by self, crew and targets.</summary>
    public sealed class ResourceDialWidget : VisualElement, IDisposable
    {
        private readonly ResourceDial dial;
        private readonly Image face;
        private readonly Label caption,percentage,value,hullPercentage,hullValue;
        private readonly ResourceDialKind kind;
        private readonly int size;
        private float current,maximum=-1,hull,maxHull=-1,nextFrame;
        private bool dirty = true;
        public bool HasHull {get;private set;}
        public string Subject {get;set;} = "SHIP RESOURCES";

        /// <summary>Create a bounded dial with independently readable numeric labels.</summary>
        public ResourceDialWidget(ResourceDialKind kind,int size=128)
        {
            this.kind=kind;this.size=size;bool combined=kind==ResourceDialKind.Defense;
            string key=combined?"defense":kind.ToString().ToLowerInvariant();name="gauge-"+key;
            style.width=size;style.height=size;style.flexShrink=0;
            dial=new ResourceDial(kind,size<=100?256:384,modern:true);
            face=new Image{pickingMode=PickingMode.Ignore};face.style.position=UnityEngine.UIElements.Position.Absolute;
            face.style.left=face.style.top=0;face.style.width=face.style.height=size;Add(face);
            Color color=kind==ResourceDialKind.Reactor?new Color(1,.63f,.12f):new Color(.1f,.85f,1);
            caption=AddLabel(combined?"SHIELD / HULL":key.ToUpperInvariant(),.20f,.24f,.6f,12,size<=100?7:8,color);
            caption.name="gauge-caption";caption.style.display=DisplayStyle.None;
            percentage=AddLabel("—",.19f,combined ? .22f : .63f,.62f,size<=100?20:25,size<=64?9:size<=100?13:18,color);
            percentage.name="gauge-percentage";
            value=AddLabel("—",combined?0:.13f,combined?1:.70f,combined?1:.74f,13,combined?10:7,Color.Lerp(color,Color.white,.6f));
            value.name=combined?"gauge-shield-value":name+"-value";value.style.display=DisplayStyle.None;
            hullPercentage=AddLabel("—",.19f,.64f,.62f,size<=100?19:23,size<=64?8:size<=100?12:16,new Color(1,.25f,.16f));
            hullPercentage.name="gauge-hull-percentage";
            hullValue=AddLabel("—",0,1,1,13,10,new Color(1,.55f,.47f));hullValue.style.top=size+14;
            hullValue.name="gauge-hull-value";
            hullPercentage.style.display=hullValue.style.display=DisplayStyle.None;
        }

        /// <summary>Publish an authoritative reactor or single-resource value.</summary>
        public void Set(float fraction,float current,float maximum)
        {
            float availableMaximum = fraction >= 0 ? maximum : -1;
            dirty |= this.current != current || this.maximum != availableMaximum;
            this.current=current;this.maximum=availableMaximum;
            percentage.text=fraction>=0?$"{Mathf.Clamp01(fraction)*100:0.#}%":"—";
            value.text=maximum>=0&&fraction>=0?$"{Mathf.Max(0,current):N0} / {maximum:N0}":"NO DATA";
            tooltip=kind.ToString().ToUpperInvariant()+": "+(maximum>=0&&fraction>=0?$"{Mathf.Max(0,current):N1} / {maximum:N0} ({percentage.text})":"Telemetry unavailable");
        }

        /// <summary>Keep shield and hull independent; a native zero hull capacity hides the red instrument entirely.</summary>
        public void SetDefense(float shieldFraction,float maxShield,float hull,float maxHull,bool hasHull)
        {
            Set(shieldFraction,Mathf.Max(0,shieldFraction)*maxShield,maxShield);
            dirty |= this.hull != hull || this.maxHull != maxHull || HasHull != hasHull;
            this.hull=hull;this.maxHull=maxHull;HasHull=hasHull;
            caption.text=hasHull?"SHIELD / HULL":"SHIELD";
            hullPercentage.text=maxHull>0?$"{Mathf.Clamp01(hull/maxHull)*100:0.#}%":"—";
            hullValue.text=maxHull>0?$"H {Mathf.Max(0,hull):N0} / {maxHull:N0}":"H NO DATA";
            value.text="S "+value.text;
            hullPercentage.style.display=hasHull?DisplayStyle.Flex:DisplayStyle.None;
            hullValue.style.display=DisplayStyle.None;
            percentage.style.top=(hasHull ? .22f : .63f)*size;
            style.height=size;
            tooltip="Shield: "+(shieldFraction>=0&&maxShield>=0?$"{Mathf.Max(0,shieldFraction)*maxShield:N1} / {maxShield:N0} ({shieldFraction*100:0.#}%)":"Telemetry unavailable")+
                (hasHull?"\nHull: "+(maxHull>0?$"{Mathf.Max(0,hull):N1} / {maxHull:N0} ({Mathf.Clamp01(hull/maxHull)*100:0.#}%)":"Telemetry unavailable"):"");
        }

        /// <summary>Animate only requested visible instruments, at no more than 24 Hz.</summary>
        public void Tick(float seconds)
        {
            if(!dirty||seconds<nextFrame)return;
            face.image=kind==ResourceDialKind.Defense?dial.RenderDefense(current,maximum,hull,maxHull,HasHull,seconds):dial.Render(current,maximum,seconds,compact:true);
            dirty = false;
            nextFrame=seconds+1f/24;
        }

        /// Returns:
        ///     Label: A cached label positioned relative to the square dial face.
        private Label AddLabel(string text,float x,float y,float width,float height,int fontSize,Color color)
        {
            var label=new Label(text){pickingMode=PickingMode.Ignore};label.style.position=UnityEngine.UIElements.Position.Absolute;
            label.style.left=x*size;label.style.top=y*size;label.style.width=width*size;label.style.height=height;
            label.style.fontSize=fontSize;label.style.color=color;label.style.unityTextAlign=TextAnchor.MiddleCenter;
            label.style.unityFont=Resources.Load<Font>("Fonts/IBMPlexSans-SemiBold");label.style.whiteSpace=WhiteSpace.NoWrap;
            label.style.paddingLeft=label.style.paddingRight=0;Add(label);return label;
        }

        /// <summary>Release the owned GPU instrument face.</summary>
        public void Dispose(){dial.Dispose();}
    }
}
