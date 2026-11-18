using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Sign to speech: reads the wearer's own signing from Quest hand tracking and says it aloud for the hearing
    /// people around them. Two kinds of input, told apart by how much the hand travels:
    ///   • fingerspelling: a handshape held still for a moment is a letter (A–Z, matched against the avatar's own
    ///     letter shapes, <see cref="SignAvatar.Letter"/>); letters run together into a word
    ///   • signs: a movement that comes to rest is matched against every recorded sign in the
    ///     <see cref="SignDictionary"/> (dynamic time warping over where the hands are and what shape they make)
    /// Both are learned by posing the avatar's hidden copy (<see cref="SignAvatar.Probe"/>) through each letter and
    /// sign and measuring it the same way as the wearer's hands: in the body's frame (shoulder widths from the eyes) and
    /// the hand's own frame (palm lengths from the wrist), so it doesn't depend on anyone's size. The right hand leads;
    /// the left one is mirrored when it's the one spelling.
    /// The sentence builds up in a bubble below the view. Lower your hands and it is spoken; shake your head before
    /// then to cancel it. With nothing signed, a nod says "Yes." and a shake "No."; a shake while signing negates the
    /// sentence. The headset can't see your face, so questions come from the wh-signs (WHAT, WHERE…) alone.
    /// </summary>
    public partial class SignReader : MonoBehaviour
    {
        public CaptionDirector director;
        public SignAvatar signAvatar;
        public Transform head;
        public OVRHand leftHand, rightHand;
        public Material bubbleMaterial;
        public TMP_FontAsset font;

        [Tooltip("Hold a handshape this long (s) to spell its letter.")]
        public float letterHold = 0.22f;
        [Tooltip("Hands lowered this long (s): the sentence is spoken.")]
        public float speakAfter = 1.1f;
        [Tooltip("Largest handshape difference that still counts as a letter (palm lengths, RMS).")]
        public float letterThreshold = 0.26f;
        [Tooltip("Largest movement difference that still counts as a sign (per frame).")]
        public float signThreshold = 0.62f;
        [Tooltip("Only everyday signs (Everyday): with all ~1000, rare signs (CHAMPION, GOOSE…) crowd out the ones meant.")]
        public bool everydayOnly = true;

        // The best sign must beat the next one by this much (cost ratio), or it's not sure enough to say.
        const float Margin = 0.9f;

        /// <summary>The signs sign to speech reads: everyday conversation (the dictionary's other signs only for the avatar).</summary>
        static readonly HashSet<string> Everyday = new()
        {
            "HELLO", "HI", "YES", "NO", "PLEASE", "SORRY", "THANK-YOU", "FINE", "GOOD", "BAD", "OK", "NAME", "DEAF", "HEARING",
            "I", "ME", "MY", "YOU", "YOUR", "WE", "HE", "SHE", "WHAT", "WHERE", "WHO", "HOW", "WHEN", "WHY", "WHICH",
            "WANT", "NEED", "LIKE", "LOVE", "HELP", "EAT", "DRINK", "WATER", "COFFEE", "HUNGRY", "THIRSTY", "FOOD", "HOME",
            "FAMILY", "FRIEND", "MOTHER", "FATHER", "SCHOOL", "WORK", "TODAY", "TOMORROW", "YESTERDAY", "NOW", "LATER",
            "MORNING", "NIGHT", "BATHROOM", "AGAIN", "SLOW", "UNDERSTAND", "KNOW", "SEE", "GO", "COME", "MORE", "FINISH",
            "BYE", "NICE", "MEET", "SIGN", "LANGUAGE", "LEARN", "TEACH", "LIVE", "CALL", "PHONE", "TIME", "HOUSE", "NOT",
            "HAPPY", "SAD", "TIRED", "SICK", "DOCTOR", "WAIT", "STOP", "READY", "SAME", "DIFFERENT", "WELCOME", "TEAM",
            "TOGETHER", "EXCITED", "PROBLEM", "IMPORTANT", "REMEMBER", "FORGET", "HAVE", "GET", "GIVE", "MAKE", "BUY", "CAR",
            "BUS", "WALK", "RESTAURANT", "MILK", "TEA", "MUCH", "FEEL", "HOT", "COLD", "FUN", "PLAY", "TALK", "ASK", "TELL",
            "SAY", "REPEAT", "WRITE", "DAY", "WEEK",
        };
        [Tooltip("The wearer's shoulder width (m): scales their hand positions to the avatar's.")]
        public float shoulderWidth = 0.38f;

        const float Hz = 15f;            // movement frames per second
        const int Window = 60;           // frames kept: 4 s
        const int Joints = 21;           // wrist; thumb CMC MCP IP tip; then index…little MCP PIP DIP tip
        static readonly int[] ShapeJoints = { 2, 3, 4, 6, 7, 8, 10, 11, 12, 14, 15, 16, 18, 19, 20 };
        static readonly int[] Tips = { 4, 8, 12, 16, 20 };
        const int ShapeDims = 45 + 6;    // the joints in the hand's frame, then its direction and palm in the body's
        const int MoveDims = 3 + 3 + 15 + 6;

        /// <summary>Simulator only (SimSigner): read the hands of this first-person avatar instead of hand tracking.</summary>
        internal static Animator SimHands;

        public bool Active => director != null && director.mode == CaptionDirector.CaptionMode.SignToSpeech && director.live;

        struct Body { public Vector3 Eyes, Right, Up, Forward; public float Scale; }

        sealed class Hand
        {
            public readonly Vector3[] P = new Vector3[Joints];
            public bool Valid;
        }

        sealed class SignTemplate
        {
            public string Gloss;
            public float[][] Frames;
        }

        // Learned from the avatar.
        readonly Dictionary<char, float[]> _letters = new();
        readonly List<SignTemplate> _signs = new();
        float[] _restLeft; // the avatar's resting left wrist (for one-handed signs when the wearer's left isn't seen)
        int _learned = -1;  // signs learned so far (-1: not started)
        List<SignDictionary.Sign> _toLearn;

        // The wearer, now and recently.
        readonly Hand _right = new(), _left = new();
        readonly List<float[]> _frames = new();
        readonly List<float> _frameTimes = new();
        float _nextFrame, _speed, _moving, _still;
        Vector3 _lastWrist;
        readonly Queue<(float t, Vector3 p)> _path = new();
        int _consumed;           // frames before this are used up by an earlier sign or letter
        bool _signedThisRest;    // a sign ended at this rest: its final hold isn't also a letter
        char _held; float _heldFor; char _lastLetter; bool _letterArmed = true;
        Vector3 _letterAt;

        // The sentence.
        readonly List<string> _tokens = new();
        readonly StringBuilder _spelling = new();
        float _lastInput, _handsDownFor;
        bool _negated;
        Speech _speech;
        CaptionBubble _bubble;
        string _shown = "";

        // Head gestures.
        float _prevYaw, _prevPitch;
        readonly List<(float t, int dir)> _yawSwings = new(), _pitchSwings = new();
        Transform _space;

        void Start()
        {
            _bubble = CaptionBubble.Create("Signed speech", transform, head, bubbleMaterial, font, font, new Color(0.85f, 0.9f, 1f));
            _bubble.MakePlain();
            _bubble.showIdleOrb = false;
            _bubble.collapseAfterSeconds = 4f;
        }

        void OnDestroy() => _speech?.Dispose();

        void Update()
        {
            var on = Active;
            if (_bubble != null) _bubble.Suppressed = !on;
            if (!on) { Reset(); HideHands(); return; }
            _speech ??= new Speech();
            if (!Learn()) { Say("Learning signs…", false); return; }

            var t = Time.time;
            if (SimHands != null) { ReadAvatar(SimHands, false, _right); ReadAvatar(SimHands, true, _left); }
            else { Read(rightHand, _right); Read(leftHand, _left); }
            var body = Wearer();
            HeadGestures(t);

            // Which hand is signing: the right, unless only the left is up.
            var lead = _right.Valid && Up(_right, body) ? _right : _left.Valid && Up(_left, body) ? _left : null;
            var leftLeads = lead == _left;
            var up = lead != null;
            _handsDownFor = up ? 0f : _handsDownFor + Time.deltaTime;

            if (up)
            {
                var w = lead.P[0];
                var v = Time.deltaTime > 0 ? (w - _lastWrist).magnitude / Time.deltaTime : 0f;
                _lastWrist = w;
                _speed = Mathf.Lerp(_speed, v, 1f - Mathf.Exp(-Time.deltaTime * 12f));
                _path.Enqueue((t, w));
                while (_path.Count > 0 && t - _path.Peek().t > 0.6f) _path.Dequeue();

                if (t >= _nextFrame)
                {
                    _nextFrame = t + 1f / Hz;
                    var f = new float[MoveDims];
                    Move(lead == _left ? _left : _right, lead == _left ? _right : _left, leftLeads, body, f);
                    _frames.Add(f);
                    _frameTimes.Add(t);
                    if (_frames.Count > Window)
                    {
                        _frames.RemoveAt(0);
                        _frameTimes.RemoveAt(0);
                        _consumed = Mathf.Max(0, _consumed - 1);
                    }
                }

                if (_speed > 0.28f) { _moving += Time.deltaTime; _still = 0f; _signedThisRest = false; }
                else if (_speed < 0.12f)
                {
                    _still += Time.deltaTime;
                    // A movement has just come to rest: was it a sign?
                    if (_moving > 0.2f && _still > 0.08f) { TrySign(); _moving = 0f; }
                    Spell(lead, leftLeads, body);
                }
            }
            else
            {
                // Hands down: anything moving just before counts, then the sentence is spoken.
                if (_moving > 0.2f) { TrySign(); _moving = 0f; }
                _held = '\0';
                _frames.Clear();
                _frameTimes.Clear();
                _consumed = 0;
                _path.Clear();
                if (_handsDownFor > 0.6f) EndWord();
                if (_handsDownFor > speakAfter && HasSentence) Speak();
            }
            if (_spelling.Length > 0 && t - _lastInput > 1.4f && PathLength() > 0.08f) EndWord();
            Show(body);
            UpdateHands(true, lead, body);
        }

        void Reset()
        {
            _tokens.Clear();
            _spelling.Clear();
            _frames.Clear();
            _frameTimes.Clear();
            _consumed = 0;
            _negated = false;
            _shown = "";
        }

        bool HasSentence => _tokens.Count > 0 || _spelling.Length > 0;

        // ---------------- learning from the avatar ----------------

        // A few signs a frame, so starting the mode doesn't stall the headset. True once everything is learned.
        bool Learn()
        {
            if (_learned >= 0 && _toLearn != null && _learned >= _toLearn.Count) return true;
            var probe = signAvatar != null ? signAvatar.Probe : null;
            if (probe == null) return false;
            var a = probe.Animator;
            var body = AvatarBody(a);
            var h = new Hand();
            if (_learned < 0)
            {
                // Letters: each fitted handshape on the right hand.
                for (var c = 'A'; c <= 'Z'; c++)
                {
                    if (!Handshapes.Has(c) || c is 'J' or 'Z') continue; // J and Z are drawn in the air, not held
                    probe.Apply(signAvatar.Letter(c));
                    ReadAvatar(a, false, h);
                    var f = new float[ShapeDims];
                    Shape(h, false, AvatarBody(a), f, 0);
                    _letters[c] = f;
                }
                probe.Apply(signAvatar.Rest);
                ReadAvatar(a, true, h);
                _restLeft = Local(h.P[0], AvatarBody(a));
                _toLearn = signAvatar.dictionary != null ? signAvatar.dictionary.signs : new List<SignDictionary.Sign>();
                _learned = 0;
                return false;
            }
            var muscles = new float[SignPose.Muscles];
            var r = new Hand();
            for (var n = 0; n < 8 && _learned < _toLearn.Count; n++, _learned++)
            {
                var s = _toLearn[_learned];
                if (s.Frames < 2 || string.IsNullOrEmpty(s.gloss)) continue;
                if (everydayOnly && !Everyday.Contains(Canon(s.gloss.ToUpperInvariant()))) continue;
                var (from, to) = s.Span(true);
                var frames = new List<float[]>();
                for (var t = from; t <= to + 1e-3f; t += 1f / Hz)
                {
                    System.Array.Copy(signAvatar.Ready, muscles, muscles.Length);
                    s.Sample(t, muscles);
                    probe.Apply(muscles);
                    body = AvatarBody(a);
                    ReadAvatar(a, false, r);
                    ReadAvatar(a, true, h);
                    var f = new float[MoveDims];
                    Move(r, h, false, body, f);
                    frames.Add(f);
                }
                if (frames.Count >= 3) _signs.Add(new SignTemplate { Gloss = Canon(s.gloss.ToUpperInvariant()), Frames = frames.ToArray() });
            }
            if (_learned >= _toLearn.Count)
            {
                probe.Apply(signAvatar.Rest);
                Debug.Log($"[Captions] Sign to speech: learned {_letters.Count} letters and {_signs.Count} signs");
                return true;
            }
            return false;
        }

        static readonly HumanBodyBones[][] AvatarBones =
        {
            new[]
            {
                HumanBodyBones.RightHand, HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
                HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
                HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal,
            },
            new[]
            {
                HumanBodyBones.LeftHand, HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
                HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,
            },
        };

        // The avatar's joints in the same order as the wearer's: humanoid hands have no fingertip bones, so a tip is the
        // distal bone's end (its child) or, without one, the distal bone carried on as far again.
        static void ReadAvatar(Animator a, bool left, Hand into)
        {
            var b = AvatarBones[left ? 1 : 0];
            Transform T(int i) => a.GetBoneTransform(b[i]);
            into.P[0] = T(0).position;
            var k = 1;
            for (var finger = 0; finger < 5; finger++)
            {
                var first = 1 + finger * 3;
                var p0 = T(first);
                var p1 = T(first + 1);
                var p2 = T(first + 2);
                into.P[k++] = p0.position;
                into.P[k++] = p1.position;
                into.P[k++] = p2.position;
                into.P[k++] = p2.childCount > 0 ? p2.GetChild(0).position : p2.position + (p2.position - p1.position) * 0.8f;
            }
            // (thumb: CMC MCP IP tip; fingers: MCP PIP DIP tip — the same joints as OpenXR's)
            into.Valid = true;
        }

        static Body AvatarBody(Animator a)
        {
            var root = a.transform;
            var l = a.GetBoneTransform(HumanBodyBones.LeftEye);
            var r = a.GetBoneTransform(HumanBodyBones.RightEye);
            var eyes = l != null && r != null ? (l.position + r.position) / 2 : a.GetBoneTransform(HumanBodyBones.Head).position + root.up * 0.08f;
            var shoulders = Vector3.Distance(a.GetBoneTransform(HumanBodyBones.LeftUpperArm).position, a.GetBoneTransform(HumanBodyBones.RightUpperArm).position);
            return new Body { Eyes = eyes, Right = root.right, Up = root.up, Forward = root.forward, Scale = Mathf.Max(0.05f, shoulders) };
        }

        // ---------------- the wearer ----------------

        static readonly OVRSkeleton.BoneId[] XrJoints =
        {
            OVRSkeleton.BoneId.XRHand_Wrist,
            OVRSkeleton.BoneId.XRHand_ThumbMetacarpal, OVRSkeleton.BoneId.XRHand_ThumbProximal, OVRSkeleton.BoneId.XRHand_ThumbDistal, OVRSkeleton.BoneId.XRHand_ThumbTip,
            OVRSkeleton.BoneId.XRHand_IndexProximal, OVRSkeleton.BoneId.XRHand_IndexIntermediate, OVRSkeleton.BoneId.XRHand_IndexDistal, OVRSkeleton.BoneId.XRHand_IndexTip,
            OVRSkeleton.BoneId.XRHand_MiddleProximal, OVRSkeleton.BoneId.XRHand_MiddleIntermediate, OVRSkeleton.BoneId.XRHand_MiddleDistal, OVRSkeleton.BoneId.XRHand_MiddleTip,
            OVRSkeleton.BoneId.XRHand_RingProximal, OVRSkeleton.BoneId.XRHand_RingIntermediate, OVRSkeleton.BoneId.XRHand_RingDistal, OVRSkeleton.BoneId.XRHand_RingTip,
            OVRSkeleton.BoneId.XRHand_LittleProximal, OVRSkeleton.BoneId.XRHand_LittleIntermediate, OVRSkeleton.BoneId.XRHand_LittleDistal, OVRSkeleton.BoneId.XRHand_LittleTip,
        };

        // OpenXR hand joints, in tracking space, into the world.
        void Read(OVRHand hand, Hand into)
        {
            into.Valid = false;
            if (hand == null || !hand.IsTracked) return;
            var data = ((OVRSkeleton.IOVRSkeletonDataProvider)hand).GetSkeletonPoseData();
            var pos = data.BoneTranslations;
            if (!data.IsDataValid || pos == null) return;
            if (_space == null)
            {
                var rig = FindFirstObjectByType<OVRCameraRig>();
                _space = rig != null ? rig.trackingSpace : head.parent;
            }
            for (var i = 0; i < Joints; i++)
            {
                var b = (int)XrJoints[i];
                if (b < 0 || b >= pos.Length) return;
                var p = pos[b].FromFlippedZVector3f();
                into.P[i] = _space != null ? _space.TransformPoint(p) : p;
            }
            into.Valid = true;
        }

        Body Wearer()
        {
            var f = head.forward;
            f.y = 0;
            f = f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            return new Body { Eyes = head.position, Forward = f, Up = Vector3.up, Right = Vector3.Cross(Vector3.up, f), Scale = shoulderWidth };
        }

        // In signing space: above the waist and in front of the body.
        static bool Up(Hand h, in Body b)
        {
            var p = Local(h.P[0], b);
            return p[1] > -1.75f && p[2] > 0.1f;
        }

        // A point relative to the eyes, in the body's frame, in shoulder widths.
        static float[] Local(Vector3 p, in Body b)
        {
            var d = (p - b.Eyes) / b.Scale;
            return new[] { Vector3.Dot(d, b.Right), Vector3.Dot(d, b.Up), Vector3.Dot(d, b.Forward) };
        }

        // ---------------- features ----------------

        // The handshape: every joint past the knuckles in the hand's own frame (towards the thumb, along the fingers,
        // out of the palm), in palm lengths, so it reads the same at any size or angle; then which way the fingers point
        // and the palm faces in the body's frame (P and K differ only there). A left hand is mirrored into a right one.
        static void Shape(Hand j, bool left, in Body b, float[] into, int at)
        {
            var w = j.P[0];
            var along = j.P[9] - w;
            var s = Mathf.Max(1e-3f, along.magnitude);
            along /= s;
            var across = Vector3.ProjectOnPlane(j.P[5] - j.P[17], along).normalized;
            var palm = Vector3.Cross(across, along);
            if (left) palm = -palm;
            var k = at;
            foreach (var i in ShapeJoints)
            {
                var d = (j.P[i] - w) / s;
                into[k++] = Vector3.Dot(d, across);
                into[k++] = Vector3.Dot(d, along);
                into[k++] = Vector3.Dot(d, palm);
            }
            var mx = left ? -1f : 1f;
            into[k++] = mx * Vector3.Dot(along, b.Right);
            into[k++] = Vector3.Dot(along, b.Up);
            into[k++] = Vector3.Dot(along, b.Forward);
            into[k++] = mx * Vector3.Dot(palm, b.Right);
            into[k++] = Vector3.Dot(palm, b.Up);
            into[k] = Vector3.Dot(palm, b.Forward);
        }

        readonly float[] _shapeTmp = new float[ShapeDims];

        // One movement frame: where the leading and other wrists are, the leading hand's fingertips and which way it
        // points and faces. (The leading hand is mirrored into a right one when it's the left.)
        void Move(Hand lead, Hand other, bool leftLeads, in Body b, float[] into)
        {
            var mx = leftLeads ? -1f : 1f;
            var p = Local(lead.P[0], b);
            into[0] = mx * p[0]; into[1] = p[1]; into[2] = p[2];
            var q = other.Valid ? Local(other.P[0], b) : _restLeft ?? new[] { -0.5f, -1.6f, 0.3f };
            into[3] = mx * q[0]; into[4] = q[1]; into[5] = q[2];
            Shape(lead, leftLeads, b, _shapeTmp, 0);
            var k = 6;
            foreach (var tip in Tips)
            {
                var o = (System.Array.IndexOf(ShapeJoints, tip)) * 3;
                into[k++] = _shapeTmp[o];
                into[k++] = _shapeTmp[o + 1];
                into[k++] = _shapeTmp[o + 2];
            }
            for (var i = 0; i < 6; i++) into[k++] = _shapeTmp[45 + i];
        }

        // Weighted distance between two movement frames: wrists count most, the handshape less (tracking is noisiest
        // there), the other hand less again (many signs leave it down).
        static float MoveDistance(float[] a, float[] b)
        {
            float sum = 0;
            for (var i = 0; i < 3; i++) { var d = a[i] - b[i]; sum += d * d; }
            for (var i = 3; i < 6; i++) { var d = a[i] - b[i]; sum += 0.4f * d * d; }
            for (var i = 6; i < 21; i++) { var d = a[i] - b[i]; sum += 0.12f * d * d; }
            for (var i = 21; i < MoveDims; i++) { var d = a[i] - b[i]; sum += 0.35f * d * d; }
            return Mathf.Sqrt(sum);
        }

        static float ShapeDistance(float[] a, float[] b)
        {
            float shape = 0, dir = 0;
            for (var i = 0; i < 45; i++) { var d = a[i] - b[i]; shape += d * d; }
            for (var i = 45; i < ShapeDims; i++) { var d = a[i] - b[i]; dir += d * d; }
            return Mathf.Sqrt(shape / 45f) + 0.35f * Mathf.Sqrt(dir / 6f);
        }

        // ---------------- signs ----------------

        // The movement since the last sign against every learned sign: subsequence dynamic time warping (the sign may
        // start anywhere in the window, and must end now), cost per template frame.
        void TrySign()
        {
            var n = _frames.Count - _consumed;
            var best = BestSign(out var bestCost, out var second, out var secondCost);
            if (best == null) return;
            var ok = bestCost < signThreshold && bestCost < secondCost * Margin;
            Debug.Log($"[Captions] sign in: {(ok ? best : "?")} · best {best} {bestCost:F2}, next {second} {secondCost:F2} · {n} frames");
            ShowRead(ok ? best : $"? {best.ToLowerInvariant()}", ok ? ReadOk : Unsure, ok);
            LogAttempt(best, bestCost, second, secondCost, ok);
            if (!ok) return;
            EndWord();
            _tokens.Add(best);
            _lastInput = Time.time;
            _consumed = _frames.Count;
            _signedThisRest = true;
            _held = '\0';
        }

        // Every attempt's movement frames, to see how real signing differs from the avatar's (pull with adb:
        // files/sign_attempts.jsonl in the app's data folder). Kept under 2 MB.
        void LogAttempt(string best, float cost, string second, float cost2, bool ok)
        {
            try
            {
                var path = System.IO.Path.Combine(Application.persistentDataPath, "sign_attempts.jsonl");
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 2_000_000) return;
                var sb = new StringBuilder();
                sb.Append("{\"t\":").Append(Time.time.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"best\":\"").Append(best).Append("\",\"cost\":").Append(cost.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"second\":\"").Append(second).Append("\",\"cost2\":").Append(cost2.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"ok\":").Append(ok ? "true" : "false").Append(",\"frames\":[");
                for (var i = _consumed; i < _frames.Count; i++)
                {
                    if (i > _consumed) sb.Append(',');
                    sb.Append('[');
                    for (var d = 0; d < MoveDims; d++) { if (d > 0) sb.Append(','); sb.Append(_frames[i][d].ToString("F3", System.Globalization.CultureInfo.InvariantCulture)); }
                    sb.Append(']');
                }
                sb.Append("]}\n");
                System.IO.File.AppendAllText(path, sb.ToString());
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] sign log: {e.Message}"); }
        }

        string BestSign(out float bestCost, out string second, out float secondCost)
        {
            string best = null;
            second = null;
            bestCost = secondCost = float.MaxValue;
            if (_frames.Count - _consumed < 3 || _signs.Count == 0) return null;
            foreach (var s in _signs)
            {
                var cost = Dtw(s.Frames, _consumed);
                if (cost < bestCost)
                {
                    if (best != s.Gloss) { second = best; secondCost = bestCost; }
                    best = s.Gloss; bestCost = cost;
                }
                else if (cost < secondCost && s.Gloss != best) { second = s.Gloss; secondCost = cost; }
            }
            return best;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor check of the matching (not of hand tracking): each learned sign played back with noise, at another
        /// speed and after the end of a different sign, should still come out as itself; each letter's nearest other
        /// letter shows how far apart the handshapes are.
        /// </summary>
        public static string SelfTest(SignAvatar avatar, float noise = 0.05f, float speed = 1.25f)
        {
            avatar.PrepareProbe();
            var go = new GameObject("Sign reader test");
            try
            {
                var r = go.AddComponent<SignReader>();
                r.signAvatar = avatar;
                for (var i = 0; i < 1000 && !r.Learn(); i++) { }
                var rng = new System.Random(1);
                float N() => (float)(rng.NextDouble() * 2 - 1) * noise * 1.7f;
                int right = 0, accepted = 0, wrongAccepted = 0;
                var costs = new List<float>();
                var confusions = new List<string>();
                for (var k = 0; k < r._signs.Count; k++)
                {
                    var s = r._signs[k];
                    var before = r._signs[(k + 7) % r._signs.Count];
                    r._frames.Clear();
                    for (var i = Mathf.Max(0, before.Frames.Length - 4); i < before.Frames.Length; i++) r._frames.Add(before.Frames[i]);
                    var count = Mathf.Max(3, Mathf.RoundToInt(s.Frames.Length / speed));
                    for (var i = 0; i < count; i++)
                    {
                        var src = s.Frames[Mathf.Min(s.Frames.Length - 1, Mathf.RoundToInt(i * speed))];
                        var f = new float[MoveDims];
                        for (var d = 0; d < MoveDims; d++) f[d] = src[d] + N();
                        r._frames.Add(f);
                    }
                    r._consumed = 0;
                    var got = r.BestSign(out var c, out _, out var c2);
                    var ok = c < r.signThreshold && c < c2 * Margin;
                    if (got == s.Gloss) right++; else if (confusions.Count < 12) confusions.Add($"{s.Gloss}→{got}");
                    if (ok) { accepted++; if (got != s.Gloss) wrongAccepted++; }
                    costs.Add(c);
                }
                costs.Sort();
                var gaps = new List<string>();
                foreach (var (c, f) in r._letters)
                {
                    float near = float.MaxValue; var who = ' ';
                    foreach (var (c2, f2) in r._letters) if (c2 != c) { var d = ShapeDistance(f, f2); if (d < near) { near = d; who = c2; } }
                    if (near < 0.25f) gaps.Add($"{c}~{who} {near:F2}");
                }
                return $"signs {r._signs.Count}: top-1 {right}/{r._signs.Count}, accepted {accepted} ({wrongAccepted} wrong), " +
                       $"cost median {(costs.Count > 0 ? costs[costs.Count / 2] : 0):F2} p90 {(costs.Count > 0 ? costs[costs.Count * 9 / 10] : 0):F2} · " +
                       $"confused: {string.Join(", ", confusions)} · letters {r._letters.Count}, close pairs: {string.Join(", ", gaps)}";
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
#endif

        // Recorded more than once under different names: one sign, so they don't compete with each other.
        static readonly Dictionary<string, string> Synonyms = new()
        {
            ["TOO"] = "ALSO", ["MAD"] = "ANGRY", ["FINISHED"] = "FINISH", ["OKAY"] = "OK", ["MOM"] = "MOTHER", ["DAD"] = "FATHER",
        };

        static string Canon(string gloss) => Synonyms.TryGetValue(gloss, out var c) ? c : gloss;

        float[] _prev = new float[Window + 1], _cur = new float[Window + 1];
        readonly int[] _startPrev = new int[Window + 1], _startCur = new int[Window + 1];

        float Dtw(float[][] tpl, int from)
        {
            var n = _frames.Count - from;
            var m = tpl.Length;
            // Row by row over the template; columns are the wearer's frames. Free start: row 0 costs only its own match.
            for (var j = 0; j < n; j++)
            {
                var d = MoveDistance(tpl[0], _frames[from + j]);
                _prev[j] = d;
                _startPrev[j] = j;
            }
            for (var i = 1; i < m; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    var d = MoveDistance(tpl[i], _frames[from + j]);
                    float best = _prev[j]; var st = _startPrev[j];                                   // template stretched
                    if (j > 0 && _prev[j - 1] < best) { best = _prev[j - 1]; st = _startPrev[j - 1]; }   // in step
                    if (j > 0 && _cur[j - 1] < best) { best = _cur[j - 1]; st = _startCur[j - 1]; }     // wearer slower
                    _cur[j] = d + best;
                    _startCur[j] = st;
                }
                (_prev, _cur) = (_cur, _prev);
                for (var j = 0; j < n; j++) _startPrev[j] = _startCur[j];
            }
            var used = n - _startPrev[n - 1];
            // A sign done at a wildly different speed from the recording isn't that sign.
            if (used < m * 0.4f || used > m * 2.5f) return float.MaxValue;
            return _prev[n - 1] / m;
        }

        // ---------------- fingerspelling ----------------

        readonly float[] _live = new float[ShapeDims];

        void Spell(Hand lead, bool leftLeads, in Body b)
        {
            if (_signedThisRest || _letters.Count == 0) return;
            // Fingerspelling barely moves the wrist; after a bigger movement this rest is a sign's end, not a letter.
            if (PathLength() > 0.12f) { _held = '\0'; return; }
            // Spelled up by the shoulder, not with the hands resting lower down.
            if (Local(lead.P[0], b)[1] < -1.2f) { _held = '\0'; return; }
            Shape(lead, leftLeads, b, _live, 0);
            char best = '\0', second = '\0';
            float bestD = float.MaxValue, secondD = float.MaxValue;
            foreach (var (c, f) in _letters)
            {
                var d = ShapeDistance(_live, f);
                if (d < bestD) { second = best; secondD = bestD; best = c; bestD = d; }
                else if (d < secondD) { second = c; secondD = d; }
            }
            if (bestD > letterThreshold) { _held = '\0'; _letterArmed = true; return; }
            if (best != _held) { _held = best; _heldFor = 0f; return; }
            _heldFor += Time.deltaTime;
            // The same letter twice (LL, EE) needs the hand to move a little in between, as signers do.
            if (best == _lastLetter && !_letterArmed && (lead.P[0] - _letterAt).magnitude > 0.025f) _letterArmed = true;
            if (best != _lastLetter) _letterArmed = true;
            var hold = best is 'C' or 'O' ? letterHold * 2.2f : letterHold;
            if (_heldFor < hold || !_letterArmed) return;
            _spelling.Append(best);
            _lastLetter = best;
            _letterArmed = false;
            _letterAt = lead.P[0];
            _lastInput = Time.time;
            _consumed = _frames.Count;
            ShowRead($"letter {best}", Holding, false);
            Debug.Log($"[Captions] sign in: letter {best} ({bestD:F2}, next {second} {secondD:F2})");
            UiFeedback.Play(UiSound.Tick, lead.P[0]);
        }

        float PathLength()
        {
            float len = 0;
            var first = true;
            var last = Vector3.zero;
            foreach (var (_, p) in _path)
            {
                if (!first) len += (p - last).magnitude;
                last = p;
                first = false;
            }
            return len;
        }

        void EndWord()
        {
            if (_spelling.Length == 0) return;
            _tokens.Add("#" + _spelling); // # marks a spelled word
            _spelling.Clear();
            _lastLetter = '\0';
        }

        // ---------------- the head ----------------

        // A shake (yaw back and forth) or a nod (pitch), from the headset itself: three quick swings within a second.
        void HeadGestures(float t)
        {
            var e = head.rotation.eulerAngles;
            var dt = Mathf.Max(1e-3f, Time.deltaTime);
            var yawRate = Mathf.DeltaAngle(_prevYaw, e.y) / dt;
            var pitchRate = Mathf.DeltaAngle(_prevPitch, e.x) / dt;
            _prevYaw = e.y;
            _prevPitch = e.x;
            if (Swing(_yawSwings, yawRate, t)) OnShake();
            else if (Swing(_pitchSwings, pitchRate, t)) OnNod();
        }

        static bool Swing(List<(float t, int dir)> swings, float rate, float t)
        {
            swings.RemoveAll(s => t - s.t > 1.1f);
            if (Mathf.Abs(rate) < 70f) return false;
            var dir = rate > 0 ? 1 : -1;
            if (swings.Count > 0 && swings[^1].dir == dir) return false;
            swings.Add((t, dir));
            if (swings.Count < 3) return false;
            swings.Clear();
            return true;
        }

        void OnShake()
        {
            if (_handsDownFor > 0.3f && HasSentence)
            {
                Reset();
                _bubble.Show(Label, "Cancelled", final: true);
                return;
            }
            if (HasSentence) { _negated = true; return; }
            SayNow("No.");
        }

        void OnNod()
        {
            if (!HasSentence && _handsDownFor > 0.3f) SayNow("Yes.");
        }

        // ---------------- English and speech ----------------

        static readonly HashSet<string> WhSigns = new() { "WHAT", "WHERE", "WHO", "WHEN", "WHY", "HOW", "WHICH" };
        static readonly HashSet<string> Subjects = new() { "I", "you", "he", "she", "we", "they", "it" };

        /// <summary>The signs as plain English: ASL order kept, with a few common fixes (ME → I, ME NAME → my name is,
        /// a wh-sign at the end moved to the front).</summary>
        public static string English(List<string> tokens, bool negated, out bool question)
        {
            var words = new List<string>();
            foreach (var t in tokens) words.Add(t);
            question = false;
            if (words.Count > 1 && WhSigns.Contains(words[^1]))
            {
                words.Insert(0, words[^1]);
                words.RemoveAt(words.Count - 1);
            }
            foreach (var w in words) if (WhSigns.Contains(w)) question = true;

            var o = new List<string>();
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var next = i + 1 < words.Count ? words[i + 1] : null;
                if (w.StartsWith("#")) { var s = w.Substring(1).ToLowerInvariant(); o.Add(s.Length > 1 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s.ToUpperInvariant()); continue; }
                switch (w)
                {
                    case "ME" when next == "NAME": o.Add("my"); continue;
                    case "YOU" when next == "NAME": o.Add("your"); continue;
                    case "NAME" when i > 0 && words[i - 1] is "ME" or "MY": o.Add("name is"); continue;
                    case "ME": case "I": o.Add("I"); continue;
                    case "MY": o.Add("my"); continue;
                    case "YOU": o.Add("you"); continue;
                    case "YOUR": o.Add("your"); continue;
                }
                o.Add(w.Replace('-', ' ').ToLowerInvariant());
            }
            // "What your name" → "What is your name"
            if (question && o.Count > 1 && o[1] is "your" or "my" or "the" or "his" or "her" or "their") o.Insert(1, "is");
            if (negated && !tokens.Contains("NOT") && !tokens.Contains("NO") && !tokens.Contains("NEVER"))
            {
                if (o.Count > 1 && Subjects.Contains(o[0])) o.Insert(1, o[0] == "I" ? "don't" : "not");
                else o.Insert(0, "no,");
            }
            if (o.Count == 0) return "";
            var text = string.Join(" ", o);
            return char.ToUpperInvariant(text[0]) + text.Substring(1) + (question ? "?" : ".");
        }

        const string Label = "You · signing";

        void Show(in Body b)
        {
            _bubble.Target = head.position + b.Forward * 0.85f + Vector3.down * 0.3f;
            if (!HasSentence) return;
            var text = English(_tokens, _negated, out _);
            // The letters being spelled, dimmed as the words still changing (the bubble strips markup from text).
            var spelling = _spelling.Length;
            if (spelling > 0)
                text = (text.Length > 0 ? text.TrimEnd('.', '?') + " " : "") + string.Join(" ", _spelling.ToString().ToCharArray()) + "…";
            if (text == _shown) { _bubble.KeepOpen(); return; }
            _shown = text;
            _bubble.Show(Label, text, final: false, unstableWords: spelling);
        }

        void Say(string status, bool final) { if (_shown != status) { _shown = status; _bubble.Show(Label, status, final); } }

        void Speak()
        {
            EndWord();
            var text = English(_tokens, _negated, out _);
            Reset();
            if (text.Length > 0) SayNow(text);
        }

        /// <summary>A sentence the wearer signed (and the headset said aloud), for the saved transcript.</summary>
        public static event System.Action<string> Signed;

        void SayNow(string text)
        {
            Signed?.Invoke(text);
            var spoken = _speech != null && _speech.Say(text);
            _bubble.Show(spoken ? "You said" : _speech != null && _speech.Failed ? "Not spoken · no voice on this headset" : "You said", text, final: true);
            _shown = text;
            director.Note($"Signed: \"{text}\"" + (spoken ? "" : " (not spoken)"));
        }
    }
}
