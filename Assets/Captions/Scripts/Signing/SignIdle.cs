using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The signing avatar while it has nothing to sign, so it waits like an interpreter instead of standing frozen
    /// with its arms hanging (breathing, sway and blinks come from <see cref="SignBody"/> and <see cref="SignFace"/>):
    ///   • it changes how it stands every 7–16 s: hands folded low in front (an interpreter's waiting pose), the
    ///     same with its weight on one hip, or arms relaxed at its sides
    ///   • the fingers and wrists move a little, never quite still
    ///   • now and then it glances away into the room and back
    ///   • while the person it signs for is speaking it listens: small nods and a slight head tilt
    /// Replaces the target pose between sentences (<see cref="Pose"/>) and adds its motion on top
    /// (<see cref="Add"/>), before the spring, so every change eases in.
    /// </summary>
    public sealed class SignIdle
    {
        readonly System.Random _random;
        readonly float _seed;
        float _clock, _switchAt, _glanceAt, _glanceStart = -9f, _glanceLength, _listenUntil = -9f, _nodAt, _nodAge = 9f;
        float _resting, _listening, _glanceDir = 1f;
        int _posture;

        static float[][] _postures;
        static int[] _fingers;
        static int _lWristBend = -2, _rWristBend, _lWristSide, _rWristSide, _headTurn, _neckTurn, _headTilt, _headNod, _neckNod,
            _spineLR, _chestLR;

        /// <summary>How far the head is turned away on a glance (−1…1, eased): the eyes go with it.</summary>
        public float Away { get; private set; }

        public SignIdle(int seed = 0)
        {
            _random = new System.Random(seed);
            _seed = (seed & 0xffff) * 0.31f;
            _posture = 1;
            _switchAt = Range(7f, 16f);
            _glanceAt = Range(8f, 16f);
        }

        float Range(float a, float b) => a + (b - a) * (float)_random.NextDouble();

        float Noise(float hz, float channel) => 2f * Mathf.PerlinNoise(_seed + channel * 13.1f, _clock * hz) - 1f;

        static void Set(float[] m, string name, float v)
        {
            var i = SignPose.Index(name);
            if (i >= 0) m[i] = v;
        }

        static void Add(float[] m, int i, float v)
        {
            if (i >= 0) m[i] += v;
        }

        static void Resolve()
        {
            if (_postures != null) return;
            // Hands folded low in front, the right over the left, palms in (found by measuring the hand positions in
            // the Editor: wrists ~20 cm in front of the hips, ~7 cm either side of the middle).
            var folded = Handshapes.Rest();
            Set(folded, "Left Arm Down-Up", -0.85f);
            Set(folded, "Left Arm Front-Back", 0f);
            Set(folded, "Left Arm Twist In-Out", -0.15f);
            Set(folded, "Left Forearm Stretch", 0.55f);
            Set(folded, "Left Forearm Twist In-Out", -0.5f);
            Set(folded, "Right Arm Down-Up", -0.85f);
            Set(folded, "Right Arm Front-Back", -0.05f);
            Set(folded, "Right Arm Twist In-Out", -0.1f);
            Set(folded, "Right Forearm Stretch", 0.42f);
            Set(folded, "Right Forearm Twist In-Out", -0.5f);
            // The same with the weight on one hip.
            var shifted = (float[])folded.Clone();
            Set(shifted, "Spine Left-Right", 0.12f);
            Set(shifted, "Chest Left-Right", -0.06f);
            Set(shifted, "Head Tilt Left-Right", -0.06f);
            _postures = new[] { Handshapes.Rest(), folded, shifted };

            var fingers = new System.Collections.Generic.List<int>();
            foreach (var side in new[] { "Left", "Right" })
            foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                var i = SignPose.Index($"{side} {f} 2 Stretched");
                if (i >= 0) fingers.Add(i);
            }
            _fingers = fingers.ToArray();
            _lWristBend = SignPose.Index("Left Hand Down-Up");
            _rWristBend = SignPose.Index("Right Hand Down-Up");
            _lWristSide = SignPose.Index("Left Hand In-Out");
            _rWristSide = SignPose.Index("Right Hand In-Out");
            _headTurn = SignPose.Index("Head Turn Left-Right");
            _neckTurn = SignPose.Index("Neck Turn Left-Right");
            _headTilt = SignPose.Index("Head Tilt Left-Right");
            _headNod = SignPose.Index("Head Nod Down-Up");
            _neckNod = SignPose.Index("Neck Nod Down-Up");
            _spineLR = SignPose.Index("Spine Left-Right");
            _chestLR = SignPose.Index("Chest Left-Right");
        }

        /// <summary>The person it signs for is speaking (live words): listen for a moment.</summary>
        public void Listen() => _listenUntil = _clock + 1.5f;

        /// <summary>The resting pose to move towards between sentences.</summary>
        public void Pose(float[] into)
        {
            Resolve();
            System.Array.Copy(_postures[_posture], into, into.Length);
        }

        /// <summary>
        /// Advances the idle clock and adds its motion onto the target muscles. resting: nothing being signed for a
        /// moment (the hands are on their way to, or in, the resting pose).
        /// </summary>
        public void Add(float[] muscles, float dt, bool resting)
        {
            Resolve();
            _clock += dt;
            _resting = Mathf.MoveTowards(_resting, resting ? 1f : 0f, dt / 0.8f);
            _listening = Mathf.MoveTowards(_listening, resting && _clock < _listenUntil ? 1f : 0f, dt / 0.5f);
            if (!resting)
            {
                // Signing: start the next rest folded, and give it time before changing.
                _posture = 1;
                _switchAt = _clock + Range(7f, 16f);
                _glanceAt = Mathf.Max(_glanceAt, _clock + Range(4f, 10f));
            }
            else if (_clock >= _switchAt)
            {
                // Mostly folded (it reads as attentive); now and then arms at the sides.
                var next = _random.NextDouble() < 0.25 ? 0 : _random.NextDouble() < 0.5 ? 1 : 2;
                if (next == _posture) next = _posture == 0 ? 1 : 0;
                _posture = next;
                _switchAt = _clock + Range(7f, 16f);
            }
            var k = _resting;
            if (k <= 0f) { Away = 0f; return; }

            // Fingers and wrists: slow, small, each its own way.
            for (var i = 0; i < _fingers.Length; i++) Add(muscles, _fingers[i], 0.06f * k * Noise(0.25f, i));
            Add(muscles, _lWristBend, 0.05f * k * Noise(0.18f, 20));
            Add(muscles, _rWristBend, 0.05f * k * Noise(0.18f, 21));
            Add(muscles, _lWristSide, 0.04f * k * Noise(0.15f, 22));
            Add(muscles, _rWristSide, 0.04f * k * Noise(0.15f, 23));

            // A glance into the room and back: not while listening.
            if (_clock >= _glanceAt && _listening < 0.1f)
            {
                _glanceStart = _clock;
                _glanceLength = Range(1.2f, 2.4f);
                _glanceAt = _clock + _glanceLength + Range(8f, 18f);
                var dir = _random.NextDouble() < 0.5 ? -1f : 1f;
                _glanceDir = dir * Range(0.6f, 1f);
            }
            var g = _clock - _glanceStart;
            var away = g < 0f || g > _glanceLength ? 0f
                : Mathf.SmoothStep(0f, 1f, Mathf.Min(g / 0.35f, (_glanceLength - g) / 0.45f, 1f));
            Away = _glanceDir * away * k;
            Add(muscles, _headTurn, 0.22f * Away);
            Add(muscles, _neckTurn, 0.12f * Away);
            Add(muscles, _headNod, 0.03f * Mathf.Abs(Away)); // a touch of lift as it looks off

            // Listening: a slight tilt towards them and a small nod every few seconds.
            var l = _listening * k;
            Add(muscles, _headTilt, 0.07f * l);
            Add(muscles, _neckNod, -0.03f * l);
            _nodAge += dt;
            if (l > 0.5f && _clock >= _nodAt)
            {
                _nodAge = 0f;
                _nodAt = _clock + Range(2.2f, 4f);
            }
            var nod = _nodAge > 1f ? 0f : Mathf.Sin(Mathf.Clamp01(_nodAge / 0.6f) * Mathf.PI);
            Add(muscles, _headNod, -0.09f * nod * l);

            // A slow weight shift on top of SignBody's sway, larger at rest.
            var shift = 0.04f * k * Noise(0.05f, 30);
            Add(muscles, _spineLR, shift);
            Add(muscles, _chestLR, -0.5f * shift);
        }
    }
}
