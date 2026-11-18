using System;
using System.Threading.Tasks;
using Unity.InferenceEngine;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// A face's identity as 128 numbers (SFace, OpenCV Zoo, Apache-2.0): two pictures of one person give vectors that
    /// point the same way (cosine ~0.7–0.8), two people ~0.25. The face is first turned and scaled so the eyes, nose
    /// and mouth land where the model expects them (<see cref="Align"/>).
    /// EasyCom offline: the nearest known person by this embedding is right 99% of the time, even 30 s apart.
    /// </summary>
    public sealed class FaceEmbedder : IDisposable
    {
        public const int Size = 112, Dim = 128;
        readonly Worker _worker;
        readonly Tensor<float> _input = new(new TensorShape(1, 3, Size, Size));

        // Where SFace wants right eye, left eye, nose tip and mouth centre in its 112×112 input (top-left origin).
        static readonly Vector2[] Template =
        {
            new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f), new(56.1396f, 92.2848f),
        };

        public FaceEmbedder(ModelAsset model, BackendType backend = BackendType.CPU)
        {
            _worker = new Worker(ModelLoader.Load(model), backend);
        }

        /// <summary>Embedding of an aligned face: planar RGB 0–255 (3×112×112). Unit length.</summary>
        public async Task<float[]> Run(float[] rgbPlanar)
        {
            _input.Upload(rgbPlanar);
            _worker.SetInput(0, _input);
            _worker.Schedule();
            using var output = await (_worker.PeekOutput() as Tensor<float>).ReadbackAndCloneAsync();
            var e = output.DownloadToArray();
            double n = 0;
            foreach (var v in e) n += v * v;
            var inv = (float)(1.0 / Math.Sqrt(n + 1e-12));
            for (var i = 0; i < e.Length; i++) e[i] *= inv;
            return e;
        }

        /// <summary>
        /// Similarity (rotation, uniform scale, shift) taking the model's 112×112 input to picture points: least squares
        /// over right eye, left eye, nose and mouth (picture pixels, top-left origin). picture = (a·x − b·y + tx, b·x + a·y + ty).
        /// </summary>
        public static (float a, float b, float tx, float ty) Align(Vector2 rightEye, Vector2 leftEye, Vector2 nose, Vector2 mouth)
        {
            Span<Vector2> dst = stackalloc Vector2[] { rightEye, leftEye, nose, mouth };
            Vector2 ms = default, md = default;
            for (var i = 0; i < 4; i++) { ms += Template[i]; md += dst[i]; }
            ms /= 4f; md /= 4f;
            float sxx = 0, sxy = 0, ss = 0;
            for (var i = 0; i < 4; i++)
            {
                var s = Template[i] - ms; var d = dst[i] - md;
                sxx += s.x * d.x + s.y * d.y;   // dot
                sxy += s.x * d.y - s.y * d.x;   // cross
                ss += s.sqrMagnitude;
            }
            var a = sxx / ss; var b = sxy / ss;
            return (a, b, md.x - (a * ms.x - b * ms.y), md.y - (b * ms.x + a * ms.y));
        }

        public static float Cosine(float[] x, float[] y)
        {
            float s = 0;
            for (var i = 0; i < x.Length; i++) s += x[i] * y[i];
            return s;
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _input.Dispose();
        }
    }
}
