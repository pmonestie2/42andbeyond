using System;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    public sealed partial class SpaceEnvironmentBackground
    {
        private sealed class PlanetSlot
        {
            public Transform Root, Surface, Clouds, Atmosphere, Ring;
            public Material SurfaceMaterial, CloudMaterial, AtmosphereMaterial, RingMaterial;
            public readonly Transform[] Moons=new Transform[2];
            public readonly Transform[] MoonSurfaces=new Transform[2];
            public readonly Material[] MoonMaterials=new Material[2];
            public SectorSceneryProfile.Body Spec;
        }
        private Transform showcasePlanets, debrisField, sectorScenery;
        private readonly PlanetSlot[] planetSlots=new PlanetSlot[SectorSceneryProfile.MaximumBodies];
        private readonly Mesh[] irregularMoons=new Mesh[2];
        private SectorSceneryProfile sectorProfile;
        public string SectorFingerprint => sectorProfile?.Fingerprint ?? "showcase";
        public int SectorBodyCount => sectorProfile?.Bodies.Count ?? 3;

        /// <summary>Apply a stable sector recipe to reusable bodies without touching server-owned scenery.</summary>
        public void ConfigureSector(SectorSceneryProfile profile)
        {
            if(sectorProfile?.Fingerprint==profile.Fingerprint)return;
            EnsureSectorPool();sectorProfile=profile;elapsed=0;
            showcasePlanets.gameObject.SetActive(false);sectorScenery.gameObject.SetActive(true);
            Sun.localScale=Vector3.one*(.85f+(profile.Seed&255)/255f*.45f);
            hole.gameObject.SetActive(profile.BlackHole);hole.localPosition=profile.HolePosition;
            SetSkyProfile(accretion,profile);
            for(int index=0;index<planetSlots.Length;index++)
            {
                PlanetSlot slot=planetSlots[index];bool visible=index<profile.Bodies.Count;
                slot.Root.gameObject.SetActive(visible);if(!visible)continue;
                SectorSceneryProfile.Body spec=profile.Bodies[index];slot.Spec=spec;
                slot.Root.localPosition=spec.Position;
                slot.Root.name="Sector world "+(index+1)+(spec.Gas?" — gas giant":" — terrestrial");
                slot.Surface.localScale=Vector3.one*spec.Radius;
                slot.SurfaceMaterial.SetTexture("_BaseMap",SurfaceTexture(spec.Surface));
                slot.SurfaceMaterial.SetTexture("_TerrainMap",SurfaceTexture(spec.Terrain));
                slot.SurfaceMaterial.SetFloat("_TerrainBlend",spec.TerrainBlend);
                slot.SurfaceMaterial.SetFloat("_TerrainSeed",profile.Seed%4093+index*37);
                slot.SurfaceMaterial.SetFloat("_BodyFlow",spec.Gas?1.1f:0);
                slot.SurfaceMaterial.SetColor("_Tint",spec.Tint);
                slot.SurfaceMaterial.SetFloat("_Ocean",spec.Surface==0?1:0);
                slot.SurfaceMaterial.SetFloat("_HasNight",spec.Surface==0? .6f:0);
                slot.Clouds.gameObject.SetActive(spec.Clouds);slot.Clouds.localScale=Vector3.one*spec.Radius*1.013f;
                slot.CloudMaterial.SetFloat("_Opacity",spec.Surface==3? .9f:.68f);
                slot.Atmosphere.gameObject.SetActive(spec.Gas||spec.Clouds||spec.Surface>=4);
                slot.Atmosphere.localScale=Vector3.one*spec.Radius*1.04f;
                slot.AtmosphereMaterial.SetColor("_Tint",spec.Atmosphere);
                slot.AtmosphereMaterial.SetFloat("_Strength",spec.Gas? .65f:1.1f);
                slot.Ring.gameObject.SetActive(spec.Rings);slot.Ring.localScale=Vector3.one*(spec.Radius/6f);
                slot.RingMaterial.SetFloat("_PlanetRadius",spec.Radius);
                for(int moon=0;moon<2;moon++)
                {
                    slot.Moons[moon].gameObject.SetActive(moon<spec.Moons);
                    float scale=spec.Radius*(moon==0? .23f:.15f);
                    slot.MoonSurfaces[moon].localScale=Vector3.one*scale;
                    bool irregular=((profile.Seed>>(index+moon))&1)!=0;
                    slot.MoonSurfaces[moon].GetComponent<MeshFilter>().sharedMesh=irregular?irregularMoons[(index+moon)%2]:Load<Mesh>("Generated/PlanetSphere");
                    slot.MoonMaterials[moon].SetTexture("_BaseMap",SurfaceTexture((spec.Terrain+moon)%6));
                }
            }
            int rockIndex=0;
            foreach(Transform rock in debrisField)
            {
                float phase=(rockIndex*137.50776f+(profile.Seed%360))*Mathf.Deg2Rad;
                rock.localPosition=profile.LayoutRotation*new Vector3(Mathf.Cos(phase)*(11500+rockIndex%7*270),
                    -3500+Mathf.Sin(phase*2)*1100,27500+Mathf.Sin(phase)*2800);
                rock.localScale=Vector3.one*(rockIndex<8?100+rockIndex*17:20+rockIndex%13*3);
                if(rockIndex<8)rock.GetComponent<MeshFilter>().sharedMesh=irregularMoons[rockIndex%2];
                rockIndex++;
            }
            TintSun(currentSunColor);UpdateSectorBodies(0);
        }

        /// <summary>Keep the generated sky and the black-hole sky sample in the same sector orientation and palette.</summary>
        public static void SetSkyProfile(Material material,SectorSceneryProfile profile)
        {
            material.SetFloat("_SectorYaw",profile.SkyYaw);material.SetColor("_SectorTint",profile.SkyTint);
        }

        /// Returns:
        ///     Texture2D: One original high-resolution source map used by the recipe.
        private static Texture2D SurfaceTexture(int index)
        {return Load<Texture2D>("Textures/"+SectorSceneryProfile.SurfaceMaps[index]);}

        /// <summary>Allocate a bounded planet/moon pool once and retain shared imported meshes.</summary>
        private void EnsureSectorPool()
        {
            if(sectorScenery!=null)return;
            sectorScenery=Child(transform,"Sector celestial bodies");
            foreach(var model in new[]{(name:"Eros",slot:0),(name:"Itokawa",slot:1)})
            {
                GameObject asset=Load<GameObject>("Models/"+model.name);
                irregularMoons[model.slot]=asset.GetComponentInChildren<MeshFilter>().sharedMesh;
            }
            Mesh sphere=Load<Mesh>("Generated/PlanetSphere");Mesh annulus=Load<Mesh>("Generated/SaturnAnnulus");
            for(int index=0;index<planetSlots.Length;index++)
            {
                string prefix="Sector "+index+" ";var slot=new PlanetSlot();planetSlots[index]=slot;
                slot.Root=Child(sectorScenery,"Pooled celestial body "+index);
                slot.SurfaceMaterial=SlotMaterial(prefix+"surface","EarthSurface");
                slot.Surface=MeshObject(slot.Root,"Surface",sphere,slot.SurfaceMaterial,1,Vector3.zero);
                slot.CloudMaterial=SlotMaterial(prefix+"clouds","EarthClouds");
                slot.Clouds=MeshObject(slot.Root,"Clouds",sphere,slot.CloudMaterial,1,Vector3.zero);
                slot.AtmosphereMaterial=SlotMaterial(prefix+"atmosphere","EarthAtmosphere");
                slot.Atmosphere=MeshObject(slot.Root,"Atmosphere",sphere,slot.AtmosphereMaterial,1,Vector3.zero);
                slot.RingMaterial=SlotMaterial(prefix+"rings","SaturnRings");
                slot.Ring=MeshObject(slot.Root,"Rings",annulus,slot.RingMaterial,1,Vector3.zero);
                for(int moon=0;moon<2;moon++)
                {
                    slot.Moons[moon]=Child(slot.Root,"Moon "+(moon+1));
                    slot.MoonMaterials[moon]=SlotMaterial(prefix+"moon "+moon,"MoonSurface");
                    slot.MoonSurfaces[moon]=MeshObject(slot.Moons[moon],"Surface",sphere,slot.MoonMaterials[moon],1,Vector3.zero);
                }
            }
        }

        /// Returns:
        ///     Material: A per-slot material retained across sector changes and disposed with its owner.
        private Material SlotMaterial(string key,string template)
        {
            if(materials.TryGetValue(key,out Material existing))return existing;
            var material=new Material(Load<Material>("Generated/"+template)){name=key};materials.Add(key,material);
            if(material.HasProperty("_SunPosition"))litMaterials.Add(material);
            if(material.HasProperty("_Ambient"))material.SetFloat("_Ambient",.24f);
            return material;
        }

        /// <summary>Advance only instantiated bodies; recipes and meshes remain unchanged between frames.</summary>
        private void UpdateSectorBodies(float seconds)
        {
            for(int index=0;index<sectorProfile.Bodies.Count;index++)
            {
                PlanetSlot slot=planetSlots[index];SectorSceneryProfile.Body spec=slot.Spec;
                float phase=spec.Phase*Mathf.Deg2Rad;
                slot.Root.localPosition=spec.Position+new Vector3(Mathf.Sin(seconds*.045f+phase),Mathf.Sin(seconds*.06f+phase),0)*150;
                slot.Surface.localRotation=Quaternion.Euler(spec.Tilt)*Quaternion.Euler(0,spec.Phase+seconds*spec.Spin,0);
                slot.Clouds.localRotation=Quaternion.Euler(spec.Tilt)*Quaternion.Euler(0,spec.Phase+seconds*spec.Spin*1.6f,0);
                slot.Ring.localRotation=Quaternion.Euler(spec.Tilt+new Vector3(Mathf.Sin(seconds*.12f+phase)*3,seconds*.6f,0));
                slot.RingMaterial.SetVector("_PlanetCenter",slot.Root.position);
                for(int moon=0;moon<spec.Moons;moon++)
                {
                    float angle=(spec.Phase+moon*155+seconds*(moon==0?3.5f:-2.6f))*Mathf.Deg2Rad;
                    float orbit=Mathf.Min(moon==0?6300:8000,spec.Radius*(moon==0?2.75f:3.5f));
                    slot.Moons[moon].localPosition=new Vector3(Mathf.Cos(angle),Mathf.Sin(angle)*.15f,Mathf.Sin(angle))*orbit;
                    slot.MoonSurfaces[moon].localRotation=Quaternion.Euler(seconds*2,seconds*3,moon*32);
                }
            }
            hole.localRotation=sectorProfile.LayoutRotation*Quaternion.Euler(Mathf.Sin(seconds*.1f)*2,0,Mathf.Sin(seconds*.08f)*2);
        }
    }
}
