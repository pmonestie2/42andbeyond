using System;
using FortyTwoAndBeyond.Client;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise per-frame turning against sparse authoritative heading samples.</summary>
    public static class HeadingPresentationChecks
    {
        /// <summary>Verify phase continuity at several frame rates, both directions, and after steering stops.</summary>
        public static void Run()
        {
            CheckArrowBindings();
            foreach(int fps in new[]{30,60,120})foreach(float rate in new[]{-1f/883,1f/883})
            {
                float step=1f/fps;Quaternion rendered=Quaternion.identity;
                float[] identity={0,0,0,1};float previousSample=0;
                Quaternion sample=Quaternion.identity;
                for(int frame=1;frame<=fps*2;frame++)
                {
                    float time=frame*step;
                    if(time-previousSample>=.1f)
                    {previousSample=time;sample=Quaternion.AngleAxis(rate*840*Mathf.Rad2Deg*time,Vector3.forward);}
                    var source=new[]{sample.x,sample.y,sample.z,sample.w};
                    Quaternion target=HeadingPresentation.Project(source,0,rate,time-previousSample);
                    rendered=HeadingPresentation.Advance(rendered,target,0,rate,step,true);
                    Quaternion expected=HeadingPresentation.Project(identity,0,rate,time);
                    if(Quaternion.Angle(rendered,expected)>.12f)
                        throw new InvalidOperationException("Turning phase stuttered at "+fps+" FPS, frame "+frame);
                }
                Quaternion stopped=rendered;
                for(int frame=0;frame<fps;frame++)rendered=HeadingPresentation.Advance(rendered,stopped,0,0,step,false);
                if(Quaternion.Angle(rendered,stopped)>.05f)throw new InvalidOperationException("Stopped steering drifted");
            }
            Debug.Log("PASS: continuous bidirectional native-rate heading at 30/60/120 FPS with 10 Hz samples and stationary stop");
        }
        /// <summary>Check default migration while restoring every saved keyboard preference.</summary>
        private static void CheckArrowBindings()
        {
            const string prefix="42andBeyond.key.";
            var saved=new System.Collections.Generic.Dictionary<ControlAction,int>();
            foreach(var action in KeyBindings.Actions)
                if(PlayerPrefs.HasKey(prefix+action))saved[action]=PlayerPrefs.GetInt(prefix+action);
            try
            {
                foreach(var action in KeyBindings.Actions)PlayerPrefs.DeleteKey(prefix+action);
                var defaults=new KeyBindings();
                if(defaults.Get(ControlAction.TurnLeft)!=KeyCode.LeftArrow||defaults.Get(ControlAction.TurnRight)!=KeyCode.RightArrow)
                    throw new InvalidOperationException("Default turning keys are not arrows");
                if (defaults.Get(ControlAction.NearestHostile) != KeyCode.E || defaults.Get(ControlAction.NextHostile) != KeyCode.D)
                    throw new InvalidOperationException("Enemy targeting defaults must be E nearest and D next");
                if (defaults.Get(ControlAction.Follow) != KeyCode.Space || defaults.Get(ControlAction.Forward) != KeyCode.W ||
                    defaults.Get(ControlAction.Reverse) != KeyCode.S || defaults.Get(ControlAction.Warp) != KeyCode.Q ||
                    defaults.Get(ControlAction.Brake) == KeyCode.Space)
                    throw new InvalidOperationException("Space must follow, W/S thrust, and Q warp");
                PlayerPrefs.SetInt(prefix+ControlAction.Follow,(int)KeyCode.R);
                PlayerPrefs.SetInt(prefix+ControlAction.Brake,(int)KeyCode.Space);
                PlayerPrefs.SetInt(prefix+ControlAction.NextHostile,(int)KeyCode.T);
                PlayerPrefs.SetInt(prefix+ControlAction.TurnLeft,(int)KeyCode.A);
                PlayerPrefs.SetInt(prefix+ControlAction.TurnRight,(int)KeyCode.D);
                var migrated=new KeyBindings();
                if(migrated.Get(ControlAction.TurnLeft)!=KeyCode.LeftArrow||migrated.Get(ControlAction.TurnRight)!=KeyCode.RightArrow)
                    throw new InvalidOperationException("Legacy steering bindings did not migrate");
                if (migrated.Get(ControlAction.NextHostile) != KeyCode.D || migrated.Get(ControlAction.NearestHostile) != KeyCode.E)
                    throw new InvalidOperationException("Legacy hostile selection did not migrate without a steering conflict");
                if (migrated.Get(ControlAction.Follow) != KeyCode.Space || migrated.Get(ControlAction.Brake) != KeyCode.X ||
                    migrated.Get(ControlAction.Forward) != KeyCode.W || migrated.Get(ControlAction.Warp) != KeyCode.Q)
                    throw new InvalidOperationException("Legacy follow/brake keys did not migrate without displacing W/Q");
                PlayerPrefs.SetInt(prefix+ControlAction.TurnLeft,(int)KeyCode.J);
                if(new KeyBindings().Get(ControlAction.TurnLeft)!=KeyCode.J)
                    throw new InvalidOperationException("Custom steering key was overwritten");
            }
            finally
            {
                foreach(var action in KeyBindings.Actions)PlayerPrefs.DeleteKey(prefix+action);
                foreach(var pair in saved)PlayerPrefs.SetInt(prefix+pair.Key,pair.Value);
                PlayerPrefs.Save();
            }
        }

    }
}
