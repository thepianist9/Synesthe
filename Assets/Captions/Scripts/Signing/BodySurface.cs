using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The front of the character's body (hips, spine, chest, collarbones, neck and head, not the arms) as seen by
    /// someone facing it: a depth map on a 1 cm grid in the character's own space, from its skinned meshes as they
    /// are posed when this is built. <see cref="Behind"/> says how far a point is behind that surface, so a hand
    /// that has gone into the chest or behind the face can be brought out in front of it. Hands beside the body
    /// aren't behind anything: there is no surface there.
    /// </summary>
    public sealed class BodySurface
    {
        const float Cell = 0.01f;
        readonly Transform _root;
        readonly float _x0, _y0;
        readonly int _w, _h;
        readonly float[] _front; // z of the frontmost surface per cell; NegativeInfinity = none

        static readonly HumanBodyBones[] Torso =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck,
            HumanBodyBones.Head, HumanBodyBones.Jaw, HumanBodyBones.LeftEye, HumanBodyBones.RightEye,
            HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
        };

        BodySurface(BodySurface from, Transform root)
        {
            _root = root;
            (_x0, _y0, _w, _h, _front) = (from._x0, from._y0, from._w, from._h, from._front);
        }

        /// <summary>The same surface on another copy of the character (posed alike when this was built).</summary>
        public BodySurface For(Animator other) => new(this, other.transform);

        public BodySurface(Animator animator)
        {
            _root = animator.transform;
            // Each skeleton bone → is it part of the torso? Bones the humanoid doesn't map (face, twist and helper
            // bones) count as their nearest mapped parent.
            var humanoid = new Dictionary<Transform, bool>();
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                if (animator.GetBoneTransform(b) is Transform t) humanoid[t] = System.Array.IndexOf(Torso, b) >= 0;
            bool IsTorso(Transform t)
            {
                for (; t != null; t = t.parent)
                    if (humanoid.TryGetValue(t, out var torso)) return torso;
                return false;
            }

            var tris = new List<Vector3>();
            var baked = new Mesh();
            foreach (var r in animator.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var mesh = r.sharedMesh;
                if (mesh == null || !r.enabled) continue;
                var bones = r.bones;
                var torsoBone = new bool[bones.Length];
                for (var i = 0; i < bones.Length; i++) torsoBone[i] = IsTorso(bones[i]);
                var weights = mesh.boneWeights;
                if (weights.Length != mesh.vertexCount) continue;
                r.BakeMesh(baked, true);
                var v = baked.vertices;
                var keep = new bool[v.Length];
                for (var i = 0; i < v.Length; i++)
                {
                    keep[i] = weights[i].boneIndex0 < bones.Length && torsoBone[weights[i].boneIndex0];
                    v[i] = _root.InverseTransformPoint(r.transform.TransformPoint(v[i]));
                }
                for (var s = 0; s < mesh.subMeshCount; s++)
                {
                    var idx = mesh.GetTriangles(s);
                    for (var i = 0; i + 2 < idx.Length; i += 3)
                        if (keep[idx[i]] && keep[idx[i + 1]] && keep[idx[i + 2]])
                        {
                            tris.Add(v[idx[i]]);
                            tris.Add(v[idx[i + 1]]);
                            tris.Add(v[idx[i + 2]]);
                        }
                }
            }
            Object.DestroyImmediate(baked);
            if (tris.Count == 0) return;

            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            foreach (var p in tris)
            {
                xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
            }
            _x0 = xMin;
            _y0 = yMin;
            _w = Mathf.CeilToInt((xMax - xMin) / Cell) + 1;
            _h = Mathf.CeilToInt((yMax - yMin) / Cell) + 1;
            _front = new float[_w * _h];
            for (var i = 0; i < _front.Length; i++) _front[i] = float.NegativeInfinity;
            for (var i = 0; i < tris.Count; i += 3) Raster(tris[i], tris[i + 1], tris[i + 2]);
        }

        // Every cell centre inside the triangle (seen from the front) keeps the triangle's depth there if it's further forward.
        void Raster(Vector3 a, Vector3 b, Vector3 c)
        {
            var area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-12f) return;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - _x0) / Cell));
            int i1 = Mathf.Min(_w - 1, Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - _x0) / Cell));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - _y0) / Cell));
            int j1 = Mathf.Min(_h - 1, Mathf.CeilToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - _y0) / Cell));
            for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
            {
                float x = _x0 + i * Cell, y = _y0 + j * Cell;
                var wa = ((b.x - x) * (c.y - y) - (c.x - x) * (b.y - y)) / area;
                var wb = ((c.x - x) * (a.y - y) - (a.x - x) * (c.y - y)) / area;
                var wc = 1f - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                var z = wa * a.z + wb * b.z + wc * c.z;
                ref var f = ref _front[j * _w + i];
                if (z > f) f = z;
            }
        }

        /// <summary>
        /// How far (m) the world point is behind the body's front surface plus margin, along the character's forward;
        /// 0 if it's in front of it, or beside the body.
        /// </summary>
        public float Behind(Vector3 world, float margin)
        {
            if (_front == null) return 0f;
            var p = _root.InverseTransformPoint(world);
            var i = Mathf.RoundToInt((p.x - _x0) / Cell);
            var j = Mathf.RoundToInt((p.y - _y0) / Cell);
            if (i < 0 || j < 0 || i >= _w || j >= _h) return 0f;
            var front = _front[j * _w + i];
            return float.IsNegativeInfinity(front) ? 0f : Mathf.Max(0f, front + margin - p.z);
        }

        /// <summary>
        /// How far (m) the world point is in front of the body's front surface (negative: behind it); +infinity beside
        /// the body, where there is no surface to touch.
        /// </summary>
        public float Ahead(Vector3 world)
        {
            if (_front == null) return float.PositiveInfinity;
            var p = _root.InverseTransformPoint(world);
            var i = Mathf.RoundToInt((p.x - _x0) / Cell);
            var j = Mathf.RoundToInt((p.y - _y0) / Cell);
            if (i < 0 || j < 0 || i >= _w || j >= _h) return float.PositiveInfinity;
            var front = _front[j * _w + i];
            return float.IsNegativeInfinity(front) ? float.PositiveInfinity : p.z - front;
        }

        /// <summary>Forward, in world space: the way to move a point to bring it out in front.</summary>
        public Vector3 Forward => _root.forward;

        static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };

        /// <summary>
        /// How far the hand is behind the body: the most any of its points is (wrist and knuckles kept a little in
        /// front, fingertips allowed to touch, as in signs made on the chest or chin).
        /// </summary>
        public float Behind(Animator animator, bool right)
        {
            var side = right ? "Right" : "Left";
            var wrist = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (wrist == null) return 0f;
            var worst = Behind(wrist.position, 0.03f);
            foreach (var finger in Fingers)
            {
                var proximal = Bone(animator, side + finger + "Proximal");
                var intermediate = Bone(animator, side + finger + "Intermediate");
                var distal = Bone(animator, side + finger + "Distal");
                if (proximal != null && finger is "Index" or "Little") worst = Mathf.Max(worst, Behind(proximal.position, 0.02f));
                if (distal == null || intermediate == null) continue;
                // The fingertip: on past the last joint by about that bone's length.
                var tip = distal.position + (distal.position - intermediate.position) * 0.8f;
                worst = Mathf.Max(worst, Behind(tip, 0.005f));
            }
            return worst;
        }

        static Transform Bone(Animator a, string name) =>
            System.Enum.TryParse<HumanBodyBones>(name, out var b) ? a.GetBoneTransform(b) : null;
    }
}
