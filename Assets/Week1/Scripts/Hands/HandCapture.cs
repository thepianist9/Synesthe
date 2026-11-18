using System.Collections.Generic;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Reads both hands from Meta hand tracking (OpenXR 26-joint skeleton) and retargets them to
    /// the 21-point MediaPipe hand layout used by the Kaggle ASL datasets.
    /// Positions are expressed in head space (metres) so recordings don't depend on where the
    /// wearer stood.
    /// </summary>
    public class HandCapture : MonoBehaviour
    {
        public OVRHand leftHand, rightHand;
        public OVRSkeleton leftSkeleton, rightSkeleton;
        public Transform head;

        // MediaPipe index → OpenXR hand joint id (OVRSkeleton.BoneId.XRHand_*).
        // Mapped by number: BoneId has many aliased values (Hand_Start = XRHand_Palm = 0 …),
        // so BoneId.ToString() cannot be used to look joints up.
        static readonly int[] MediaPipeToXR =
        {
            1,              // Wrist
            2, 3, 4, 5,     // Thumb metacarpal, proximal, distal, tip
            7, 8, 9, 10,    // Index proximal (MCP), intermediate, distal, tip
            12, 13, 14, 15, // Middle
            17, 18, 19, 20, // Ring
            22, 23, 24, 25, // Little
        };

        // Same mapping for the legacy OVR hand skeleton (OVRSkeleton.BoneId.Hand_*).
        static readonly int[] MediaPipeToOVR =
        {
            0,              // WristRoot
            3, 4, 5, 19,    // Thumb1..3, ThumbTip
            6, 7, 8, 20,    // Index1..3, IndexTip
            9, 10, 11, 21,  // Middle
            12, 13, 14, 22, // Ring
            16, 17, 18, 23, // Pinky1..3, PinkyTip
        };

        public struct HandFrame
        {
            public bool Tracked;
            public bool HighConfidence;
            public Vector3[] Points; // 21, head space
        }

        readonly Dictionary<OVRSkeleton, Transform[]> _maps = new();

        public HandFrame Read(bool left)
        {
            var hand = left ? leftHand : rightHand;
            var skel = left ? leftSkeleton : rightSkeleton;
            var frame = new HandFrame { Points = new Vector3[21] };
            if (hand == null || skel == null || !skel.IsInitialized) return frame;

            frame.Tracked = hand.IsTracked && skel.IsDataValid;
            frame.HighConfidence = frame.Tracked && hand.HandConfidence == OVRHand.TrackingConfidence.High;
            if (!frame.Tracked) return frame;

            var map = MapFor(skel);
            for (var i = 0; i < 21; i++)
            {
                var t = map[i];
                if (t == null) continue;
                frame.Points[i] = head != null ? head.InverseTransformPoint(t.position) : t.position;
            }
            return frame;
        }

        Transform[] MapFor(OVRSkeleton skel)
        {
            if (_maps.TryGetValue(skel, out var map) && map != null) return map;
            map = new Transform[21];
            var byId = new Dictionary<int, Transform>();
            foreach (var b in skel.Bones) byId[(int)b.Id] = b.Transform;
            var type = skel.GetSkeletonType();
            var ids = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight
                ? MediaPipeToXR : MediaPipeToOVR;
            for (var i = 0; i < 21; i++)
                byId.TryGetValue(ids[i], out map[i]);
            if (map[0] == null)
                Debug.LogWarning($"[Bench] Unknown hand skeleton ({skel.GetSkeletonType()}) — joints not mapped.");
            _maps[skel] = map;
            return map;
        }

        public bool LeftPinch => leftHand != null && leftHand.IsTracked && leftHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        public bool RightPinch => rightHand != null && rightHand.IsTracked && rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
    }
}
