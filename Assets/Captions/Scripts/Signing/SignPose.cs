using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Poses a Humanoid character from Unity's muscle values (HumanTrait.MuscleName: 95 numbers, -1…1 each).
    /// Muscle space is the same for every humanoid rig, so handshapes, fingerspelling and recorded signs play on any
    /// character with finger bones — swap the model and nothing else changes.
    /// Also drives the face blend shapes sign languages use for grammar and affect (see <see cref="SignFace"/>).
    /// </summary>
    public sealed class SignPose
    {
        public const int Muscles = 95;
        readonly HumanPoseHandler _handler;
        HumanPose _pose;
        readonly SkinnedMeshRenderer _face;
        readonly Transform _hips;
        readonly int[] _raise, _lower; // blend shape indices; -1 = the model has none
        readonly FacePoses.Driver _joints; // a joint-driven face (MetaHuman); null = blend shapes
        readonly int[] _trimMuscle; // MuscleTrim
        readonly float[] _trimValue;
        readonly TwistBones _twist;
        readonly BodySurface _clearance; // MuscleTrim.keepHandsOutOfBody
        readonly HandsApart _apart; // MuscleTrim.keepHandsApart
        readonly HandReach _reach; // MuscleTrim.reachReference

        public Animator Animator { get; }

        public SignPose(Animator animator)
        {
            Animator = animator;
            _handler = new HumanPoseHandler(animator.avatar, animator.transform);
            _handler.GetHumanPose(ref _pose);
            _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            foreach (var r in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null && (_face == null || r.sharedMesh.blendShapeCount > _face.sharedMesh.blendShapeCount)) _face = r;
            _raise = Shapes("innerBrowRaiser_L", "innerBrowRaiser_R", "outerBrowRaiser_L", "outerBrowRaiser_R");
            _lower = Shapes("browLowerer_L", "browLowerer_R");
            var jointFace = animator.GetComponentInChildren<JointFace>(true);
            if (jointFace != null && jointFace.poses != null)
            {
                var driver = new FacePoses.Driver(jointFace.poses, animator.transform);
                if (driver.Valid) _joints = driver;
            }
            var trim = animator.GetComponent<MuscleTrim>();
            _trimMuscle = new int[0];
            _trimValue = new float[0];
            if (trim != null && trim.offsets != null)
            {
                var ids = new System.Collections.Generic.List<int>();
                var values = new System.Collections.Generic.List<float>();
                foreach (var o in trim.offsets)
                {
                    var id = System.Array.IndexOf(HumanTrait.MuscleName, o.muscle);
                    if (id >= 0 && o.value != 0f) { ids.Add(id); values.Add(o.value); }
                }
                _trimMuscle = ids.ToArray();
                _trimValue = values.ToArray();
            }
            _twist = animator.GetComponent<TwistBones>();
            if (trim != null && trim.keepHandsOutOfBody) _clearance = new BodySurface(animator); // built at the prefab's pose
            if ((trim == null || trim.keepHandsApart) && new HandsApart(animator) is { Valid: true } apart) _apart = apart;
            if (trim != null && trim.reachReference != null)
            {
                var reach = new HandReach(animator, _handler, trim.reachReference);
                if (reach.Valid) _reach = reach;
            }
        }

        int[] Shapes(params string[] suffixes)
        {
            var ids = new int[suffixes.Length];
            for (var i = 0; i < suffixes.Length; i++)
            {
                ids[i] = -1;
                if (_face == null) continue;
                var mesh = _face.sharedMesh;
                for (var k = 0; k < mesh.blendShapeCount; k++)
                {
                    var n = mesh.GetBlendShapeName(k);
                    if (n == suffixes[i] || n.EndsWith("." + suffixes[i])) { ids[i] = k; break; }
                }
            }
            return ids;
        }

        /// <summary>Set every muscle (array of <see cref="Muscles"/>). The body stays where it is.</summary>
        public void Apply(float[] muscles)
        {
            System.Array.Copy(muscles, _pose.muscles, Mathf.Min(muscles.Length, _pose.muscles.Length));
            for (var i = 0; i < _trimMuscle.Length; i++)
                _pose.muscles[_trimMuscle[i]] = Mathf.Clamp(_pose.muscles[_trimMuscle[i]] + _trimValue[i], -1f, 1f);
            // SetHumanPose also places the hips (and its body position doesn't round-trip once the character has been
            // moved): keep them where the character stands.
            var hipsPos = _hips.localPosition;
            var hipsRot = _hips.localRotation;
            _handler.SetHumanPose(ref _pose);
            _hips.localPosition = hipsPos;
            _hips.localRotation = hipsRot;
            _reach?.Apply(muscles);
            _apart?.Apply();
            if (_clearance != null) ClearBody();
            if (_twist != null) _twist.Apply();
        }

        // A hand behind the torso: turn that upper arm about the shoulder so the hand moves forward by the overlap
        // (plus 3 mm), checked up to three times. Joint angles below the shoulder stay as signed.
        /// <summary>The hands' capsule check (null when MuscleTrim.keepHandsApart is off).</summary>
        public HandsApart Apart => _apart;

        void ClearBody()
        {
            for (var tries = 0; tries < 3; tries++)
            {
                var moved = false;
                for (var side = 0; side < 2; side++)
                {
                    var behind = _clearance.Behind(Animator, side == 1);
                    if (behind < 0.003f) continue;
                    var upper = Animator.GetBoneTransform(side == 0 ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                    var hand = Animator.GetBoneTransform(side == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    var reach = hand.position - upper.position;
                    var axis = Vector3.Cross(reach, _clearance.Forward);
                    if (axis.sqrMagnitude < 1e-8f) continue;
                    var angle = (behind + 0.003f) / reach.magnitude * Mathf.Rad2Deg;
                    upper.rotation = Quaternion.AngleAxis(angle, axis.normalized) * upper.rotation;
                    moved = true;
                }
                if (!moved) return;
            }
        }

        /// <summary>The face blend shapes signing drives (Meta's FACS names, as on the avatar models), 0–1 each.</summary>
        public static readonly string[] FaceChannels =
        {
            "browLowerer_L", "browLowerer_R", "innerBrowRaiser_L", "innerBrowRaiser_R", "outerBrowRaiser_L", "outerBrowRaiser_R",
            "upperLidRaiser_L", "upperLidRaiser_R", "lidTightener_L", "lidTightener_R", "eyesClosed_L", "eyesClosed_R",
            "cheekRaiser_L", "cheekRaiser_R", "cheekPuff_L", "cheekPuff_R", "noseWrinkler_L", "noseWrinkler_R",
            "upperLipRaiser_L", "upperLipRaiser_R", "lipCornerPuller_L", "lipCornerPuller_R", "lipCornerDepressor_L",
            "lipCornerDepressor_R", "dimpler_L", "dimpler_R", "lipStretcher_L", "lipStretcher_R", "lipPucker_L", "lipPucker_R",
            "lipPressor_L", "lipPressor_R", "lowerLipDepressor_L", "lowerLipDepressor_R", "jawDrop", "jawThrust",
            "jawSidewaysLeft", "jawSidewaysRight", "mouthLeft", "mouthRight", "nasolabialFurrow_L", "nasolabialFurrow_R",
            "chinRaiser_B", "chinRaiser_T", "lipTightener_L", "lipTightener_R", "lipFunneler_LB", "lipFunneler_LT",
            "lipFunneler_RB", "lipFunneler_RT", "lipSuck_LB", "lipSuck_LT", "lipSuck_RB", "lipSuck_RT", "lipsToward_LB",
            "lipsToward_LT", "lipsToward_RB", "lipsToward_RT",
        };

        int[] _faceShapes;
        (int shape, int[] channels)[] _combinations;

        /// <summary>Sets the face from <see cref="FaceChannels"/> weights (see <see cref="SignFace"/>).</summary>
        public void SetFace(float[] weights)
        {
            if (_joints != null) { _joints.Apply(weights); return; }
            if (_face == null) return;
            _faceShapes ??= Shapes(FaceChannels);
            _combinations ??= Combinations();
            for (var i = 0; i < _faceShapes.Length && i < weights.Length; i++)
                if (_faceShapes[i] >= 0) _face.SetBlendShapeWeight(_faceShapes[i], 100f * weights[i]);
            _parts ??= Parts();
            foreach (var (renderer, shape, channel) in _parts)
                renderer.SetBlendShapeWeight(shape, channel < weights.Length ? 100f * weights[channel] : 0f);
            foreach (var (shape, channels) in _combinations)
            {
                var w = 1f;
                foreach (var c in channels) w *= c < weights.Length ? weights[c] : 0f;
                _face.SetBlendShapeWeight(shape, 100f * w);
            }
        }

        (SkinnedMeshRenderer, int, int)[] _parts;

        // The face's other meshes with shapes of the same names: the teeth and tongue (mouth_ply) carry their own
        // jawDrop, jawThrust, jawSideways… Without them the lips part but the teeth stay shut.
        (SkinnedMeshRenderer, int, int)[] Parts()
        {
            var list = new System.Collections.Generic.List<(SkinnedMeshRenderer, int, int)>();
            foreach (var r in Animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == _face || r.sharedMesh == null) continue;
                var mesh = r.sharedMesh;
                for (var k = 0; k < mesh.blendShapeCount; k++)
                {
                    var name = mesh.GetBlendShapeName(k);
                    var c = Channel(name.Substring(name.LastIndexOf('.') + 1));
                    if (c >= 0) list.Add((r, k, c));
                }
            }
            return list.ToArray();
        }

        static readonly string[] Sides = { "L", "R", "LB", "LT", "RB", "RT", "B", "T" };

        // Meta's FACS face has corrective shapes for expressions that combine (cheekRaiser_lipCornerPuller_L is how the
        // cheek bunches in a real smile): each is driven by the product of its parts, as the rig expects, or the base
        // shapes just add up and a smile with raised cheeks looks flat. In-betweens (jawDrop50…) and combinations with
        // a part signing doesn't drive (eye gaze) are left at 0.
        (int, int[])[] Combinations()
        {
            var list = new System.Collections.Generic.List<(int, int[])>();
            var mesh = _face.sharedMesh;
            for (var k = 0; k < mesh.blendShapeCount; k++)
            {
                var name = mesh.GetBlendShapeName(k);
                name = name.Substring(name.LastIndexOf('.') + 1);
                if (name.IndexOfAny("0123456789".ToCharArray()) >= 0) continue;
                var parts = name.Split('_');
                var side = System.Array.IndexOf(Sides, parts[^1]) >= 0 ? parts[^1] : "";
                var count = side == "" ? parts.Length : parts.Length - 1;
                if (count < 2) continue;
                var channels = new int[count];
                for (var p = 0; p < count && channels != null; p++)
                {
                    channels[p] = Channel(parts[p] + "_" + side);
                    if (channels[p] < 0 && side.Length == 2) channels[p] = Channel(parts[p] + "_" + side[0]);
                    if (channels[p] < 0) channels[p] = Channel(parts[p]);
                    if (channels[p] < 0) channels = null;
                }
                if (channels != null) list.Add((k, channels));
            }
            return list.ToArray();
        }

        static int Channel(string name) => System.Array.IndexOf(FaceChannels, name);

        /// <summary>Yes/no questions raise the eyebrows, wh-questions (who, what, where…) lower them. 0–1 each.</summary>
        public void SetQuestionFace(float raise, float lower)
        {
            if (_joints != null)
            {
                var w = new float[FaceChannels.Length];
                foreach (var c in new[] { "innerBrowRaiser_L", "innerBrowRaiser_R", "outerBrowRaiser_L", "outerBrowRaiser_R" }) w[Channel(c)] = raise;
                foreach (var c in new[] { "browLowerer_L", "browLowerer_R" }) w[Channel(c)] = lower;
                _joints.Apply(w);
                return;
            }
            if (_face == null) return;
            foreach (var i in _raise) if (i >= 0) _face.SetBlendShapeWeight(i, 100f * raise);
            foreach (var i in _lower) if (i >= 0) _face.SetBlendShapeWeight(i, 100f * lower);
        }

        static bool[] _finger;

        /// <summary>
        /// Moves now towards target as a critically damped spring per muscle (vel carries each one's speed between
        /// frames): every move starts and stops smoothly, with no jolt as the next sign begins, and the fingers
        /// settle a little faster than the arm, so the handshape is formed as the hand arrives, as signers do.
        /// speed > 1 plays faster.
        /// </summary>
        public static void Follow(float[] now, float[] target, float[] vel, float dt, float speed = 1f)
        {
            if (_finger == null)
            {
                _finger = new bool[Muscles];
                for (var i = 0; i < Muscles; i++)
                {
                    var n = HumanTrait.MuscleName[i];
                    _finger[i] = n.Contains("Thumb") || n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little");
                }
            }
            for (var i = 0; i < now.Length; i++)
                now[i] = Mathf.SmoothDamp(now[i], target[i], ref vel[i], (_finger[i] ? 0.055f : 0.09f) / Mathf.Sqrt(speed), Mathf.Infinity, dt);
        }

        public static int Index(string muscleName) => System.Array.IndexOf(HumanTrait.MuscleName, muscleName);
    }
}
