using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// On a signing avatar whose humanoid muscles sit a little off the avatar the signs were tuned on: offsets added
    /// to every pose <see cref="SignPose"/> applies. MetaHumans: Unity reads the arm and forearm twist zeros rolled
    /// and the arms are proportioned differently, so the same muscles roll the palms and miss the reference's hand
    /// positions. Fitted against the reference avatar by MetaHumanSetup.Calibrate.
    /// </summary>
    public sealed class MuscleTrim : MonoBehaviour
    {
        [System.Serializable]
        public struct Offset
        {
            [Tooltip("Humanoid muscle name, as in HumanTrait.MuscleName")]
            public string muscle;
            public float value;
        }

        public Offset[] offsets = new Offset[0];

        [Tooltip("After each pose, bring a hand that has gone into the torso out in front of it (the signs were cleared " +
                 "for the reference body at import, not this one)")]
        public bool keepHandsOutOfBody = true;

        [Tooltip("After each pose, move hands that have gone into each other apart (fingers, palms and fists touch but " +
                 "never pass through; see HandsApart)")]
        public bool keepHandsApart = true;

        [Tooltip("The model the signs were made on (its Humanoid skeleton). Each pose, this avatar's hands go where that " +
                 "one's are relative to the head, chest and shoulders, palms turned the same way (see HandReach). Empty: " +
                 "muscle angles only")]
        public GameObject reachReference;
    }
}
