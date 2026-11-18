using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Body postures and the ASL fingerspelling alphabet as humanoid muscle values (see <see cref="SignPose"/>).
    /// Fingerspelling is the fallback for every word the sign dictionary doesn't have yet (names, rare words), so the
    /// avatar can sign anything from day one. The handshapes here were modelled from ASL alphabet references, not by
    /// a native signer: have a Deaf signer check them before showing them to anyone.
    /// Right hand dominant: letters are formed beside the right shoulder, palm towards the viewer.
    /// </summary>
    public static class Handshapes
    {
        // One finger: curl of the knuckle joint and of the two outer joints (0 = straight, 1 = fully bent), and spread
        // (-1 = against the next finger, 0 = neutral, 1 = splayed).
        public readonly struct Finger
        {
            public readonly float Base, Tip, Spread;
            public Finger(float baseCurl, float tipCurl, float spread = -0.4f) { Base = baseCurl; Tip = tipCurl; Spread = spread; }
        }

        // Thumb: Out = spread (-1 = pressed against the index, 1 = sticking out to the side); Across = swings over
        // the palm (0 = in the hand's plane, 1 = across the palm); Curl bends its two outer joints.
        // Letters only use these where no HandFit is available: with one, the thumb is fitted to its ThumbSpot.
        public readonly struct Thumb
        {
            public readonly float Out, Across, Curl;
            public Thumb(float outward, float across, float curl) { Out = outward; Across = across; Curl = curl; }
        }

        public readonly struct Shape
        {
            public readonly Thumb Thumb;
            public readonly Finger Index, Middle, Ring, Little;
            /// <summary>Wrist: Bend tips the hand forward (-) or back (+); Turn rotates the forearm (palm towards the
            /// viewer = 0, palm sideways = 1); Point drops the whole hand to point sideways/down (G, H, P, Q).</summary>
            public readonly float Bend, Turn, Point;
            public Shape(Thumb thumb, Finger index, Finger middle, Finger ring, Finger little, float bend = 0, float turn = 0, float point = 0)
            {
                Thumb = thumb; Index = index; Middle = middle; Ring = ring; Little = little; Bend = bend; Turn = turn; Point = point;
            }
        }

        static readonly Finger Straight = new(0, 0), Fist = new(1.15f, 1f), Curved = new(0.45f, 0.55f), Hook = new(0.1f, 1f);
        static readonly Finger Splayed = new(0, 0, 0.8f), Bent = new(1, 0.15f);
        static Finger Spread(Finger f, float s) => new(f.Base, f.Tip, s);

        // ASL one-handed alphabet. J and Z move: see Motion().
        static readonly Dictionary<char, Shape> Letters = new()
        {
            ['A'] = new(new(-0.8f, 0f, 0.1f), Fist, Fist, Fist, Fist),
            ['B'] = new(new(-0.6f, 1f, 0.8f), Straight, Straight, Straight, Straight),
            ['C'] = new(new(0.3f, 0.4f, 0.3f), Curved, Curved, Curved, Curved, turn: 0.8f),
            ['D'] = new(new(-0.3f, 0.8f, 0.5f), Straight, new(0.75f, 0.6f), new(0.8f, 0.7f), new(0.85f, 0.8f)),
            ['E'] = new(new(-0.6f, 1f, 0.9f), new(0.6f, 1f), new(0.6f, 1f), new(0.6f, 1f), new(0.6f, 1f)),
            ['F'] = new(new(0f, 0.6f, 0.4f), new(0.6f, 0.5f), Splayed, Splayed, Splayed),
            ['G'] = new(new(0.2f, 0f, 0f), Straight, Fist, Fist, Fist, turn: 1f, point: 1f),
            ['H'] = new(new(-0.4f, 0.8f, 0.6f), Straight, Straight, Fist, Fist, turn: 1f, point: 1f),
            ['I'] = new(new(-0.4f, 0.9f, 0.7f), Fist, Fist, Fist, Straight),
            ['J'] = new(new(-0.4f, 0.9f, 0.7f), Fist, Fist, Fist, Straight),
            ['K'] = new(new(0.2f, 0.6f, 0.2f), Spread(Straight, 0.2f), new(0.35f, 0, 0.4f), Fist, Fist),
            ['L'] = new(new(1f, 0f, 0f), Straight, Fist, Fist, Fist),
            ['M'] = new(new(-0.5f, 1f, 0.7f), new(0.85f, 0.7f), new(0.85f, 0.7f), new(0.85f, 0.7f), Fist),
            ['N'] = new(new(-0.5f, 0.9f, 0.6f), new(0.85f, 0.7f), new(0.85f, 0.7f), Fist, Fist),
            ['O'] = new(new(0f, 0.7f, 0.5f), new(0.55f, 0.6f), new(0.55f, 0.6f), new(0.55f, 0.6f), new(0.6f, 0.6f), turn: 0.7f),
            ['P'] = new(new(0.2f, 0.6f, 0.2f), Spread(Straight, 0.2f), new(0.35f, 0, 0.4f), Fist, Fist, bend: -0.6f, point: 0.6f),
            ['Q'] = new(new(0.2f, 0f, 0f), Straight, Fist, Fist, Fist, bend: -0.8f, point: 0.6f),
            ['R'] = new(new(-0.4f, 0.9f, 0.7f), Spread(Straight, -1f), Spread(Straight, -1f), Fist, Fist),
            ['S'] = new(new(-0.3f, 1f, 0.5f), Fist, Fist, Fist, Fist),
            ['T'] = new(new(-0.2f, 0.7f, 0.3f), new(0.8f, 0.8f), Fist, Fist, Fist),
            ['U'] = new(new(-0.4f, 0.9f, 0.7f), Straight, Straight, Fist, Fist),
            ['V'] = new(new(-0.4f, 0.9f, 0.7f), Spread(Straight, 0.8f), Spread(Straight, 0.6f), Fist, Fist),
            ['W'] = new(new(-0.4f, 0.9f, 0.8f), Spread(Straight, 0.8f), Straight, Spread(Straight, 0.8f), Fist),
            ['X'] = new(new(-0.4f, 0.8f, 0.6f), Hook, Fist, Fist, Fist),
            ['Y'] = new(new(1f, -0.3f, 0f), Fist, Fist, Fist, Spread(Straight, 1f)),
            ['Z'] = new(new(-0.4f, 0.8f, 0.6f), Straight, Fist, Fist, Fist),
            ['0'] = new(new(0f, 0.7f, 0.5f), new(0.55f, 0.6f), new(0.55f, 0.6f), new(0.55f, 0.6f), new(0.6f, 0.6f), turn: 0.7f),
            ['1'] = new(new(-0.4f, 0.9f, 0.7f), Straight, Fist, Fist, Fist, turn: -0.6f),
            ['2'] = new(new(-0.4f, 0.9f, 0.7f), Spread(Straight, 0.8f), Spread(Straight, 0.6f), Fist, Fist, turn: -0.6f),
            ['3'] = new(new(1f, 0f, 0f), Spread(Straight, 0.8f), Spread(Straight, 0.6f), Fist, Fist, turn: -0.6f),
            ['4'] = new(new(-0.6f, 1f, 0.8f), Splayed, Splayed, Splayed, Splayed, turn: -0.6f),
            ['5'] = new(new(1f, 0f, 0f), Splayed, Splayed, Splayed, Splayed, turn: -0.6f),
            ['6'] = new(new(0f, 0.8f, 0.4f), Splayed, Splayed, Splayed, new(0.7f, 0.5f), turn: -0.6f),
            ['7'] = new(new(0f, 0.8f, 0.4f), Splayed, Splayed, new(0.7f, 0.5f), Splayed, turn: -0.6f),
            ['8'] = new(new(0f, 0.7f, 0.4f), Splayed, new(0.7f, 0.5f), Splayed, Splayed, turn: -0.6f),
            ['9'] = new(new(0f, 0.6f, 0.4f), new(0.7f, 0.5f), Splayed, Splayed, Splayed, turn: -0.6f),
        };

        /// <summary>Where the thumb tip goes. Thumb muscles mean different things on different rigs, so
        /// <see cref="HandFit"/> searches for the thumb pose that puts the tip here, on the character in use.</summary>
        public enum ThumbSpot { Free, Out, Side, Front, Under, Between, Palm, Curve, Parallel, IndexTip, MiddleTip, RingTip, LittleTip }

        static readonly Dictionary<char, ThumbSpot> Spots = new()
        {
            ['A'] = ThumbSpot.Side, ['B'] = ThumbSpot.Palm, ['C'] = ThumbSpot.Curve, ['D'] = ThumbSpot.MiddleTip,
            ['E'] = ThumbSpot.Under, ['F'] = ThumbSpot.IndexTip, ['G'] = ThumbSpot.Parallel, ['H'] = ThumbSpot.Front,
            ['I'] = ThumbSpot.Front, ['J'] = ThumbSpot.Front, ['K'] = ThumbSpot.Between, ['L'] = ThumbSpot.Out,
            ['M'] = ThumbSpot.Under, ['N'] = ThumbSpot.Under, ['O'] = ThumbSpot.IndexTip, ['P'] = ThumbSpot.Between,
            ['Q'] = ThumbSpot.Parallel, ['R'] = ThumbSpot.Front, ['S'] = ThumbSpot.Front, ['T'] = ThumbSpot.Between,
            ['U'] = ThumbSpot.Front, ['V'] = ThumbSpot.Front, ['W'] = ThumbSpot.LittleTip, ['X'] = ThumbSpot.Front,
            ['Y'] = ThumbSpot.Out, ['Z'] = ThumbSpot.Front,
            ['0'] = ThumbSpot.IndexTip, ['1'] = ThumbSpot.Front, ['2'] = ThumbSpot.Front, ['3'] = ThumbSpot.Out,
            ['4'] = ThumbSpot.Palm, ['5'] = ThumbSpot.Out, ['6'] = ThumbSpot.LittleTip, ['7'] = ThumbSpot.RingTip,
            ['8'] = ThumbSpot.MiddleTip, ['9'] = ThumbSpot.IndexTip,
        };

        public static ThumbSpot SpotOf(char c) => Spots.TryGetValue(char.ToUpperInvariant(c), out var s) ? s : ThumbSpot.Free;

        // Handshapes signs use that the alphabet has no letter for (named as in ASL-LEX). "Flat" fingers bend at the
        // knuckle only; "curved" ones at every joint, as around a ball.
        static readonly Dictionary<string, (Shape shape, ThumbSpot spot)> Named = new()
        {
            ["openB"] = (new(new(0.2f, 0f, 0f), Straight, Straight, Straight, Straight), ThumbSpot.Parallel),
            ["flatB"] = (new(new(0.2f, 0f, 0f), Bent, Bent, Bent, Bent), ThumbSpot.Parallel),
            ["flat5"] = (new(new(1f, 0f, 0f), Spread(Bent, 0.8f), Spread(Bent, 0.6f), Spread(Bent, 0.6f), Spread(Bent, 0.8f)), ThumbSpot.Out),
            ["curved5"] = (new(new(1f, 0f, 0.4f), Spread(Curved, 0.8f), Spread(Curved, 0.6f), Spread(Curved, 0.6f), Spread(Curved, 0.8f)), ThumbSpot.Curve),
            ["flat1"] = (new(new(-0.4f, 0.9f, 0.7f), Bent, Fist, Fist, Fist), ThumbSpot.Front),
            ["babyO"] = (new(new(0f, 0.7f, 0.5f), new(0.55f, 0.6f), Fist, Fist, Fist), ThumbSpot.IndexTip),
            // NO: index and middle together, thumb out, then both snap down onto the thumb tip
            ["openU"] = (new(new(1f, 0f, 0f), Straight, Straight, Fist, Fist), ThumbSpot.Out),
            ["closedU"] = (new(new(0f, 0.7f, 0.5f), new(0.6f, 0.6f), new(0.6f, 0.6f), Fist, Fist), ThumbSpot.IndexTip),
            ["flatO"] = (new(new(0f, 0.7f, 0.3f), new(0.75f, 0.15f), new(0.75f, 0.15f), new(0.75f, 0.15f), new(0.8f, 0.15f)), ThumbSpot.IndexTip),
            ["curvedV"] = (new(new(-0.4f, 0.9f, 0.7f), Spread(Curved, 0.8f), Spread(Curved, 0.6f), Fist, Fist), ThumbSpot.Front),
            ["flatV"] = (new(new(-0.4f, 0.9f, 0.7f), Spread(Bent, 0.8f), Spread(Bent, 0.6f), Fist, Fist), ThumbSpot.Front),
            ["flatH"] = (new(new(-0.4f, 0.9f, 0.7f), Bent, Bent, Fist, Fist), ThumbSpot.Front),
            ["curvedH"] = (new(new(-0.4f, 0.9f, 0.7f), Curved, Curved, Fist, Fist), ThumbSpot.Front),
            ["ILY"] = (new(new(1f, -0.3f, 0f), Straight, Fist, Fist, Spread(Straight, 1f)), ThumbSpot.Out),
            ["open8"] = (new(new(1f, 0f, 0f), Splayed, new(0.7f, 0.1f, 0.4f), Splayed, Splayed), ThumbSpot.Out),
        };

        /// <summary>Every handshape <see cref="SetFingers"/> knows: the letters and digits, then the named ones.</summary>
        public static IEnumerable<string> Names
        {
            get
            {
                foreach (var c in Letters.Keys) if (c != 'J' && c != 'Z') yield return c.ToString();
                foreach (var n in Named.Keys) yield return n;
            }
        }

        public static ThumbSpot SpotOf(string name) =>
            Named.TryGetValue(name, out var n) ? n.spot : name.Length == 1 ? SpotOf(name[0]) : ThumbSpot.Free;

        /// <summary>Shapes the fingers and thumb of <paramref name="side"/> as handshape <paramref name="name"/> (a letter,
        /// digit or one of the named shapes), leaving the wrist as it is. False if there is no such handshape.</summary>
        /// <summary>Trial finger bends per handshape (index, middle, ring, little: knuckle and tip curl), e.g. measured on
        /// real hands (tools/signs/interhand_shapes.py). Spread and thumb stay as defined here. Empty unless an import
        /// trial fills it (SignPreview -signFingerTry), so the app's shapes don't change until the values are written in.</summary>
        public static readonly Dictionary<string, (float baseCurl, float tipCurl)[]> FingerTry = new();

        public static bool SetFingers(float[] m, string side, string name)
        {
            Shape s;
            if (Named.TryGetValue(name, out var n)) s = n.shape;
            else if (name.Length != 1 || !Letters.TryGetValue(char.ToUpperInvariant(name[0]), out s)) return false;
            if (FingerTry.TryGetValue(name, out var t) && t.Length == 4)
            {
                Finger F(Finger f, int i) => new(t[i].baseCurl, t[i].tipCurl, f.Spread);
                s = new Shape(s.Thumb, F(s.Index, 0), F(s.Middle, 1), F(s.Ring, 2), F(s.Little, 3), s.Bend, s.Turn, s.Point);
            }
            Fingers(m, side, s);
            return true;
        }

        public static bool Has(char c) => Letters.ContainsKey(char.ToUpperInvariant(c));

        /// <summary>Hands relaxed at the sides.</summary>
        public static float[] Rest()
        {
            var m = new float[SignPose.Muscles];
            Set(m, "Left Arm Down-Up", -0.75f);
            Set(m, "Right Arm Down-Up", -0.75f);
            Set(m, "Left Forearm Stretch", 0.7f);
            Set(m, "Right Forearm Stretch", 0.7f);
            Set(m, "Left Arm Front-Back", 0.2f);
            Set(m, "Right Arm Front-Back", 0.2f);
            SetHand(m, "Left", new Shape(new(0f, 0.2f, 0.3f), new(0.2f, 0.25f), new(0.25f, 0.3f), new(0.3f, 0.3f), new(0.35f, 0.3f)));
            SetHand(m, "Right", new Shape(new(0f, 0.2f, 0.3f), new(0.2f, 0.25f), new(0.25f, 0.3f), new(0.3f, 0.3f), new(0.35f, 0.3f)));
            return m;
        }

        /// <summary>Signing posture: the left hand rests in front of the waist, the right hand is raised beside the
        /// shoulder, ready to fingerspell.</summary>
        public static float[] Ready()
        {
            var m = Rest();
            // Found by search (SignPreview): hand ~20 cm out and ~20 cm in front of the right shoulder, fingers up,
            // palm towards the viewer.
            Set(m, "Right Arm Down-Up", -0.6f);
            Set(m, "Right Arm Front-Back", 0f);
            Set(m, "Right Arm Twist In-Out", 0.4f);
            Set(m, "Right Forearm Stretch", -0.6f);
            Set(m, "Right Forearm Twist In-Out", -1f);
            Set(m, "Left Forearm Stretch", 0.2f);
            Set(m, "Left Arm Front-Back", 0.4f);
            return m;
        }

        /// <summary>Between signs: hands relaxed low, elbows a little bent, as a signer waits between sentences. (The
        /// ready pose's raised open hand, held there, reads as a wave or HELLO.)</summary>
        public static float[] Neutral()
        {
            var m = Rest();
            foreach (var side in new[] { "Left", "Right" })
            {
                Set(m, side + " Forearm Stretch", 0.1f);
                Set(m, side + " Arm Front-Back", 0.45f);
            }
            return m;
        }

        /// <summary>The right hand forming <paramref name="c"/> (a letter or digit) in signing position. Unknown
        /// characters give the ready pose with a loose hand.</summary>
        public static float[] Spelling(char c) => Spelling(c, Ready());

        /// <summary>Same, from a given signing posture (e.g. one fitted to the character by <see cref="HandFit"/>).</summary>
        public static float[] Spelling(char c, float[] ready)
        {
            var m = (float[])ready.Clone();
            if (Letters.TryGetValue(char.ToUpperInvariant(c), out var s)) SetHand(m, "Right", s);
            return m;
        }

        /// <summary>J and Z are drawn in the air: extra muscle offsets at progress t (0–1) through the letter.</summary>
        public static void Motion(char c, float t, float[] m)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'J': // little finger hooks down and towards the body
                    Add(m, "Right Forearm Twist In-Out", -0.9f * Mathf.SmoothStep(0, 1, t));
                    Add(m, "Right Hand Down-Up", -0.4f * Mathf.Sin(t * Mathf.PI));
                    break;
                case 'Z': // index draws a Z: across, diagonal down-back, across
                    var x = t < 1 / 3f ? t * 3 : t < 2 / 3f ? 1 - (t - 1 / 3f) * 3 : (t - 2 / 3f) * 3;
                    var y = t < 1 / 3f ? 0 : t < 2 / 3f ? (t - 1 / 3f) * 3 : 1;
                    Add(m, "Right Arm Front-Back", -0.25f * x);
                    Add(m, "Right Arm Down-Up", -0.12f * y);
                    break;
            }
        }

        static void SetHand(float[] m, string side, Shape s)
        {
            Fingers(m, side, s);
            Add(m, $"{side} Hand Down-Up", s.Bend);
            Add(m, $"{side} Forearm Twist In-Out", s.Turn * 0.8f);
            Add(m, $"{side} Hand In-Out", s.Point * 0.6f);
        }

        static void Fingers(float[] m, string side, Shape s)
        {
            // Unity finger muscles: +1 = stretched straight, -1 = curled in.
            void F(string name, Finger f)
            {
                Set(m, $"{side} {name} 1 Stretched", 1f - 2f * f.Base);
                Set(m, $"{side} {name} 2 Stretched", 1f - 2f * f.Tip);
                Set(m, $"{side} {name} 3 Stretched", 1f - 2f * f.Tip);
                Set(m, $"{side} {name} Spread", f.Spread);
            }
            F("Index", s.Index);
            F("Middle", s.Middle);
            F("Ring", s.Ring);
            F("Little", s.Little);
            Set(m, $"{side} Thumb Spread", s.Thumb.Out);
            Set(m, $"{side} Thumb 1 Stretched", -s.Thumb.Across);
            Set(m, $"{side} Thumb 2 Stretched", 1f - 2f * s.Thumb.Curl);
            Set(m, $"{side} Thumb 3 Stretched", 1f - 2f * s.Thumb.Curl);
        }

        static readonly Dictionary<string, int> _index = new();

        public static int MuscleIndex(string name) => I(name);

        static int I(string name)
        {
            if (!_index.TryGetValue(name, out var i)) _index[name] = i = SignPose.Index(name);
            return i;
        }

        static void Set(float[] m, string name, float v) { var i = I(name); if (i >= 0) m[i] = v; }
        static void Add(float[] m, string name, float v) { var i = I(name); if (i >= 0) m[i] += v; }
    }
}
