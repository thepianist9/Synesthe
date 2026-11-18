using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// "When the voice starts, whose lips start moving fast?"
    ///
    /// Splits the mic loudness into bursts of speech (an onset = loudness rising clearly above the room after a short
    /// quiet) and, ~0.45 s after each onset, gives the burst to the face whose lips sped up at that moment. Lip SPEED
    /// (how fast the lip gap changes) is used, not the gap itself: a mouth that is simply open or closed has zero speed,
    /// while talking opens and closes it several times a second. Every word in a decided burst can then go straight to
    /// its owner — no waiting for longer evidence windows.
    /// </summary>
    public class SpeechOnsets
    {
        public class Burst
        {
            public float Start, End = float.PositiveInfinity;
            public bool Decided;
            public TrackedPerson Owner;   // null after deciding = nobody's lips sped up (off-screen speaker?)
            public float Lead;            // owner's lip-speed rise minus the next face's (lip-gap units per second)
            public bool AfterPause;       // started after quiet (vs. a loudness jump inside ongoing speech)
        }

        [System.Serializable]
        public class Settings
        {
            [Tooltip("Speech starts when loudness is this far above the room background (dB)…")]
            public float onsetDb = 9f;
            [Tooltip("…for at least this long (s).")]
            public float onsetHold = 0.06f;
            [Tooltip("A burst ends after this much quiet (below background + quietDb), and only then can a new one start.")]
            public float quietSeconds = 0.3f;
            public float quietDb = 5f;
            [Tooltip("Inside continuous speech (no pause), a new burst also starts when loudness jumps up this much (dB)…")]
            public float riseDb = 8f;
            [Tooltip("…within this long (s) — the start of a new phrase or a new speaker cutting in…")]
            public float riseWindow = 0.15f;
            [Tooltip("…but not sooner than this after the previous onset (s).")]
            public float minOnsetGap = 0.4f;
            [Tooltip("Lips judged from just before the onset to this long after it (s). Also how long a decision takes.")]
            public float lipWindow = 0.4f;
            [Tooltip("Owner's lip speed must rise at least this much over its own baseline (lip gap per second).")]
            public float minRise = 0.25f;
            [Tooltip("…and beat the next face by this much.")]
            public float minLead = 0.15f;
            [Tooltip("Onset inside ongoing speech: the current speaker's lips are already fast (no rise), while a listener " +
                     "who smiles or reacts rises from still. So there the owner must also have the fastest lips outright, " +
                     "by this factor.")]
            public float inSpeechSpeedRatio = 1.2f;
        }

        public readonly Settings settings;
        public readonly List<Burst> Bursts = new();
        public int Decided { get; private set; }
        public int Owned { get; private set; }

        float _floor = -60f;      // room background loudness (dB)
        float _processedUntil;
        float _loudSince = -1, _quietSince = -1;
        bool _voiced;
        float _lastOnset = -999f;
        readonly Queue<(float t, float db)> _recent = new(); // last riseWindow of frames, for jumps inside speech

        public SpeechOnsets(Settings s) => settings = s ?? new Settings();

        /// <summary>Main thread, every frame.</summary>
        public void Update(AudioEnvelope env, List<TrackedPerson> people, float now, System.Action<string> note)
        {
            // ---- onsets from the loudness envelope (20 ms frames, Time.time clock) ----
            foreach (var (t, db) in env.Frames)
            {
                if (t <= _processedUntil) continue;
                _processedUntil = t;
                _recent.Enqueue((t, db));
                while (_recent.Count > 0 && t - _recent.Peek().t > settings.riseWindow) _recent.Dequeue();
                if (!_voiced)
                {
                    // Background follows quiet frames quickly down, slowly up.
                    _floor = db < _floor ? Mathf.Lerp(_floor, db, 0.2f) : Mathf.Lerp(_floor, db, 0.01f);
                    if (db > _floor + settings.onsetDb)
                    {
                        if (_loudSince < 0) _loudSince = t;
                        if (t - _loudSince >= settings.onsetHold)
                        {
                            _voiced = true;
                            _quietSince = -1;
                            StartBurst(_loudSince, true);
                        }
                    }
                    else _loudSince = -1;
                }
                else
                {
                    // Jump in loudness without a pause: a new phrase, or someone else cutting in.
                    var dip = float.MaxValue;
                    foreach (var r in _recent) dip = Mathf.Min(dip, r.db);
                    if (db - dip >= settings.riseDb && t - _lastOnset >= settings.minOnsetGap)
                    {
                        if (Bursts.Count > 0) Bursts[^1].End = t - settings.riseWindow;
                        StartBurst(t - settings.riseWindow, false);
                    }
                    if (db < _floor + settings.quietDb)
                    {
                        if (_quietSince < 0) _quietSince = t;
                        if (t - _quietSince >= settings.quietSeconds)
                        {
                            _voiced = false;
                            _loudSince = -1;
                            if (Bursts.Count > 0) Bursts[^1].End = _quietSince;
                        }
                    }
                    else _quietSince = -1;
                }
            }

            // ---- decide owners once the lip window after the onset has been seen ----
            foreach (var b in Bursts)
            {
                if (b.Decided || now < b.Start + settings.lipWindow + 0.05f) continue;
                b.Decided = true;
                Decided++;
                TrackedPerson best = null;
                float bestRise = float.MinValue, second = 0f, bestSpeed = 0f, otherSpeed = 0f;
                var unmeasured = "";
                foreach (var p in people)
                {
                    if (p.LastSeen < b.Start - 0.3f) continue; // not in view at the onset
                    var on = LipSpeed(p, b.Start - 0.1f, b.Start + settings.lipWindow, out var samples);
                    var baseLine = LipSpeed(p, b.Start - 0.9f, b.Start - 0.15f, out _);
                    // A face in view whose lips weren't measured could be the talker: don't hand the burst to the other
                    // face by default (that was most wrong owners) — leave it to the slower evidence instead.
                    if (float.IsNaN(on)) { unmeasured += $"{(unmeasured.Length > 0 ? ", " : "")}{p.Label} {samples} lip samples"; continue; }
                    var rise = on - (float.IsNaN(baseLine) ? 0f : baseLine);
                    if (rise > bestRise)
                    {
                        if (best != null) { second = Mathf.Max(second, bestRise); otherSpeed = Mathf.Max(otherSpeed, bestSpeed); }
                        bestRise = rise; best = p; bestSpeed = on;
                    }
                    else { second = Mathf.Max(second, rise); otherSpeed = Mathf.Max(otherSpeed, on); }
                }
                var fastest = b.AfterPause || bestSpeed >= otherSpeed * settings.inSpeechSpeedRatio;
                if (best != null && fastest && unmeasured.Length == 0 && bestRise >= settings.minRise && bestRise - Mathf.Max(0, second) >= settings.minLead)
                {
                    b.Owner = best;
                    b.Lead = bestRise - Mathf.Max(0, second);
                    Owned++;
                }
                note?.Invoke(b.Owner != null
                    ? $"Voice started {b.Start:F2} → {b.Owner.Label}: lips sped up {bestRise:F2}/s (next {second:F2})"
                    : $"Voice started {b.Start:F2} → nobody's lips sped up clearly ({(best != null ? $"best {best.Label} {bestRise:F2}/s, next {second:F2}" : "no face in view")}{(fastest ? "" : ", not the fastest lips")}{(unmeasured.Length > 0 ? $", unmeasured: {unmeasured}" : "")})");
            }

            var cut = 0;
            while (cut < Bursts.Count && now - Bursts[cut].Start > 20f) cut++;
            if (cut > 0) Bursts.RemoveRange(0, cut);
        }

        void StartBurst(float t, bool afterPause)
        {
            Bursts.Add(new Burst { Start = t, AfterPause = afterPause });
            _lastOnset = t;
        }

        /// <summary>The decided burst a word starting at t belongs to (a word may start a little before the onset).</summary>
        public Burst At(float t)
        {
            for (var i = Bursts.Count - 1; i >= 0; i--)
            {
                var b = Bursts[i];
                if (t >= b.Start - 0.15f && t < b.End + 0.1f) return b.Decided ? b : null;
                if (t > b.End) break;
            }
            return null;
        }

        /// <summary>True if the burst for t is still waiting for its lip window (a decision will come soon).</summary>
        public bool Pending(float t)
        {
            for (var i = Bursts.Count - 1; i >= 0; i--)
            {
                var b = Bursts[i];
                if (t >= b.Start - 0.15f && t < b.End + 0.1f) return !b.Decided;
            }
            return false;
        }

        // Mean |d lip gap / dt| between two times, from the face-mesh lip gap history. NaN if too few samples.
        static float LipSpeed(TrackedPerson p, float from, float to, out int samples)
        {
            float sum = 0; var n = 0;
            samples = 0;
            var have = false; float pt = 0, pv = 0;
            foreach (var (t, v) in p.OpenHistory)
            {
                if (t < from - 0.1f) continue;
                if (t > to) break;
                if (t >= from) samples++;
                if (have && t >= from && t - pt > 1e-3f && t - pt < 0.25f)
                {
                    sum += Mathf.Abs(v - pv) / (t - pt);
                    n++;
                }
                have = true; pt = t; pv = v;
            }
            return n >= 3 ? sum / n : float.NaN;
        }
    }
}
