using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// On a signing avatar whose face moves by joints (MetaHuman): <see cref="SignPose"/> drives the face from these
    /// baked poses instead of blend shapes.
    /// </summary>
    public sealed class JointFace : MonoBehaviour
    {
        public FacePoses poses;
    }
}
