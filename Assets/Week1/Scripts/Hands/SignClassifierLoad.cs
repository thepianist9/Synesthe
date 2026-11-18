using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Stand-in for the future ASL phrase classifier, used only to put realistic load on the
    /// device in Test 5. Same input/output shape we plan for the real model:
    /// 60 frames × 2 hands × 21 joints × xyz → 46 sign classes, through a 2×256 MLP.
    /// Weights are random — the output is meaningless, only the cost matters.
    /// </summary>
    public class SignClassifierLoad : IDisposable
    {
        public const int Frames = 60, Classes = 46;
        const int InputDim = Frames * 2 * 21 * 3, Hidden = 256;

        readonly Worker _worker;
        readonly Tensor<float> _input;

        public SignClassifierLoad(BackendType backend = BackendType.CPU)
        {
            var rng = new System.Random(7);
            FunctionalTensor Weights(int rows, int cols)
            {
                var w = new float[rows * cols];
                var scale = 1f / Mathf.Sqrt(rows);
                for (var i = 0; i < w.Length; i++) w[i] = (float)(rng.NextDouble() * 2 - 1) * scale;
                return Functional.Constant(new TensorShape(rows, cols), w);
            }

            var graph = new FunctionalGraph();
            var x = graph.AddInput<float>(new TensorShape(1, InputDim));
            var h1 = Functional.Relu(Functional.MatMul(x, Weights(InputDim, Hidden)));
            var h2 = Functional.Relu(Functional.MatMul(h1, Weights(Hidden, Hidden)));
            var logits = Functional.MatMul(h2, Weights(Hidden, Classes));
            var model = graph.Compile(Functional.Softmax(logits, 1));

            _worker = new Worker(model, backend);
            _input = new Tensor<float>(new TensorShape(1, InputDim));
        }

        public async Awaitable<int> Classify(float[] window)
        {
            if (window != null && window.Length == InputDim) _input.Upload(window);
            _worker.Schedule(_input);
            using var probs = await (_worker.PeekOutput() as Tensor<float>).ReadbackAndCloneAsync();
            var best = 0;
            for (var i = 1; i < Classes; i++) if (probs[0, i] > probs[0, best]) best = i;
            return best;
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _input?.Dispose();
        }
    }
}
