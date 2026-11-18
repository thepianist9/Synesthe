using Meta.XR;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Gives glass shaders something to refract: the passthrough camera image plus the transform that maps a
    /// world position to its spot in that image. Shaders read the globals
    ///   _PtCamTex, _PtWorldToCam, _PtIntrinsics (fx, fy, cx, cy in viewport units), _PtCamEnabled.
    /// The camera is the one PersonTracker already runs, so this costs no extra capture.
    /// Note: the camera image is not colour-matched to the passthrough view, so glass keeps it subtle.
    /// </summary>
    public class GlassBackdrop : MonoBehaviour
    {
        public PassthroughCameraAccess cameraAccess;
        [Tooltip("Turn off to fall back to plain frosted glass.")]
        public bool useCamera = true;

        static readonly int Tex = Shader.PropertyToID("_PtCamTex");
        static readonly int WorldToCam = Shader.PropertyToID("_PtWorldToCam");
        static readonly int Intrinsics = Shader.PropertyToID("_PtIntrinsics");
        static readonly int Enabled = Shader.PropertyToID("_PtCamEnabled");

        void LateUpdate()
        {
            var ok = useCamera && cameraAccess != null && cameraAccess.enabled && cameraAccess.IsPlaying;
            Shader.SetGlobalFloat(Enabled, ok ? 1f : 0f);
            if (!ok) return;

            var tex = cameraAccess.GetTexture();
            if (tex == null) { Shader.SetGlobalFloat(Enabled, 0f); return; }
            var pose = cameraAccess.GetCameraPose();

            // Viewport mapping, measured through the SDK's own projection so crop and lens offset are included:
            // viewport = (x/z · f + c) per axis, in camera space (z forward).
            Vector2 Vp(Vector3 camSpace) => cameraAccess.WorldToViewportPoint(pose.position + pose.rotation * camSpace, pose);
            var c = Vp(new Vector3(0, 0, 1));
            var fx = (Vp(new Vector3(0.1f, 0, 1)).x - c.x) / 0.1f;
            var fy = (Vp(new Vector3(0, 0.1f, 1)).y - c.y) / 0.1f;

            Shader.SetGlobalTexture(Tex, tex);
            Shader.SetGlobalMatrix(WorldToCam, Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one).inverse);
            Shader.SetGlobalVector(Intrinsics, new Vector4(fx, fy, c.x, c.y));
        }

        void OnDisable() => Shader.SetGlobalFloat(Enabled, 0f);
    }
}
