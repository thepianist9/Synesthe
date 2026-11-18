using System;
using Unity.InferenceEngine;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// LR-ASD (Liao et al., IJCV 2025, the successor of Light-ASD; MIT licence, TalkSet weights): a model trained to
    /// tell from a face and the sound whether that face is the one talking. Trained on real conversations, it separates
    /// talking from laughing, smiling and nodding far better than lip-gap rules.
    /// Exported (tools/export_lr_asd.py) in two parts, so every face frame is encoded only once:
    ///   <see cref="Encode"/>: a 0.48 s chunk — 12 grey face crops (112×112, 25 fps) and 48 MFCC frames (13 coefficients,
    ///     100 per second) of the same stretch → 128 audio + 128 visual numbers per frame. Nearly all of the work.
    ///   <see cref="Score"/>: the last 1 s of those (25 frames) → a speaking logit per frame (&gt; 0 = speaking). Tiny.
    /// Offline on 6 AVA clips this picks the talking face among several 94% of the time, vs 87% for Light-ASD re-run
    /// on 0.6 s windows (what the app did before), for ~80% of its work.
    /// </summary>
    public sealed class ActiveSpeakerModel : IDisposable
    {
        public const int Chunk = 12, Context = 25, Features = 128, CropSize = 112, AudioFrames = 4 * Chunk, Coefficients = 13;
        /// <summary>Samples needed for <see cref="AudioFrames"/> MFCC frames (25 ms windows every 10 ms).</summary>
        public const int Samples = (AudioFrames - 1) * Mfcc.Step + Mfcc.Window;

        readonly Worker _encoder, _backend;
        readonly Tensor<float> _mfcc = new(new TensorShape(1, AudioFrames, Coefficients));
        readonly Tensor<float> _faces = new(new TensorShape(1, Chunk, CropSize, CropSize));
        readonly Tensor<float> _audio = new(new TensorShape(1, Context, Features));
        readonly Tensor<float> _visual = new(new TensorShape(1, Context, Features));
        readonly System.Diagnostics.Stopwatch _sw = new();
        public double LastMs { get; private set; }   // last Encode + Score

        public ActiveSpeakerModel(ModelAsset encoder, ModelAsset backend)
        {
            _encoder = new Worker(ModelLoader.Load(encoder), VisionBackend.Type);
            _backend = new Worker(ModelLoader.Load(backend), VisionBackend.Type);
        }

        ActiveSpeakerModel(Worker encoder, Worker backend) { _encoder = encoder; _backend = backend; }

        /// <summary>
        /// Loads the two parts one step per frame (model loading and worker creation must run on the main thread), so
        /// startup never stalls a single frame for the whole load.
        /// </summary>
        public static System.Collections.IEnumerator LoadStaged(ModelAsset encoder, ModelAsset backend, Action<ActiveSpeakerModel> done)
        {
            var e = ModelLoader.Load(encoder);
            yield return null;
            var ew = new Worker(e, VisionBackend.Type);
            yield return null;
            var b = ModelLoader.Load(backend);
            yield return null;
            done(new ActiveSpeakerModel(ew, new Worker(b, VisionBackend.Type)));
        }

        /// <param name="samples">16 kHz audio, <see cref="Samples"/> long, scaled like 16-bit PCM (±32768).</param>
        /// <param name="faces">Chunk × 112 × 112 grey values 0–255, top row first.</param>
        /// <returns>Per frame: audio and visual features, each Chunk × 128.</returns>
        public async Awaitable<(float[] audio, float[] visual)> Encode(float[] samples, float[] faces)
        {
            _sw.Restart();
            _mfcc.Upload(Mfcc.Compute(samples));
            _faces.Upload(faces);
            _encoder.SetInput("mfcc", _mfcc);
            _encoder.SetInput("faces", _faces);
            _encoder.Schedule();
            // Take both outputs before awaiting: the worker may be disposed meanwhile (leaving Play mode).
            var audioOut = _encoder.PeekOutput("audio") as Tensor<float>;
            var visualOut = _encoder.PeekOutput("visual") as Tensor<float>;
            using var audio = await audioOut.ReadbackAndCloneAsync();
            using var visual = await visualOut.ReadbackAndCloneAsync();
            return (audio.DownloadToArray(), visual.DownloadToArray());
        }

        /// <param name="audio">Context × 128 audio features, oldest frame first.</param>
        /// <param name="visual">Context × 128 visual features of the same frames.</param>
        /// <returns>A speaking logit per frame.</returns>
        public async Awaitable<float[]> Score(float[] audio, float[] visual)
        {
            _audio.Upload(audio);
            _visual.Upload(visual);
            _backend.SetInput("audio", _audio);
            _backend.SetInput("visual", _visual);
            _backend.Schedule();
            using var output = await (_backend.PeekOutput() as Tensor<float>).ReadbackAndCloneAsync();
            _sw.Stop();
            LastMs = _sw.Elapsed.TotalMilliseconds;
            return output.DownloadToArray();
        }

        public void Dispose()
        {
            _encoder?.Dispose();
            _backend?.Dispose();
            _mfcc.Dispose();
            _faces.Dispose();
            _audio.Dispose();
            _visual.Dispose();
        }
    }

    /// <summary>
    /// MFCCs exactly as python_speech_features.mfcc computes them with LR-ASD's settings (the model was trained on
    /// these): pre-emphasis 0.97, 25 ms rectangular windows every 10 ms, 512-point FFT power spectrum, 26 mel filters,
    /// log, orthonormal DCT-II → 13 coefficients, cepstral lifter 22, coefficient 0 replaced by log frame energy.
    /// </summary>
    public static class Mfcc
    {
        public const int Window = 400, Step = 160, Nfft = 512, Filters = 26, Coefficients = 13, Rate = 16000;
        static float[,] _bank;
        static float[] _lift;

        public static float[] Compute(float[] signal)
        {
            Init();
            var n = signal.Length;
            var frames = n <= Window ? 1 : 1 + (int)Math.Ceiling((n - Window) / (double)Step);
            var emph = new float[n];
            emph[0] = signal[0];
            for (var i = 1; i < n; i++) emph[i] = signal[i] - 0.97f * signal[i - 1];

            var outp = new float[frames * Coefficients];
            var re = new double[Nfft];
            var im = new double[Nfft];
            var power = new double[Nfft / 2 + 1];
            var logMel = new double[Filters];
            for (var f = 0; f < frames; f++)
            {
                Array.Clear(re, 0, Nfft);
                Array.Clear(im, 0, Nfft);
                for (var i = 0; i < Window; i++)
                {
                    var j = f * Step + i;
                    re[i] = j < n ? emph[j] : 0; // the last frame is zero-padded
                }
                Fft(re, im);
                double energy = 0;
                for (var k = 0; k <= Nfft / 2; k++)
                {
                    power[k] = (re[k] * re[k] + im[k] * im[k]) / Nfft;
                    energy += power[k];
                }
                for (var m = 0; m < Filters; m++)
                {
                    double e = 0;
                    for (var k = 0; k <= Nfft / 2; k++) e += power[k] * _bank[m, k];
                    logMel[m] = Math.Log(e == 0 ? double.Epsilon : e);
                }
                for (var c = 0; c < Coefficients; c++)
                {
                    double s = 0;
                    for (var m = 0; m < Filters; m++) s += logMel[m] * Math.Cos(Math.PI * c * (2 * m + 1) / (2.0 * Filters));
                    s *= c == 0 ? Math.Sqrt(1.0 / Filters) : Math.Sqrt(2.0 / Filters);
                    outp[f * Coefficients + c] = (float)(s * _lift[c]);
                }
                outp[f * Coefficients] = (float)Math.Log(energy == 0 ? double.Epsilon : energy);
            }
            return outp;
        }

        static void Init()
        {
            if (_bank != null) return;
            static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700.0);
            static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595.0) - 1);
            var bins = new int[Filters + 2];
            var high = HzToMel(Rate / 2.0);
            for (var i = 0; i < bins.Length; i++)
                bins[i] = (int)Math.Floor((Nfft + 1) * MelToHz(high * i / (Filters + 1)) / Rate);
            _bank = new float[Filters, Nfft / 2 + 1];
            for (var j = 0; j < Filters; j++)
            {
                for (var i = bins[j]; i < bins[j + 1]; i++) _bank[j, i] = (i - bins[j]) / (float)(bins[j + 1] - bins[j]);
                for (var i = bins[j + 1]; i < bins[j + 2]; i++) _bank[j, i] = (bins[j + 2] - i) / (float)(bins[j + 2] - bins[j + 1]);
            }
            _lift = new float[Coefficients];
            for (var c = 0; c < Coefficients; c++) _lift[c] = 1 + 11f * Mathf.Sin(Mathf.PI * c / 22f);
        }

        // In-place radix-2 FFT.
        static void Fft(double[] re, double[] im)
        {
            var n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (var len = 2; len <= n; len <<= 1)
            {
                var ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (var i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (var k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        var tr = re[b] * cr - im[b] * ci;
                        var ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        var ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
