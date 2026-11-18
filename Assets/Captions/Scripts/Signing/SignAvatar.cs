using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Signing mode: every person in view gets their own small avatar beside them, which signs each sentence they say
    /// once it is finished — ASL word order and question faces need the whole sentence.
    ///   finished sentence (<see cref="CaptionDirector.FinalCaption"/>) → <see cref="Gloss.FromText"/> (sign order,
    ///   question type) → each sign from the <see cref="SignDictionary"/> if recorded, else fingerspelled
    ///   (<see cref="Handshapes"/>) → muscle poses on the humanoid avatar (<see cref="SignPose"/>), blended so it never snaps.
    /// While they speak, their live English text (<see cref="CaptionDirector.LiveWords"/>) shows under the avatar.
    /// Each avatar stands on its person's caption side, flipped when that would leave the view, scaled with distance
    /// like the bubbles. Signing slowly enough to read takes longer than speech: it speeds up (to 1.3×) when behind,
    /// and drops whole old sentences rather than lag more than <see cref="maxBehindSeconds"/>.
    /// </summary>
    public class SignAvatar : MonoBehaviour
    {
        public CaptionDirector director;
        public PersonTracker tracker;
        public Transform head;
        [Tooltip("A Humanoid model with finger bones (and ideally eyebrow blend shapes). Used when signers is empty.")]
        public GameObject avatarModel;
        [Tooltip("The signers the viewer chooses between in Settings (Humanoid prefabs, e.g. the MetaHumans); one shows at a time.")]
        public GameObject[] signers;
        const string SignerKey = "captions.signer";
        /// <summary>The viewer's signer (Settings), an index into <see cref="signers"/>. Remembered.</summary>
        public static int UserSigner
        {
            get => PlayerPrefs.GetInt(SignerKey, 0);
            set { PlayerPrefs.SetInt(SignerKey, value); PlayerPrefs.Save(); }
        }
        internal GameObject Model
        {
            get
            {
                var chosen = signers is { Length: > 0 } ? signers[Mathf.Clamp(UserSigner, 0, signers.Length - 1)] : null;
                return chosen != null ? chosen : avatarModel;
            }
        }
        /// <summary>The chosen signer's name (Settings).</summary>
        public string SignerName => Model != null ? Model.name : "";
        public SignDictionary dictionary;
        public Material bubbleMaterial;
        public TMP_FontAsset font;

        [Tooltip("Avatar size at 1.2 m from the viewer (1 = life size); it grows with distance like the bubbles.")]
        public float avatarScale = 0.5f;
        [Tooltip("Resting warmth on the face (0 = neutral): a soft smile that reaches the eyes and lifted brows, eased off while the sign's own face frowns, negates or questions.")]
        [Range(0f, 1f)] public float friendliness = 0.8f;
        public float lettersPerSecond = 4.5f;
        [Tooltip("Words with no recorded sign are fingerspelled only if they are names or this short; longer ones are left to the caption (spelling every word fell minutes behind and the avatar did little else).")]
        public int maxSpelledLetters = 5;
        [Tooltip("Recorded signs play at this rate (1 = as recorded): a little slower reads better at arm's length.")]
        public float signingRate = 0.85f;
        [Tooltip("Inside a sentence, a sign's core lasts at most this long (s): dictionary signs are citation forms, slower and with more repeats than signers use in conversation, so longer ones play faster (up to maxSentenceSpeedup).")]
        public float maxSignSeconds = 1.1f;
        public float maxSentenceSpeedup = 1.7f;

        /// <summary>Playback rate for a sign of this core length: the base rate, or faster in a sentence for a long
        /// citation form (see <see cref="maxSignSeconds"/>).</summary>
        public static float RateFor(float coreSeconds, float baseRate, bool inSentence, float maxSeconds = 1.1f, float maxSpeedup = 1.7f) =>
            !inSentence || maxSeconds <= 0f ? baseRate : Mathf.Clamp(coreSeconds / maxSeconds, baseRate, baseRate * maxSpeedup);

        /// <summary>A caption split where a fluent signer's recorded sentence can play whole: each clause (between
        /// commas and sentence ends) that matches one stands alone, and the clauses between them stay together.
        /// "Hi, how are you?" → "Hi," (HELLO) + "how are you?" (recorded). No recorded clause: the text as it is.</summary>
        public static List<(string text, SignDictionary.Sign recorded)> Clauses(SignDictionary dictionary, string text)
        {
            var parts = new List<string>();
            var sb = new System.Text.StringBuilder();
            foreach (var c in text)
            {
                sb.Append(c);
                if (c is ',' or ';' or '.' or '!' or '?') { if (sb.ToString().Trim().Length > 1) parts.Add(sb.ToString().Trim()); sb.Clear(); }
            }
            if (sb.ToString().Trim().Length > 0) parts.Add(sb.ToString().Trim());
            var result = new List<(string, SignDictionary.Sign)>();
            if (dictionary == null || parts.Count < 2) { result.Add((text, null)); return result; }
            var any = false;
            var pending = "";
            foreach (var part in parts)
            {
                if (dictionary.TryMatch(part, out var whole))
                {
                    if (pending.Length > 0) result.Add((pending, null));
                    pending = "";
                    result.Add((part, whole));
                    any = true;
                }
                else pending = pending.Length > 0 ? pending + " " + part : part;
            }
            if (pending.Length > 0) result.Add((pending, null));
            if (!any) { result.Clear(); result.Add((text, null)); }
            return result;
        }
        /// <summary>The viewer's own pace (Settings): deaf signers read at different speeds. Remembered.</summary>
        public static readonly float[] Speeds = { 0.75f, 1f, 1.25f };
        const string SpeedKey = "captions.signSpeed";
        static float _userSpeed = -1f;
        public static float UserSpeed
        {
            get => _userSpeed > 0f ? _userSpeed : _userSpeed = PlayerPrefs.GetFloat(SpeedKey, 1f);
            set { _userSpeed = value; PlayerPrefs.SetFloat(SpeedKey, value); PlayerPrefs.Save(); }
        }

        [Tooltip("Hold at the end of each sign (s), before easing into the next.")]
        public float gapSeconds = 0.08f;
        [Tooltip("Hold at the end of a sentence (s), with a nod.")]
        public float sentenceHoldSeconds = 0.4f;
        [Tooltip("Hold after the sentence's subject (a pronoun first), before its verb (s): signers pause about 0.1 s there.")]
        public float subjectGapSeconds = 0.1f;
        [Tooltip("Hold before a joined clause (AND, BUT, BECAUSE, IF…) (s): about 0.13 s in ASL.")]
        public float clauseGapSeconds = 0.13f;
        [Tooltip("Never fall further behind the speech than this (s): older waiting sentences are dropped.")]
        public float maxBehindSeconds = 8f;
        [Tooltip("Keep avatars within this angle of the view's centre (degrees): one beside a person at the edge stands on their inner side.")]
        public float viewHalfAngle = 32f;
        [Tooltip("Most avatars at once (each is a full character). At the limit, the one showing signs everyone's speech.")]
        public int maxAvatars = 1;

        // Set by the conversation setup (ModeMenu): signing is for one person. Their avatar stands at placedAt (a
        // spatial anchor; null = beside them) and also signs speech nobody could be matched to (their face is often out
        // of view while you watch the avatar). Other speakers are not signed. preview shows it before the conversation.
        [HideInInspector] public TrackedPerson only;
        [HideInInspector] public Transform placedAt;
        [HideInInspector] public bool preview;

        readonly Dictionary<int, Signer> _signers = new();
        Signer _someone; // speech nobody could be matched to
        internal float[] Rest, Ready, Neutral;
        // Shoulder, arm, forearm and hand muscles: how far the hands travel between two poses.
        internal int[] ArmMuscles;
        readonly Dictionary<char, float[]> _letters = new();

        HandFit _fit;
        GameObject _probe;

        /// <summary>Switch to the next signer (Settings): the avatar showing is replaced, and the hand fitting redone for the new body.</summary>
        public void NextSigner()
        {
            if (signers is not { Length: > 1 }) return;
            UserSigner = (UserSigner + 1) % signers.Length;
            foreach (var s in _signers.Values) s.Destroy();
            _signers.Clear();
            _someone?.Destroy();
            _someone = null;
            if (_probe != null) Destroy(_probe);
            Probe = null;
            _letters.Clear();
            PrepareProbe();
        }

        void Start()
        {
            director.LiveWords += OnLiveWords;
            director.FinalCaption += OnFinalCaption;
            PrepareProbe();
        }

        /// <summary>Make the hidden fitting copy and the base poses (Start; the Editor self-test calls it directly).</summary>
        internal void PrepareProbe()
        {
            if (Probe != null) return;
            // An unseen copy of the character to fit the hand poses on (palm towards the viewer, thumb placement).
            var probe = _probe = Instantiate(Model, transform);
            probe.name = "Hand fitting (hidden)";
            probe.GetComponent<Animator>().enabled = false;
            foreach (var r in probe.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            Probe = new SignPose(probe.GetComponent<Animator>());
            _fit = new HandFit(Probe);
            Rest = Handshapes.Rest();
            Neutral = Handshapes.Neutral();
            var arms = new System.Collections.Generic.List<int>();
            for (var i = 0; i < SignPose.Muscles; i++)
            {
                var n = HumanTrait.MuscleName[i];
                if (n.Contains("Shoulder") || n.Contains(" Arm ") || n.Contains("Forearm") || n.Contains("Hand ")) arms.Add(i);
            }
            ArmMuscles = arms.ToArray();
            Ready = _fit.Ready(Handshapes.Ready());
        }

        void OnDestroy()
        {
            if (director == null) return;
            director.LiveWords -= OnLiveWords;
            director.FinalCaption -= OnFinalCaption;
        }

        bool Signing => director.mode == CaptionDirector.CaptionMode.Signing && (director.live || preview);
        bool Listening => director.mode == CaptionDirector.CaptionMode.Signing && director.live;

        void OnLiveWords(TrackedPerson who, int id, string[] words, int settled)
        {
            if (Listening && Route(ref who)) For(who).Live(id, words, settled);
        }

        void OnFinalCaption(TrackedPerson who, int id, string text)
        {
            if (Listening && Route(ref who)) For(who).Final(id, text);
        }

        // One-person signing: their speech and unmatched speech go to their avatar; anyone else's is skipped.
        bool Route(ref TrackedPerson who)
        {
            if (only == null) return true;
            if (who != null && who != only) return false;
            who = only;
            return true;
        }

        /// <summary>The setup's first look at the avatar: it signs "hello".</summary>
        public void Greet()
        {
            if (only != null) For(only).Final(-1, "hello");
        }

        /// <summary>Where the one-person avatar's eyes are now, and its size; false before it has been placed.</summary>
        public bool AvatarEyes(out Vector3 eyes, out float scale)
        {
            eyes = default;
            scale = avatarScale;
            if (only == null || !_signers.TryGetValue(only.Id, out var s) || !s.Placed) return false;
            eyes = s.Eyes;
            scale = s.Scale;
            return true;
        }

        Signer For(TrackedPerson who)
        {
            if (who != null && _signers.TryGetValue(who.Id, out var s)) return s;
            if (who == null && _someone != null) return _someone;
            if (_signers.Count + (_someone != null ? 1 : 0) >= Mathf.Max(1, maxAvatars))
            {
                // At the limit: the avatar already showing signs this speech too.
                foreach (var shown in _signers.Values) return shown;
                return _someone;
            }
            if (who == null) return _someone = new Signer(this, null);
            return _signers[who.Id] = new Signer(this, who);
        }

        /// <summary>The hidden copy of the character: <see cref="SignReader"/> poses it to learn what each sign looks like.</summary>
        internal SignPose Probe { get; private set; }

        internal float[] Letter(char c)
        {
            if (!_letters.TryGetValue(c, out var shape))
            {
                _letters[c] = shape = Handshapes.Spelling(c, Ready);
                _fit.Thumb(shape, Handshapes.SpotOf(c));
            }
            return shape;
        }

        void Update()
        {
            var on = Signing;
            // One avatar per person in view (made the first time they are seen in signing mode).
            // With one person chosen, just theirs, always there (it stands at its anchor, not by their face).
            if (on && only != null) For(only);
            else if (on) foreach (var p in tracker.People) if (p.Visible) For(p);

            List<int> gone = null;
            foreach (var (id, s) in _signers)
            {
                var present = only != null ? s.Who == only : tracker.People.Contains(s.Who) && Time.time - s.Who.LastSeen < 3f;
                if (!present && s.Idle) (gone ??= new()).Add(id);
                else s.Tick(on, present);
            }
            if (gone != null) foreach (var id in gone) { _signers[id].Destroy(); _signers.Remove(id); }
            _someone?.Tick(on, _someone.Shown);
        }

        void LateUpdate()
        {
            foreach (var s in _signers.Values) s.Pose();
            _someone?.Pose();
        }

        /// <summary>One person's avatar: its sign queue, pose, place and caption.</summary>
        class Signer
        {
            class Item
            {
                public string Gloss;
                public SignDictionary.Sign Sign; // null → fingerspelled
                public string Letters;
                public Gloss.Kind Kind;
                public int Sentence;
                public bool Negated; // a frown and a headshake while it plays
                public bool Last; // ends its sentence: held a little longer, with a nod
                public bool Emphatic; // an exclamation: crisper moves, sharper holds, a stronger beat
                public float Lead = 0.22f; // easing in from the previous pose (s), set when it starts
                public float Gap = -1f; // hold after it (s); < 0: the avatar's gapSeconds
                public float From, To; // the part of Sign played (seconds): see SignDictionary.Sign.Span
                public float Rate = -1f; // playback rate; < 0: the avatar's signingRate
                public float At(float t) => Mathf.Min(From + t, To);
            }

            public readonly TrackedPerson Who;
            readonly SignAvatar _m;
            readonly Transform _root;
            readonly SignPose _pose;
            readonly CaptionBubble _caption;
            readonly float _headHeight, _hipsHeight;
            readonly float[] _now = new float[SignPose.Muscles], _target = new float[SignPose.Muscles], _muscleVel = new float[SignPose.Muscles];
            readonly float[] _from = new float[SignPose.Muscles], _first = new float[SignPose.Muscles];
            // Eyes: on the viewer, glancing at the hand for pointing and placing signs (seconds of glance left).
            readonly Transform _headBone, _leftEye, _rightEye, _rightHand, _leftHand;
            readonly Vector3 _faceForward; // in the head bone's space
            Vector3 _gaze, _gazeVel, _saccade;
            readonly Vector3 _faceUp; // in the head bone's space
            Vector2 _headFollow, _headFollowVel; // degrees the head turns (x) and nods (y) towards a far gaze target
            float _eyePitch; // degrees, + up: the lids follow (next frame's face)
            readonly float[] _faceOut = new float[SignPose.FaceChannels.Length];
            static readonly int LidsClosedL = System.Array.IndexOf(SignPose.FaceChannels, "eyesClosed_L"), LidsClosedR = System.Array.IndexOf(SignPose.FaceChannels, "eyesClosed_R"),
                LidsUpL = System.Array.IndexOf(SignPose.FaceChannels, "upperLidRaiser_L"), LidsUpR = System.Array.IndexOf(SignPose.FaceChannels, "upperLidRaiser_R");
            float _glance, _nextSaccade;
            readonly LinkedList<Item> _queue = new();
            Item _current;
            readonly HashSet<int> _somber = new(); // sentences signed without the resting smile
            float _warmth = 1f;
            int _sentences;
            float _t, _idleSince, _lastWord = float.NegativeInfinity;
            float _raise, _lower;
            readonly SignFace _face = new(UnityEngine.Random.Range(0, 1 << 30));
            readonly SignBody _body = new(UnityEngine.Random.Range(0, 1 << 30));
            readonly SignIdle _idle = new(UnityEngine.Random.Range(0, 1 << 30));
            Vector3 _vel, _goal;
            bool _placed;
            float _side, _scale;

            public bool Busy => _current != null || _queue.Count > 0;
            public bool Placed => _placed;
            public Vector3 Eyes => _goal;
            public float Scale => _scale;
            public bool Idle => !Busy && Time.time - _lastWord > 2f;
            // Stays until its hands are back down (Neutral, then Rest after 1.2 s), not gone mid-sign.
            public bool Shown => Busy || Time.time - _idleSince < 2.5f;

            public Signer(SignAvatar m, TrackedPerson who)
            {
                _m = m;
                Who = who;
                System.Array.Copy(m.Rest, _now, _now.Length);
                var go = Object.Instantiate(m.Model, m.transform);
                go.name = who != null ? $"Signing avatar {who.Label}" : "Signing avatar";
                _root = go.transform;
                var animator = go.GetComponent<Animator>();
                animator.enabled = false; // posed directly from muscle values
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _pose = new SignPose(animator);
                _headHeight = animator.GetBoneTransform(HumanBodyBones.Head).position.y - _root.position.y;
                _headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                _leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                _rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
                _rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                _leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                _faceForward = Quaternion.Inverse(_headBone.rotation) * _root.forward;
                _faceUp = Quaternion.Inverse(_headBone.rotation) * _root.up;
                _hipsHeight = animator.GetBoneTransform(HumanBodyBones.Hips).position.y - _root.position.y;
                // A half-body signer, like an interpreter on TV: legs folded away.
                foreach (var leg in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
                    animator.GetBoneTransform(leg).localScale = Vector3.zero;

                _caption = CaptionBubble.Create($"Signing caption {who?.Label}", m.transform, m.head, m.bubbleMaterial, m.font, m.font, Color.white);
                _caption.MakePlain();
                _caption.showIdleOrb = false;
                _caption.collapseAfterSeconds = 2.5f;
                _root.gameObject.SetActive(false);
            }

            public void Destroy()
            {
                Object.Destroy(_root.gameObject);
                Object.Destroy(_caption.gameObject);
            }

            string Label => Who != null ? Who.Label : "Someone";

            // While they speak: their live English text under the avatar (signing waits for the whole sentence).
            public void Live(int id, string[] words, int settled)
            {
                _idle.Listen();
                if (Busy) return; // the sentence being signed stays up until it is done
                _caption.Show(Label, Pretty(string.Join(" ", words)), final: false, unstableWords: words.Length - Mathf.Min(settled, words.Length));
            }

            // The finished sentence, in ASL order with its question face.
            // A sentence a fluent signer recorded plays whole (real ASL grammar); anything else is signed word by word
            // in gloss order, which is only an approximation of ASL: the caption says so.
            public void Final(int id, string text)
            {
                // A sentence about feeling ill, sad or angry: no resting smile through any of it (the next sentence
                // number is taken just below, on every path).
                if (SignFace.SomberText(text)) _somber.Add(_sentences + 1);
                var sentence = Gloss.FromText(text);
                var emphatic = text.TrimEnd().EndsWith("!");
                if (_m.dictionary != null && _m.dictionary.TryMatch(text, out var whole))
                {
                    _queue.AddLast(new Item { Gloss = whole.english, Sign = whole, Kind = sentence.Kind, Sentence = ++_sentences, Negated = SignFace.NegativeEnglish(whole.english), To = whole.Span(false).to, Last = true, Emphatic = emphatic });
                    _lastWord = Time.time;
                    _caption.Show(Label, Pretty(text), final: true);
                    Debug.Log($"[Captions] sign {Label}: recorded sentence \"{whole.english}\" · behind {Behind():F1} s");
                    return;
                }
                var clauses = Clauses(_m.dictionary, text);
                if (clauses.Count > 1)
                {
                    // Part recorded by a fluent signer, part word by word: one sentence, so it is dropped as one.
                    var number = ++_sentences;
                    var log = new System.Text.StringBuilder();
                    foreach (var (part, recorded) in clauses)
                    {
                        var kind = Gloss.FromText(part).Kind;
                        if (recorded != null)
                        {
                            _queue.AddLast(new Item { Gloss = recorded.english, Sign = recorded, Kind = kind, Sentence = number, Negated = SignFace.NegativeEnglish(recorded.english), To = recorded.Span(false).to, Last = true, Emphatic = emphatic });
                            _lastWord = Time.time;
                            log.Append($"[\"{recorded.english}\"] ");
                        }
                        else
                        {
                            var g = Gloss.FromText(part);
                            Enqueue(g, number, emphatic);
                            log.Append(g).Append(' ');
                        }
                    }
                    _caption.Show(Label, Pretty(text), final: true);
                    Debug.Log($"[Captions] sign {Label}: {log}· behind {Behind():F1} s");
                    return;
                }
                var (signed, spelled, left) = Enqueue(sentence, ++_sentences, emphatic);
                _caption.Show($"{Label} · approximate", Pretty(text), final: true);
                Debug.Log($"[Captions] sign {Label}: {sentence} · {signed} signed, {spelled} spelled, {left} left to the caption · behind {Behind():F1} s");
            }

            (int signed, int spelled, int left) Enqueue(Gloss.Sentence sentence, int number, bool emphatic = false)
            {
                string previous = null;
                var left = 0;
                var items = new List<Item>();
                foreach (var g in sentence.Signs)
                {
                    var item = new Item { Gloss = g, Kind = sentence.Kind, Sentence = number, Negated = SignFace.Negates(g, previous), Emphatic = emphatic };
                    previous = g;
                    if (FindSign(g, sentence.Names.Contains(g), out var s)) { item.Sign = s; item.Gloss = s.gloss; }
                    else
                    {
                        item.Letters = Spellable(g);
                        if (item.Letters.Length == 0) continue;
                        // A name is spelled; a long everyday word without a sign is left to the caption.
                        if (!sentence.Names.Contains(g) && item.Letters.Length > _m.maxSpelledLetters) { left++; continue; }
                    }
                    if (items.Count > 0 && items[^1].Sign != null && items[^1].Sign == item.Sign) continue; // "go go go" → GO
                    items.Add(item);
                }
                // Within the sentence, from one sign's core straight to the next (no dip to the lap between them).
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i].Sign != null)
                    {
                        (items[i].From, items[i].To) = items[i].Sign.Span(i > 0);
                        // (the rate is relative to the default signingRate, so the viewer's pace still scales it)
                        items[i].Rate = RateFor(items[i].To - items[i].From, 0.85f, items.Count > 1, _m.maxSignSeconds, _m.maxSentenceSpeedup);
                    }
                    items[i].Last = i == items.Count - 1;
                    // Pauses by boundary, as signers make them (Huenerfauth's ASL timing): longer before a joined
                    // clause, a little after a pronoun subject, the usual short hold between the signs of a phrase.
                    if (!items[i].Last && Clause.Contains(items[i + 1].Gloss)) items[i].Gap = _m.clauseGapSeconds;
                    else if (i == 0 && !items[i].Last && Subjects.Contains(items[i].Gloss)) items[i].Gap = _m.subjectGapSeconds;
                    _queue.AddLast(items[i]);
                    _lastWord = Time.time;
                }
                var signed = items.FindAll(i => i.Sign != null).Count;
                return (signed, items.Count - signed, left);
            }

            bool FindSign(string gloss, bool name, out SignDictionary.Sign sign)
            {
                sign = null;
                if (_m.dictionary == null || name) return false;
                foreach (var form in Gloss.Forms(gloss))
                    if (_m.dictionary.TryGet(form, out sign)) return true;
                return false;
            }

            static string Spellable(string gloss)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in gloss) if (Handshapes.Has(c)) sb.Append(char.ToUpperInvariant(c));
                return sb.ToString();
            }

            // Easing in, the sign itself, then its hold.
            float Seconds(Item i) => i.Lead + Body(i) + (i.Last ? _m.sentenceHoldSeconds : i.Gap >= 0f ? i.Gap : _m.gapSeconds);
            float Body(Item i) => i.Sign != null ? (i.To - i.From) / RateOf(i) : i.Letters.Length / _m.lettersPerSecond;
            // Time into the sign itself (after the lead-in), in the recording's seconds or letters.
            float SignTime => _current == null ? 0f : Mathf.Max(0f, _t - _current.Lead) * (_current.Sign != null ? RateOf(_current) : 1f);
            float RateOf(Item i) => i.Rate > 0f ? i.Rate * _m.signingRate / 0.85f : _m.signingRate;

            // Seconds of signing still to do.
            float Behind()
            {
                var s = _current != null ? Mathf.Max(0, Seconds(_current) - _t) : 0f;
                foreach (var i in _queue) s += Seconds(i);
                return s / UserSpeed;
            }

            public void Tick(bool on, bool present)
            {
                var show = on && present;
                if (_root.gameObject.activeSelf != show) _root.gameObject.SetActive(show);
                _caption.Suppressed = !show;
                if (!on)
                {
                    _queue.Clear();
                    _current = null;
                    return;
                }

                // Too far behind: drop the oldest whole waiting sentences — never the one being signed, never the newest.
                var behind = Behind();
                while (behind > _m.maxBehindSeconds && _queue.Count > 0)
                {
                    var drop = _queue.First.Value.Sentence;
                    if (_current != null && drop == _current.Sentence)
                    {
                        var next = _queue.First;
                        while (next != null && next.Value.Sentence == drop) next = next.Next;
                        if (next == null) break;
                        drop = next.Value.Sentence;
                    }
                    if (drop == _queue.Last.Value.Sentence) break;
                    var n = _queue.First;
                    while (n != null) { var nx = n.Next; if (n.Value.Sentence == drop) _queue.Remove(n); n = nx; }
                    Debug.Log($"[Captions] sign {Label}: dropped a sentence to catch up ({behind:F1} s behind)");
                    behind = Behind();
                }
                var speed = Mathf.Clamp(1f + (behind - 3f) / 8f, 1f, 1.3f);

                var dt = Time.deltaTime;
                if (_current != null)
                {
                    var held = _t >= _current.Lead + Body(_current);
                    _t += dt * speed * UserSpeed;
                    // The sentence's last sign has just finished: a small nod closes it.
                    if (!held && _current.Last && _t >= _current.Lead + Body(_current)) _body.Nod();
                    if (_t >= Seconds(_current)) _current = null;
                }
                var started = false;
                if (_current == null && _queue.Count > 0)
                {
                    _current = _queue.First.Value;
                    _queue.RemoveFirst();
                    _t = 0;
                    started = true;
                    BeginLead();
                }
                if (_current != null) _idleSince = Time.time;

                var kind = _current?.Kind ?? (_queue.Count > 0 ? _queue.First.Value.Kind : Gloss.Kind.Statement);
                var faceK = 1f - Mathf.Exp(-dt * 8f);
                _raise = Mathf.Lerp(_raise, kind == Gloss.Kind.YesNoQuestion ? 0.8f : 0f, faceK);
                _lower = Mathf.Lerp(_lower, kind == Gloss.Kind.WhQuestion ? 0.7f : 0f, faceK);
                var (mouth, open) = Mouthing();
                _face.Tick(dt, _current?.Sign, _current?.At(SignTime) ?? 0f, _current?.Gloss, _current is { Negated: true }, started, _raise, _lower, mouth, open);
                _glance -= dt;

                Target();
                _face.AddHead(_target);
                _idle.Add(_target, dt, _current == null && _queue.Count == 0 && Time.time - _idleSince > 0.5f);
                _body.Add(_target, dt, _current != null, _raise, _lower, _m.Ready);
                SignPose.Follow(_now, _target, _muscleVel, dt, speed * UserSpeed * (_current is { Emphatic: true } ? 1.35f : 1f));

                if (show) Place();
                if (Busy) _caption.KeepOpen();
                // Under the avatar's waist: the panel grows upwards from here.
                _caption.Target = _root.position + Vector3.up * (_hipsHeight * _root.localScale.y * 0.55f);
            }

            // A sign starting: ease from where the hands are to its first frame, over a time that grows with how far
            // they have to travel (a short hop within signing space, longer from the lap), instead of letting the
            // sign's own clock run while the spring is still catching up (which clipped each sign's start and made
            // the hands jump between signs). The body takes a small beat with it.
            void BeginLead()
            {
                System.Array.Copy(_now, _from, _from.Length);
                PoseAt(0f, _first);
                var far = 0f;
                foreach (var i in _m.ArmMuscles) far = Mathf.Max(far, Mathf.Abs(_first[i] - _from[i]));
                _current.Lead = Mathf.Clamp(0.12f + 0.3f * far, 0.14f, 0.4f) * (_current.Emphatic ? 0.8f : 1f);
                _body.Beat(_current.Emphatic ? 1.6f : 1f);
                _glance = Glances.Contains(_current.Gloss) ? _current.Lead + Mathf.Min(0.6f, Body(_current)) : 0f;
            }

            // Fingerspelling: the mouth shapes the word as it is spelled, as signers mouth a spelled name or word.
            (char, float) Mouthing()
            {
                if (_current == null || _current.Sign != null || _t < _current.Lead) return ('\0', 0f);
                var pos = SignTime * _m.lettersPerSecond;
                var idx = (int)pos;
                if (idx >= _current.Letters.Length) return ('\0', 0f);
                return (_current.Letters[idx], Mathf.Sin(Mathf.Clamp01(pos - idx) * Mathf.PI));
            }

            // The pose of the current item at a time into it (seconds or letters as in SignTime).
            void PoseAt(float signTime, float[] into)
            {
                if (_current.Sign != null)
                {
                    System.Array.Copy(_m.Ready, into, into.Length);
                    _current.Sign.Sample(_current.At(signTime), into);
                    return;
                }
                var pos = signTime * _m.lettersPerSecond;
                var idx = Mathf.Min((int)pos, _current.Letters.Length - 1);
                var c = _current.Letters[idx];
                System.Array.Copy(_m.Letter(c), into, into.Length);
                Handshapes.Motion(c, Mathf.Clamp01(pos - idx), into);
            }

            // The pose this frame should move towards.
            void Target()
            {
                if (_current == null)
                {
                    // Hands down in front between sentences; soon after, with nothing more to sign, its resting pose
                    // (hands folded or at its sides: SignIdle).
                    if (Time.time - _idleSince > 0.5f) _idle.Pose(_target);
                    else System.Array.Copy(_m.Neutral, _target, _target.Length);
                    return;
                }
                if (_t < _current.Lead)
                {
                    // Minimum-jerk ease: no jolt leaving the last sign or arriving at this one.
                    var u = _t / _current.Lead;
                    var k = u * u * u * (u * (u * 6f - 15f) + 10f);
                    for (var i = 0; i < _target.Length; i++) _target[i] = Mathf.LerpUnclamped(_from[i], _first[i], k);
                    return;
                }
                if (_current.Sign != null)
                {
                    System.Array.Copy(_m.Ready, _target, _target.Length);
                    _current.Sign.Sample(_current.At(SignTime), _target);
                    return;
                }
                var pos = SignTime * _m.lettersPerSecond;
                var idx = (int)pos;
                if (idx >= _current.Letters.Length)
                {
                    System.Array.Copy(_m.Ready, _target, _target.Length); // the pause between words
                    return;
                }
                var c = _current.Letters[idx];
                System.Array.Copy(_m.Letter(c), _target, _target.Length);
                Handshapes.Motion(c, pos - idx, _target);
                // A double letter (LL, EE): the hand slides sideways a little for the second one, so both are seen.
                if (idx > 0 && _current.Letters[idx - 1] == c && Handshapes.MuscleIndex("Right Arm Front-Back") is var fb and >= 0)
                    _target[fb] -= 0.12f;
            }

            void Place()
            {
                var head = _m.head;
                var fwd = Flat(head.forward);
                Vector3 goal;
                float scale;
                if (Who != null && Who == _m.only && _m.placedAt != null)
                {
                    // Placed by the viewer (spatial anchor): stays there, sized for its distance like the bubbles.
                    goal = _m.placedAt.position;
                    scale = _m.avatarScale * Mathf.Clamp(Vector3.Distance(goal, head.position) / 1.2f, 0.6f, 3f);
                }
                else if (Who == null)
                {
                    // Nobody to stand beside: lower right of the view.
                    scale = _m.avatarScale;
                    goal = head.position + fwd * 1.2f + Vector3.Cross(Vector3.up, fwd) * 0.4f + Vector3.down * 0.1f;
                }
                else if (_placed && Time.time - Who.LastSeen > 0.3f)
                {
                    // Face not seen right now: the head estimate drifts, so stay where we were.
                    goal = _goal;
                    scale = _scale;
                }
                else
                {
                    var anchor = Who.HeadPosition;
                    var toPerson = anchor - head.position;
                    var right = Vector3.Cross(Vector3.up, Flat(toPerson)).normalized;
                    var dist = Mathf.Clamp(toPerson.magnitude / 1.2f, 0.6f, 3f);
                    scale = _m.avatarScale * dist;
                    // Clear of the face: half a face, a gap, then half the avatar's shoulders.
                    var sideways = _m.tracker.faceWidthMeters * 0.5f + 0.05f * dist + 0.25f * scale;
                    // The person's caption side (away from other faces) unless that leaves the view; then the inner side.
                    if (_side == 0) _side = _m.director.SideOf(Who);
                    float Off(float s) => Mathf.Abs(Vector3.SignedAngle(fwd, Flat(anchor + right * s * sideways - head.position), Vector3.up));
                    if (Off(_side) > _m.viewHalfAngle && Off(-_side) < Off(_side)) _side = -_side;
                    goal = anchor + right * _side * sideways;
                }
                _goal = goal;
                _scale = scale;
                // The avatar's eyes at the speaker's eye level.
                var root = goal - Vector3.up * _headHeight * scale;
                _root.position = _placed ? Vector3.SmoothDamp(_root.position, root, ref _vel, 0.3f) : root;
                _placed = true;
                _root.localScale = Vector3.one * scale;
                _root.rotation = Quaternion.LookRotation(Flat(head.position - _root.position), Vector3.up);
            }

            public void Pose()
            {
                if (!_root.gameObject.activeSelf) return;
                _pose.Apply(_now);
                // The upper lids follow the eyes: lower as they look down, lifted a little as they look up.
                System.Array.Copy(_face.Now, _faceOut, _faceOut.Length);
                var down = Mathf.Clamp01(-_eyePitch / 30f) * 0.3f;
                var up = Mathf.Clamp01(_eyePitch / 25f) * 0.35f;
                if (LidsClosedL >= 0) { _faceOut[LidsClosedL] = Mathf.Max(_faceOut[LidsClosedL], down); _faceOut[LidsClosedR] = Mathf.Max(_faceOut[LidsClosedR], down); }
                if (LidsUpL >= 0) { _faceOut[LidsUpL] = Mathf.Max(_faceOut[LidsUpL], up); _faceOut[LidsUpR] = Mathf.Max(_faceOut[LidsUpR], up); }
                _warmth = Mathf.MoveTowards(_warmth, _current != null && _somber.Contains(_current.Sentence) ? 0f : 1f, Time.deltaTime / 0.3f);
                SignFace.Warm(_faceOut, _m.friendliness * _warmth);
                _pose.SetFace(_faceOut);
                Look();
            }

            static readonly HashSet<string> Clause = new() { "AND", "BUT", "BECAUSE", "IF", "SO", "OR", "THEN", "WHEN", "WHILE", "UNTIL", "BEFORE", "AFTER" };
            static readonly HashSet<string> Subjects = new() { "I", "ME", "YOU", "HE", "SHE", "IT", "WE", "THEY", "MY", "YOUR", "HE-SHE-IT", "HESHEIT" };

            // Pointing and placing signs: the eyes go to the hand, as signers' do when they set up a place in space.
            // Directional (agreeing and spatial) verbs too: signers look where the object is about three times in four,
            // from just before the verb (Thompson, Emmorey &amp; Kluender 2006). Plain verbs and verbs moving towards the
            // signer (BORROW, TAKE) keep the eyes on the viewer.
            static readonly HashSet<string> Glances = new()
            {
                "THERE", "THAT", "THIS", "HERE", "IT", "HE", "SHE", "THEY", "HIM", "HER", "THEM", "HIS", "THEIR", "LOOK", "SEE",
                "OVER-THERE", "LEFT", "RIGHT", "UP", "DOWN",
                "GIVE", "TELL", "ASK", "SHOW", "HELP", "SEND", "PAY", "INFORM", "TEACH", "BRING", "MEET", "VISIT", "INVITE",
                "BLAME", "PUT", "MOVE", "GO", "THROW", "CARRY", "FOLLOW",
            };

            // The eyes, after the pose (whose eye muscles are 0, looking straight out of the face): on the viewer, with
            // small saccades every second or two so they don't look painted on; on the hand during a glance.
            void Look()
            {
                if (_leftEye == null || _rightEye == null) return;
                var dt = Time.deltaTime;
                if (Time.time >= _nextSaccade)
                {
                    _nextSaccade = Time.time + UnityEngine.Random.Range(0.8f, 2.2f);
                    _saccade = UnityEngine.Random.insideUnitSphere * 0.02f;
                }
                var hand = _rightHand;
                if (_leftHand != null && _rightHand != null && _current != null)
                {
                    // Whichever hand is further out from the body is the one placing something.
                    var chest = _headBone.position + Vector3.down * 0.25f * _scale;
                    if ((_leftHand.position - chest).sqrMagnitude > (_rightHand.position - chest).sqrMagnitude) hand = _leftHand;
                }
                var target = _glance > 0f && hand != null ? hand.position : _m.head.position + _saccade * Mathf.Max(1f, _scale * 2f);
                var face = _headBone.rotation * _faceForward;
                // Glancing away at rest (SignIdle): the eyes lead the head off into the room.
                var away = Mathf.Abs(_idle.Away);
                if (away > 0.01f) target = Vector3.Lerp(target, _headBone.position + face * 2f + Vector3.down * 0.2f, away);
                _gaze = _gaze == Vector3.zero ? target : Vector3.SmoothDamp(_gaze, target, ref _gazeVel, 0.045f, Mathf.Infinity, dt);

                // The head joins a far look: the eyes lead, the head follows more slowly and takes what is beyond
                // about 15° (people turn the head for big shifts and let the eyes do small ones).
                var toGaze = Quaternion.Inverse(_headBone.rotation) * (_gaze - _headBone.position);
                var right = Vector3.Cross(_faceUp, _faceForward);
                var yaw = Mathf.Atan2(Vector3.Dot(toGaze, right), Vector3.Dot(toGaze, _faceForward)) * Mathf.Rad2Deg;
                var pitch = Mathf.Atan2(Vector3.Dot(toGaze, _faceUp), new Vector2(Vector3.Dot(toGaze, right), Vector3.Dot(toGaze, _faceForward)).magnitude) * Mathf.Rad2Deg;
                static float Beyond(float deg, float free, float max) => Mathf.Clamp(Mathf.Sign(deg) * Mathf.Max(0f, Mathf.Abs(deg) - free) * 0.6f, -max, max);
                var wantHead = new Vector2(Beyond(yaw, 15f, 20f), Beyond(pitch, 12f, 10f));
                _headFollow = Vector2.SmoothDamp(_headFollow, wantHead, ref _headFollowVel, 0.16f, Mathf.Infinity, dt);
                if (_headFollow.sqrMagnitude > 1e-4f)
                {
                    var head = _headBone.rotation;
                    _headBone.rotation = Quaternion.AngleAxis(_headFollow.x, head * _faceUp) * Quaternion.AngleAxis(-_headFollow.y, head * right) * head;
                }
                _eyePitch = Mathf.Clamp(pitch - _headFollow.y, -25f, 25f); // as far as the eyes turn (below)

                foreach (var eye in new[] { _leftEye, _rightEye })
                {
                    var to = _gaze - eye.position;
                    if (to.sqrMagnitude < 1e-6f) continue;
                    // Within what eyes do without the head (about 25°); past that, as far as they go.
                    var dir = Vector3.RotateTowards(face, to.normalized, 25f * Mathf.Deg2Rad, 0f);
                    eye.rotation = Quaternion.FromToRotation(face, dir) * eye.rotation;
                }
            }
        }

        static string Pretty(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized; }
    }
}
