using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// One voice profile per person, learned only from moments the faces were sure about — so a wrong caption never
    /// teaches the wrong voice.
    ///
    /// While there is speech, the last windowSeconds of audio are fingerprinted every hopSeconds (TitaNet-small, via
    /// VoicePrints' extractor). A window joins person P's profile when the face evidence for its words was sure it was
    /// P (CaptionDirector marks those words) and nobody else. Each window's fingerprint is then compared with every
    /// profile, and CaptionDirector uses the result as one more piece of evidence per word: "this sounds like P".
    ///
    /// On AVA movie clips TitaNet-small separates people with ~19% equal error rate from 1 s of speech (same-person
    /// median similarity 0.42, different 0.18) and ~14% from 3 s. Through a glasses mic in a room (EasyCom) it is
    /// much harder: 38% from 2 s, 34% from 3 s, 30% from 3 s minus the session's average fingerprint
    /// (<see cref="VoicePrints.Score"/>). Six models were compared there (TitaNet small/large, WeSpeaker ResNet34 and
    /// CAM++, 3D-Speaker CAM++ and ERes2NetV2): only ERes2NetV2 was clearly better (24%), at 7× the compute
    /// (113 vs 16 ms per 3 s clip on one Mac core), too much for the Quest next to speech recognition and vision.
    /// So windows are 3 s (the models are trained on 2–3 s; TitaNet's paper uses 3 s windows for room audio) and a
    /// single window is a hint, not proof — it is weighed, never obeyed.
    /// </summary>
    public class VoiceProfiles
    {
        [System.Serializable]
        public class Settings
        {
            [Tooltip("Settings layout. Below Version, the values below are replaced on start by this layout's (a field missing " +
                     "from a saved scene keeps its code default, so 0 = saved before versions existed). Set it to Version to " +
                     "tune the values by hand.")]
            public int version;
            [Tooltip("Fingerprint this much audio (s)…")]
            public float windowSeconds = 3f;
            [Tooltip("…every this many seconds while there is speech.")]
            public float hopSeconds = 1f;
            [Tooltip("A window joins a profile when at least this share of its words were sure for that person.")]
            [Range(0.3f, 1f)] public float sureShare = 0.6f;
            [Tooltip("A profile is used once it has this many windows.")]
            public int minWindows = 3;
            [Tooltip("How strongly a voice match counts: evidence × exp(gain × (similarity − the best other person's)), " +
                     "capped at maxFactor either way.")]
            public float gain = 5.5f; // v1 was 8 on plain cosine; scores minus the session average spread ~1.5× wider
            public float maxFactor = 3f;
            [Tooltip("With only one profile to compare against: a score above this counts for that person (EasyCom, " +
                     "3 s minus the session average: same median 0.20, different −0.13). Early in a session, before " +
                     "VoicePrints.CentreAfter fingerprints, scores are plain cosine and sameLevelPlain applies instead.")]
            public float sameLevel = SameLevelCentred;
            public float sameLevelPlain = 0.32f;

            public const int Version = 2;
            const float SameLevelCentred = 0.05f;

            /// <summary>Values saved by an older layout (scenes keep them) are replaced by this one's.</summary>
            public void Upgrade()
            {
                if (version >= Version) return;
                // v2 (Nov 2026): 1.5 s windows every 0.5 s → 3 s every 1 s; scores minus the session average.
                // Replaying this class on EasyCom (6 sessions, profiles taught by 30% of turns, 5% of those wrong):
                // named right 84.5% → 89.5%; at margin ≥ 0.10, 64% → 77% of windows named, 95% → 98% of those right.
                // A 0.75 s hop was no better than 1 s.
                windowSeconds = 3f; hopSeconds = 1f; gain = 5.5f; sameLevel = SameLevelCentred; sameLevelPlain = 0.32f;
                version = Version;
            }
        }

        class Window
        {
            public float From, To;
            public float[] Embedding;
            public bool Checked;
        }

        class Profile
        {
            public float[] Sum;
            public int Count;
            public float[] Mean;
        }

        readonly Settings _s;
        readonly VoicePrints _extractor;
        readonly List<Window> _windows = new();
        readonly Dictionary<int, Profile> _profiles = new();
        float _nextHop;
        int _pending;

        public VoiceProfiles(VoicePrints extractor, Settings s)
        {
            _extractor = extractor;
            _s = s ?? new Settings();
            _s.Upgrade();
        }

        public int Count(int personId) => _profiles.TryGetValue(personId, out var p) ? p.Count : 0;

        const int KeptWindows = 30; // a profile from an earlier session counts as at most this many windows, so it still adapts

        /// <summary>Start a person's profile from one kept from an earlier session (the person is remembered by face).</summary>
        public void Seed(int personId, float[] sum, int count)
        {
            if (sum == null || count <= 0 || _profiles.ContainsKey(personId)) return;
            var k = count > KeptWindows ? (float)KeptWindows / count : 1f;
            var s = new float[sum.Length];
            for (var i = 0; i < s.Length; i++) s[i] = sum[i] * k;
            var n = Mathf.Min(count, KeptWindows);
            _profiles[personId] = new Profile { Sum = s, Count = n, Mean = Normalize(s) };
        }

        /// <summary>A person's profile to keep for next time (null until it is usable).</summary>
        public (float[] sum, int count) Export(int personId) =>
            _profiles.TryGetValue(personId, out var p) && p.Count >= _s.minWindows ? ((float[])p.Sum.Clone(), p.Count) : default;

        /// <summary>
        /// Main thread, every frame. <paramref name="sureOwner"/>(from, to) = the person the faces were sure about for
        /// the words in that stretch (-1 = mixed or unsure, -2 = words not decided yet).
        /// </summary>
        public void Update(float now, bool speech, System.Func<float, float, int> sureOwner, System.Action<string> note)
        {
            if (_extractor == null || !_extractor.Ready) return;
            if (speech && now >= _nextHop && _pending < 3)
            {
                _nextHop = now + _s.hopSeconds;
                var to = now - 0.05f;
                var w = new Window { From = to - _s.windowSeconds, To = to };
                if (_extractor.Embed(w.From, w.To, e =>
                    {
                        _pending--;
                        if (e == null) return;
                        w.Embedding = e;
                        _windows.Add(w);
                    }))
                    _pending++;
            }

            foreach (var w in _windows)
            {
                if (w.Checked) continue;
                var owner = sureOwner(w.From, w.To);
                if (owner == -2) continue; // its words aren't decided yet
                w.Checked = true;
                if (owner < 0) continue;
                if (!_profiles.TryGetValue(owner, out var p)) _profiles[owner] = p = new Profile { Sum = new float[w.Embedding.Length] };
                for (var i = 0; i < p.Sum.Length; i++) p.Sum[i] += w.Embedding[i];
                p.Count++;
                p.Mean = Normalize(p.Sum);
                if (p.Count == _s.minWindows) note?.Invoke($"Voice profile for Person {owner} ready ({p.Count} sure windows)");
            }
            _windows.RemoveAll(w => now - w.To > 20f);
        }

        /// <summary>
        /// The person whose profile this fingerprint (unit length; one clip or a voice's average) clearly matches, or -1.
        /// Clear = above sameLevel and at least <paramref name="margin"/> above every other profile. Only once scores
        /// are centred (plain cosine puts different people too close through the Quest mic). Lets VoicePrints tie a
        /// voice it heard to the person the faces taught, instead of starting yet another anonymous voice.
        /// </summary>
        public int PersonOf(float[] e, System.Predicate<int> present, float margin = 0.15f)
        {
            if (e == null || _extractor == null || !_extractor.Centred) return -1;
            int best = -1; float bestS = float.NegativeInfinity, second = float.NegativeInfinity;
            foreach (var kv in _profiles)
            {
                if (kv.Value.Count < _s.minWindows || present != null && !present(kv.Key)) continue;
                var s = _extractor.Score(kv.Value.Mean, e);
                if (s > bestS) { second = bestS; bestS = s; best = kv.Key; }
                else if (s > second) second = s;
            }
            if (best < 0 || bestS < _s.sameLevel + 0.1f) return -1;
            return float.IsNegativeInfinity(second) || bestS - second >= margin ? best : -1;
        }

        /// <summary>
        /// How much more (factor &gt; 1) or less (&lt; 1) the voice around t sounds like this person than like anyone else
        /// in view. 1 = no information (no profile, or no fingerprint covers t).
        /// </summary>
        public float Factor(int personId, float t, IEnumerable<int> others, out float similarity)
        {
            similarity = float.NaN;
            if (!_profiles.TryGetValue(personId, out var mine) || mine.Count < _s.minWindows) return 1f;
            var w = Covering(t);
            if (w == null) return 1f;
            similarity = _extractor.Score(mine.Mean, w.Embedding);
            var best = float.NegativeInfinity;
            foreach (var o in others)
                if (o != personId && _profiles.TryGetValue(o, out var q) && q.Count >= _s.minWindows)
                    best = Mathf.Max(best, _extractor.Score(q.Mean, w.Embedding));
            // Nobody else to compare with: only say "sounds like them" (never "doesn't"), as the Quest mic gives lower
            // same-voice similarity than clean audio.
            var f = float.IsNegativeInfinity(best)
                ? Mathf.Max(1f, Mathf.Exp(_s.gain * (similarity - (_extractor.Centred ? _s.sameLevel : _s.sameLevelPlain))))
                : Mathf.Exp(_s.gain * (similarity - best));
            return Mathf.Clamp(f, 1f / _s.maxFactor, _s.maxFactor);
        }

        // The latest fingerprint whose window contains t (with a little room after it: a word lasts ~0.3 s).
        Window Covering(float t)
        {
            for (var i = _windows.Count - 1; i >= 0; i--)
            {
                var w = _windows[i];
                if (t >= w.From + 0.2f && t + 0.25f <= w.To) return w;
            }
            return null;
        }

        static float[] Normalize(float[] v)
        {
            double n = 0;
            foreach (var x in v) n += x * x;
            var inv = n > 0 ? (float)(1.0 / System.Math.Sqrt(n)) : 0f;
            var r = new float[v.Length];
            for (var i = 0; i < v.Length; i++) r[i] = v[i] * inv;
            return r;
        }
    }
}
