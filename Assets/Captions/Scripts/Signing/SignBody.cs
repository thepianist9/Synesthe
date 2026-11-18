using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The signing avatar's body between and under the signs, so it doesn't stand like a statue with moving arms:
    ///   • it breathes (chest and shoulders, about 14 breaths a minute)
    ///   • it sways and shifts its weight a little, slowly and never the same way twice
    ///   • while signing it leans in towards the viewer, more as the arms reach up and out, and the shoulders turn
    ///     with whichever hand is working harder
    ///   • yes/no questions bring the head forward, wh-questions tilt it, held through the sentence (ASL grammar,
    ///     with the brows in <see cref="SignFace"/>)
    ///   • each sign lands with a small beat (head dips, chest and shoulders follow), and a sentence ends with a nod
    ///   • the torso answers how fast the hands move: brisk signing brings a little more of the body with it
    /// The head counters most of the sway, so the avatar keeps looking at the viewer. Added onto the target muscles
    /// before the spring (<see cref="SignPose.Follow"/>), so all of it eases in and out.
    /// </summary>
    public sealed class SignBody
    {
        float _clock, _engaged, _beatAge = 9f, _nodAge = 9f, _energy, _beatStrength = 1f;
        float[] _last;
        readonly float _seed;

        public SignBody(int seed = 0) => _seed = (seed & 0xffff) * 0.37f;

        static int _spineFB = -2, _spineLR, _spineTwist, _chestFB, _chestLR, _chestTwist, _upperFB, _neckNod, _neckTilt,
            _neckTurn, _headNod, _headTilt, _headTurn, _lShoulder, _rShoulder, _lArm, _rArm, _lArmFB, _rArmFB, _lElbow, _rElbow;

        static void Resolve()
        {
            if (_spineFB != -2) return;
            _spineFB = SignPose.Index("Spine Front-Back");
            _spineLR = SignPose.Index("Spine Left-Right");
            _spineTwist = SignPose.Index("Spine Twist Left-Right");
            _chestFB = SignPose.Index("Chest Front-Back");
            _chestLR = SignPose.Index("Chest Left-Right");
            _chestTwist = SignPose.Index("Chest Twist Left-Right");
            _upperFB = SignPose.Index("UpperChest Front-Back");
            _neckNod = SignPose.Index("Neck Nod Down-Up");
            _neckTilt = SignPose.Index("Neck Tilt Left-Right");
            _neckTurn = SignPose.Index("Neck Turn Left-Right");
            _headNod = SignPose.Index("Head Nod Down-Up");
            _headTilt = SignPose.Index("Head Tilt Left-Right");
            _headTurn = SignPose.Index("Head Turn Left-Right");
            _lShoulder = SignPose.Index("Left Shoulder Down-Up");
            _rShoulder = SignPose.Index("Right Shoulder Down-Up");
            _lArm = SignPose.Index("Left Arm Down-Up");
            _rArm = SignPose.Index("Right Arm Down-Up");
            _lArmFB = SignPose.Index("Left Arm Front-Back");
            _rArmFB = SignPose.Index("Right Arm Front-Back");
            _lElbow = SignPose.Index("Left Forearm Stretch");
            _rElbow = SignPose.Index("Right Forearm Stretch");
        }

        static void Add(float[] m, int i, float v)
        {
            if (i >= 0) m[i] += v;
        }

        static float Get(float[] m, int i) => i >= 0 ? m[i] : 0f;

        /// <summary>A sign is starting: a small beat of the head and chest as it lands.</summary>
        public void Beat(float strength = 1f) { _beatAge = 0f; _beatStrength = strength; }

        /// <summary>A sentence has ended: a slower, deeper nod.</summary>
        public void Nod() => _nodAge = 0f;

        // A pulse that rises and falls back over about 3×peak seconds (0 → 1 at peak → 0).
        static float Pulse(float age, float peak) => age > 6f * peak ? 0f : age / peak * Mathf.Exp(1f - age / peak);

        // Smooth noise in -1…1, a new value about every 1/hz seconds.
        float Noise(float hz, float channel) => 2f * Mathf.PerlinNoise(_seed + channel * 17.3f, _clock * hz) - 1f;

        /// <summary>
        /// Adds this frame's body motion onto the target muscles. signing: a sign or fingerspelling is playing;
        /// raise/lower: the question brows (0–1), which bring the head forward or tilt it; ready: the ready pose, to
        /// tell how far each arm is reaching from it.
        /// </summary>
        public void Add(float[] muscles, float dt, bool signing, float raise, float lower, float[] ready)
        {
            Resolve();
            _clock += dt;
            _beatAge += dt;
            _nodAge += dt;
            _engaged = Mathf.MoveTowards(_engaged, signing ? 1f : 0f, dt / 0.6f);

            // Breathing: in, the chest opens and the shoulders lift a touch.
            var breath = Mathf.Sin(_clock * 2f * Mathf.PI / 4.3f);
            Add(muscles, _upperFB, -0.04f * breath);
            Add(muscles, _lShoulder, 0.03f * breath);
            Add(muscles, _rShoulder, 0.03f * breath);

            // Sway and weight shift: slow, small, from the hips up; the head keeps most of it level.
            var side = 0.1f * Noise(0.13f, 0);
            var turn = 0.08f * Noise(0.11f, 1);
            var lean = 0.05f * Noise(0.09f, 2);
            Add(muscles, _spineLR, side);
            Add(muscles, _chestLR, 0.5f * side);
            Add(muscles, _headTilt, -0.9f * side);
            Add(muscles, _spineTwist, turn);
            // Rocking forward and back only at rest: while signing the lean below takes over (more would bring the
            // chest into hands held on it).
            Add(muscles, _spineFB, (1f - _engaged) * lean);
            Add(muscles, _headNod, 0.6f * (1f - _engaged) * lean);
            Add(muscles, _headTurn, -0.6f * turn + 0.03f * Noise(0.2f, 3));

            // Signing: lean in, more as the arms reach up and out; the shoulders turn with the busier hand (its
            // shoulder comes forward). How far each arm is from ready, in muscle units.
            float Reach(int arm, int armFB, int elbow) => ready == null ? 0f
                : Mathf.Abs(Get(muscles, arm) - Get(ready, arm)) + Mathf.Abs(Get(muscles, armFB) - Get(ready, armFB))
                  + 0.5f * Mathf.Abs(Get(muscles, elbow) - Get(ready, elbow));
            var right = Reach(_rArm, _rArmFB, _rElbow);
            var left = Reach(_lArm, _lArmFB, _lElbow);
            var into = _engaged * (0.04f + 0.05f * Mathf.Clamp01(Mathf.Max(right, left)));
            Add(muscles, _spineFB, into);
            Add(muscles, _chestFB, 0.5f * into);
            Add(muscles, _headNod, 0.6f * into); // eyes stay on the viewer
            var twist = _engaged * Mathf.Clamp(0.12f * (left - right), -0.09f, 0.09f);
            Add(muscles, _chestTwist, twist);
            Add(muscles, _headTurn, -0.7f * twist);

            // How fast the hands are moving (arm muscles per second, smoothed): the chest rises into brisk signing
            // and the head moves with it, rather than the arms working on a still body.
            if (_last == null) _last = (float[])muscles.Clone();
            var moved = Mathf.Abs(Get(muscles, _rArm) - Get(_last, _rArm)) + Mathf.Abs(Get(muscles, _rArmFB) - Get(_last, _rArmFB))
                        + Mathf.Abs(Get(muscles, _lArm) - Get(_last, _lArm)) + Mathf.Abs(Get(muscles, _lArmFB) - Get(_last, _lArmFB));
            System.Array.Copy(muscles, _last, muscles.Length);
            if (dt > 0f) _energy = Mathf.Lerp(_energy, Mathf.Clamp01(moved / dt / 3f), 1f - Mathf.Exp(-dt * 6f));
            var e = _engaged * _energy;
            Add(muscles, _upperFB, -0.05f * e);
            Add(muscles, _lShoulder, 0.04f * e);
            Add(muscles, _rShoulder, 0.04f * e);
            Add(muscles, _headTilt, 0.05f * e * Noise(0.7f, 4));

            // The beat as a sign lands, and the nod that closes a sentence.
            var beat = _beatStrength * Pulse(_beatAge, 0.09f);
            Add(muscles, _headNod, -0.1f * beat);
            Add(muscles, _neckNod, -0.04f * beat);
            Add(muscles, _spineFB, 0.025f * beat);
            Add(muscles, _lShoulder, 0.025f * beat);
            Add(muscles, _rShoulder, 0.025f * beat);
            var nod = Pulse(_nodAge, 0.16f);
            Add(muscles, _headNod, -0.22f * nod);
            Add(muscles, _neckNod, -0.08f * nod);
            Add(muscles, _spineFB, 0.03f * nod);

            // Questions: yes/no brings the head (and a little of the body) forward; wh- tilts it and leans in.
            Add(muscles, _spineFB, 0.05f * raise + 0.03f * lower);
            Add(muscles, _neckNod, -0.2f * raise - 0.08f * lower);
            Add(muscles, _headNod, -0.1f * raise);
            Add(muscles, _neckTilt, 0.16f * lower);
        }
    }
}
