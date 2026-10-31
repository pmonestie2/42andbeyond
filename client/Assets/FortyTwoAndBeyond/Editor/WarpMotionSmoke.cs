using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Measure Unity's own-ship camera travel against a disposable native warp.</summary>
    [InitializeOnLoad]
    public static class WarpMotionSmoke
    {
        private static readonly FieldInfo RenderedPosition = typeof(SpaceClient).GetField(
            "renderedPosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static int stage=-3, sampledFrames, lastFrame;
        private static Quaternion previousHeading;
        private static float minTurn=float.MaxValue,maxTurn;
        private static readonly FieldInfo Ship=typeof(SpaceClient).GetField("ship",BindingFlags.Instance|BindingFlags.NonPublic);
        private static readonly FieldInfo Worker=typeof(SpaceClient).GetField("worker",BindingFlags.Instance|BindingFlags.NonPublic);
        private static double deadline, travelUntil;
        private static double lastAt;
        private static float longestPause, pause, peakSpeed;
        private static UiViewportDriver sampler;
        private static double Now => System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        private static Vector3 lastPosition, startPosition;
        private static bool connected;

        /// <summary>Register only for an explicitly owned local graphical smoke.</summary>
        static WarpMotionSmoke()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--42andbeyond-warp-motion-smoke") < 0)
                return;
            deadline = EditorApplication.timeSinceStartup + 75;
            EditorApplication.update += Tick;
        }

        /// <summary>Enter free-flight warp and sample rendered movement for two server pose intervals.</summary>
        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup > deadline)
            { Fail("Warp motion smoke timed out at stage " + stage); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.OpenScene("Assets/FortyTwoAndBeyond/Space.unity");
                Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameView != null)
                {
                    EditorWindow window = EditorWindow.GetWindow(gameView);
                    window.maximized = true;
                    window.Focus();
                }
                EditorApplication.EnterPlaymode();
                return;
            }
            if (!EditorApplication.isPlaying) return;
            SpaceClient client = UnityEngine.Object.FindFirstObjectByType<SpaceClient>();
            if (client == null) return;
            if(sampler==null){sampler=client.gameObject.AddComponent<UiViewportDriver>();sampler.LateTick=()=>SampleFrame(client);}
            if (!connected)
            { connected = true; client.StartLocalSession("127.0.0.1", "local", "local"); }
            if (client.ChoosingCharacter) client.ChooseCharacter(client.Characters[0].Slot);
            if (client.ConnectionError != null) { Fail(client.ConnectionError); return; }
            Snapshot snapshot = client.CurrentSnapshot;
            if (snapshot?.Ready != true || !snapshot.HasPosition) return;
            if(stage==-3)
            {
                client.Thrust(2);
                ((ClientWorker)Worker.GetValue(client)).Submit(new ClientCommand(CommandKind.Steer,yaw:1));
                travelUntil=EditorApplication.timeSinceStartup+2;stage=-2;return;
            }
            if(stage==-2)
            {
                if(EditorApplication.timeSinceStartup<travelUntil)return;
                if(Mathf.Abs(snapshot.AngularZ)<.0001f){Fail("Owned server did not acknowledge steering");return;}
                lastPosition=ReadRendered(client);previousHeading=((Transform)Ship.GetValue(client)).rotation;
                lastAt=Now;lastFrame=Time.frameCount;
                travelUntil=EditorApplication.timeSinceStartup+3;stage=-1;return;
            }
            if(stage==-1)
            {
                if(EditorApplication.timeSinceStartup<travelUntil)return;
                if(sampledFrames<40||minTurn<20||maxTurn>minTurn*1.3f)
                {Fail($"Normal flight stepped at packet boundaries: {sampledFrames} frames, turn {minTurn:0.0}..{maxTurn:0.0} deg/s");return;}
                client.SendChat("/fps on");
                if(!client.ShowPerformance||!client.PerformanceText.Contains("poses")){Fail("/fps did not enable pose diagnostics");return;}
                client.SendChat("/fps off");
                ((ClientWorker)Worker.GetValue(client)).Submit(new ClientCommand(CommandKind.Steer));client.Thrust(4);
                Debug.Log($"PASS: real-server normal flight, {sampledFrames} frames, turn {minTurn:0.0}..{maxTurn:0.0} deg/s; /fps local diagnostics");
                sampledFrames=0;longestPause=pause=peakSpeed=0;travelUntil=EditorApplication.timeSinceStartup+1;stage=-4;return;
            }
            if(stage==-4)
            {
                if(EditorApplication.timeSinceStartup<travelUntil)return;
                stage=0;
            }
            if (stage == 0)
            {
                client.SelectFreeFlight();
                client.WarpSelected();
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (snapshot.WarpPhase != WarpPhase.Traveling) return;
                if (RenderedPosition == null) { Fail("Own render position field is unavailable."); return; }
                lastPosition = startPosition = ReadRendered(client);
                lastAt = Now;
                lastFrame = Time.frameCount;
                travelUntil = EditorApplication.timeSinceStartup + 1.2;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (snapshot.WarpPhase != WarpPhase.Traveling)
                { Fail("Free-flight warp ended before two pose intervals."); return; }
                if (EditorApplication.timeSinceStartup < travelUntil) return;
                float distance = Vector3.Distance(ReadRendered(client), startPosition);
                if (sampledFrames < 30 || distance < 16f || longestPause > .18f || peakSpeed > 60f)
                { Fail($"Warp render: {sampledFrames} frames, {distance:0.0} world units, " +
                    $"pause {longestPause:0.000}s, peak {peakSpeed:0.0} world units/s."); return; }
                WarpLens lens = UnityEngine.Object.FindFirstObjectByType<WarpLens>();
                if (lens == null || !lens.enabled || lens.Strength < .5f ||
                    UnityEngine.Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None)
                        .Any(line => line.name == "Warp velocity streak"))
                { Fail("Warp failed to activate scene lensing or retained a foreground ribbon."); return; }
                Directory.CreateDirectory("Builds/verification");
                ScreenCapture.CaptureScreenshot("Builds/verification/warp-motion.png");
                Debug.Log($"PASS: own warp render advanced {distance:0.0} world units in " +
                    $"{sampledFrames} frames; longest pause {longestPause:0.000}s; " +
                    $"peak {peakSpeed:0.0} world units/s.");
                travelUntil = EditorApplication.timeSinceStartup + 1;
                stage = 3;
                return;
            }
            if (stage != 3 || EditorApplication.timeSinceStartup < travelUntil) return;
            if (!File.Exists("Builds/verification/warp-motion.png"))
            { Fail("Missing warp-motion.png screenshot."); return; }
            EditorApplication.update -= Tick;
            EditorApplication.Exit(0);
        }

        /// <summary>Measure actual rendered frames after SpaceClient.LateUpdate, using the presentation clock.</summary>
        private static void SampleFrame(SpaceClient client)
        {
            if(stage==-1&&Time.frameCount!=lastFrame)
            {
                double now=Now;float elapsed=(float)(now-lastAt);
                Quaternion rotation=((Transform)Ship.GetValue(client)).rotation;
                if(elapsed>.002f&&elapsed<.08f)
                {
                    float turn=Quaternion.Angle(rotation,previousHeading)/elapsed;
                    minTurn=Mathf.Min(minTurn,turn);maxTurn=Mathf.Max(maxTurn,turn);
                }
                previousHeading=rotation;Sample(client);
            }
            else if(stage==2)Sample(client);
        }

        /// <returns>Vector3: The foreground ship's visual sector position.</returns>
        private static Vector3 ReadRendered(SpaceClient client)
        { return (Vector3)RenderedPosition.GetValue(client); }

        /// <summary>Measure a new rendered frame without treating duplicate editor ticks as pauses.</summary>
        private static void Sample(SpaceClient client)
        {
            if (Time.frameCount == lastFrame) return;
            double now = Now;
            Vector3 position = ReadRendered(client);
            float elapsed = (float)(now - lastAt);
            if (elapsed > .002f && elapsed < .08f)
            {
                float speed = Vector3.Distance(position, lastPosition) / elapsed;
                peakSpeed = Mathf.Max(peakSpeed, speed);
                pause = speed < .8f ? pause + elapsed : 0;
                longestPause = Mathf.Max(longestPause, pause);
                sampledFrames++;
            }
            lastFrame = Time.frameCount;
            lastAt = now;
            lastPosition = position;
        }

        /// <summary>Exit the disposable Editor with a concrete failure.</summary>
        private static void Fail(string reason)
        {
            Debug.LogError(reason);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(1);
        }
    }
}
