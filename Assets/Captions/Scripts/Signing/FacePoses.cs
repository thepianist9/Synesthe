using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// A joint-driven face (MetaHuman): its expressions come from ~875 face joints, not blend shapes. Each of
    /// <see cref="SignPose.FaceChannels"/> is a pose baked in Unreal (face control rig keyed one channel per frame,
    /// see MetaHumanFaceBake): how far each joint moves and turns from rest at full weight. Weights blend linearly,
    /// sparse per channel, so a frame only touches the joints the active channels move.
    /// </summary>
    public sealed class FacePoses : ScriptableObject
    {
        /// <summary>Joint names (unique in the face skeleton).</summary>
        public string[] joints;
        public Vector3[] restPosition;
        public Quaternion[] restRotation;
        /// <summary>Per channel, its entries are [start[c], start[c + 1]) in joint/position/rotation.</summary>
        public int[] start;
        public int[] entryJoint;
        public Vector3[] entryPosition;
        public Quaternion[] entryRotation;

        /// <summary>Drives one face skeleton from channel weights.</summary>
        public sealed class Driver
        {
            readonly FacePoses _poses;
            readonly Transform[] _bones;
            readonly Vector3[] _pos;
            readonly Quaternion[] _rot;
            readonly bool[] _touched;

            public Driver(FacePoses poses, Transform root)
            {
                _poses = poses;
                _bones = new Transform[poses.joints.Length];
                var byName = new System.Collections.Generic.Dictionary<string, Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) byName.TryAdd(t.name, t);
                for (var i = 0; i < _bones.Length; i++) byName.TryGetValue(poses.joints[i], out _bones[i]);
                _pos = new Vector3[_bones.Length];
                _rot = new Quaternion[_bones.Length];
                _touched = new bool[_bones.Length];
            }

            /// <summary>True when the skeleton has the baked joints (the model is the one baked).</summary>
            public bool Valid
            {
                get
                {
                    var found = 0;
                    foreach (var b in _bones) if (b != null) found++;
                    return found > _bones.Length / 2;
                }
            }

            public void Apply(float[] weights)
            {
                var p = _poses;
                System.Array.Copy(p.restPosition, _pos, _pos.Length);
                System.Array.Copy(p.restRotation, _rot, _rot.Length);
                var channels = Mathf.Min(weights.Length, p.start.Length - 1);
                for (var c = 0; c < channels; c++)
                {
                    var w = weights[c];
                    if (w <= 0.001f) continue;
                    for (var e = p.start[c]; e < p.start[c + 1]; e++)
                    {
                        var j = p.entryJoint[e];
                        _pos[j] += w * p.entryPosition[e];
                        _rot[j] = _rot[j] * Quaternion.SlerpUnclamped(Quaternion.identity, p.entryRotation[e], w);
                        _touched[j] = true;
                    }
                }
                // Joints no channel moves stay as they are; any moved last frame go back to rest.
                for (var j = 0; j < _bones.Length; j++)
                {
                    if (!_touched[j] || _bones[j] == null) continue;
                    _bones[j].SetLocalPositionAndRotation(_pos[j], _rot[j]);
                    if (_pos[j] == p.restPosition[j] && _rot[j] == p.restRotation[j]) _touched[j] = false;
                }
            }
        }
    }
}
