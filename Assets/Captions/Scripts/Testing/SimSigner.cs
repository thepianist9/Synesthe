using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Sign to speech in the Meta XR Simulator, whose hands only make a few preset gestures. A copy of the signing
    /// avatar is worn as the viewer's own body (its head hidden, its eyes at the headset's), plays a script of signs and
    /// fingerspelled words, and <see cref="SignReader"/> reads its hands (<see cref="SignReader.SimHands"/>) instead
    /// of hand tracking. The movement is the avatar's own, so this shows the mode working, not how well it reads
    /// a real person's signing. Editor testing and trailer footage only.
    /// </summary>
    public class SimSigner : MonoBehaviour
    {
        public SignAvatar signAvatar;
        public Transform head;
        [Tooltip("Steps separated by '|': a gloss (HELLO), spell:NAME (fingerspelled), or rest:seconds (hands down).")]
        public string script = "rest:1|HELLO|spell:SAM|rest:2";
        public float letterSeconds = 0.55f;
        public float easeSeconds = 0.22f;
        public float signRate = 0.85f;
        [Tooltip("Metres the eyes sit behind the headset, so the face never shows.")]
        public float eyesBehind = 0.03f;

        SignPose _pose;
        Transform _root, _headBone;
        Vector3 _eyesLocal;
        readonly float[] _from = new float[SignPose.Muscles], _now = new float[SignPose.Muscles], _to = new float[SignPose.Muscles];
        readonly Queue<string> _steps = new();
        string _step;
        float _stepT, _stepLen, _ease;
        SignDictionary.Sign _sign;
        float _signFrom;
        string _letters;
        int _letterIdx;

        public bool Done => _step == null && _steps.Count == 0;

        void Start()
        {
            signAvatar.PrepareProbe();
            var go = Instantiate(signAvatar.Model);
            go.name = "Your body (simulated)";
            var anim = go.GetComponent<Animator>();
            anim.enabled = false;
            _root = go.transform;
            _pose = new SignPose(anim);
            _pose.Apply(signAvatar.Rest);
            var l = anim.GetBoneTransform(HumanBodyBones.LeftEye);
            var r = anim.GetBoneTransform(HumanBodyBones.RightEye);
            _headBone = anim.GetBoneTransform(HumanBodyBones.Head);
            var eyes = l != null && r != null ? (l.position + r.position) / 2 : _headBone.position + _root.up * 0.08f;
            _eyesLocal = _root.InverseTransformPoint(eyes);
            var shoulders = Vector3.Distance(anim.GetBoneTransform(HumanBodyBones.LeftUpperArm).position,
                anim.GetBoneTransform(HumanBodyBones.RightUpperArm).position);
            var reader = FindFirstObjectByType<SignReader>();
            if (reader != null) reader.shoulderWidth = shoulders;
            System.Array.Copy(signAvatar.Rest, _now, _now.Length);
            SignReader.SimHands = anim;
            Play(script);
        }

        void OnDestroy()
        {
            if (_pose != null && SignReader.SimHands == _pose.Animator) SignReader.SimHands = null;
            if (_root != null) Destroy(_root.gameObject);
        }

        public void Play(string s)
        {
            _steps.Clear();
            foreach (var p in s.Split('|')) if (p.Trim().Length > 0) _steps.Enqueue(p.Trim());
            _step = null;
        }

        void Next()
        {
            _step = _steps.Count > 0 ? _steps.Dequeue() : null;
            _stepT = 0;
            _sign = null;
            _letters = null;
            _letterIdx = -1;
            System.Array.Copy(_now, _from, _from.Length);
            if (_step == null) { System.Array.Copy(signAvatar.Rest, _to, _to.Length); _stepLen = 1e9f; _ease = 0.4f; return; }
            if (_step.StartsWith("rest:"))
            {
                System.Array.Copy(signAvatar.Rest, _to, _to.Length);
                _stepLen = float.Parse(_step.Substring(5), System.Globalization.CultureInfo.InvariantCulture);
                _ease = 0.4f;
            }
            else if (_step.StartsWith("spell:"))
            {
                _letters = _step.Substring(6).ToUpperInvariant();
                _stepLen = _letters.Length * letterSeconds;
                _ease = easeSeconds;
            }
            else if (signAvatar.dictionary.TryGet(_step, out _sign))
            {
                var (a, b) = _sign.Span(false);
                _signFrom = a;
                _stepLen = (b - a) / signRate;
                _ease = easeSeconds;
            }
            else { Debug.LogWarning($"[SimSigner] no sign {_step}"); Next(); }
        }

        void LateUpdate()
        {
            if (_pose == null) return;
            if (_step == null && _steps.Count > 0) Next();
            _stepT += Time.deltaTime;
            if (_step != null && _stepT >= _stepLen) Next();

            // The target pose for this moment of the step.
            if (_sign != null)
            {
                System.Array.Copy(signAvatar.Ready, _to, _to.Length);
                _sign.Sample(_signFrom + _stepT * signRate, _to);
            }
            else if (_letters != null)
            {
                var i = Mathf.Min(_letters.Length - 1, (int)(_stepT / letterSeconds));
                var into = _stepT - i * letterSeconds;
                if (i != _letterIdx) { _letterIdx = i; System.Array.Copy(_now, _from, _from.Length); }
                System.Array.Copy(signAvatar.Letter(_letters[i]), _to, _to.Length);
                var k = Mathf.SmoothStep(0, 1, Mathf.Clamp01(into / easeSeconds));
                for (var m = 0; m < _now.Length; m++) _now[m] = Mathf.Lerp(_from[m], _to[m], k);
                Apply();
                return;
            }
            var e = Mathf.SmoothStep(0, 1, Mathf.Clamp01(_stepT / Mathf.Max(0.01f, _ease)));
            for (var m = 0; m < _now.Length; m++) _now[m] = Mathf.Lerp(_from[m], _to[m], e);
            Apply();
        }

        void Apply()
        {
            _pose.Apply(_now);
            // Worn as your own body: facing where you face, eyes just behind yours, the head out of sight.
            var f = head.forward;
            f.y = 0;
            f = f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            _root.rotation = Quaternion.LookRotation(f, Vector3.up);
            _root.position += head.position - f * eyesBehind - _root.TransformPoint(_eyesLocal);
            _headBone.localScale = Vector3.one * 0.001f;
        }
    }
}
