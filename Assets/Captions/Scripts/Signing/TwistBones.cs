using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Forearm roll for rigs whose forearm skin hangs on twist bones (MetaHumans: lowerarm_twist_01/02 carry it, the
    /// lowerarm bone none). Unity's humanoid rolls the forearm bone at the elbow and the hand at the wrist; the twist
    /// bones, children of the forearm, never turn, so the wrist skin wrings like a sweet wrapper. After each pose:
    /// the forearm's roll moves to the hand (the elbow end of the forearm doesn't turn), and each twist bone takes the
    /// hand's roll in proportion to how far down the forearm it sits. Rest rotations are the prefab's T-pose, saved
    /// by MetaHumanSetup. Also turns the finger knuckle helpers (see <see cref="half"/>).
    /// </summary>
    public sealed class TwistBones : MonoBehaviour
    {
        [System.Serializable]
        public struct Arm
        {
            public Transform lower, hand;
            public Quaternion lowerRest, handRest;
            public Transform[] twist;
            public Quaternion[] twistRest;
            public float[] share; // 0 at the elbow, 1 at the wrist
        }

        public Arm[] arms;

        // Knuckle helpers (MetaHuman *_half_* bones at each finger joint): Unreal turns each by half its joint's bend
        // so the knuckle keeps its volume; left alone they turn fully with the joint and the knuckle pinches thin.
        public Transform[] half;
        public Quaternion[] halfRest, jointRest;

        // Palm cupping: the ring and little fingers' metacarpals (MetaHuman *_metacarpal_* bones, which Unity's humanoid
        // doesn't have) bend with their finger, so the palm hollows as the hand closes (C, O, claw handshapes) instead
        // of staying a flat board; the finger is turned back by as much, so its own bend doesn't change.
        public Transform[] cup;
        public Quaternion[] cupRest, cupFingerRest;
        public float[] cupShare;
        const float CupMaxDegrees = 18f;

        /// <summary>Captures the current pose as rest for the ring and little metacarpals (editor setup).</summary>
        public void CaptureCup(Transform handL, Transform handR)
        {
            var c = new System.Collections.Generic.List<Transform>();
            var share = new System.Collections.Generic.List<float>();
            foreach (var hand in new[] { handL, handR })
                foreach (Transform t in hand)
                {
                    var f = t.name.StartsWith("pinky_metacarpal") ? 0.35f : t.name.StartsWith("ring_metacarpal") ? 0.18f : 0f;
                    if (f <= 0f || t.childCount == 0 || !t.name.Contains("metacarpal_") || t.name.Contains("slide")) continue;
                    Transform finger = null;
                    foreach (Transform k in t) if (!k.name.Contains("slide") && !k.name.Contains("half")) { finger = k; break; }
                    if (finger == null) continue;
                    c.Add(t);
                    share.Add(f);
                }
            cup = c.ToArray();
            cupShare = share.ToArray();
            cupRest = c.ConvertAll(t => t.localRotation).ToArray();
            cupFingerRest = c.ConvertAll(t => Finger(t).localRotation).ToArray();
        }

        static Transform Finger(Transform metacarpal)
        {
            foreach (Transform k in metacarpal) if (!k.name.Contains("slide") && !k.name.Contains("half")) return k;
            return null;
        }

        /// <summary>Captures the current pose as rest for each side's lowerarm_twist_* bones (editor setup).</summary>
        public void Capture(Transform lowerL, Transform handL, Transform lowerR, Transform handR)
        {
            arms = new[] { Make(lowerL, handL), Make(lowerR, handR) };
            var h = new System.Collections.Generic.List<Transform>();
            foreach (var hand in new[] { handL, handR })
                foreach (var t in hand.GetComponentsInChildren<Transform>(true))
                    if (t.name.Contains("_half_") && t.parent != null && t.localPosition.sqrMagnitude < 1e-8f) h.Add(t);
            half = h.ToArray();
            halfRest = h.ConvertAll(t => t.localRotation).ToArray();
            jointRest = h.ConvertAll(t => t.parent.localRotation).ToArray();
        }

        static Arm Make(Transform lower, Transform hand)
        {
            var twist = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in lower)
                if (c.name.Contains("twist")) twist.Add(c);
            var a = new Arm
            {
                lower = lower, hand = hand, lowerRest = lower.localRotation, handRest = hand.localRotation,
                twist = twist.ToArray(), twistRest = new Quaternion[twist.Count], share = new float[twist.Count],
            };
            var len = hand.localPosition.magnitude;
            for (var i = 0; i < twist.Count; i++)
            {
                a.twistRest[i] = twist[i].localRotation;
                a.share[i] = Mathf.Clamp01(Vector3.Dot(twist[i].localPosition, hand.localPosition) / (len * len));
            }
            return a;
        }

        /// <summary>Angle (degrees) of q's twist about axis (both in the same frame), q = swing * twist.</summary>
        static float Twist(Quaternion q, Vector3 axis)
        {
            var p = Vector3.Dot(new Vector3(q.x, q.y, q.z), axis);
            var angle = 2f * Mathf.Atan2(p, q.w) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(0f, angle);
        }

        public void Apply()
        {
            if (cup != null)
                for (var i = 0; i < cup.Length; i++)
                {
                    var m = cup[i];
                    var finger = m != null ? Finger(m) : null;
                    if (finger == null) continue;
                    // The finger's bend from rest, in the metacarpal's frame; the metacarpal takes a share of it.
                    var bend = finger.localRotation * Quaternion.Inverse(cupFingerRest[i]);
                    bend.ToAngleAxis(out var angle, out var axis);
                    if (angle > 180f) angle -= 360f;
                    var share = Quaternion.AngleAxis(Mathf.Clamp(angle * cupShare[i], -CupMaxDegrees, CupMaxDegrees), axis);
                    m.localRotation = cupRest[i] * share;
                    finger.localRotation = Quaternion.Inverse(share) * finger.localRotation;
                }
            if (half != null)
                for (var i = 0; i < half.Length; i++)
                {
                    if (half[i] == null) continue;
                    var bend = Quaternion.Inverse(jointRest[i]) * half[i].parent.localRotation; // in the joint's frame
                    half[i].localRotation = Quaternion.Slerp(Quaternion.Inverse(bend), Quaternion.identity, 0.5f) * halfRest[i];
                }
            if (arms == null) return;
            foreach (var a in arms)
            {
                if (a.lower == null || a.hand == null) continue;
                var axis = a.hand.localPosition.normalized; // the forearm's line, in the forearm's frame

                // Forearm: keep the bend, drop the roll (in its rest frame), and keep the hand where it was.
                var handWorld = a.hand.rotation;
                var delta = Quaternion.Inverse(a.lowerRest) * a.lower.localRotation;
                var restAxis = axis; // the hand's offset is fixed in the forearm's frame, so the axis is the same at rest
                var lowerRoll = Twist(delta, restAxis);
                a.lower.localRotation = a.lowerRest * (delta * Quaternion.AngleAxis(-lowerRoll, restAxis));
                a.hand.rotation = handWorld;

                // Hand's roll about the forearm's line, relative to rest, in the forearm's frame.
                var handRoll = Twist(a.hand.localRotation * Quaternion.Inverse(a.handRest), axis);
                for (var i = 0; i < a.twist.Length; i++)
                    if (a.twist[i] != null)
                        a.twist[i].localRotation = Quaternion.AngleAxis(handRoll * a.share[i], axis) * a.twistRest[i];
            }
        }
    }
}
