using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>An isolated playable design preview; it never connects to a server or changes game settings.</summary>
    public sealed class ResourceGaugePreview : MonoBehaviour
    {
        public float previewSeconds = -1;
        private ResourceDial[] large, compact;
        private GUIStyle heading, caption, detail;
        private bool varyLevels;
        private readonly float[] fractions={.78f,.997f,1};
        private readonly float[] capacities={134,100000,72000};

        /// <summary>Create the same three reusable instruments at showcase and actual HUD resolutions.</summary>
        private void Start()
        {
            large=new ResourceDial[3];compact=new ResourceDial[3];
            for(int index=0;index<3;index++)
            {large[index]=new ResourceDial((ResourceDialKind)index,1024);compact[index]=new ResourceDial((ResourceDialKind)index,512);}
            Font font=Resources.Load<Font>("Fonts/IBMPlexSans-SemiBold");
            heading=new GUIStyle{font=font,fontSize=25,normal={textColor=new Color(.91f,.95f,1)}};
            caption=new GUIStyle{font=font,fontSize=13,normal={textColor=new Color(.5f,.65f,.75f)}};
            detail=new GUIStyle(caption){fontSize=15,wordWrap=true};
        }

        /// <summary>Draw both scales, with adjustable real values and independently animated center imagery.</summary>
        private void OnGUI()
        {
            if(large==null)return;
            Matrix4x4 previous=GUI.matrix;
            float scale=Mathf.Min(Screen.width/1400f,Screen.height/1080f);
            GUI.matrix=Matrix4x4.TRS(new Vector3((Screen.width-1400*scale)/2,(Screen.height-1080*scale)/2,0),Quaternion.identity,Vector3.one*scale);
            try
            {
                GUI.Label(new Rect(38,23,900,35),"42 / BEYOND     —     RESOURCE INSTRUMENTS",heading);
                GUI.Label(new Rect(40,61,900,25),"LIVE UNITY PREVIEW   /   TEXTURED METAL · ANIMATED CORES · REAL RESOURCE VALUES",caption);
                float seconds=previewSeconds>=0?previewSeconds:Time.unscaledTime;
                Vector2[] centers={new Vector2(485,333),new Vector2(265,748),new Vector2(705,748)};
                Vector2[] smallCenters={new Vector2(1170,242),new Vector2(1075,413),new Vector2(1265,413)};
                for(int index=0;index<3;index++)
                {
                    float fraction=varyLevels ? .5f+.45f*Mathf.Sin(seconds*.6f+index*1.7f):fractions[index];
                    large[index].Draw(new Rect(centers[index].x-255,centers[index].y-255,510,510),fraction*capacities[index],capacities[index],seconds);
                    compact[index].Draw(new Rect(smallCenters[index].x-105,smallCenters[index].y-105,210,210),fraction*capacities[index],capacities[index],seconds);
                }
                GUI.Label(new Rect(1000,105,370,25),"COMPACT HUD SCALE",heading);
                GUI.Label(new Rect(1010,548,340,70),"Same instruments, rendered smaller.\nPercentage rings and center energy remain live.",detail);
                string[] names={"REACTOR RESERVE","SHIELD CHARGE","HULL INTEGRITY"};
                for(int index=0;index<3;index++)
                {
                    GUI.Label(new Rect(1010,645+index*65,340,22),names[index]+"   "+(fractions[index]*100).ToString("0.#")+"%",caption);
                    fractions[index]=GUI.HorizontalSlider(new Rect(1010,677+index*65,330,20),fractions[index],0,1);
                }
                varyLevels=GUI.Toggle(new Rect(1010,865,340,30),varyLevels," Animate discharge / recovery");
                GUI.Label(new Rect(40,1032,1300,26),"DESIGN PREVIEW  •  Runtime text, fill and animation; no baked values  •  Live game HUD is unchanged",caption);
            }
            finally{GUI.matrix=previous;}
        }

        /// <summary>Release the isolated preview's render surfaces when leaving its scene.</summary>
        private void OnDestroy()
        {
            if(large!=null)foreach(ResourceDial dial in large)dial?.Dispose();
            if(compact!=null)foreach(ResourceDial dial in compact)dial?.Dispose();
        }
    }
}
