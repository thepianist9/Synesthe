using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Keeps the two hands from passing through each other: each hand is capsules (the palm's four spokes and every
    /// finger bone), each as thick as the character's own hand mesh around that bone. Where a capsule of one hand goes
    /// into one of the other, both arms (elbow and shoulder) move their hands apart by the overlap (half each, the
    /// hands keeping their turn), so fists that press together end up touching instead of merged. 
    /// </summary>
    public sealed class HandsApart
    {
        public const float Margin = 0.002f;
        readonly Animator _animator;
        readonly Segment[][] _hands = new Segment[2][];

        struct Segment
        {
            public Transform From, To; // To null: the last bone of a finger, which points on by Length
            public Vector3 TipLocal;   // its tip in From's space (To null)
            public float Radius;
            public Vector3 A => From.position;
            public Vector3 B => To != null ? To.position : From.TransformPoint(TipLocal);
        }

        static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static readonly string[] Phalanges = { "Proximal", "Intermediate", "Distal" };

        public bool Valid { get; }

        public HandsApart(Animator animator)
        {
            _animator = animator;
            Valid = true;
            for (var side = 0; side < 2; side++)
            {
                var s = side == 0 ? "Left" : "Right";
                var hand = Bone(s + "Hand");
                if (hand == null) { Valid = false; return; }
                var list = new List<Segment>();
                foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
                    if (Bone(s + f + "Proximal") is Transform p) list.Add(new Segment { From = hand, To = p });
                foreach (var f in Fingers)
                    for (var k = 0; k < 3; k++)
                    {
                        var bone = Bone(s + f + Phalanges[k]);
                        if (bone == null) continue;
                        var next = k < 2 ? Bone(s + f + Phalanges[k + 1]) : null;
                        var seg = new Segment { From = bone, To = next };
                        if (next == null && bone.parent != null)
                        {
                            // the last bone points on from the one before it, about 0.8 as long
                            var dir = bone.position - bone.parent.position;
                            seg.TipLocal = bone.InverseTransformPoint(bone.position + 0.8f * dir);
                        }
                        list.Add(seg);
                    }
                _hands[side] = list.ToArray();
            }
            MeasureRadii();
        }

        Transform Bone(string name) =>
            System.Enum.TryParse<HumanBodyBones>(name, out var b) ? _animator.GetBoneTransform(b) : null;

        // Each capsule's radius: the 30th-percentile distance from its axis of the hand mesh's vertices nearest to it.
        void MeasureRadii()
        {
            var distances = new List<float>[2][];
            for (var side = 0; side < 2; side++)
            {
                distances[side] = new List<float>[_hands[side].Length];
                for (var i = 0; i < distances[side].Length; i++) distances[side][i] = new List<float>();
            }
            var baked = new Mesh();
            foreach (var r in _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null) continue;
                // which hand (0 left, 1 right, -1 neither) each of the mesh's bones belongs to
                var bones = r.bones;
                var sideOf = new int[bones.Length];
                var any = false;
                for (var b = 0; b < bones.Length; b++)
                {
                    sideOf[b] = -1;
                    for (var t = bones[b]; t != null; t = t.parent)
                        if (t == _hands[0][0].From) { sideOf[b] = 0; break; }
                        else if (t == _hands[1][0].From) { sideOf[b] = 1; break; }
                    any |= sideOf[b] >= 0;
                }
                if (!any) continue;
                var weights = r.sharedMesh.boneWeights; // strongest bone first
                r.BakeMesh(baked, true);
                var m = r.transform.localToWorldMatrix;
                var verts = baked.vertices;
                for (var v = 0; v < verts.Length && v < weights.Length; v++)
                {
                    var bi = weights[v].boneIndex0;
                    var side = bi < sideOf.Length ? sideOf[bi] : -1;
                    if (side < 0) continue;
                    var p = m.MultiplyPoint3x4(verts[v]);
                    int best = -1; float bestD = 0.05f;
                    for (var i = 0; i < _hands[side].Length; i++)
                    {
                        var d = Vector3.Distance(p, Closest(_hands[side][i].A, _hands[side][i].B, p));
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    if (best >= 0) distances[side][best].Add(bestD);
                }
            }
            if (Application.isPlaying) Object.Destroy(baked); else Object.DestroyImmediate(baked);
            for (var side = 0; side < 2; side++)
                for (var i = 0; i < _hands[side].Length; i++)
                {
                    var d = distances[side][i];
                    d.Sort();
                    _hands[side][i].Radius = d.Count >= 8 ? d[d.Count * 3 / 10] : (i < 4 ? 0.012f : 0.008f); // 30th percentile: a palm is wider than thick, and its edges are far from any spoke
                }
        }

        /// <summary>Each capsule's bone and radius (mm), for checking.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (var side = 0; side < 2; side++)
                foreach (var g in _hands[side])
                    sb.Append(g.From.name).Append(g.To != null ? ">" + g.To.name : "").Append(' ')
                      .Append((g.Radius * 1000f).ToString("0.0")).Append('\n');
            return sb.ToString();
        }

        /// <summary>The deepest overlap between the hands now (m, ≤ 0 = apart), for checking.</summary>
        public float Overlap()
        {
            var depth = float.NegativeInfinity;
            foreach (var l in _hands[0])
                foreach (var r in _hands[1])
                {
                    var (pl, pr) = ClosestBetween(l.A, l.B, r.A, r.B);
                    depth = Mathf.Max(depth, l.Radius + r.Radius - Vector3.Distance(pl, pr));
                }
            return depth;
        }

        /// <summary>Moves the hands apart where they overlap. Up to three passes (moving one pair can press another).</summary>
        /// <summary>For checking (null = off): each pass's overlap and direction, and the deepest overlap seen before /
        /// after <see cref="Apply"/>.</summary>
        public static System.Text.StringBuilder Log;
        public static float WorstBefore, WorstAfter;

        public void Apply()
        {
            if (!Valid) return;
            if (Log == null) { ApplyPasses(); return; }
            WorstBefore = Mathf.Max(WorstBefore, Overlap());
            ApplyPasses();
            WorstAfter = Mathf.Max(WorstAfter, Overlap());
        }

        void ApplyPasses()
        {
            var lh = _hands[0][0].From.position;
            var rh = _hands[1][0].From.position;
            if ((lh - rh).sqrMagnitude > 0.3f * 0.3f) return; // hands this far apart can't touch
            for (var pass = 0; pass < 6; pass++)
            {
                // the way apart: every overlapping pair's normal weighted by its overlap (the deepest pair alone flips
                // about between passes when fingers interleave), leaning on the line between the palms
                var depth = 0f;
                var sum = Vector3.zero;
                foreach (var l in _hands[0])
                    foreach (var r in _hands[1])
                    {
                        var (pl, pr) = ClosestBetween(l.A, l.B, r.A, r.B);
                        var gap = pr - pl;
                        var d = gap.magnitude;
                        var over = l.Radius + r.Radius + Margin - d;
                        if (over <= 0f) continue;
                        depth = Mathf.Max(depth, over);
                        if (d > 1e-5f) sum += over * gap / d;
                    }
                if (depth <= 0f) return;
                var palms = Palm(1) - Palm(0);
                var normal = (sum.normalized + 0.5f * palms.normalized).normalized;
                if (Log != null && Log.Length < 4000) Log.Append(pass).Append(' ').Append((depth * 1000f).ToString("0.0")).Append(' ').Append(normal.ToString("F2")).Append('\n');
                Push(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, -normal * (0.5f * depth + 0.001f));
                Push(HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, normal * (0.5f * depth + 0.001f));
            }
        }

        /// <summary>Brings the hands together until they touch (skin to skin, with the check's margin), by
        /// <paramref name="weight"/> of the gap: for frames where the signer's hands touch (pose files' "handsTouch").</summary>
        public void Touch(float weight)
        {
            if (!Valid || weight <= 0f) return;
            for (var pass = 0; pass < 4; pass++)
            {
                var gap = float.MaxValue;
                var dir = Vector3.zero;
                foreach (var l in _hands[0])
                    foreach (var r in _hands[1])
                    {
                        var (pl, pr) = ClosestBetween(l.A, l.B, r.A, r.B);
                        var d = Vector3.Distance(pl, pr);
                        var g = d - l.Radius - r.Radius;
                        if (g < gap && d > 1e-5f) { gap = g; dir = (pr - pl) / d; }
                    }
                var close = (gap - Margin) * weight;
                if (close <= 0.0005f) return;
                Push(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, dir * (0.5f * close));
                Push(HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, -dir * (0.5f * close));
            }
        }

        Vector3 Palm(int side)
        {
            var c = Vector3.zero;
            for (var i = 0; i < 4; i++) c += _hands[side][i].A + _hands[side][i].B;
            return c / 8f;
        }

        // Moves the hand by `move` with the arm's two bones (elbow bend for the reach, shoulder for the direction), the
        // hand keeping its turn in the world: hands that meet end-on along the forearms can only part by the elbows bending.
        void Push(HumanBodyBones upperArm, HumanBodyBones hand, Vector3 move)
        {
            var upper = _animator.GetBoneTransform(upperArm);
            var lower = _animator.GetBoneTransform(upperArm == HumanBodyBones.LeftUpperArm ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var h = _animator.GetBoneTransform(hand);
            Vector3 s = upper.position, e = lower.position, w = h.position, target = w + move;
            var handRot = h.rotation;
            float a = Vector3.Distance(s, e), b = Vector3.Distance(e, w);
            var d = Mathf.Clamp(Vector3.Distance(s, target), Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            var n = Vector3.Cross(e - s, w - e);
            if (n.sqrMagnitude < 1e-10f) n = Vector3.Cross(e - s, _animator.transform.up);
            if (n.sqrMagnitude > 1e-10f)
            {
                var cur = Vector3.Angle(s - e, w - e);
                var want = Mathf.Acos(Mathf.Clamp((a * a + b * b - d * d) / (2f * a * b), -1f, 1f)) * Mathf.Rad2Deg;
                lower.rotation = Quaternion.AngleAxis(cur - want, n.normalized) * lower.rotation;
            }
            upper.rotation = Quaternion.FromToRotation(h.position - s, target - s) * upper.rotation;
            h.rotation = handRot;
        }

        static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-10f));
            return a + t * ab;
        }

        // Closest points of two segments.
        static (Vector3, Vector3) ClosestBetween(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a < 1e-10f && e < 1e-10f) return (p1, p2);
            if (a < 1e-10f) { s = 0; t = Mathf.Clamp01(f / e); }
            else
            {
                var c = Vector3.Dot(d1, r);
                if (e < 1e-10f) { t = 0; s = Mathf.Clamp01(-c / a); }
                else
                {
                    var b = Vector3.Dot(d1, d2);
                    var denom = a * e - b * b;
                    s = denom > 1e-10f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1) { t = 1; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            return (p1 + s * d1, p2 + t * d2);
        }
    }
}
