using System;
using System.Diagnostics;
using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// MediaPipe Face Mesh (192×192, 468 landmarks, LiteRT .tflite imported by Unity Inference Engine).
    /// Runs on one face at a time: a square region around a BlazeFace box, enlarged 1.5× as MediaPipe does.
    /// Used for the lips: inner-lip gap (13–14) over eye-corner distance (33–263) is a scale-free "mouth open" value
    /// that BlazeFace's single mouth keypoint can't give.
    /// The crop is turned so the eye line is level (<see cref="Run"/>'s roll), as the Face Mesh paper (Kartynnik et al.
    /// 2019) and MediaPipe's own pipeline do: the model was trained on upright faces. The image goes in as an upright
    /// square √2 larger and is turned inside the graph (GridSample), so no extra shader is needed. On EasyCom glasses
    /// video the lip gap told talking from silent faces with AUC 0.680 vs 0.662 upright, and on heads tilted more
    /// than 10° (a fifth of the frames: the wearer's own head tilts the picture) 0.742 vs 0.673.
    /// </summary>
    public class FaceLandmarker : IDisposable
    {
        public const int UpperInnerLip = 13, LowerInnerLip = 14, RightEyeOuter = 33, LeftEyeOuter = 263;
        public const int MouthRight = 61, MouthLeft = 291;

        const int InputSize = 192;
        const int NumPoints = 468;
        const int PadSize = 272;                       // the upright crop: 192 × √2 rounded up, room for any roll
        const float Pad = PadSize / (float)InputSize;

        readonly Worker _worker;
        readonly BackendType _backendType;
        readonly Tensor<float> _input, _grid;
        readonly float[] _gridData = new float[InputSize * InputSize * 2];
        float _gridRoll = float.NaN;
        readonly RenderTexture _crop;
        readonly Stopwatch _sw = new();

        public double LastInferenceMs { get; private set; }
        /// <summary>Of that, time spent holding Unity's main thread (the rest is waiting), ms.</summary>
        public double LastMainThreadMs { get; private set; }
        readonly Stopwatch _main = new Stopwatch();
        readonly TensorFromTexture _fill = new(PadSize);

        public struct Result
        {
            /// <summary>Landmarks in source-image pixels, origin top-left (z dropped).</summary>
            public Vector2[] Points;
            /// <summary>0..1 — how sure the model is that the region holds a face.</summary>
            public float Presence;
            /// <summary>Inner-lip gap / eye-corner distance: ~0 closed, ~0.3 wide open.</summary>
            public float MouthOpen;
        }

        public FaceLandmarker(ModelAsset modelAsset, BackendType backend)
        {
            _backendType = backend;
            // Expects pixels in [0, 1], NHWC. In front of it: the turn, sampling the padded crop through a grid.
            var model = ModelLoader.Load(modelAsset);
            var graph = new FunctionalGraph();
            var image = graph.AddInput<float>(new TensorShape(1, PadSize, PadSize, 3), "image");
            var grid = graph.AddInput<float>(new TensorShape(1, InputSize, InputSize, 2), "grid");
            var turned = Functional.GridSample(image.Permute(new[] { 0, 3, 1, 2 }), grid, "bilinear", "border", false)
                .Permute(new[] { 0, 2, 3, 1 });
            var outputs = Functional.Forward(model, turned);
            _worker = new Worker(graph.Compile(outputs[0], outputs[1]), backend);
            _input = new Tensor<float>(new TensorShape(1, PadSize, PadSize, 3));
            _grid = new Tensor<float>(new TensorShape(1, InputSize, InputSize, 2));
            _crop = new RenderTexture(PadSize, PadSize, 0, RenderTextureFormat.ARGB32);
        }

        /// <summary>
        /// <paramref name="faceBox"/>: the detected face in source pixels, origin top-left.
        /// <paramref name="roll"/>: the eye line's angle in the picture (radians, from the right eye to the left eye, y down),
        /// from the detector's eye keypoints or the last landmarks; the crop is turned by it so the face is upright.
        /// </summary>
        public async Awaitable<Result> Run(Texture source, Rect faceBox, float roll = 0f)
        {
            _sw.Restart();
            _main.Restart();
            var side = Mathf.Max(faceBox.width, faceBox.height) * 1.5f;
            var padded = side * Pad;
            var roi = new Rect(faceBox.center.x - padded / 2, faceBox.center.y - padded / 2, padded, padded);
            SetGrid(roll);
            // Texture UVs start bottom-left; ToTensor puts the top row first, like the face detector.
            var scale = new Vector2(roi.width / source.width, roi.height / source.height);
            var offset = new Vector2(roi.x / source.width, 1f - roi.yMax / source.height);
            Graphics.Blit(source, _crop, scale, offset);
            _main.Stop();
            // GPU backend: the tensor lives on the GPU, so the copy is a GPU pass (no stall); CPU: read back without one.
            var copied = _backendType == BackendType.CPU && await _fill.Fill(_crop, _input); // (see TensorFromTexture)
            _main.Start();
            if (!copied) TextureConverter.ToTensor(_crop, _input, new TextureTransform().SetTensorLayout(TensorLayout.NHWC));

            _worker.SetInput("image", _input);
            _worker.SetInput("grid", _grid);
            _worker.Schedule();
            _main.Stop();
            using var pts = await (_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync();      // (1,1,1,1404)
            using var presence = await (_worker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync(); // (1,1,1,1)
            _main.Start();
            _sw.Stop();
            LastInferenceMs = _sw.Elapsed.TotalMilliseconds;
            LastMainThreadMs = _main.Elapsed.TotalMilliseconds; // decoding the output comes on top

            var data = pts.DownloadToArray();
            var points = new Vector2[NumPoints];
            float cos = Mathf.Cos(_gridRoll), sin = Mathf.Sin(_gridRoll), k = side / InputSize;
            var c = faceBox.center;
            for (var i = 0; i < NumPoints; i++)
            {
                // model pixels (turned crop) → offset from the centre → turned back into the picture
                float dx = (data[3 * i] - InputSize / 2f) * k, dy = (data[3 * i + 1] - InputSize / 2f) * k;
                points[i] = new Vector2(c.x + cos * dx - sin * dy, c.y + sin * dx + cos * dy);
            }
            var p = presence.DownloadToArray()[0];
            var eyes = Vector2.Distance(points[RightEyeOuter], points[LeftEyeOuter]);
            return new Result
            {
                Points = points,
                Presence = 1f / (1f + Mathf.Exp(-Mathf.Clamp(p, -50f, 50f))),
                MouthOpen = eyes > 1e-3f ? Vector2.Distance(points[UpperInnerLip], points[LowerInnerLip]) / eyes : 0f,
            };
        }

        // The sampling grid: output pixel (u, v) of the 192 crop (−1..1) reads the padded crop at R(roll)·(u, v) / Pad.
        // Rebuilt only when the roll changes by more than about half a degree.
        void SetGrid(float roll)
        {
            if (Mathf.Abs(roll - _gridRoll) < 0.01f) return;
            _gridRoll = roll;
            float cos = Mathf.Cos(roll) / Pad, sin = Mathf.Sin(roll) / Pad;
            for (int j = 0, o = 0; j < InputSize; j++)
            {
                var v = (j + 0.5f) / (InputSize / 2f) - 1f;
                for (var i = 0; i < InputSize; i++, o += 2)
                {
                    var u = (i + 0.5f) / (InputSize / 2f) - 1f;
                    _gridData[o] = cos * u - sin * v;
                    _gridData[o + 1] = sin * u + cos * v;
                }
            }
            _grid.Upload(_gridData);
        }

        /// <summary>The eye line's angle (radians, y down) from landmarks, for the next <see cref="Run"/> on this face.</summary>
        public static float Roll(Vector2[] points)
        {
            var d = points[LeftEyeOuter] - points[RightEyeOuter];
            return Mathf.Atan2(d.y, d.x);
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _input?.Dispose();
            _grid?.Dispose();
            if (_crop != null) _crop.Release();
        }
    }
}
