using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>A stable client-only recipe; it contains no server objects or gameplay state.</summary>
    public sealed class SectorSceneryProfile
    {
        public sealed class Body
        {
            public readonly int Surface;
            public readonly int Terrain;
            public readonly Vector3 Position;
            public readonly Vector3 Tilt;
            public readonly Color Tint;
            public readonly Color Atmosphere;
            public readonly float Radius;
            public readonly float Spin;
            public readonly float Phase;
            public readonly float TerrainBlend;
            public readonly bool Gas;
            public readonly bool Clouds;
            public readonly bool Rings;
            public readonly int Moons;

            /// <summary>Keep one immutable planet description independent of its instantiated meshes.</summary>
            public Body(int surface, int terrain, Vector3 position, Vector3 tilt, Color tint,
                Color atmosphere, float radius, float spin, float phase, float terrainBlend,
                bool gas, bool clouds, bool rings, int moons)
            {
                Surface=surface;Terrain=terrain;Position=position;Tilt=tilt;Tint=tint;Atmosphere=atmosphere;
                Radius=radius;Spin=spin;Phase=phase;TerrainBlend=terrainBlend;Gas=gas;Clouds=clouds;Rings=rings;Moons=moons;
            }
        }

        public const int MinimumBodies = 6;
        public const int MaximumBodies = 9;

        public static readonly string[] SurfaceMaps = {
            "8k_earth_daymap", "4k_mercury", "4k_mars", "4k_venus_surface", "4k_makemake_fictional", "4k_eris_fictional",
            "8k_jupiter", "8k_saturn", "2k_uranus", "2k_neptune"
        };
        public readonly uint Seed;
        public readonly ReadOnlyCollection<Body> Bodies;
        public readonly bool BlackHole;
        public readonly float SkyYaw;
        public readonly Color SkyTint;
        public readonly Vector3 HolePosition;
        public readonly Quaternion LayoutRotation;
        public readonly string Fingerprint;

        /// <summary>Store a complete stable recipe and a readable fingerprint for regression checks.</summary>
        private SectorSceneryProfile(uint seed, Body[] bodies, bool blackHole, float skyYaw,
            Color skyTint, Vector3 holePosition, Quaternion layoutRotation)
        {
            Seed=seed;Bodies=Array.AsReadOnly(bodies);BlackHole=blackHole;SkyYaw=skyYaw;
            SkyTint=skyTint;HolePosition=holePosition;LayoutRotation=layoutRotation;
            var text=new StringBuilder(seed.ToString("X8",CultureInfo.InvariantCulture));
            text.Append(blackHole?'H':'S');
            foreach(Body body in bodies)
            {
                text.Append('|').Append(body.Surface).Append(':').Append(body.Terrain).Append(':').Append(body.Rings?'R':'-').Append(body.Clouds?'C':'-').Append(body.Moons);
                foreach(float value in new[]{body.Radius,body.Position.x,body.Position.y,body.Position.z,body.Spin,body.Phase,body.TerrainBlend})
                    text.Append(':').Append(value.ToString("R",CultureInfo.InvariantCulture));
            }
            Fingerprint=text.ToString();
        }

        /// Returns:
        ///     SectorSceneryProfile: A reproducible composition keyed to sector ID, or normalized name before login.
        public static SectorSceneryProfile Generate(uint sectorId, string sectorName)
        {
            string name=(sectorName??string.Empty).Trim().ToLowerInvariant();
            uint seed=2166136261;
            string key=sectorId==0 ? name : "sector:"+sectorId.ToString(CultureInfo.InvariantCulture);
            foreach(char letter in key)seed=unchecked((seed^letter)*16777619);
            var random=new SeedRandom(seed);
            bool blackHole=random.Unit()<.22f;
            int count=MinimumBodies+random.Index(MaximumBodies-MinimumBodies+1);
            float yaw=random.Range(-6,6);
            Quaternion rotation=Quaternion.Euler(0,yaw,0);
            // Five worlds fill the forward panorama; the remaining worlds reward looking around.
            int[] composition={0,1,2,3,4};
            for(int index=composition.Length-1;index>0;index--)
            {
                int swap=random.Index(index+1);int held=composition[index];
                composition[index]=composition[swap];composition[swap]=held;
            }
            float[] longitudes={-30,-15,0,15,30,-85,88,153,-151};
            float[] latitudes={9,-12,15,-10,10,5,-6,11,-9};
            var bodies=new Body[count];
            int forced=NamedSurface(name);
            var surfaces=new List<int>();
            // Reserve recognizable Earth geography for Earth sectors rather than repeat it everywhere.
            for(int surface=forced==0?0:1;surface<SurfaceMaps.Length;surface++)surfaces.Add(surface);
            for(int index=0;index<count;index++)
            {
                int surface=index==0&&forced>=0?forced:surfaces[random.Index(surfaces.Count)];
                surfaces.Remove(surface);
                bool gas=surface>=6;
                bool clouds=surface==0 || surface==3 && random.Unit()<.7f;
                bool rings=gas ? random.Unit()<.7f : random.Unit()<.18f;
                float radius=random.Range(index==0?2050:1000,index==0?3000:2050);
                int placement=index<5?composition[index]:index;
                float longitude=(longitudes[placement]+random.Range(-3,3))*Mathf.Deg2Rad;
                float latitude=(latitudes[placement]+random.Range(-3,3))*Mathf.Deg2Rad;
                float distance=random.Range(21500,27500);
                Vector3 position=rotation*new Vector3(Mathf.Sin(longitude)*Mathf.Cos(latitude),
                    Mathf.Sin(latitude),Mathf.Cos(longitude)*Mathf.Cos(latitude))*distance;
                Color tint=Color.Lerp(Color.white,Color.HSVToRGB(random.Unit(),random.Range(.24f,.58f),1),gas? .5f:.4f);
                Color atmosphere=Color.HSVToRGB(gas?random.Range(.48f,.68f):random.Range(.45f,.64f),.72f,1);
                int terrain=random.Index(6);
                float blend=gas||index==0&&forced>=0?0:random.Range(.35f,.8f);
                int moons=random.Unit()<.25f?0:1+random.Index(2);
                bodies[index]=new Body(surface,terrain,position,new Vector3(random.Range(-18,18),random.Range(0,360),random.Range(-32,32)),
                    tint,atmosphere,radius,random.Range(1.4f,4.8f)*(random.Unit()<.18f?-1:1),random.Range(0,360),blend,gas,clouds,rings,moons);
            }
            Color skyTint=Color.Lerp(Color.white,Color.HSVToRGB(random.Unit(),.4f,1),.3f);
            return new SectorSceneryProfile(seed,bodies,blackHole,random.Range(0,6.2831853f),skyTint,
                rotation*new Vector3(random.Range(-2200,2200),-6500,23500),rotation);
        }

        /// Returns:
        ///     int: A matching real-world map for named Solar System sectors, or -1 for fictional sectors.
        private static int NamedSurface(string name)
        {
            string[] names={"earth","mercury","mars","venus","makemake","eris","jupiter","saturn","uranus","neptune"};
            for(int index=0;index<names.Length;index++)if(name.Contains(names[index]))return index;
            return -1;
        }

        /// <summary>A local xorshift stream, independent of Unity's global random state and runtime Random versions.</summary>
        private struct SeedRandom
        {
            private uint state;
            /// <summary>Start a non-zero deterministic stream.</summary>
            public SeedRandom(uint seed){state=seed==0?1:seed;}
            /// Returns:
            ///     float: The next deterministic sample in [0, 1).
            public float Unit(){state^=state<<13;state^=state>>17;state^=state<<5;return (state&0x00ffffff)/16777216f;}
            /// Returns:
            ///     float: A deterministic value within the specified interval.
            public float Range(float minimum,float maximum){return minimum+(maximum-minimum)*Unit();}
            /// Returns:
            ///     int: A deterministic index below the exclusive upper bound.
            public int Index(int count){return Math.Min(count-1,(int)(Unit()*count));}
        }
    }
}
