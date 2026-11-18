using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Voice fingerprints: a speaker embedding (NeMo TitaNet-small via sherpa-onnx) for a stretch of speech,
    /// grouped into voices on the fly — no enrolment. Each voice remembers which faces it was confidently seen
    /// coming from, so a voice can be put on the right face (or named while that person is out of view) even when
    /// the lips alone can't tell.
    ///
    /// Keeps the last 20 s of mic audio. Embeddings run on a background thread (~0.3% of real time on a Mac).
    /// Nothing is stored on disk: voices are forgotten when the app closes.
    ///
    /// Scoring (Nov 2026, EasyCom: real conversations recorded on glasses, 27 people): once the session has
    /// <see cref="CentreAfter"/> fingerprints, every comparison first subtracts the session's average fingerprint
    /// (what the room, the mic and the wearer add to everyone). Through a glasses mic, plain cosine put two different
    /// people's averaged voices as high as 0.73 and the same person's as low as 0.62 (so merges at 0.55 joined
    /// different people); minus the session average: same ≥ 0.29, different ≤ 0.09. One 3 s clip against a
    /// person's voice: same median 0.20, different −0.13 (plain: 0.50 / 0.29). The papers' back-ends (WeSpeaker,
    /// 3D-Speaker) do the same job with AS-norm, which needs a cohort of other people's voices shipped with the app;
    /// it scored only 2 points better here. Clips are 3 s: the models are trained on 2–3 s chunks, and every model
    /// that behaved was clearly better at 3 s than at 2 s (TitaNet-small 68% → 76% named right).
    /// </summary>
    public class VoicePrints : IDisposable
    {
        /// <summary>One fingerprint (or, once old, the summary of a voice's older fingerprints: Weight > 1).</summary>
        public class Print
        {
            public float[] E;        // unit length (summary: normalised sum)
            public int Weight = 1;
            public float At;         // Time.time
            public bool Short;       // under NewVoiceSeconds: may join a voice but not start one
            public readonly Dictionary<int, float> Faces = new();
            public Voice Owner;
        }

        public class Voice
        {
            public int Id;
            public float[] Mean;   // unit length
            public int Count;
            public float LastHeard;  // Time.time
            public readonly Dictionary<int, float> Faces = new(); // person id → evidence (sum over its clips)
            public string Label => $"Voice {Id}";
            public int ProfiledAt;   // Count when last compared with the people's voice profiles
            public readonly List<Print> Clips = new();

            /// <summary>The face this voice belongs to, if the evidence is clear; -1 otherwise.</summary>
            public int BoundPerson(float minEvidence = 1f)
            {
                int best = -1; float bestW = 0, total = 0;
                foreach (var kv in Faces)
                {
                    total += kv.Value;
                    if (kv.Value > bestW) { bestW = kv.Value; best = kv.Key; }
                }
                return bestW >= minEvidence && bestW >= 0.6f * total ? best : -1;
            }
        }

        [Tooltip("(Before regrouping, 2026-11-10: match-or-new.) Plain cosine to count as the same voice, early in a session (fewer than CentreAfter fingerprints). " +
                 "Clean test voices: same ≥ 0.71, different ≤ 0.63 (1.2 s); through the Quest's mic the same speaker " +
                 "reaches median 0.51 (3 s).")]
        public float MatchThreshold = 0.45f;
        [Tooltip("Early in a session: two voices are merged into one person only above this plain cosine.")]
        public float MergeThreshold = 0.55f;
        [Tooltip("Once centred: a 3 s clip is this voice above this (EasyCom: same median 0.20, 10th percentile −0.04; " +
                 "different median −0.13, 90th percentile 0.06).")]
        public float CentredMatchThreshold = 0.10f;
        [Tooltip("Once centred: two voices are one person above this (EasyCom: same ≥ 0.29, median 0.62; different ≤ 0.09). " +
                 "0.4, not lower: replaying a 35-minute, 4-person headset recording, merging at 0.2 joined different " +
                 "people twice, at 0.4 never (purity 94%, 4 voices + 1 mixed, 2 clips left over; plain 0.45 / 0.55: " +
                 "purity 95% but 63 voices, 76 clips in voices of their own).")]
        public float CentredMergeThreshold = 0.4f;
        [Tooltip("Subtract the session's average fingerprint once it has this many (before that, the average is mostly " +
                 "one or two people and would cancel their own voice).")]
        public int CentreAfter = 15;
        [Tooltip("…and only once two different voices have been heard: two voices with this many prints each in plain " +
                 "grouping, or tied to two different faces. Offline (2026-11-10): one TTS speaker 1 voice / 0 switches " +
                 "(was 5–8 voices); 2-person Quest-mic clip and 6 EasyCom sessions as before (EasyCom 67% vs 68% at the " +
                 "time, same at the end). 3: one-speaker long clip split; 8: EasyCom at the time 60%.")]
        public int CentreOnceVoices = 5;
        [Tooltip("Regrouping (every new fingerprint, all kept fingerprints are grouped again, average linkage): two groups " +
                 "join above this plain cosine before the session is centred.")]
        public float RegroupThreshold = 0.45f;
        [Tooltip("Same, once centred. Offline (2026-11-10, TitaNet-small, 3 s clips, audio only): the 2-person video " +
                 "through the Quest mic — voice right at the moment 85% vs 58% with match-or-new + merging, at the end " +
                 "87% vs 76%, 3 voices vs 4; 6 EasyCom sessions (3–4 talkers on glasses) — 68% vs 66% at the moment, " +
                 "77% vs 70% at the end, 1.7 extra voices vs 3.0, no more voices mixing two people. 0 joined people " +
                 "on EasyCom (7 mixed voices); 0.1 left as many extra voices as before.")]
        public float CentredRegroupThreshold = 0.05f;
        [Tooltip("Fingerprints kept one by one; older ones are folded into one summary per voice.")]
        public int KeepClips = 100;
        [Tooltip("Shortest stretch of speech worth a fingerprint (s).")]
        public float MinSeconds = 2.0f;
        [Tooltip("A new voice is only created from at least this much speech (s) — short clips are too noisy.")]
        public float NewVoiceSeconds = 2.5f;

        public readonly List<Voice> Voices = new();

        /// <summary>The person whose voice profile (taught by sure face evidence, VoiceProfiles) this fingerprint clearly
        /// matches, or -1. Ties voices to people: on the Quest the same person drifted into 5–6 voices in 2 minutes of a
        /// 2-person video (2026-11-10), each new one unbound, so its words couldn't be named and a "voice changed" split
        /// cut a person's own turn.</summary>
        public Func<float[], int> PersonOf;
        public string Error { get; private set; }
        public bool Ready => _extractor != null;
        public int Pending => _pending;

        const int Rate = MicStream.OutputRate;
        readonly float[] _ring = new float[Rate * 20];
        long _written;
        double _lastRealtime;
        SpeakerEmbeddingExtractor _extractor;
        readonly object _extractorLock = new();
        readonly ConcurrentQueue<(float[] embedding, float seconds, Action<Voice, float> done)> _results = new();
        readonly ConcurrentQueue<(float[] embedding, Action<float[]> done)> _raw = new();
        int _pending;
        int _nextId = 1;
        double[] _sessionSum;
        int _sessionCount;
        float[] _sessionMean; // unit-length average fingerprint (null until CentreAfter)

        /// <summary>Comparisons subtract the session's average fingerprint (enough of it has been heard).</summary>
        public bool Centred => _sessionMean != null && _heardTwo;
        // Two different voices have been heard (plain grouping: two voices of CentreOnceVoices prints, or two voices
        // tied to different faces). Until then the session average is one person's voice, and subtracting it leaves
        // noise: one speaker split into many voices (2026-11-10 headset replays: 2–6 mid-sentence splits per
        // one-voice clip; offline 5–8 voices for one TTS speaker). Latches on.
        bool _heardTwo;

        // Main thread: every fingerprint made in this session counts towards its average.
        void AddToSession(float[] unit)
        {
            _sessionSum ??= new double[unit.Length];
            if (unit.Length != _sessionSum.Length) return;
            for (var i = 0; i < unit.Length; i++) _sessionSum[i] += unit[i];
            _sessionCount++;
            if (_sessionCount < CentreAfter) return;
            var m = new float[unit.Length];
            for (var i = 0; i < m.Length; i++) m[i] = (float)(_sessionSum[i] / _sessionCount);
            _sessionMean = m;
        }

        /// <summary>How alike two fingerprints (or averages of them) are: cosine, minus the session's average once
        /// <see cref="Centred"/>. Same scale as <see cref="CentredMatchThreshold"/> / <see cref="MatchThreshold"/>.</summary>
        public float Score(float[] a, float[] b)
        {
            if (a == null || b == null) return 0f;
            var m = Centred ? _sessionMean : null;
            if (m == null || m.Length != a.Length || m.Length != b.Length) return Dot(a, b);
            double ab = 0, aa = 0, bb = 0;
            for (var i = 0; i < m.Length; i++)
            {
                var x = a[i] - m[i]; var y = b[i] - m[i];
                ab += x * y; aa += x * x; bb += y * y;
            }
            return aa > 0 && bb > 0 ? (float)(ab / Math.Sqrt(aa * bb)) : 0f;
        }

        public bool Init()
        {
            try
            {
                var c = new SpeakerEmbeddingExtractorConfig
                {
                    Model = ModelFiles.PathOf("speaker/titanet_small.onnx"),
                    NumThreads = 1,
                    Provider = "cpu",
                    Debug = 0,
                };
                _extractor = new SpeakerEmbeddingExtractor(c);
                return true;
            }
            catch (Exception e)
            {
                Error = $"voice fingerprints off: {e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>Main thread: 16 kHz samples, <paramref name="realtime"/> = Time.realtimeSinceStartup of the last sample.</summary>
        public void Push(float[] samples, double realtime)
        {
            foreach (var s in samples) _ring[_written++ % _ring.Length] = s;
            _lastRealtime = realtime;
        }

        /// <summary>
        /// Fingerprint the speech between two Time.time moments; <paramref name="done"/> is called later on the main
        /// thread (from <see cref="Update"/>) with the matching voice (null if too short or unclear) and the similarity.
        /// </summary>
        public bool Request(float from, float to, Action<Voice, float> done)
        {
            if (!Ready || to - from < MinSeconds) return false;
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            var endIdx = _written - (long)((_lastRealtime - (to + offset)) * Rate);
            var startIdx = endIdx - (long)((to - from) * Rate);
            endIdx = Math.Min(endIdx, _written);
            startIdx = Math.Max(startIdx, Math.Max(0, _written - _ring.Length));
            var n = (int)(endIdx - startIdx);
            if (n < MinSeconds * Rate) return false;
            var clip = new float[n];
            for (var i = 0; i < n; i++) clip[i] = _ring[(startIdx + i) % _ring.Length];

            Interlocked.Increment(ref _pending);
            Task.Run(() =>
            {
                float[] e = null;
                try
                {
                    BackgroundThread.Nice(5);
                    lock (_extractorLock)
                    {
                        if (_extractor == null) return;
                        using var stream = _extractor.CreateStream();
                        stream.AcceptWaveform(Rate, clip);
                        stream.InputFinished();
                        if (_extractor.IsReady(stream)) e = _extractor.Compute(stream);
                    }
                }
                catch (Exception ex) { Debug.LogWarning($"[Captions] voice fingerprint failed: {ex.Message}"); }
                finally { _results.Enqueue((e, n / (float)Rate, done)); }
            });
            return true;
        }

        /// <summary>
        /// Just the fingerprint (unit length) of the speech between two Time.time moments, for VoiceProfiles;
        /// <paramref name="done"/> is called on the main thread from <see cref="Update"/> (null if it failed).
        /// </summary>
        public bool Embed(float from, float to, Action<float[]> done)
        {
            var clip = Clip(from, to, 1f);
            if (clip == null) return false;
            Task.Run(() =>
            {
                float[] e = null;
                try { BackgroundThread.Nice(5); e = Compute(clip); }
                catch (Exception ex) { Debug.LogWarning($"[Captions] voice fingerprint failed: {ex.Message}"); }
                finally { _raw.Enqueue((e, done)); }
            });
            return true;
        }

        float[] Clip(float from, float to, float minSeconds)
        {
            if (!Ready || to - from < minSeconds) return null;
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            var endIdx = _written - (long)((_lastRealtime - (to + offset)) * Rate);
            var startIdx = endIdx - (long)((to - from) * Rate);
            endIdx = Math.Min(endIdx, _written);
            startIdx = Math.Max(startIdx, Math.Max(0, _written - _ring.Length));
            var n = (int)(endIdx - startIdx);
            if (n < minSeconds * Rate) return null;
            var clip = new float[n];
            for (var i = 0; i < n; i++) clip[i] = _ring[(startIdx + i) % _ring.Length];
            return clip;
        }

        float[] Compute(float[] clip)
        {
            lock (_extractorLock)
            {
                if (_extractor == null) return null;
                using var stream = _extractor.CreateStream();
                stream.AcceptWaveform(Rate, clip);
                stream.InputFinished();
                return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
            }
        }

        /// <summary>Main thread, every frame: match finished fingerprints to voices and report back.</summary>
        public void Update()
        {
            while (_raw.TryDequeue(out var raw))
            {
                var e = raw.embedding == null || raw.embedding.Length == 0 ? null : Normalize(raw.embedding);
                if (e != null) AddToSession(e);
                raw.done(e);
            }
            // A voice heard once and never tied to a face is usually a mixed or noisy clip: forget it after a minute.
            foreach (var v in Voices)
                if (v.Count == 1 && v.Faces.Count == 0 && Time.time - v.LastHeard > 60f) _clips.Remove(v.Clips[0]);
            Voices.RemoveAll(v => v.Count == 1 && v.Faces.Count == 0 && Time.time - v.LastHeard > 60f);
            TieByProfile();
            while (_results.TryDequeue(out var r))
            {
                Interlocked.Decrement(ref _pending);
                if (r.embedding == null || r.embedding.Length == 0) { r.done(null, 0); continue; }
                var unit = Normalize(r.embedding);
                AddToSession(unit);
                var (voice, sim) = Match(unit, r.seconds);
                r.done(voice, sim);
            }
        }

        readonly List<Print> _clips = new();
        readonly System.Diagnostics.Stopwatch _regroupWatch = new();
        /// <summary>Time the last regrouping took (ms, main thread) and how many fingerprints it grouped.</summary>
        public double LastRegroupMs { get; private set; }
        public int ClipCount => _clips.Count;

        (Voice, float) Match(float[] e, float seconds)
        {
            var clip = new Print { E = e, At = Time.time, Short = seconds < NewVoiceSeconds };
            _clips.Add(clip);
            var pid = PersonOf?.Invoke(e) ?? -1;
            if (pid >= 0) clip.Faces[pid] = 1f;
            Regroup();
            var v = clip.Owner;
            if (v == null) return (null, -1f);
            if (v.Count == 1 && pid >= 0) Debug.Log($"[Captions] {v.Label} sounds like Person {pid}'s voice profile");
            return (v, v.Count == 1 ? 1f : Score(v.Mean, e));
        }

        // Every kept fingerprint is grouped again (average linkage, stop at the bar), so a clip that went to the wrong
        // voice early can move, and two voices that turn out alike join — the online-clustering papers' fix for
        // early mistakes piling up (re-clustering all stored embeddings; Google's Turn-to-Diarize re-runs its
        // clustering over all turns for each new one). Groups tied to two different faces never join. Names stay:
        // a group takes the voice most of its weight had; a voice left with nothing is reported as merged.
        void Regroup()
        {
            _regroupWatch.Restart();
            FoldOld();
            var snap = _clips.ToArray(); // indices below are into this
            var n = snap.Length;
            var bar = Centred ? CentredRegroupThreshold : RegroupThreshold;
            // similarity between groups (average linkage, weighted); groups start as single clips
            var sim = new float[n, n];
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++) sim[i, j] = sim[j, i] = Score(snap[i].E, snap[j].E);
            var members = new List<int>[n]; var weight = new float[n]; var person = new int[n]; var alive = new bool[n];
            var faces = new Dictionary<int, float>[n];
            for (var i = 0; i < n; i++)
            {
                members[i] = new List<int> { i }; weight[i] = snap[i].Weight; alive[i] = true;
                faces[i] = new Dictionary<int, float>(snap[i].Faces); person[i] = BoundOf(faces[i]);
            }
            while (true)
            {
                int bi = -1, bj = -1; var best = float.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    if (!alive[i]) continue;
                    for (var j = i + 1; j < n; j++)
                    {
                        if (!alive[j] || sim[i, j] <= best) continue;
                        if (person[i] >= 0 && person[j] >= 0 && person[i] != person[j]) continue;
                        best = sim[i, j]; bi = i; bj = j;
                    }
                }
                if (bi < 0 || best < bar) break;
                for (var k = 0; k < n; k++)  // Lance–Williams: average linkage of the joined group
                {
                    if (!alive[k] || k == bi || k == bj) continue;
                    sim[bi, k] = sim[k, bi] = (weight[bi] * sim[bi, k] + weight[bj] * sim[bj, k]) / (weight[bi] + weight[bj]);
                }
                members[bi].AddRange(members[bj]); weight[bi] += weight[bj]; alive[bj] = false;
                foreach (var kv in faces[bj]) faces[bi][kv.Key] = (faces[bi].TryGetValue(kv.Key, out var w) ? w : 0f) + kv.Value;
                person[bi] = BoundOf(faces[bi]);
            }

            // Name the groups: biggest first, each takes the old voice holding most of its weight.
            var groups = new List<List<int>>();
            for (var i = 0; i < n; i++) if (alive[i]) groups.Add(members[i]);
            groups.Sort((x, y) => Sum(y).CompareTo(Sum(x)));
            float Sum(List<int> g) { float t = 0; foreach (var k in g) t += snap[k].Weight; return t; }
            var taken = new HashSet<Voice>(); var newOwner = new Voice[n];
            var lonelyShort = new List<Print>();
            foreach (var g in groups)
            {
                var votes = new Dictionary<Voice, float>();
                foreach (var k in g)
                    if (snap[k].Owner != null && !taken.Contains(snap[k].Owner))
                        votes[snap[k].Owner] = (votes.TryGetValue(snap[k].Owner, out var w) ? w : 0f) + snap[k].Weight;
                Voice v = null; var most = 0f;
                foreach (var kv in votes) if (kv.Value > most) { most = kv.Value; v = kv.Key; }
                if (v == null)
                {
                    if (g.Count == 1 && snap[g[0]].Short) { lonelyShort.Add(snap[g[0]]); continue; } // too short to start a voice
                    v = new Voice { Id = _nextId++ };
                    var closest = Voices.Count > 0 ? Voices[0] : null; var cs = -1f;
                    foreach (var o in Voices) { var sc = Score(o.Mean, snap[g[0]].E); if (sc > cs) { cs = sc; closest = o; } }
                    if (closest != null) Debug.Log($"[Captions] New voice: closest was {closest.Label} at {cs:F2} (< {bar:F2}{(Centred ? ", centred" : "")})");
                }
                taken.Add(v);
                foreach (var k in g) newOwner[k] = v;
            }

            // Old voices that kept nothing: say where most of their clips went.
            var gone = new List<(Voice gone, Voice kept)>();
            foreach (var old in Voices)
            {
                if (taken.Contains(old)) continue;
                var votes = new Dictionary<Voice, float>();
                for (var k = 0; k < n; k++)
                    if (snap[k].Owner == old && newOwner[k] != null)
                        votes[newOwner[k]] = (votes.TryGetValue(newOwner[k], out var w) ? w : 0f) + snap[k].Weight;
                Voice to = null; var most = 0f;
                foreach (var kv in votes) if (kv.Value > most) { most = kv.Value; to = kv.Key; }
                if (to != null) gone.Add((old, to));
            }

            // Rebuild the voices from their clips.
            foreach (var v in taken) { v.Clips.Clear(); v.Faces.Clear(); }
            for (var k = 0; k < n; k++)
            {
                snap[k].Owner = newOwner[k];
                newOwner[k]?.Clips.Add(snap[k]);
            }
            foreach (var c in lonelyShort) _clips.Remove(c);
            Voices.Clear();
            foreach (var v in taken)
            {
                if (v.Clips.Count == 0) continue;
                var m = new float[v.Clips[0].E.Length]; v.Count = 0; v.LastHeard = float.NegativeInfinity;
                foreach (var c in v.Clips)
                {
                    for (var i = 0; i < m.Length; i++) m[i] += c.E[i] * c.Weight;
                    v.Count += c.Weight; v.LastHeard = Mathf.Max(v.LastHeard, c.At);
                    foreach (var kv in c.Faces) v.Faces[kv.Key] = (v.Faces.TryGetValue(kv.Key, out var w) ? w : 0f) + kv.Value;
                }
                v.Mean = Normalize(m);
                Voices.Add(v);
            }
            Voices.Sort((x, y) => x.Id.CompareTo(y.Id));
            foreach (var (g, k) in gone) Merged?.Invoke(g, k, Score(g.Mean, k.Mean));
            _regroupWatch.Stop();
            LastRegroupMs = _regroupWatch.Elapsed.TotalMilliseconds;
            if (!_heardTwo && _sessionMean != null)
            {
                var big = 0; var people = new HashSet<int>();
                foreach (var v in Voices)
                {
                    if (v.Count >= CentreOnceVoices) big++;
                    var pid = v.BoundPerson(); if (pid >= 0) people.Add(pid);
                }
                if (big >= 2 || people.Count >= 2)
                {
                    _heardTwo = true;
                    Debug.Log($"[Captions] Voices: two people heard ({(people.Count >= 2 ? "tied to two faces" : $"{big} voices of {CentreOnceVoices}+ prints")}) — comparing against the session average from now on");
                    Regroup();
                }
            }
        }

        static int BoundOf(Dictionary<int, float> faces, float minEvidence = 1f)
        {
            int best = -1; float bestW = 0, total = 0;
            foreach (var kv in faces) { total += kv.Value; if (kv.Value > bestW) { bestW = kv.Value; best = kv.Key; } }
            return bestW >= minEvidence && bestW >= 0.6f * total ? best : -1;
        }

        // Past KeepClips: each voice's older fingerprints become one summary clip (weighted), so long sessions stay cheap.
        void FoldOld()
        {
            if (_clips.Count <= KeepClips) return;
            var cut = _clips.Count - KeepClips * 3 / 4;
            var byVoice = new Dictionary<Voice, Print>();
            var keep = new List<Print>();
            for (var k = 0; k < _clips.Count; k++)
            {
                var c = _clips[k];
                if (k >= cut || c.Owner == null) { if (k >= cut) keep.Add(c); continue; }
                if (!byVoice.TryGetValue(c.Owner, out var sum))
                {
                    sum = new Print { E = new float[c.E.Length], Weight = 0, At = c.At, Owner = c.Owner };
                    byVoice[c.Owner] = sum;
                }
                for (var i = 0; i < c.E.Length; i++) sum.E[i] = sum.E[i] * sum.Weight + c.E[i] * c.Weight;
                sum.Weight += c.Weight;
                for (var i = 0; i < c.E.Length; i++) sum.E[i] /= sum.Weight;
                sum.At = Mathf.Max(sum.At, c.At);
                foreach (var kv in c.Faces) sum.Faces[kv.Key] = (sum.Faces.TryGetValue(kv.Key, out var w) ? w : 0f) + kv.Value;
            }
            _clips.Clear();
            foreach (var sum in byVoice.Values) { sum.E = Normalize(sum.E); _clips.Add(sum); }
            _clips.AddRange(keep);
        }

        // Voices nobody is tied to yet: compare their average with the people's profiles whenever it has grown.
        void TieByProfile()
        {
            if (PersonOf == null) return;
            foreach (var v in Voices)
            {
                if (v.Count < 3 || v.Count == v.ProfiledAt || v.BoundPerson() >= 0 || v.Clips.Count == 0) continue;
                v.ProfiledAt = v.Count;
                var pid = PersonOf(v.Mean);
                if (pid < 0) continue;
                var c = v.Clips[^1];
                c.Faces[pid] = (c.Faces.TryGetValue(pid, out var w0) ? w0 : 0f) + 1f;
                v.Faces[pid] = (v.Faces.TryGetValue(pid, out var w1) ? w1 : 0f) + 1f;
                Debug.Log($"[Captions] {v.Label} tied to Person {pid} by their voice profile");
            }
        }

        /// <summary>(gone, kept, similarity) — a voice was found to be the same person as another.</summary>
        public event Action<Voice, Voice, float> Merged;

        /// <summary>This voice was confidently seen coming from this face.</summary>
        public void Bind(Voice v, int personId)
        {
            // older evidence fades, so a voice can move to a new face ID
            foreach (var c in v.Clips)
                foreach (var k in new List<int>(c.Faces.Keys))
                    if (k != personId) c.Faces[k] *= 0.8f;
            foreach (var k in new List<int>(v.Faces.Keys))
                if (k != personId) v.Faces[k] *= 0.8f;
            if (v.Clips.Count > 0)
            {
                var c = v.Clips[0];
                foreach (var x in v.Clips) if (x.At > c.At) c = x;
                c.Faces[personId] = (c.Faces.TryGetValue(personId, out var w0) ? w0 : 0f) + 1f;
            }
            v.Faces[personId] = (v.Faces.TryGetValue(personId, out var w) ? w : 0f) + 1f;
        }

        public float Similarity(Voice a, Voice b) => a == null || b == null ? 0f : Score(a.Mean, b.Mean);

        static float Dot(float[] a, float[] b)
        {
            float s = 0;
            for (var i = 0; i < Mathf.Min(a.Length, b.Length); i++) s += a[i] * b[i];
            return s;
        }

        static float[] Normalize(float[] v)
        {
            double n = 0;
            foreach (var x in v) n += x * x;
            var inv = n > 0 ? (float)(1.0 / Math.Sqrt(n)) : 0f;
            var r = new float[v.Length];
            for (var i = 0; i < v.Length; i++) r[i] = v[i] * inv;
            return r;
        }

        public void Dispose()
        {
            lock (_extractorLock)
            {
                _extractor?.Dispose();
                _extractor = null;
            }
        }
    }
}
