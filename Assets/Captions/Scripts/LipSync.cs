using System.Collections.Generic;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Loudness of the mic over the last few seconds, in 20 ms frames on the Time.time clock (same as the tracker).
    /// </summary>
    public class AudioEnvelope
    {
        const int FrameSamples = MicStream.OutputRate / 50;
        const float KeepSeconds = 12f; // a finished sentence is matched to faces up to ~8 s back
        readonly List<(float t, float db)> _frames = new();
        readonly float[] _carry = new float[FrameSamples];
        int _carryCount;

        /// <summary>Main thread: 16 kHz samples, <paramref name="realtime"/> = Time.realtimeSinceStartup of the last sample.</summary>
        public void Push(float[] samples, double realtime)
        {
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            var end = realtime - offset; // last sample, Time.time clock
            for (var i = 0; i < samples.Length; i++)
            {
                _carry[_carryCount++] = samples[i];
                if (_carryCount < FrameSamples) continue;
                _carryCount = 0;
                double e = 0;
                foreach (var s in _carry) e += s * s;
                var db = 10f * Mathf.Log10((float)(e / FrameSamples) + 1e-10f);
                _frames.Add(((float)(end - (samples.Length - 1 - i) / (double)MicStream.OutputRate), Mathf.Max(db, -80f)));
            }
            var cut = 0;
            while (cut < _frames.Count && end - _frames[cut].t > KeepSeconds) cut++;
            if (cut > 0) _frames.RemoveRange(0, cut);
        }

        /// <summary>All kept 20 ms frames, oldest first (Time.time, dB).</summary>
        public IReadOnlyList<(float t, float db)> Frames => _frames;

        /// <summary>Mean loudness (dB) of the frames within ±40 ms of <paramref name="t"/>; NaN if none.</summary>
        public float At(float t)
        {
            float sum = 0; var n = 0;
            for (var i = _frames.Count - 1; i >= 0; i--)
            {
                var d = _frames[i].t - t;
                if (d > 0.04f) continue;
                if (d < -0.04f) break;
                sum += _frames[i].db; n++;
            }
            return n > 0 ? sum / n : float.NaN;
        }
    }

    /// <summary>The last 30 s of mic audio (16 kHz) on the Time.time clock, so a stretch of speech can be cut out later.</summary>
    public class AudioRing
    {
        const int Rate = MicStream.OutputRate;
        readonly float[] _ring = new float[Rate * 30];
        long _written;
        double _lastTime; // Time.time of the last sample
        /// <summary>Time.time of the newest sample (how far audio lags the clock is Time.time − this).</summary>
        public double LastTime => _lastTime;

        public void Push(float[] samples, double realtime)
        {
            foreach (var x in samples) _ring[_written++ % _ring.Length] = x;
            _lastTime = realtime - (Time.realtimeSinceStartupAsDouble - Time.timeAsDouble);
        }

        /// <summary>Samples between two Time.time moments (clipped to what is still kept); null if none.</summary>
        public float[] Get(double from, double to)
        {
            var end = _written - (long)((_lastTime - to) * Rate);
            var start = _written - (long)((_lastTime - from) * Rate);
            end = System.Math.Min(end, _written);
            start = System.Math.Max(start, System.Math.Max(0, _written - _ring.Length));
            if (end - start < Rate / 4) return null;
            var clip = new float[end - start];
            for (var i = 0; i < clip.Length; i++) clip[i] = _ring[(start + i) % _ring.Length];
            return clip;
        }
    }

    /// <summary>
    /// Audio-visual sync: does this face's lip gap rise and fall with the loudness of what we hear?
    /// The talker's lips follow the sound; a listener who nods, smiles or chews moves their mouth out of step.
    /// Pearson correlation over a window, best over a few small delays (camera frames lag the mic a little).
    /// </summary>
    public static class LipSync
    {
        // Camera frames and mic audio reach the app with different delays on Quest; search both directions.
        static readonly float[] Lags = { -0.16f, -0.08f, 0f, 0.08f, 0.16f, 0.24f };
        static readonly List<(float t, float v)> Buf = new();
        static readonly List<float> Xs = new(), Ys = new();

        /// <summary>−1..1, or NaN when there are too few lip samples (no face mesh, face not seen) or no sound.</summary>
        public static float Score(TrackedPerson p, AudioEnvelope audio, float from, float to, int minSamples = 8)
        {
            p.OpenSamples(from, to, Buf);
            if (Buf.Count < minSamples) return float.NaN;
            var best = float.NaN;
            foreach (var lag in Lags)
            {
                Xs.Clear(); Ys.Clear();
                foreach (var s in Buf)
                {
                    var a = audio.At(s.t - lag);
                    if (float.IsNaN(a)) continue;
                    Xs.Add(s.v); Ys.Add(a);
                }
                if (Xs.Count < minSamples) continue;
                var r = Pearson(Xs, Ys);
                if (!float.IsNaN(r) && (float.IsNaN(best) || r > best)) best = r;
            }
            return best;
        }

        static float Pearson(List<float> x, List<float> y)
        {
            var n = x.Count;
            float mx = 0, my = 0;
            for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
            mx /= n; my /= n;
            float sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < n; i++)
            {
                var dx = x[i] - mx; var dy = y[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            // A still mouth or a flat sound has no shape to compare.
            if (sxx < 1e-6f || syy < 1e-3f) return float.NaN;
            return sxy / Mathf.Sqrt(sxx * syy);
        }
    }
}
