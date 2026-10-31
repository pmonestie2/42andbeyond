using System;
using FortyTwoAndBeyond.Client;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Check the rendered meter proportions, not just the shader's source constants.</summary>
    public static class ResourceDialChecks
    {
        /// <summary>Sample all sixty lit segment centers for exact 2:1 defense and shield-only coverage.</summary>
        public static void Run()
        {
            using(var dial=new ResourceDial(ResourceDialKind.Defense,384))
            {
                var combined=CountZones(dial.RenderDefense(1,1,1,1,true,0));
                if(combined.blue!=40||combined.red!=20)
                    throw new InvalidOperationException("Full-circle defense must render 40 blue shield and 20 red hull segments: "+combined);
                CheckOpposingFill(dial.RenderDefense(.5f,1,.5f,1,true,0));
                var organic=CountZones(dial.RenderDefense(1,1,0,0,false,0));
                if(organic.blue!=60||organic.red!=0)
                    throw new InvalidOperationException("Shield-only contact did not use the complete circle: "+organic);
            }
            using(var dial=new ResourceDial(ResourceDialKind.Defense,384,modern:true))
            {
                var combined=CountZones(dial.RenderDefense(1,1,1,1,true,0),.39f);
                if(combined.blue!=40||combined.red!=20)
                    throw new InvalidOperationException("Minimal defense lost its 2:1 proportions: "+combined);
                CheckOpposingFill(dial.RenderDefense(.5f,1,.5f,1,true,0),.39f);
                var organic=CountZones(dial.RenderDefense(1,1,0,0,false,0),.39f);
                if(organic.blue!=60||organic.red!=0)
                    throw new InvalidOperationException("Minimal shield-only face lost full coverage: "+organic);
                CheckTransparentCorners(dial.RenderDefense(1,1,1,1,true,0));
            }
            Debug.Log("PASS: GPU samples confirm 240-degree shields, 120-degree hull and full-circle shield-only contacts, and opposing half-fill directions");
        }

        /// <summary>Check partially depleted arcs so a reversed hull fill cannot hide behind full-circle tests.</summary>
        private static void CheckOpposingFill(RenderTexture surface,float radius=.27f)
        {
            var pixels=new Texture2D(surface.width,surface.height,TextureFormat.RGB24,false);
            RenderTexture previous=RenderTexture.active;
            try
            {
                RenderTexture.active=surface;pixels.ReadPixels(new Rect(0,0,surface.width,surface.height),0,0);pixels.Apply();
                Color hullStart=SampleArc(pixels,327,radius),hullEnd=SampleArc(pixels,267,radius);
                Color shieldStart=SampleArc(pixels,63,radius),shieldEnd=SampleArc(pixels,183,radius);
                if(hullStart.r<hullEnd.r*2||shieldStart.b<shieldEnd.b*2)
                    throw new InvalidOperationException("Shield and hull do not fill from their common origin in opposing directions");
            }
            finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels);}
        }

        /// <summary>Prevent the new minimal dial from restoring a rectangular or armored background.</summary>
        private static void CheckTransparentCorners(RenderTexture surface)
        {
            var pixels=new Texture2D(surface.width,surface.height,TextureFormat.RGBA32,false);
            RenderTexture previous=RenderTexture.active;
            try
            {
                RenderTexture.active=surface;pixels.ReadPixels(new Rect(0,0,surface.width,surface.height),0,0);pixels.Apply();
                foreach(var point in new[]{new Vector2Int(2,2),new Vector2Int(2,surface.height-3),
                    new Vector2Int(surface.width-3,2),new Vector2Int(surface.width-3,surface.height-3)})
                    if(pixels.GetPixel(point.x,point.y).a>.01f)
                        throw new InvalidOperationException("Minimal instrument corners are not transparent.");
            }
            finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels);}
        }

        /// Returns:
        ///     Color: One rendered segment center at the specified shader-space angle.
        private static Color SampleArc(Texture2D pixels,float degrees,float radius)
        {
            float angle=degrees*Mathf.Deg2Rad;
            return pixels.GetPixel(Mathf.RoundToInt((.5f+Mathf.Sin(angle)*radius)*pixels.width),
                Mathf.RoundToInt((.5f+Mathf.Cos(angle)*radius)*pixels.height));
        }

        /// Returns:
        ///     (int blue, int red): Segment centers dominated by the expected resource color.
        private static (int blue,int red) CountZones(RenderTexture surface,float radius=.27f)
        {
            var pixels=new Texture2D(surface.width,surface.height,TextureFormat.RGB24,false);
            RenderTexture previous=RenderTexture.active;
            try
            {
                RenderTexture.active=surface;pixels.ReadPixels(new Rect(0,0,surface.width,surface.height),0,0);pixels.Apply();
                int blue=0,red=0;
                for(int index=0;index<60;index++)
                {
                    float angle=(index*6+3)*Mathf.Deg2Rad;
                    Color sample=pixels.GetPixel(Mathf.RoundToInt((.5f+Mathf.Sin(angle)*radius)*surface.width),Mathf.RoundToInt((.5f+Mathf.Cos(angle)*radius)*surface.height));
                    if(sample.b>sample.r*1.3f)blue++;
                    else if(sample.r>sample.b*1.3f)red++;
                }
                return(blue,red);
            }
            finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels);}
        }
    }
}
