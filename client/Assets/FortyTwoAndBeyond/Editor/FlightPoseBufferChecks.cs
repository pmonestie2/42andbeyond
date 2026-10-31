using System;
using FortyTwoAndBeyond.Client;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise actual 200 ms pose cadence with jitter instead of ideal per-frame targets.</summary>
    public static class FlightPoseBufferChecks
    {
        /// <summary>Check curved travel, turning, stop, stale packets, tick wrap and bounded packet loss.</summary>
        public static void Run()
        {
            foreach(int fps in new[]{30,60,120})foreach(float rate in new[]{-.8f,.8f})
            {
                var buffer=new FlightPoseBuffer();int next=0;Vector3 previous=default;
                for(int frame=0;frame<fps*4;frame++)
                {
                    double now=frame/(double)fps;
                    while(next<20&&Arrival(next)<=now)
                    {
                        float time=next*.2f;
                        buffer.Add((uint)(next*200),Arrival(next),Position(time,rate),Rotation(time,rate),3,0,rate/840);
                        next++;
                    }
                    if(!buffer.Evaluate(now,out Vector3 position,out Quaternion rotation))continue;
                    float displayed=Mathf.Max(0,(float)(now-.05-buffer.Delay));
                    if(Vector3.Distance(position,Position(displayed,rate))>.003f||Quaternion.Angle(rotation,Rotation(displayed,rate))>.1f)
                        throw new InvalidOperationException("Jitter changed the rendered server timeline at "+fps+" FPS");
                    if(now>.5&&Mathf.Abs(Vector3.Distance(position,previous)*fps-3)>.025f)
                        throw new InvalidOperationException("Packet boundary caused a velocity step at "+fps+" FPS");
                    previous=position;
                }
            }
            var stopped=new FlightPoseBuffer();
            stopped.Add(uint.MaxValue-99,0,Vector3.zero,Quaternion.identity,0,0,0);
            stopped.Add(100,.2,Vector3.zero,Quaternion.identity,0,0,0);
            stopped.Add(50,.3,Vector3.one*20,Quaternion.identity,0,0,0); // stale, not a new trajectory
            stopped.Evaluate(2,out Vector3 rest,out Quaternion unused);
            if(rest!=Vector3.zero)throw new InvalidOperationException("Stopped pose drifted or accepted a stale tick");
            stopped.Clear();stopped.Add(1,0,Vector3.zero,Quaternion.identity,3,0,0);
            stopped.Evaluate(2,out Vector3 lost,out unused);
            if(lost.magnitude>.451f)throw new InvalidOperationException("Packet loss caused unbounded prediction");
            stopped.Clear();stopped.Add(2,2,Vector3.one*60,Quaternion.identity,0,0,0);
            stopped.Evaluate(2,out Vector3 teleported,out unused);
            if(teleported!=Vector3.one*60)throw new InvalidOperationException("Sector reset interpolated through a teleport");
            Debug.Log("PASS: timestamped flight interpolation at 30/60/120 FPS, 5 Hz poses with 35 ms jitter; continuous speed, stationary stop, tick wrap, stale rejection and bounded loss");
        }

        /// Returns:
        ///     double: A deliberately jittered delivery time for a five-hertz server pose.
        private static double Arrival(int index) => index*.2+.05+(index==0?0:Math.Sin(index*2.1)*.035);
        /// Returns:
        ///     Vector3: Exact constant-speed curved travel for the given turn rate.
        private static Vector3 Position(float time,float rate) => new Vector3(3/rate*(1-Mathf.Cos(rate*time)),0,3/rate*Mathf.Sin(rate*time));
        /// Returns:
        ///     Quaternion: Exact heading on the curved test trajectory.
        private static Quaternion Rotation(float time,float rate) => Quaternion.AngleAxis(rate*time*Mathf.Rad2Deg,Vector3.up);
    }
}
