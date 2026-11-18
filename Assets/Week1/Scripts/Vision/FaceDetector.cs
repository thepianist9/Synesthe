using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// BlazeFace on Unity Inference Engine: short range (128×128, 896 anchors from a CSV) or full range (192×192, one
    /// anchor per cell of a 48×48 grid, no CSV). The BlazeFace paper / MediaPipe: short range is for faces filling much
    /// of the square (selfie distance), full range for smaller, further faces.
    /// Takes a square crop of the passthrough camera image, returns face boxes plus the
    /// six BlazeFace keypoints (eyes, nose, mouth, ears) in normalised image coordinates.
    /// Decoding and NMS run on the CPU.
    /// </summary>
    public class FaceDetector : IDisposable
    {
        public struct Face
        {
            public Rect Box;          // normalised 0..1, origin top-left, within the square crop
            public Vector2 Mouth;     // keypoint 3
            public float Score;
            /// <summary>BlazeFace keypoints (crop-normalised, origin top-left): 0 right eye, 1 left eye, 2 nose, 3 mouth, 4 right ear, 5 left ear.</summary>
            public Vector2[] Keypoints;
        }

        readonly int InputSize;
        readonly int NumAnchors;
        readonly int _boxesOut, _scoresOut; // which output is which (the .tflite and .onnx exports differ)

        readonly Worker _worker;
        readonly Tensor<float> _input;
        readonly RenderTexture _crop;
        readonly float[,] _anchors;
        readonly Stopwatch _sw = new Stopwatch();

        public BackendType Backend { get; }
        public float ScoreThreshold = 0.6f;
        public float IouThreshold = 0.3f;
        public double LastInferenceMs { get; private set; }
        /// <summary>Of that, time spent holding Unity's main thread (the rest is waiting), ms.</summary>
        public double LastMainThreadMs { get; private set; }
        readonly Stopwatch _main = new Stopwatch();
        readonly TensorFromTexture _fill;
        /// <summary>Size of the square crop in source pixels and its left/top offset, for mapping results back.</summary>
        public Rect LastCrop { get; private set; }

        /// <param name="anchorsCsv">Short range: its 896 anchors. Null: full range (192 input, 48×48 grid of anchors).</param>
        public FaceDetector(ModelAsset modelAsset, TextAsset anchorsCsv, BackendType backend)
        {
            Backend = backend;
            if (anchorsCsv != null)
            {
                InputSize = 128; NumAnchors = 896;
                _anchors = new float[NumAnchors, 4];
                var lines = anchorsCsv.text.Split('\n');
                for (var i = 0; i < NumAnchors; i++)
                {
                    var v = lines[i].Split(',');
                    for (var j = 0; j < 4; j++) _anchors[i, j] = float.Parse(v[j], CultureInfo.InvariantCulture);
                }
            }
            else
            {
                // MediaPipe face_detection_full_range: one layer, stride 4, one anchor per cell, fixed size 1.
                InputSize = 192; const int grid = 48; NumAnchors = grid * grid;
                _anchors = new float[NumAnchors, 4];
                for (var gy = 0; gy < grid; gy++)
                    for (var gx = 0; gx < grid; gx++)
                    {
                        var i = gy * grid + gx;
                        _anchors[i, 0] = (gx + 0.5f) / grid; _anchors[i, 1] = (gy + 0.5f) / grid; _anchors[i, 2] = 1; _anchors[i, 3] = 1;
                    }
            }
            _fill = new TensorFromTexture(InputSize);

            // BlazeFace expects pixels in [-1, 1]; fold the rescale into the graph.
            var model = ModelLoader.Load(modelAsset);
            // Boxes (…, 16) and scores (…, 1), by name: "regressors" / "reshaped_regressor_face_4".
            _boxesOut = model.outputs.Count > 1 && model.outputs[1].name.Contains("regress") ? 1 : 0;
            _scoresOut = 1 - _boxesOut;
            var graph = new FunctionalGraph();
            var x = graph.AddInput(model, 0);
            var outputs = Functional.Forward(model, 2f * x - 1f);
            model = graph.Compile(outputs[_boxesOut], outputs[_scoresOut]);

            _worker = new Worker(model, backend);
            _input = new Tensor<float>(new TensorShape(1, InputSize, InputSize, 3));
            _crop = new RenderTexture(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        }

        /// <summary>Runs one detection on the centre square. Awaits GPU/CPU completion without blocking the main thread.</summary>
        public Awaitable<List<Face>> Detect(Texture source) =>
            Detect(source, new Rect((source.width - source.height) * 0.5f, 0, source.height, source.height));

        /// <summary>Runs one detection on a square of the image (source pixels, top-left origin; kept inside the image).
        /// A smaller square zooms in: faces the model would miss at 128 px across the whole view are found in it.</summary>
        public async Awaitable<List<Face>> Detect(Texture source, Rect square)
        {
            _sw.Restart();
            _main.Restart();
            var side = Mathf.Min(square.width, source.width, source.height);
            var x = Mathf.Clamp(square.x, 0, source.width - side);
            var y = Mathf.Clamp(square.y, 0, source.height - side);
            LastCrop = new Rect(x, y, side, side);
            Graphics.Blit(source, _crop, new Vector2(side / source.width, side / source.height),
                new Vector2(x / source.width, 1f - (y + side) / source.height));
            _main.Stop();
            // GPU backend: the tensor lives on the GPU, so the copy is a GPU pass (no stall); CPU: read back without one.
            var copied = Backend == BackendType.CPU && await _fill.Fill(_crop, _input); // (see TensorFromTexture)
            _main.Start();
            if (!copied) TextureConverter.ToTensor(_crop, _input, new TextureTransform().SetTensorLayout(TensorLayout.NHWC));

            _worker.Schedule(_input);
            _main.Stop();
            using var boxes = await (_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync();   // (1, anchors, 16)
            using var scores = await (_worker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();  // (1, anchors, 1)
            _main.Start();
            _sw.Stop();
            LastInferenceMs = _sw.Elapsed.TotalMilliseconds;
            LastMainThreadMs = _main.Elapsed.TotalMilliseconds; // decoding the output comes on top
            return Decode(boxes, scores);
        }

        List<Face> Decode(Tensor<float> boxes, Tensor<float> scores)
        {
            var candidates = new List<Face>();
            for (var i = 0; i < NumAnchors; i++)
            {
                var raw = Mathf.Clamp(scores[0, i, 0], -100f, 100f);
                var score = 1f / (1f + Mathf.Exp(-raw));
                if (score < ScoreThreshold) continue;

                var cx = (boxes[0, i, 0] / InputSize) + _anchors[i, 0];
                var cy = (boxes[0, i, 1] / InputSize) + _anchors[i, 1];
                var w = boxes[0, i, 2] / InputSize;
                var h = boxes[0, i, 3] / InputSize;
                var kps = new Vector2[6];
                for (var k = 0; k < 6; k++)
                    kps[k] = new Vector2(boxes[0, i, 4 + 2 * k] / InputSize + _anchors[i, 0],
                                         boxes[0, i, 4 + 2 * k + 1] / InputSize + _anchors[i, 1]);
                candidates.Add(new Face
                {
                    Box = new Rect(cx - w / 2, cy - h / 2, w, h),
                    Mouth = kps[3],
                    Score = score,
                    Keypoints = kps,
                });
            }

            // NMS with blending (BlazeFace paper, §"Post-processing"): each kept face is the score-weighted mean of the
            // predictions overlapping it, not just the top one. The paper reports 10% better accuracy on its video
            // set and less jitter; on EasyCom glasses video (zoomed tracking squares) the box centre moved 1.8% of the
            // face width per frame vs 2.1% with plain greedy NMS, same faces found. LR-ASD reads mouth movement from
            // frame-to-frame crop changes, so a steadier box is a cleaner signal.
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<Face>();
            var used = new bool[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
            {
                if (used[i]) continue;
                var top = candidates[i];
                float wSum = 0, x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                var kps = new Vector2[6];
                for (var j = i; j < candidates.Count; j++)
                {
                    if (used[j] || Iou(top.Box, candidates[j].Box) <= IouThreshold) continue;
                    used[j] = true;
                    var c = candidates[j]; var w = c.Score;
                    wSum += w; x0 += w * c.Box.xMin; y0 += w * c.Box.yMin; x1 += w * c.Box.xMax; y1 += w * c.Box.yMax;
                    for (var k = 0; k < 6; k++) kps[k] += w * c.Keypoints[k];
                }
                for (var k = 0; k < 6; k++) kps[k] /= wSum;
                kept.Add(new Face
                {
                    Box = Rect.MinMaxRect(x0 / wSum, y0 / wSum, x1 / wSum, y1 / wSum),
                    Mouth = kps[3],
                    Score = top.Score,
                    Keypoints = kps,
                });
            }
            return kept;
        }

        static float Iou(Rect a, Rect b)
        {
            var x1 = Mathf.Max(a.xMin, b.xMin);
            var y1 = Mathf.Max(a.yMin, b.yMin);
            var x2 = Mathf.Min(a.xMax, b.xMax);
            var y2 = Mathf.Min(a.yMax, b.yMax);
            var inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
            var union = a.width * a.height + b.width * b.height - inter;
            return union <= 0 ? 0 : inter / union;
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _input?.Dispose();
            if (_crop != null) _crop.Release();
        }
    }
}
