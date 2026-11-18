using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The signing avatar's face and head: in sign languages these are grammar, not decoration. Each frame it builds
    /// the face from, strongest first:
    ///   • the recorded signer's own expression for the sign (brows, squints, cheeks, mouth; see extract_pose.py)
    ///   • the affect of signs that carry one (HAPPY smiles, SAD and SORRY droop, MAD frowns…), muting what contradicts it
    ///   • questions: brows raised for yes/no, lowered for wh-questions, held through the sentence
    ///   • negation (NOT, NEVER, CAN'T and the sign after them; a recorded sentence with "not"): a frown and a headshake
    ///   • YES: a nod
    ///   • blinks, at sign boundaries as signers blink, and every few seconds otherwise
    /// Weights are 0–1 per channel in <see cref="SignPose.FaceChannels"/>; <see cref="SignPose.SetFace"/> applies them.
    /// </summary>
    public sealed class SignFace
    {
        public readonly float[] Now = new float[SignPose.FaceChannels.Length];
        readonly float[] _target = new float[SignPose.FaceChannels.Length];
        readonly float[] _smooth = new float[SignPose.FaceChannels.Length], _vel = new float[SignPose.FaceChannels.Length];
        readonly System.Random _random;
        float _clock, _nextBlink = 2f, _blinkAt = -1f, _shake, _nod;

        public SignFace(int seed = 0) => _random = new System.Random(seed);

        static readonly int[] Frowns = Ids("browLowerer_L", "browLowerer_R", "lipCornerDepressor_L", "lipCornerDepressor_R", "noseWrinkler_L", "noseWrinkler_R");
        static readonly int[] MouthShapes = Ids("jawDrop", "lipPucker_L", "lipPucker_R", "lipFunneler_LT", "lipFunneler_RT", "lipSuck_LB", "lipSuck_RB");
        static readonly (int channel, float weight)[] Warmth =
        {
            // (This face's smile shapes are subtle: corners 0.6 with cheeks 0.4 is a warm closed-mouth smile.)
            // A soft closed-mouth smile with the cheeks lifted (the eyes smile too, Duchenne), brows a little up and open.
            (I("lipCornerPuller_L"), 0.75f), (I("lipCornerPuller_R"), 0.75f), (I("cheekRaiser_L"), 0.5f), (I("cheekRaiser_R"), 0.5f),
            (I("dimpler_L"), 0.1f), (I("dimpler_R"), 0.1f), (I("innerBrowRaiser_L"), 0.15f), (I("innerBrowRaiser_R"), 0.15f),
            (I("outerBrowRaiser_L"), 0.08f), (I("outerBrowRaiser_R"), 0.08f), (I("lidTightener_L"), 0.08f), (I("lidTightener_R"), 0.08f),
        };
        static int I(string name) => System.Array.IndexOf(SignPose.FaceChannels, name);
        static int[] Ids(params string[] names) => System.Array.ConvertAll(names, I);

        // Resting warmth (SignAvatar.friendliness): raises the friendly channels to their level unless the sign's
        // face is already above it. A frown, negation or wh-question face takes it away (the smile would
        // contradict it); mouthing halves it so the lip shapes stay readable.
        public static void Warm(float[] face, float amount)
        {
            if (amount <= 0f) return;
            var frown = 0f;
            foreach (var i in Frowns) if (i >= 0) frown = Mathf.Max(frown, face[i]);
            var mouth = 0f;
            foreach (var i in MouthShapes) if (i >= 0) mouth = Mathf.Max(mouth, face[i]);
            var k = amount * (1f - Mathf.Clamp01((frown - 0.15f) * 3f)) * (1f - 0.5f * Mathf.Clamp01(mouth * 2f));
            foreach (var (i, w) in Warmth) if (i >= 0) face[i] = Mathf.Max(face[i], w * k);
        }

        static readonly Dictionary<string, int> Channel = new();

        static int Index(string channel)
        {
            if (Channel.Count == 0)
                for (var i = 0; i < SignPose.FaceChannels.Length; i++) Channel[SignPose.FaceChannels[i]] = i;
            return Channel.TryGetValue(channel, out var c) ? c : -1;
        }

        // Expressions by name: (channel without its _L/_R side, weight); both sides unless the channel has none.
        static readonly (string, float)[] Smile = { ("lipCornerPuller", 0.55f), ("cheekRaiser", 0.35f), ("lidTightener", 0.1f) };
        static readonly (string, float)[] Sad = { ("innerBrowRaiser", 0.55f), ("lipCornerDepressor", 0.5f), ("browLowerer", 0.15f), ("lowerLipDepressor", 0.1f), ("chinRaiser_B", 0.35f) };
        static readonly (string, float)[] Angry = { ("browLowerer", 0.75f), ("lidTightener", 0.45f), ("noseWrinkler", 0.25f), ("lipPressor", 0.35f), ("upperLipRaiser", 0.15f), ("lipTightener", 0.35f) };
        static readonly (string, float)[] Surprised = { ("innerBrowRaiser", 0.7f), ("outerBrowRaiser", 0.7f), ("upperLidRaiser", 0.55f), ("jawDrop", 0.3f) };
        static readonly (string, float)[] Afraid = { ("innerBrowRaiser", 0.6f), ("outerBrowRaiser", 0.3f), ("upperLidRaiser", 0.45f), ("lipStretcher", 0.4f), ("browLowerer", 0.2f) };
        static readonly (string, float)[] Disgust = { ("noseWrinkler", 0.6f), ("upperLipRaiser", 0.45f), ("browLowerer", 0.3f) };
        static readonly (string, float)[] Pleading = { ("innerBrowRaiser", 0.35f), ("lipCornerPuller", 0.2f) };
        static readonly (string, float)[] Negative = { ("browLowerer", 0.35f), ("lipCornerDepressor", 0.45f), ("lipPressor", 0.2f), ("chinRaiser_B", 0.3f) };

        static readonly Dictionary<string, (string, float)[]> Affect = Build();

        // What each expression rules out of the recorded face: a dictionary signer often smiles into the camera
        // before and after SAD, and that grin mustn't survive into it.
        static readonly Dictionary<(string, float)[], string[]> Opposes = new()
        {
            [Smile] = new[] { "lipCornerDepressor", "browLowerer", "lipPressor", "chinRaiser_B" },
            [Sad] = new[] { "lipCornerPuller", "cheekRaiser", "upperLipRaiser", "dimpler" },
            [Angry] = new[] { "lipCornerPuller", "cheekRaiser", "innerBrowRaiser", "outerBrowRaiser" },
            [Surprised] = new[] { "browLowerer", "lidTightener" },
            [Afraid] = new[] { "lipCornerPuller", "cheekRaiser" },
            [Disgust] = new[] { "lipCornerPuller", "innerBrowRaiser", "outerBrowRaiser" },
            [Pleading] = new[] { "browLowerer", "lipCornerDepressor" },
            [Negative] = new[] { "lipCornerPuller", "cheekRaiser" },
        };

        static Dictionary<string, (string, float)[]> Build()
        {
            var d = new Dictionary<string, (string, float)[]>();
            void Add((string, float)[] e, params string[] glosses) { foreach (var g in glosses) d[g] = e; }
            Add(Smile, "HAPPY", "GLAD", "FUN", "LOVE", "LIKE", "THANK-YOU", "THANKS", "THANK", "GOOD", "NICE", "WELCOME",
                "HELLO", "HI", "ENJOY", "FRIEND", "SMILE", "BEAUTIFUL", "WONDERFUL", "GREAT", "EXCITED", "FINE", "COOL", "FUNNY");
            Add(Sad, "SAD", "SORRY", "SICK", "TIRED", "HURT", "PAIN", "BAD", "LONELY", "CRY", "MISS", "DISAPPOINTED", "WORRY", "UPSET");
            Add(Angry, "MAD", "ANGRY", "HATE", "FRUSTRATED", "ANNOYED");
            Add(Surprised, "SURPRISE", "SURPRISED", "WOW", "REALLY", "AMAZING", "SHOCK");
            Add(Afraid, "SCARED", "AFRAID", "FEAR", "NERVOUS");
            Add(Disgust, "DISGUST", "GROSS", "YUCK");
            Add(Pleading, "PLEASE", "HELP");
            return d;
        }

        /// <summary>A sign with a sad, angry, afraid or disgusted face (SICK, SORRY, MAD…).</summary>
        public static bool Somber(string gloss) =>
            Affect.TryGetValue(gloss.ToUpperInvariant(), out var e) && (e == Sad || e == Angry || e == Afraid || e == Disgust);

        /// <summary>An English sentence with such a word in it ("I feel sick today"): signed without the resting smile.</summary>
        public static bool SomberText(string english)
        {
            foreach (var w in english.Split(' ', ',', '.', '!', '?', ';', ':'))
                if (w.Length > 0)
                    foreach (var form in Gloss.Forms(w.ToUpperInvariant()))
                        if (Somber(form)) return true;
            return false;
        }

        static readonly HashSet<string> Negators = new() { "NOT", "NO", "NEVER", "NONE", "NOTHING", "NOBODY", "CAN'T", "DON'T", "WON'T" };

        /// <summary>A negating sign, or the one it negates (the sign after NOT, CAN'T, NEVER).</summary>
        public static bool Negates(string gloss, string previousGloss) =>
            Negators.Contains(gloss) || (previousGloss is "NOT" or "CAN'T" or "NEVER" or "DON'T" or "WON'T");

        /// <summary>A recorded sentence's English says no.</summary>
        public static bool NegativeEnglish(string english)
        {
            var e = " " + english.ToLowerInvariant().Replace('’', '\'') + " ";
            return e.Contains("n't") || e.Contains(" not ") || e.Contains(" no ") || e.Contains(" no.") || e.Contains(" never ") || e.StartsWith(" no");
        }

        // Channels by expression name: both sides' indices (or the one, for the jaw and mouth), looked up once.
        static readonly Dictionary<string, int[]> Sides = new();

        void Put(string name, float w)
        {
            if (!Sides.TryGetValue(name, out var idx))
            {
                var l = new List<int>();
                foreach (var c in new[] { name + "_L", name + "_R", name }) if (Index(c) is var i and >= 0) l.Add(i);
                Sides[name] = idx = l.ToArray();
            }
            foreach (var i in idx) _target[i] = Mathf.Max(_target[i], w);
        }

        void Put((string, float)[] expression, float scale)
        {
            if (Opposes.TryGetValue(expression, out var off))
                foreach (var name in off)
                {
                    Put(name, 0f); // resolves the indices
                    foreach (var i in Sides[name]) _target[i] = 0f;
                }
            foreach (var (name, w) in expression) Put(name, w * scale);
        }

        /// <summary>
        /// Advances the face by dt. sign/t: the recorded sign playing and how far into it (null between signs);
        /// gloss: the sign's gloss; started: a new sign began this frame; raise/lower: question brows (0–1).
        /// </summary>
        public void Tick(float dt, SignDictionary.Sign sign, float t, string gloss, bool negated, bool started, float raise, float lower,
            char mouth = '\0', float open = 0f)
        {
            _clock += dt;
            System.Array.Clear(_target, 0, _target.Length);
            Put("lipCornerPuller", 0.08f); // at rest, pleasant rather than blank
            sign?.SampleFace(t, _target);
            if (gloss != null && Affect.TryGetValue(gloss.ToUpperInvariant(), out var affect)) Put(affect, 1f);
            Put("innerBrowRaiser", raise);
            Put("outerBrowRaiser", raise);
            Put("upperLidRaiser", 0.4f * raise);
            Put("browLowerer", lower);
            Put("lidTightener", 0.4f * lower);
            if (negated) Put(Negative, 1f);
            if (mouth != '\0') Mouth(mouth, open);
            Derive("L");
            Derive("R");
            for (var i = 0; i < Now.Length; i++)
                Now[i] = _smooth[i] = Mathf.Clamp01(Mathf.SmoothDamp(_smooth[i], _target[i], ref _vel[i], 0.07f, Mathf.Infinity, dt));
            _shake = Mathf.MoveTowards(_shake, negated ? 1f : 0f, dt / 0.15f);
            _nod = Mathf.MoveTowards(_nod, gloss is "YES" || (gloss != null && gloss.StartsWith("Yes")) ? 1f : 0f, dt / 0.15f);

            // Blinks: quick (0.15 s) and on top of everything.
            if ((started && _clock - _blinkAt > 2f) || _clock >= _nextBlink)
            {
                _blinkAt = _clock;
                _nextBlink = _clock + 3f + 3f * (float)_random.NextDouble();
            }
            var b = _clock - _blinkAt;
            var closed = b < 0.15f ? 1f - Mathf.Abs(b - 0.075f) / 0.075f : 0f;
            if (Index("eyesClosed_L") is var el and >= 0) Now[el] = Mathf.Max(Now[el], closed);
            if (Index("eyesClosed_R") is var er and >= 0) Now[er] = Mathf.Max(Now[er], closed);
        }

        // Mouthing a fingerspelled word, letter by letter (open: 0 → 1 → 0 across the letter): the open vowels open
        // the jaw, E and I spread the lips, O, U and W round them, B, M and P close them, F and V tuck the lower lip.
        void Mouth(char c, float open)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'A': Put("jawDrop", 0.35f * open); break;
                case 'E': case 'I': case 'Y': Put("lipStretcher", 0.4f * open); Put("jawDrop", 0.12f * open); break;
                case 'O': Funnel(0.5f * open); Put("jawDrop", 0.22f * open); break;
                case 'U': case 'W': case 'Q': Put("lipPucker", 0.55f * open); break;
                case 'B': case 'M': case 'P': Put("lipPressor", 0.5f * open); break;
                case 'F': case 'V': Put("lipSuck_LB", 0.45f * open); Put("lipSuck_RB", 0.45f * open); break;
                case 'R': Funnel(0.25f * open); break;
                default: Put("jawDrop", 0.1f * open); break;
            }
        }

        void Funnel(float w)
        {
            foreach (var c in new[] { "lipFunneler_LB", "lipFunneler_LT", "lipFunneler_RB", "lipFunneler_RT" }) Put(c, w);
        }

        // A smile bunches the cheeks and deepens the fold beside the nose; a raised upper lip wrinkles the nose; a frown
        // pushes the chin up. The face tracker never reports cheeks or nose, so these come from the mouth.
        void Derive(string side)
        {
            float Get(string c) => Index(c) is var i and >= 0 ? _target[i] : 0f;
            void Raise(string c, float w) { if (Index(c) is var i and >= 0) _target[i] = Mathf.Max(_target[i], Mathf.Clamp01(w)); }
            var smile = Get("lipCornerPuller_" + side);
            var sneer = Get("upperLipRaiser_" + side);
            Raise("cheekRaiser_" + side, 0.75f * smile);
            Raise("nasolabialFurrow_" + side, Mathf.Max(0.6f * smile, 0.5f * sneer));
            Raise("noseWrinkler_" + side, 0.5f * sneer);
            Raise("chinRaiser_B", 0.4f * Get("lipCornerDepressor_" + side));
        }

        static int _turn = -2, _neckTurn, _nodMuscle, _neckNod;

        /// <summary>Headshake for negation and a nod for YES, added onto the pose's target muscles.</summary>
        public void AddHead(float[] muscles)
        {
            if (_turn == -2)
            {
                _turn = SignPose.Index("Head Turn Left-Right");
                _neckTurn = SignPose.Index("Neck Turn Left-Right");
                _nodMuscle = SignPose.Index("Head Nod Down-Up");
                _neckNod = SignPose.Index("Neck Nod Down-Up");
            }
            var shake = _shake * Mathf.Sin(_clock * 2f * Mathf.PI * 2.2f);
            if (_turn >= 0) muscles[_turn] += 0.3f * shake;
            if (_neckTurn >= 0) muscles[_neckTurn] += 0.15f * shake;
            var nod = _nod * -Mathf.Abs(Mathf.Sin(_clock * Mathf.PI * 2f));
            if (_nodMuscle >= 0) muscles[_nodMuscle] += 0.35f * nod;
            if (_neckNod >= 0) muscles[_neckNod] += 0.15f * nod;
        }
    }
}
