using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Fits the hand poses to the character in use. Muscle space is shared by every humanoid, but thumbs and forearm
    /// twist are not: each rig's thumb bones point their own way, so the same thumb numbers tuck the thumb on one
    /// character and stick it out on another. So instead of fixed thumb numbers, each letter says where the thumb
    /// tip goes (<see cref="Handshapes.ThumbSpot"/>: beside the index, across the fingers, under the fingertips,
    /// touching a fingertip, …) and this searches the thumb's muscles for the pose that puts it there, on this
    /// character. Likewise the signing posture's forearm twist is searched so the palm faces the viewer.
    /// Runs on a pose-only copy of the character, once per letter (about 200 poses, a few ms).
    /// </summary>
    public sealed class HandFit
    {
        readonly SignPose _pose;
        readonly Transform _root, _wrist, _thumbTip;
        readonly Transform[] _mcp = new Transform[4], _pip = new Transform[4], _dip = new Transform[4], _tip = new Transform[4];
        readonly int _thumb1, _thumbSpread, _thumb2, _thumb3;

        public HandFit(SignPose pose)
        {
            _pose = pose;
            var a = pose.Animator;
            _root = a.transform;
            _wrist = a.GetBoneTransform(HumanBodyBones.RightHand);
            var fingers = new[]
            {
                (HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal),
                (HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal),
                (HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal),
                (HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal),
            };
            for (var i = 0; i < 4; i++)
            {
                _mcp[i] = a.GetBoneTransform(fingers[i].Item1);
                _pip[i] = a.GetBoneTransform(fingers[i].Item2);
                _dip[i] = a.GetBoneTransform(fingers[i].Item3);
                _tip[i] = End(_dip[i]);
            }
            _thumbTip = End(a.GetBoneTransform(HumanBodyBones.RightThumbDistal));
            _thumb1 = SignPose.Index("Right Thumb 1 Stretched");
            _thumbSpread = SignPose.Index("Right Thumb Spread");
            _thumb2 = SignPose.Index("Right Thumb 2 Stretched");
            _thumb3 = SignPose.Index("Right Thumb 3 Stretched");
        }

        // The last bone of a chain (a fingertip end bone if the rig has one).
        static Transform End(Transform t)
        {
            while (t.childCount > 0) t = t.GetChild(0);
            return t;
        }

        // Where a joint is (a fingertip without an end bone: one more segment past the last joint).
        Vector3 TipOf(int finger) => _tip[finger] != _dip[finger] ? _tip[finger].position
            : _dip[finger].position + (_dip[finger].position - _pip[finger].position) * 0.8f;

        Vector3 ThumbTip => _thumbTip.position;
        float Spacing => Vector3.Distance(_mcp[0].position, _mcp[1].position); // one finger's width
        Vector3 Radial => (_mcp[0].position - _mcp[3].position).normalized;   // towards the thumb side
        // Palm side of a right hand: little→index crossed with wrist→fingers.
        Vector3 Palm => Vector3.Cross(_mcp[3].position - _mcp[0].position, _mcp[1].position - _wrist.position).normalized;

        /// <summary>The signing posture with the forearm turned so the right palm faces the viewer, fingers up.</summary>
        public float[] Ready(float[] ready)
        {
            // Only the forearm turns: the upper arm (where the hand is) stays as the posture put it.
            var m = (float[])ready.Clone();
            var twist = SignPose.Index("Right Forearm Twist In-Out");
            float best = float.NegativeInfinity, bestT = m[twist];
            for (var t = -1f; t <= 1.001f; t += 0.05f)
            {
                m[twist] = t;
                _pose.Apply(m);
                var up = (_mcp[1].position - _wrist.position).normalized;
                var score = Vector3.Dot(Palm, _root.forward) + 0.5f * Vector3.Dot(up, _root.up);
                if (score > best) { best = score; bestT = t; }
            }
            m[twist] = bestT;
            return m;
        }

        /// <summary>Set the right thumb's muscles in <paramref name="m"/> (a full pose with the fingers already
        /// shaped) so the thumb tip lands on <paramref name="spot"/>.</summary>
        public void Thumb(float[] m, Handshapes.ThumbSpot spot)
        {
            if (spot == Handshapes.ThumbSpot.Free) return;
            // Coarse grid, then two finer passes around the best: thumb base swing, spread, and the outer joints' bend.
            float b1 = 0, bs = 0, bc = 0, best = float.PositiveInfinity;
            void Try(float t1, float sp, float c)
            {
                m[_thumb1] = Mathf.Clamp(t1, -1, 1);
                m[_thumbSpread] = Mathf.Clamp(sp, -1, 1);
                m[_thumb2] = m[_thumb3] = Mathf.Clamp(c, -1, 1);
                _pose.Apply(m);
                var cost = Cost(spot);
                if (cost < best) { best = cost; b1 = m[_thumb1]; bs = m[_thumbSpread]; bc = m[_thumb2]; }
            }
            for (var t1 = -1f; t1 <= 1.001f; t1 += 0.5f)
            for (var sp = -1f; sp <= 1.001f; sp += 0.5f)
            for (var c = -1f; c <= 1.001f; c += 0.5f)
                Try(t1, sp, c);
            foreach (var step in new[] { 0.25f, 0.125f })
            {
                float c1 = b1, cs = bs, cc = bc;
                for (var i = -1; i <= 1; i++)
                for (var j = -1; j <= 1; j++)
                for (var k = -1; k <= 1; k++)
                    Try(c1 + i * step, cs + j * step, cc + k * step);
            }
            m[_thumb1] = b1;
            m[_thumbSpread] = bs;
            m[_thumb2] = m[_thumb3] = bc;
        }

        float Cost(Handshapes.ThumbSpot spot)
        {
            var w = Spacing;
            var palm = Palm;
            var tip = ThumbTip;
            Vector3 target;
            switch (spot)
            {
                case Handshapes.ThumbSpot.Out: // straight out to the side, in the palm's plane (L, Y, 3, 5)
                    var len = Vector3.Distance(_wrist.position, _mcp[0].position);
                    target = _mcp[0].position + Radial * len * 0.9f - (_mcp[1].position - _wrist.position).normalized * w;
                    break;
                case Handshapes.ThumbSpot.Side: // up along the side of the curled index (A)
                    target = Vector3.Lerp(_mcp[0].position, _pip[0].position, 0.7f) + Radial * w * 0.7f;
                    break;
                case Handshapes.ThumbSpot.Front: // across the front of the curled fingers (S, and holding fingers down)
                    target = Vector3.Lerp(_pip[1].position, _pip[2].position, 0.3f) + palm * w * 0.9f;
                    break;
                case Handshapes.ThumbSpot.Under: // under the bent fingertips (E, M, N)
                    target = Vector3.Lerp(TipOf(0), TipOf(1), 0.5f) + palm * w * 0.3f
                             - (_mcp[1].position - _wrist.position).normalized * w * 0.6f;
                    break;
                case Handshapes.ThumbSpot.Between: // poking up between index and middle (K, P, T)
                    target = Vector3.Lerp(_pip[0].position, _pip[1].position, 0.5f) + palm * w * 0.5f;
                    break;
                case Handshapes.ThumbSpot.Palm: // folded across the palm (B, 4)
                    target = Vector3.Lerp(_mcp[2].position, _wrist.position, 0.3f) + palm * w * 0.6f;
                    break;
                case Handshapes.ThumbSpot.Curve: // opposite the curved fingers, leaving a gap (C)
                    target = Vector3.Lerp(_mcp[0].position, TipOf(0), 0.5f) + palm * w * 2.2f;
                    break;
                case Handshapes.ThumbSpot.Parallel: // alongside the pointing index, on its palm side (G, Q)
                    target = _pip[0].position + palm * w * 0.9f;
                    break;
                case Handshapes.ThumbSpot.IndexTip: target = TipOf(0); break;
                case Handshapes.ThumbSpot.MiddleTip: target = TipOf(1); break;
                case Handshapes.ThumbSpot.RingTip: target = TipOf(2); break;
                case Handshapes.ThumbSpot.LittleTip: target = TipOf(3); break;
                default: return 0;
            }
            var cost = (tip - target).sqrMagnitude;
            // Never behind the hand: a thumb tip on the back of the hand is a broken thumb, however close.
            var behind = Vector3.Dot(tip - _mcp[1].position, palm);
            if (behind < -w * 0.3f) cost += (behind + w * 0.3f) * (behind + w * 0.3f) * 4f;
            return cost;
        }
    }
}
