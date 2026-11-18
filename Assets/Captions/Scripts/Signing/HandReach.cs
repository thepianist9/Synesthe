using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Puts a signing avatar's hands where the avatar the signs were made on puts them, relative to its own body, and
    /// turns each palm the same way. Muscle values retarget by joint angle, so on a body with other proportions the
    /// same angles land a hand beside the chin instead of on it and roll the palm a little; for a sign, where the hand
    /// is against the face and chest and which way the palm faces is the meaning.
    /// Each pose: the reference skeleton (a hidden, mesh-less copy shared by every signer) takes the same muscles;
    /// each of its hands is read as an offset from the reference's head, neck, chest, spine and that shoulder (nearest
    /// counts most, scaled by arm length, in the chest's frame); the same offsets from this avatar's own landmarks give
    /// the target; a two-bone solve reaches it keeping the elbow where the muscle pose put it, and the hand takes the
    /// reference palm's rotation. Egocentric retargeting after Molla, Galvan Debarba &amp; Boulic (IEEE TVCG 2018).
    /// </summary>
    public sealed class HandReach
    {
        static readonly HumanBodyBones[] Landmarks = { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine };

        sealed class Rig
        {
            public Transform root, chest;
            public Transform[] landmarks; // Landmarks (null where the rig has none), then the left and right upper arms
            public Transform[] upper = new Transform[2], lower = new Transform[2], hand = new Transform[2];
            public Transform[] index = new Transform[2], middle = new Transform[2], little = new Transform[2]; // proximal finger bones
            public Quaternion[] palm = new Quaternion[2]; // the palm's frame (from the knuckles) in the hand bone's
            public Vector3[] knuckle = new Vector3[2]; // the middle knuckle in the hand bone's frame
            public Quaternion chestToFrame; // chest rotation → a frame that is the root's at the zero pose

            public Rig(Animator a)
            {
                root = a.transform;
                chest = a.GetBoneTransform(HumanBodyBones.UpperChest) ?? a.GetBoneTransform(HumanBodyBones.Chest) ?? a.GetBoneTransform(HumanBodyBones.Spine);
                landmarks = new Transform[Landmarks.Length + 2];
                for (var i = 0; i < Landmarks.Length; i++) landmarks[i] = a.GetBoneTransform(Landmarks[i]);
                for (var s = 0; s < 2; s++)
                {
                    upper[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                    lower[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                    hand[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    index[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
                    middle[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                    little[s] = a.GetBoneTransform(s == 0 ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
                    if (hand[s] == null || index[s] == null || middle[s] == null || little[s] == null) continue;
                    // The knuckles sit still in the hand bone's frame, so the palm's frame is a fixed turn of the
                    // hand bone's: rigs whose hand bones point different ways still match palm to palm.
                    var along = middle[s].position - hand[s].position;
                    var across = index[s].position - little[s].position;
                    palm[s] = Quaternion.Inverse(hand[s].rotation) * Quaternion.LookRotation(along, Vector3.Cross(along, across));
                    knuckle[s] = hand[s].InverseTransformVector(along);
                }
                landmarks[Landmarks.Length] = upper[0];
                landmarks[Landmarks.Length + 1] = upper[1];
            }

            public bool Valid => chest != null && upper[0] && lower[0] && hand[0] && middle[0] && upper[1] && lower[1] && hand[1] && middle[1];
            public Quaternion Frame => chest.rotation * chestToFrame;
            public float Arm(int s) => Vector3.Distance(upper[s].position, lower[s].position) + Vector3.Distance(lower[s].position, hand[s].position);
            public void Capture() => chestToFrame = Quaternion.Inverse(chest.rotation) * root.rotation;
        }

        // One hidden reference skeleton per reference model, shared by every signer (each pose sets it afresh).
        static readonly Dictionary<GameObject, (Animator animator, HumanPoseHandler handler, Rig rig)> Shared = new();

        readonly Rig _me, _ref;
        readonly HumanPoseHandler _refHandler;
        HumanPose _refPose;

        public bool Valid { get; }

        /// <summary>Weight of the reach, 0 = the muscle pose as it is, 1 = hands where the reference's are.</summary>
        public float weight = 1f;

        public HandReach(Animator animator, HumanPoseHandler handler, GameObject reference)
        {
            if (reference == null) return;
            if (!Shared.TryGetValue(reference, out var shared) || shared.animator == null)
            {
                var go = Object.Instantiate(reference);
                go.name = $"{reference.name} (sign reach reference)";
                go.hideFlags = HideFlags.HideAndDontSave;
                if (Application.isPlaying) Object.DontDestroyOnLoad(go);
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                var a = go.GetComponentInChildren<Animator>();
                if (a == null || a.avatar == null || !a.avatar.isHuman) { Object.DestroyImmediate(go); return; }
                a.enabled = false;
                var rig = new Rig(a);
                var h = new HumanPoseHandler(a.avatar, a.transform);
                var zero = new HumanPose();
                h.GetHumanPose(ref zero);
                zero.muscles = new float[HumanTrait.MuscleCount];
                h.SetHumanPose(ref zero);
                rig.Capture();
                shared = (a, h, rig);
                Shared[reference] = shared;
            }
            _ref = shared.rig;
            _refHandler = shared.handler;
            _refHandler.GetHumanPose(ref _refPose);
            _me = new Rig(animator);
            if (!_ref.Valid || !_me.Valid) return;

            // This avatar at the zero pose too (every transform put back afterwards): frames and hand offsets.
            var all = animator.GetComponentsInChildren<Transform>(true);
            var pos = new Vector3[all.Length];
            var rot = new Quaternion[all.Length];
            for (var i = 0; i < all.Length; i++) { pos[i] = all[i].localPosition; rot[i] = all[i].localRotation; }
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            pose.muscles = new float[HumanTrait.MuscleCount];
            handler.SetHumanPose(ref pose);
            _me.Capture();
            for (var i = 0; i < all.Length; i++) { all[i].localPosition = pos[i]; all[i].localRotation = rot[i]; }
            Valid = true;
        }

        /// <summary>After the muscle pose: reach both hands. muscles: the pose as signed (before any trim).</summary>
        public void Apply(float[] muscles)
        {
            if (!Valid || weight <= 0f) return;
            System.Array.Copy(muscles, _refPose.muscles, Mathf.Min(muscles.Length, _refPose.muscles.Length));
            _refHandler.SetHumanPose(ref _refPose);
            var refFrame = _ref.Frame;
            var refInv = Quaternion.Inverse(refFrame);
            var meFrame = _me.Frame;
            // Sideways from the midline scales with the shoulders' width, the rest with arm length: on a narrower
            // body the hands come in as much as the shoulders do, instead of the arms splaying out.
            var width = Vector3.Distance(_me.upper[0].position, _me.upper[1].position) / Mathf.Max(1e-4f, Vector3.Distance(_ref.upper[0].position, _ref.upper[1].position));
            for (var s = 0; s < 2; s++)
            {
                var scale = _me.Arm(s) / Mathf.Max(1e-4f, _ref.Arm(s));
                // The palm's middle knuckle is what touches the chin or the chest: that point is placed.
                var h = _ref.middle[s].position;
                var target = Vector3.zero;
                var total = 0f;
                for (var i = 0; i < _ref.landmarks.Length; i++)
                {
                    // Missing on either rig, or the other side's shoulder: not an anchor for this hand.
                    if (_ref.landmarks[i] == null || _me.landmarks[i] == null || i == Landmarks.Length + 1 - s) continue;
                    var offset = h - _ref.landmarks[i].position;
                    var d2 = offset.sqrMagnitude + 1e-4f;
                    var w = 1f / (d2 * d2); // the nearest landmark counts by far the most
                    var local = refInv * offset;
                    // (head and neck: a head is about the same size on any adult, so those keep arm length throughout)
                    local = i >= 2 && i < Landmarks.Length ? new Vector3(local.x * width, local.y * scale, local.z * scale) : local * scale;
                    target += w * (_me.landmarks[i].position + meFrame * local);
                    total += w;
                }
                target /= total;
                var palm = meFrame * (refInv * (_ref.hand[s].rotation * _ref.palm[s]));
                var goal = palm * Quaternion.Inverse(_me.palm[s]);
                target -= goal * _me.knuckle[s]; // the wrist, from the knuckle
                if (weight < 1f)
                {
                    target = Vector3.Lerp(_me.hand[s].position, target, weight);
                    goal = Quaternion.Slerp(_me.hand[s].rotation, goal, weight);
                }
                Solve(_me.upper[s], _me.lower[s], _me.hand[s], target, goal, meFrame * Vector3.forward);
            }
        }

        // Two-bone reach: bend the elbow to the distance, swing the shoulder onto the target, then turn the arm about
        // the shoulder-target line so the elbow is back on the side the muscle pose put it.
        static void Solve(Transform upper, Transform lower, Transform hand, Vector3 target, Quaternion handRotation, Vector3 forward)
        {
            var s = upper.position;
            var e = lower.position;
            var h = hand.position;
            var hint = e;
            var a = Vector3.Distance(s, e);
            var b = Vector3.Distance(e, h);
            var d = Mathf.Clamp(Vector3.Distance(s, target), Mathf.Abs(a - b) + 1e-4f, (a + b) * 0.9995f);
            var now = Vector3.Angle(s - e, h - e);
            var want = Mathf.Acos(Mathf.Clamp((a * a + b * b - d * d) / (2f * a * b), -1f, 1f)) * Mathf.Rad2Deg;
            var axis = Vector3.Cross(e - s, h - e);
            if (axis.sqrMagnitude < 1e-10f) axis = Vector3.Cross(e - s, forward); // straight arm: bend forward
            if (axis.sqrMagnitude > 1e-12f) lower.rotation = Quaternion.AngleAxis(now - want, axis.normalized) * lower.rotation;

            h = hand.position;
            upper.rotation = Quaternion.FromToRotation(h - s, target - s) * upper.rotation;

            var line = target - s;
            if (line.sqrMagnitude > 1e-8f)
            {
                line.Normalize();
                var from = Vector3.ProjectOnPlane(lower.position - s, line);
                var to = Vector3.ProjectOnPlane(hint - s, line);
                if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                    upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(from, to, line), line) * upper.rotation;
            }
            hand.rotation = handRotation;
        }
    }
}
