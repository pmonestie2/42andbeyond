using System;
using System.IO;
using FortyTwoAndBeyond.Client;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Capture moving sky, solar and accretion shaders with an isolated camera and scene layer.</summary>
    public sealed class DynamicSpacePreview : IDisposable
    {
        private readonly GameObject root, cameraObject;
        private readonly Camera camera;
        private readonly RenderTexture target;
        private readonly Vector3 sun = SpaceEnvironmentBackground.SunPosition, blackHole = SpaceEnvironmentBackground.BlackHolePosition;
        private Color32[][] first;
        private readonly NativeWormholeVisual portal;

        /// <summary>Reuse the tested art factory without changing live flight scenery or its camera.</summary>
        public DynamicSpacePreview(SpaceArt art)
        {
            root = new GameObject("Owned dynamic-space preview");
            Transform scenery = art.Environment(root.transform);
            Transform star = art.Sun(scenery); star.position = sun;
            portal = new NativeWormholeVisual(root.transform);
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
            root.SetActive(false);
            cameraObject = new GameObject("Owned dynamic-space camera", typeof(Camera), typeof(Skybox));
            camera = cameraObject.GetComponent<Camera>(); camera.enabled = false; camera.allowHDR = true;
            cameraObject.AddComponent<SpaceBloom>();
            camera.clearFlags = CameraClearFlags.Skybox; camera.fieldOfView = 45;
            camera.nearClipPlane = .1f; camera.farClipPlane = 100000;
            cameraObject.GetComponent<Skybox>().material = art.Sky;
            target = new RenderTexture(1440, 900, 24, RenderTextureFormat.ARGBHalf); target.Create(); camera.targetTexture = target;
        }

        /// <summary>Save the first or second set of actual GPU-rendered animation frames.</summary>
        public void Capture(bool baseline)
        {
            foreach (string shader in new[] { "SpaceEnvironment/Shaders/StarfieldSkybox", "SpaceEnvironment/Shaders/StellarSurface", "SpaceEnvironment/Shaders/StellarCorona", "SpaceEnvironment/Shaders/BlackHole", "SpaceEnvironment/Shaders/PlanetSurface", "SpaceEnvironment/Shaders/PlanetClouds", "SpaceEnvironment/Shaders/Atmosphere", "SpaceEnvironment/Shaders/SaturnRings", "SpaceBloom", "WormholePlasma" })
                if (Resources.Load<Shader>(shader)?.isSupported != true) throw new InvalidOperationException("Unsupported space shader: " + shader);
            var frames = new Color32[4][];
            camera.cullingMask = 0; camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            frames[0] = Render("stars", baseline);
            camera.cullingMask = 1 << 30; camera.transform.position = sun - Vector3.forward * 6000;
            camera.transform.LookAt(sun); frames[1] = Render("sun", baseline);
            camera.transform.position = blackHole + new Vector3(3000, 1200, -6500);
            camera.transform.LookAt(blackHole); frames[2] = Render("black-hole", baseline);
            portal.SetPose(new Vector3(0, 0, 12), Quaternion.identity, 1);
            camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            frames[3] = Render("wormhole", baseline); portal.SetVisible(false);
            if (baseline) { first = frames; return; }
            camera.transform.position = SpaceEnvironmentBackground.EarthPosition + new Vector3(4000, 1700, -9000);
            camera.transform.LookAt(SpaceEnvironmentBackground.EarthPosition); Render("earth", false);
            camera.transform.position = SpaceEnvironmentBackground.SaturnPosition + new Vector3(-4000, 3000, -10000);
            camera.transform.LookAt(SpaceEnvironmentBackground.SaturnPosition); Render("saturn", false);
            camera.transform.SetPositionAndRotation(new Vector3(0, 1, -12), Quaternion.identity);
            camera.fieldOfView = 60; Render("flight-environment", false); camera.fieldOfView = 45;
            for (int index = 0; index < frames.Length; index++)
            {
                long difference = 0;
                for (int pixel = 0; pixel < frames[index].Length; pixel++)
                {
                    Color32 a = first[index][pixel], b = frames[index][pixel];
                    difference += Math.Abs(a.r-b.r) + Math.Abs(a.g-b.g) + Math.Abs(a.b-b.b);
                }
                double mean = difference / (frames[index].Length * 3.0);
                if (mean < .03) throw new InvalidOperationException("Space animation remained static: layer " + index + ", difference=" + mean);
                Debug.Log("Dynamic space layer " + index + " mean pixel difference=" + mean.ToString("0.000"));
            }
        }

        /// <returns>Color32[]: Pixels of one rendered animation frame, also saved as a standalone PNG.</returns>
        private Color32[] Render(string name, bool baseline)
        {
            root.SetActive(true); camera.Render(); root.SetActive(false);
            RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes("Builds/verification/dynamic-" + name + (baseline ? "-before.png" : "-after.png"), texture.EncodeToPNG());
            Color32[] pixels = texture.GetPixels32(); Object.Destroy(texture); return pixels;
        }

        /// <summary>Check reproducibility, varied recipes and a bounded reusable pool, then capture representative sectors.</summary>
        public void ValidateSectors()
        {
            var fingerprints=new System.Collections.Generic.HashSet<string>();
            for(uint id=1000;id<1100;id++)
            {
                var a=SectorSceneryProfile.Generate(id,"sector "+id);var b=SectorSceneryProfile.Generate(id,"sector "+id);
                if(a.Fingerprint!=b.Fingerprint||a.Bodies.Count<SectorSceneryProfile.MinimumBodies||a.Bodies.Count>SectorSceneryProfile.MaximumBodies)throw new InvalidOperationException("Unstable sector composition");
                var surfaces=new System.Collections.Generic.HashSet<int>();
                foreach(var body in a.Bodies)
                    if(!surfaces.Add(body.Surface))throw new InvalidOperationException("Repeated primary surface within one sector");
                int forward=0;
                foreach(var body in a.Bodies)if(body.Position.z>0 && Mathf.Abs(body.Position.x/body.Position.z)<1)forward++;
                if(forward<5)throw new InvalidOperationException("Forward panorama lost its five visible worlds");
                fingerprints.Add(a.Fingerprint);
            }
            if(fingerprints.Count!=100)throw new InvalidOperationException("Sector recipes do not provide distinct identities");
            var background=root.GetComponentInChildren<SpaceEnvironmentBackground>(true);
            var sky=cameraObject.GetComponent<Skybox>().material;
            int objectCount=0;
            foreach(var sample in new[]{(id:1076u,name:"Asteroid Belt Alpha"),(id:1060u,name:"Earth"),(id:1015u,name:"Saturn"),(id:1135u,name:"New Edinburgh")})
            {
                var profile=SectorSceneryProfile.Generate(sample.id,sample.name);background.ConfigureSector(profile);SpaceEnvironmentBackground.SetSkyProfile(sky,profile);
                int count=background.GetComponentsInChildren<Transform>(true).Length;
                if(objectCount!=0&&count!=objectCount)throw new InvalidOperationException("Sector changes rebuilt the celestial object pool");objectCount=count;
                string original=background.SectorFingerprint;background.ConfigureSector(SectorSceneryProfile.Generate(9999,"elsewhere"));background.ConfigureSector(profile);
                if(background.SectorFingerprint!=original)throw new InvalidOperationException("Returning to a sector rerolled its planets");
                camera.cullingMask=1<<30;camera.fieldOfView=65;camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.Euler(-5,0,0));
                Render("sector-"+sample.id,false);
            }
            camera.fieldOfView=45;
            Debug.Log("PASS: 100 deterministic distinct sector recipes, return identity, bounded celestial pool and representative rendered sectors");
        }

        /// <summary>Verify that a changing sky cannot alter pixels well inside opaque celestial bodies.</summary>
        /// Raises:
        ///     InvalidOperationException: A celestial body reveals the sky through its opaque interior.
        public void ValidateOpacity()
        {
            camera.nearClipPlane=.015f;camera.farClipPlane=35000;camera.cullingMask=1<<30;
            SpaceBloom bloom=cameraObject.GetComponent<SpaceBloom>();bloom.enabled=false;
            Material sky=cameraObject.GetComponent<Skybox>().material;float intensity=sky.GetFloat("_SkyIntensity");
            try
            {
                foreach(var body in new[]{(name:"earth",center:SpaceEnvironmentBackground.EarthPosition),(name:"saturn",center:SpaceEnvironmentBackground.SaturnPosition),(name:"sun",center:sun)})
                {
                    camera.transform.position=Vector3.zero;camera.transform.LookAt(body.center);
                    sky.SetFloat("_SkyIntensity",0);Color32[] dark=Render("opacity-"+body.name+"-dark",false);
                    sky.SetFloat("_SkyIntensity",intensity);Color32[] light=Render("opacity-"+body.name+"-light",false);
                    long difference=0;int samples=0;
                    for(int y=target.height/2-16;y<target.height/2+16;y++)
                        for(int x=target.width/2-16;x<target.width/2+16;x++)
                        {int pixel=y*target.width+x;difference+=Math.Abs(dark[pixel].r-light[pixel].r)+Math.Abs(dark[pixel].g-light[pixel].g)+Math.Abs(dark[pixel].b-light[pixel].b);samples+=3;}
                    double mean=difference/(double)samples;
                    Debug.Log("OPAQUE CELESTIAL "+body.name+" interior sky difference="+mean.ToString("0.000"));
                    if(mean>1)throw new InvalidOperationException(body.name+" shows the sky through its opaque interior: "+mean);
                }
            }
            finally{sky.SetFloat("_SkyIntensity",intensity);bloom.enabled=true;camera.nearClipPlane=.1f;camera.farClipPlane=100000;}
        }

        /// <summary>Release only the isolated camera, render target and preview geometry.</summary>
        public void Dispose()
        { portal.Dispose(); camera.targetTexture = null; target.Release(); Object.Destroy(target); Object.Destroy(cameraObject); Object.Destroy(root); }
    }
}
