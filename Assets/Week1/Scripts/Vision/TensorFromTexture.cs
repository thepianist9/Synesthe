using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

namespace Week1Bench
{
    /// <summary>
    /// A small RGBA render texture into an NHWC float tensor (pixels 0..1, top row first), the same as
    /// <c>TextureConverter.ToTensor</c> with an NHWC layout, without stalling the main thread.
    /// For a CPU-backend tensor, ToTensor waits for the GPU right there (15–25 ms a face on a Quest 3, a dropped frame
    /// each time); this asks for the pixels and carries on, and the copy lands a frame or two later.
    /// </summary>
    public sealed class TensorFromTexture
    {
        readonly int _size;
        readonly float[] _data;

        public TensorFromTexture(int size)
        {
            _size = size;
            _data = new float[size * size * 3];
        }

        /// <summary>Reads <paramref name="rt"/> (size × size) into <paramref name="tensor"/> (1, size, size, 3).
        /// False if the GPU couldn't read it back (the tensor keeps its old contents).</summary>
        public async Awaitable<bool> Fill(RenderTexture rt, Tensor<float> tensor)
        {
            var req = await AsyncGPUReadback.RequestAsync(rt, 0, TextureFormat.RGBA32);
            if (req.hasError) return false;
            var rgba = req.GetData<byte>();
            var n = _size;
            const float k = 1f / 255f;
            for (var y = 0; y < n; y++)
            {
                var src = (n - 1 - y) * n * 4; // readback rows start at the bottom; the tensor's start at the top
                var dst = y * n * 3;
                for (var x = 0; x < n; x++, src += 4, dst += 3)
                {
                    _data[dst] = rgba[src] * k;
                    _data[dst + 1] = rgba[src + 1] * k;
                    _data[dst + 2] = rgba[src + 2] * k;
                }
            }
            tensor.Upload(_data);
            return true;
        }
    }
}
