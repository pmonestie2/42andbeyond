using System;
using System.Collections.Generic;
using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Render normal flight on a delayed server timeline, independent of packet arrival jitter.</summary>
    public sealed class FlightPoseBuffer
    {
        private readonly List<Sample> samples = new List<Sample>(16);
        private uint previousTick;
        private double clock, previousReceived;
        public float Interval { get; private set; } = .2f;
        public float Delay => Mathf.Clamp(Interval + .04f, .12f, .4f);
        /// Returns:
        ///     float: Seconds since the last accepted movement packet, excluding unrelated snapshots.
        public float Age(double now) => samples.Count == 0 ? 0 : (float)Math.Max(0, now - previousReceived);

        private readonly struct Sample
        {
            public readonly double Time;
            public readonly Vector3 Position, Velocity;
            public readonly Quaternion Rotation;
            public readonly float AngularY, AngularZ;
            /// <summary>Store one immutable visual pose and its authoritative velocity.</summary>
            public Sample(double time, Vector3 position, Quaternion rotation, float speed, float angularY, float angularZ)
            { Time=time; Position=position; Rotation=rotation; Velocity=rotation*Vector3.forward*speed; AngularY=angularY; AngularZ=angularZ; }
        }

        /// <summary>Forget the old sector or travel mode without blending across a teleport.</summary>
        public void Clear()
        { samples.Clear(); previousTick=0; clock=previousReceived=0; Interval=.2f; }

        /// <summary>Append an authoritative pose using server time; reject duplicate or out-of-order ticks.</summary>
        public void Add(uint tick, double received, Vector3 position, Quaternion rotation, float speed, float angularY, float angularZ)
        {
            if(samples.Count==0) clock=received;
            else
            {
                int elapsed=unchecked((int)(tick-previousTick));
                if(elapsed<=0)return;
                if(elapsed>2000 || Vector3.Distance(position,samples[samples.Count-1].Position)>50)
                { Clear(); clock=received; }
                else
                {
                    float seconds=elapsed*.001f;
                    Interval=Mathf.Lerp(Interval,seconds,.1f);
                    clock+=seconds;
                }
            }
            previousTick=tick; previousReceived=received;
            samples.Add(new Sample(clock,position,rotation,speed,angularY,angularZ));
            if(samples.Count>16)samples.RemoveAt(0);
        }

        /// Returns:
        ///     bool: Whether a pose is available, including bounded prediction if its successor is late.
        public bool Evaluate(double now, out Vector3 position, out Quaternion rotation)
        {
            position=default; rotation=Quaternion.identity;
            if(samples.Count==0)return false;
            double time=now-Delay;
            Sample first=samples[0];
            if(time<=first.Time){position=first.Position;rotation=first.Rotation;return true;}
            for(int index=1;index<samples.Count;index++)
            {
                Sample b=samples[index];
                if(time>b.Time)continue;
                Sample a=samples[index-1];
                float duration=(float)(b.Time-a.Time),t=(float)((time-a.Time)/duration);
                float t2=t*t,t3=t2*t;
                // Hermite tangents preserve velocity through packet boundaries and are bounded at abrupt stops.
                float limit=3*Vector3.Distance(a.Position,b.Position)/duration;
                Vector3 av=Vector3.ClampMagnitude(a.Velocity,limit),bv=Vector3.ClampMagnitude(b.Velocity,limit);
                position=(2*t3-3*t2+1)*a.Position+(t3-2*t2+t)*duration*av+
                    (-2*t3+3*t2)*b.Position+(t3-t2)*duration*bv;
                Quaternion from=HeadingPresentation.Rotate(a.Rotation,a.AngularY,a.AngularZ,t*duration);
                Quaternion to=HeadingPresentation.Rotate(b.Rotation,b.AngularY,b.AngularZ,(t-1)*duration);
                rotation=Quaternion.Slerp(from,to,t2*(3-2*t));
                return true;
            }
            Sample last=samples[samples.Count-1];
            float age=Mathf.Clamp((float)(time-last.Time),0,.15f);
            rotation=HeadingPresentation.Rotate(last.Rotation,last.AngularY,last.AngularZ,age);
            position=last.Position+last.Velocity*age;
            return true;
        }
    }
}
