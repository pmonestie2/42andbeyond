using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Animate authored station suit parts from local or observed room movement.</summary>
    public sealed class StationSuitMotion : MonoBehaviour
    {
        private Transform leftArm, rightArm, leftLeg, rightLeg;
        private bool walking, engaged, hasPose, contact;
        private Vector3 lastPose;
        private float movingUntil, stride, phase;

        /// <summary>Bind the suit's four articulated pivots after construction.</summary>
        public void Configure(Transform leftArm, Transform rightArm, Transform leftLeg,
            Transform rightLeg, bool contact)
        {
            this.leftArm = leftArm; this.rightArm = rightArm;
            this.leftLeg = leftLeg; this.rightLeg = rightLeg;
            this.contact = contact;
        }

        /// <summary>Use the local pilot's actual walking input for its stride.</summary>
        public void SetWalking(bool value) { walking = value; }

        /// <summary>Use a changed server pose as a short peer walking cue.</summary>
        public void ReportPose(Vector3 position)
        {
            if (hasPose)
            {
                float distance = Vector3.Distance(lastPose, position);
                if (distance > .012f && distance < 1f)
                    movingUntil = Time.unscaledTime + .3f;
            }
            lastPose = position; hasPose = true;
        }

        /// <summary>Mark only the NPC in an active server dialogue as engaged.</summary>
        public void SetEngaged(bool value) { engaged = contact && value; }

        /// <summary>Move only the suit pivots; room pose and position remain authoritative.</summary>
        private void Update()
        {
            if (leftArm == null || rightArm == null || leftLeg == null || rightLeg == null) return;
            float delta = Mathf.Min(Time.unscaledDeltaTime, .1f);
            bool moving = walking || Time.unscaledTime < movingUntil;
            stride = Mathf.MoveTowards(stride, moving ? 1f : 0f, delta * 4f);
            phase += delta * Mathf.Lerp(1.4f, 8f, stride);
            float step = Mathf.Sin(phase) * stride;
            float idle = Mathf.Sin(phase * .6f) * (1f - stride);
            leftLeg.localRotation = Quaternion.Euler(step * 16f, 0, 0);
            rightLeg.localRotation = Quaternion.Euler(-step * 16f, 0, 0);
            leftArm.localRotation = Quaternion.Euler(-step * 11f + idle, 0, 0);
            rightArm.localRotation = Quaternion.Euler(step * 11f - idle -
                (engaged ? 17f : 0f), 0, 0);
        }
    }
}
