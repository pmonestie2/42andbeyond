using UnityEngine;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Continuous visual heading from native rates; gameplay orientation remains server-owned.</summary>
    public static class HeadingPresentation
    {
        /// Returns:
        ///     Quaternion: The server heading advanced by its native yaw/pitch rates for the visual horizon.
        public static Quaternion Project(float[] orientation,float angularY,float angularZ,float seconds)
        {
            Quaternion source=new Quaternion(orientation[0],orientation[1],orientation[2],orientation[3]).normalized;
            source=source*Quaternion.AngleAxis(angularZ*840f*Mathf.Rad2Deg*seconds,Vector3.forward)*
                Quaternion.AngleAxis(angularY*840f*Mathf.Rad2Deg*seconds,Vector3.up);
            Vector3 forward=source*Vector3.right,up=source*Vector3.forward;
            return Quaternion.LookRotation(new Vector3(forward.y,forward.z,forward.x),new Vector3(up.y,up.z,up.x));
        }

        /// Returns:
        ///     Quaternion: The rendered heading advanced by authoritative native turn rates.
        public static Quaternion Rotate(Quaternion rotation,float angularY,float angularZ,float seconds)
        { return rotation*Quaternion.AngleAxis(angularZ*840f*Mathf.Rad2Deg*seconds,Vector3.up)*
            Quaternion.AngleAxis(angularY*840f*Mathf.Rad2Deg*seconds,Vector3.right); }

        /// Returns:
        ///     Quaternion: A phase-continuous rendered heading, gently corrected toward the latest server projection.
        public static Quaternion Advance(Quaternion rendered,Quaternion target,float angularY,float angularZ,float seconds,bool integrate)
        {
            if(integrate)rendered=Rotate(rendered,angularY,angularZ,seconds);
            return Quaternion.Slerp(rendered,target,1-Mathf.Exp(-14f*seconds));
        }
    }
}
