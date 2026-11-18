using System;
using System.Collections.Generic;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The voice's pitch (F0), 50 times a second, from the mic audio: a turn change between a low and a high voice
    /// (a man and a woman, an adult and a child) shows in the pitch within a few tenths of a second, long before a
    /// voice fingerprint (1.5 s of audio) or the lips (several words) can tell. Autocorrelation on 40 ms frames at
    /// 8 kHz, 70–400 Hz; a frame counts as voiced when it is loud enough and clearly periodic.
    /// Main thread, ~2 ms of work per second of audio. Keeps the last 60 s, from which it learns the two voice ranges
    /// in the room (<see cref="Learn"/>): a pitch counts as the low or the high voice only when the voices heard
    /// clearly fall into two groups (not two men, say).
    /// </summary>
    public class PitchTrack
    {
        const int InRate = MicStream.OutputRate;      // 16 kHz
        const int Rate = 8000;                         // analysed at 8 kHz
        const int Frame = Rate * 40 / 1000;            // 320 samples
        const int Hop = Rate * 20 / 1000;              // a frame every 20 ms
        const int MinLag = Rate / 400, MaxLag = Rate / 70;

        readonly float[] _ring = new float[Frame];
        long _count;
        int _sinceHop;
        float _half;                                   // 16 → 8 kHz: mean of each pair
        bool _odd;
        readonly float[] _x = new float[Frame];

        /// <summary>One frame: Time.time of its middle, F0 in Hz (0 = not voiced).</summary>
        public readonly List<(float t, float f0)> Frames = new();

        /// <summary>Main thread: 16 kHz samples; <paramref name="realtime"/> = Time.realtimeSinceStartup of the last one.</summary>
        public void Push(float[] samples, double realtime)
        {
            var toTime = (float)(Time.timeAsDouble - Time.realtimeSinceStartupAsDouble);
            var n = samples.Length;
            for (var i = 0; i < n; i++)
            {
                if (!_odd) { _half = samples[i]; _odd = true; continue; }
                _odd = false;
                var s = (_half + samples[i]) * 0.5f;
                _ring[_count++ % Frame] = s;
                if (_count < Frame || ++_sinceHop < Hop) continue;
                _sinceHop = 0;
                // This frame ends at input sample i; its middle is 20 ms before that.
                var t = (float)(realtime - (n - 1 - i) / (double)InRate) + toTime - 0.02f;
                Frames.Add((t, F0()));
            }
            var keep = Time.time - 60f;
            var drop = 0;
            while (drop < Frames.Count && Frames[drop].t < keep) drop++;
            if (drop > 0) Frames.RemoveRange(0, drop);
        }

        float F0()
        {
            var mean = 0f;
            for (var i = 0; i < Frame; i++) { _x[i] = _ring[(_count + i) % Frame]; mean += _x[i]; } // oldest first
            mean /= Frame;
            var energy = 0f;
            for (var i = 0; i < Frame; i++) { _x[i] -= mean; energy += _x[i] * _x[i]; }
            if (energy / Frame < 1e-5f) return 0f; // quieter than about −50 dBFS
            var best = 0f;
            var bestLag = 0;
            for (var lag = MinLag; lag <= MaxLag; lag++)
            {
                var ac = 0f;
                for (var i = 0; i + lag < Frame; i++) ac += _x[i] * _x[i + lag];
                ac /= Frame - lag;
                if (ac > best) { best = ac; bestLag = lag; }
            }
            // Clearly periodic only: a peak at least 40 % of the frame's energy (per sample).
            if (bestLag == 0 || best < 0.4f * energy / Frame) return 0f;
            // The peak at twice the period is often as high as the period's own: prefer the shortest lag nearly as high.
            for (var div = 3; div >= 2; div--)
            {
                var lag = Mathf.RoundToInt(bestLag / (float)div);
                if (lag < MinLag) continue;
                var ac = 0f;
                for (var i = 0; i + lag < Frame; i++) ac += _x[i] * _x[i + lag];
                ac /= Frame - lag;
                if (ac > 0.85f * best) { bestLag = lag; break; }
            }
            return Rate / (float)bestLag;
        }

        readonly List<float> _tmp = new();

        /// <summary>Median F0 (Hz) of the voiced frames between two Time.time moments; 0 if fewer than
        /// <paramref name="minVoiced"/> of them.</summary>
        public float Median(float from, float to, int minVoiced, out int voiced)
        {
            _tmp.Clear();
            for (var i = Frames.Count - 1; i >= 0; i--)
            {
                var (t, f) = Frames[i];
                if (t < from) break;
                if (t <= to && f > 0) _tmp.Add(f);
            }
            voiced = _tmp.Count;
            if (voiced < Mathf.Max(1, minVoiced)) return 0f;
            _tmp.Sort();
            return _tmp[voiced / 2];
        }

        // The two voice groups, in log2 Hz (0 = not known: one voice, or voices too alike in pitch).
        float _low, _high, _learnedAt = -99f;
        readonly List<float> _logs = new();

        /// <summary>Two groups of voices, at least <paramref name="minSemitones"/> apart, each at least 15 % of the
        /// voiced frames heard in the last minute? (2-means on log pitch, refreshed every second.)</summary>
        public bool Learn(float minSemitones = 5f)
        {
            if (Time.time - _learnedAt < 1f) return _low > 0;
            _learnedAt = Time.time;
            _logs.Clear();
            foreach (var (_, f) in Frames) if (f > 0) _logs.Add(Mathf.Log(f, 2f));
            _low = _high = 0;
            if (_logs.Count < 200) return false; // 4 s of voiced speech
            _logs.Sort();
            float lo = _logs[_logs.Count / 5], hi = _logs[_logs.Count * 4 / 5];
            int nLo = 0, nHi = 0;
            for (var it = 0; it < 8; it++)
            {
                float sLo = 0, sHi = 0;
                nLo = nHi = 0;
                var mid = (lo + hi) / 2;
                foreach (var x in _logs) { if (x < mid) { sLo += x; nLo++; } else { sHi += x; nHi++; } }
                if (nLo == 0 || nHi == 0) return false;
                lo = sLo / nLo; hi = sHi / nHi;
            }
            if ((hi - lo) * 12f < minSemitones || nLo < 0.15f * _logs.Count || nHi < 0.15f * _logs.Count) return false;
            _low = lo; _high = hi;
            return true;
        }

        public float LowHz => _low > 0 ? Mathf.Pow(2f, _low) : 0f;
        public float HighHz => _high > 0 ? Mathf.Pow(2f, _high) : 0f;

        /// <summary>Which voice group the speech between two moments is: −1 low, +1 high, 0 unclear (too few voiced
        /// frames, between the groups, or no two groups).</summary>
        public int Group(float from, float to, int minVoiced)
        {
            if (_low <= 0) return 0;
            var f = Median(from, to, minVoiced, out _);
            return f > 0 ? GroupOf(f) : 0;
        }

        /// <summary>−1 / +1 / 0 for one pitch: within the lower or upper third between the groups' centres, or beyond.</summary>
        public int GroupOf(float hz)
        {
            if (_low <= 0 || hz <= 0) return 0;
            var x = Mathf.Log(hz, 2f);
            var third = (_high - _low) / 3f;
            return x < _low + third ? -1 : x > _high - third ? 1 : 0;
        }

        /// <summary>Semitones from <paramref name="a"/> up to <paramref name="b"/>.</summary>
        public static float Semitones(float a, float b) => 12f * Mathf.Log(b / a, 2f);
    }
}
