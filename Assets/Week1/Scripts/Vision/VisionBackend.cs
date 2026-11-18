using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Where the live vision models run.
    /// <para><see cref="Type"/> (face mesh, speaker model) is the CPU by default. Alone, they are faster there, and
    /// on the GPU (2026-11-07, and again in a 6-minute stress run 2026-11-08) the speaker model ran a third as often
    /// (49 vs 132 runs a minute) and words given to a person fell to 0–4%. <c>-e vgpu 1</c> moves them to the GPU (test).</para>
    /// <para><see cref="Detector"/> (BlazeFace face detector) runs on the GPU by default. It is the heaviest CPU model,
    /// and Sentis' CPU backend spreads it over every job worker, which starved speech-to-text during non-stop speech.
    /// On the GPU it leaves those cores to speech and the speaker model. <c>-e vdet cpu</c> puts it back (test).</para>
    /// </summary>
    public static class VisionBackend
    {
        static BackendType? _type, _detector, _mesh;

        /// <summary>Face mesh (lips). Same as <see cref="Type"/> unless <c>-e vmesh gpu|cpu</c> (or
        /// <see cref="MeshOverride"/>) puts it elsewhere: the mesh alone on the GPU leaves the CPU to speech-to-text
        /// and the speaker model, so mesh tracking at the camera rate can be afforded (test, 2026-11-09).</summary>
        public static BackendType Mesh { get { Read(); return _mesh ?? _type.Value; } }

        /// <summary>Face mesh and speaker model.</summary>
        public static BackendType Type { get { Read(); return _type.Value; } }

        /// <summary>Face detector.</summary>
        public static BackendType Detector { get { Read(); return _detector.Value; } }

        static void Read()
        {
            if (_type != null) return;
            _type = BackendType.CPU;
            _detector = BackendType.GPUCompute;
            // Editor tests (HarnessRunner -harnessMeshBackend gpu|cpu).
            var env = System.Environment.GetEnvironmentVariable("CAPTIONS_VMESH");
            if (env == "gpu") _mesh = BackendType.GPUCompute;
            else if (env == "cpu") _mesh = BackendType.CPU;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                // Face mesh on the GPU on the headset by default (with mesh tracking, 2026-11-09): -e vmesh cpu puts it back.
                _mesh = BackendType.GPUCompute;
                if (intent.Call<string>("getStringExtra", "vgpu") == "1") _type = BackendType.GPUCompute;
                var det = intent.Call<string>("getStringExtra", "vdet");
                var mesh = intent.Call<string>("getStringExtra", "vmesh");
                if (mesh == "cpu") _mesh = BackendType.CPU;
                else if (mesh == "gpu") _mesh = BackendType.GPUCompute;
                if (det == "cpu") _detector = BackendType.CPU;
                else if (det == "gpu") _detector = BackendType.GPUCompute;
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] vision backend extra: {e.Message}"); }
#endif
        }
    }
}
