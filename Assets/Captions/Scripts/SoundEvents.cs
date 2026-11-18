using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Sounds that aren't words, for the captions: someone laughs, cries, sighs, gasps, screams, coughs, sneezes; or
    /// applause and cheering. Speech-to-text drops them, but to someone who can't hear they are half the meaning.
    /// CED-tiny (AudioSet's 527 sound classes, 6 MB int8, via sherpa-onnx) looks at the last 2 s of the mic once a second,
    /// off the main thread (~4 ms on one Mac core).
    /// Measured on EasyCom (13 min of four people at a table, laughter labelled): laughs at 0.2 found 18 of 29 laughs and
    /// flagged nothing else; the misses are mostly short laughs inside speech. Over 104 min of table talk and film the
    /// other classes fired a handful of times each, mostly on real crying/clapping in the films; coughs and sneezes
    /// need more (0.5): a hard consonant can look like one.
    /// The same windows also give the alerts (smoke alarm, siren, doorbell…: <see cref="Alert"/>), which run all the
    /// time, whatever the mode and also in the menus; ModeMenu.Alerts.cs shows them by priority.
    /// Sounds the wearer taught (<see cref="Taught"/>: their door knock, the washer's tune, the oven timer; recorded in
    /// ModeMenu.Sounds.cs, each pinned to where it comes from) are found by their fingerprint: all 527 class scores of a
    /// window, square-rooted and normalised, compared with the recorded ones (cosine). No new model, nothing trained.
    /// </summary>
    public sealed class SoundEvents : IDisposable
    {
        public const string ModelFile = "sound/ced_tiny.int8.onnx", LabelsFile = "sound/labels.csv";
        const int Rate = MicStream.OutputRate;
        const float Window = 2f, Every = 1f;
        const float Repeat = 4f;     // the same sound again within this long (after its last window) is the same laugh
        public const float MinRms = 0.003f; // quieter than this (about −50 dBFS): nothing worth tagging, don't run the model

        /// <summary>A sound heard: the caption word ("laughs"), whether a person makes it (else it's the room's:
        /// applause), how sure (0–1), and when (Time.time, the middle of the window).</summary>
        public struct Heard { public string Word; public bool Personal; public float Prob, Time; }

        // AudioSet class → caption word, how sure the model must be, and whether a person makes it.
        static readonly (string cls, string word, float min, bool personal)[] Classes =
        {
            ("Laughter", "laughs", 0.2f, true), ("Giggle", "laughs", 0.2f, true), ("Chuckle, chortle", "laughs", 0.2f, true),
            ("Belly laugh", "laughs", 0.2f, true), ("Snicker", "laughs", 0.2f, true), ("Baby laughter", "laughs", 0.2f, true),
            ("Crying, sobbing", "cries", 0.35f, true), ("Whimper", "cries", 0.35f, true), ("Wail, moan", "cries", 0.4f, true),
            ("Baby cry, infant cry", "a baby cries", 0.4f, false),
            ("Sigh", "sighs", 0.4f, true), ("Gasp", "gasps", 0.4f, true), ("Screaming", "screams", 0.4f, true),
            ("Cough", "coughs", 0.5f, true), ("Sneeze", "sneezes", 0.5f, true),
            ("Applause", "applause", 0.4f, false), ("Clapping", "applause", 0.4f, false), ("Cheering", "cheering", 0.4f, false),
        };

        // ---- Alerts: sounds that need attention, heard all the time (any mode, the menu, paused) ----
        // Measured (Nov 11) on 25 min of film and table talk: no class below reached 0.2 except knock (0.38 once),
        // phone (0.29) and bark (0.20). Synthetic smoke-alarm beeps (T3, 3.1 kHz) scored 0.18 alone and 0.48 under
        // speech, so the bar for alarms is low and a second window must agree (see Update) unless the first is sure.
        public enum Priority { Info, Warning, Danger }

        /// <summary>An alert: what (Kind: "smoke"), its name and advice for the panel, its icon, how urgent, how sure,
        /// when (Time.time).</summary>
        public struct Alert { public string Kind, Name, Advice, Glyph, Source; public Priority Level; public float Prob, Time; }

        // ---- Taught sounds ----
        public const string TaughtPrefix = "taught:";
        const int Classes527 = 527;
        /// <summary>A sound the wearer taught: its id (also its pin's), name, icon and fingerprints (unit vectors, 527).</summary>
        public sealed class Learned { public string Id, Name, Glyph; public float[][] Examples; public float[] Room; }
        /// <summary>The taught sounds (replaced whole by the menu; read by the tagging thread).</summary>
        public volatile Learned[] Taught = Array.Empty<Learned>();
        /// <summary>While set, every window's loudness and fingerprint is handed over (<see cref="Captured"/>) to teach one.</summary>
        public volatile bool Capturing;
        public readonly ConcurrentQueue<(float rms, float[] print, float time)> Captured = new();
        // Cosine between fingerprints: a match from this; one window this sure is enough, else a second must agree.
        public const float MatchMin = 0.8f, MatchSure = 0.9f;
        public const float TaughtMinRms = 0.0018f; // about −55 dBFS

        /// <summary>Square-rooted class scores, normalised: loud and quiet takes of a sound look alike, and a few strong
        /// classes don't drown out the many weak ones that tell two beeps apart.</summary>
        static float[] Print(float[] probs)
        {
            var f = new float[probs.Length];
            double n = 0;
            for (var i = 0; i < f.Length; i++) { f[i] = (float)Math.Sqrt(Math.Max(0f, probs[i])); n += f[i] * f[i]; }
            n = Math.Sqrt(n);
            if (n > 1e-6) for (var i = 0; i < f.Length; i++) f[i] = (float)(f[i] / n);
            return f;
        }

        public static float Similarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0f;
            double d = 0;
            for (var i = 0; i < a.Length; i++) d += a[i] * b[i];
            return (float)d;
        }

        static readonly (string kind, string name, string advice, string glyph, Priority level, float min, string[] cls)[] Alerts =
        {
            ("smoke", "Smoke alarm", "Check for smoke or fire. Leave if you need to.", "\ue285", Priority.Danger, 0.15f,
                new[] { "Smoke detector, smoke alarm", "Fire alarm" }),
            ("alarm", "Alarm sounding", "Find out what it is before carrying on.", "\ue82a", Priority.Danger, 0.3f,
                new[] { "Alarm", "Civil defense siren" }),
            ("siren", "Siren nearby", "An emergency vehicle may be close.", "\uf3a7", Priority.Warning, 0.2f,
                new[] { "Siren", "Emergency vehicle", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)" }),
            ("glass", "Glass breaking", "Something broke nearby.", "\uf14a", Priority.Warning, 0.3f, new[] { "Shatter", "Smash, crash" }),
            ("scream", "Someone screamed", "Someone may need help.", "\uebe9", Priority.Warning, 0.4f, new[] { "Screaming" }),
            ("baby", "Baby crying", "", "\ueb41", Priority.Warning, 0.35f, new[] { "Baby cry, infant cry" }),
            ("caralarm", "Car alarm", "", "\uf083", Priority.Warning, 0.3f, new[] { "Car alarm" }),
            ("doorbell", "Doorbell", "", "\uefff", Priority.Info, 0.15f, new[] { "Doorbell", "Ding-dong" }),
            ("knock", "Knocking", "", "\ueffd", Priority.Info, 0.35f, new[] { "Knock" }),
            ("phone", "Phone ringing", "", "\uf0dd", Priority.Info, 0.25f, new[] { "Telephone bell ringing", "Ringtone", "Telephone" }),
            ("beep", "Beeping", "A timer or an appliance.", "\ue855", Priority.Info, 0.15f, new[] { "Alarm clock", "Beep, bleep", "Buzzer" }),
            ("dog", "Dog barking", "", "\uf149", Priority.Info, 0.4f, new[] { "Bark" }),
        };
        const float Confirm = 3f; // a second window within this long confirms an alert (one at 2× its bar is enough)

        /// <summary>An alert of this kind, as if heard now (for tests: -e alert smoke, the editor's A key).</summary>
        public static Alert? Make(string kind, float prob = 1f)
        {
            foreach (var a in Alerts)
                if (a.kind == kind)
                    return new Alert { Kind = a.kind, Name = a.name, Advice = a.advice, Glyph = a.glyph, Level = a.level, Prob = prob, Time = UnityEngine.Time.time };
            return null;
        }

        public static string[] Kinds => Array.ConvertAll(Alerts, a => a.kind);

        readonly ConcurrentQueue<Alert> _alerts = new();
        readonly System.Collections.Generic.Dictionary<string, float> _alertSeen = new(); // kind → Time.time of its last window

        public string Error { get; private set; }
        public bool Ready => _tagger != null;

        AudioTagging _tagger;
        readonly object _lock = new();
        readonly float[] _ring = new float[Rate * 4];
        long _written;
        double _lastRealtime;
        float _nextRun;
        int _busy;
        readonly ConcurrentQueue<Heard> _found = new();
        // Per caption word: Time.time of the last window it was heard in (to say a long laugh once).
        readonly System.Collections.Generic.Dictionary<string, float> _lastHeard = new();

        public bool Init()
        {
            try
            {
                _tagger = new AudioTagging(new AudioTaggingConfig
                {
                    Model = new AudioTaggingModelConfig { CED = ModelFiles.PathOf(ModelFile), NumThreads = 1, Provider = "cpu", Debug = 0 },
                    Labels = ModelFiles.PathOf(LabelsFile),
                    TopK = 20,
                });
                return true;
            }
            catch (Exception e)
            {
                Error = $"sounds off: {e.GetType().Name}: {e.Message}";
                _tagger = null;
                return false;
            }
        }

        /// <summary>Main thread: 16 kHz samples, <paramref name="realtime"/> = Time.realtimeSinceStartup of the last one.</summary>
        public void Push(float[] samples, double realtime)
        {
            foreach (var s in samples) _ring[_written++ % _ring.Length] = s;
            _lastRealtime = realtime;
        }

        /// <summary>Main thread, every frame: starts a check once a second and hands over what was heard (each sound once)
        /// and the alerts (every confirmed window, so a panel can say it's still sounding).</summary>
        /// <param name="listen">False while no mode is up (main menu, setup): no caption tags. Alerts are always on.</param>
        public void Update(Action<Heard> heard, bool listen, Action<Alert> alert)
        {
            while (_found.TryDequeue(out var h))
            {
                if (!listen) continue;
                var fresh = !_lastHeard.TryGetValue(h.Word, out var last) || h.Time - last > Repeat;
                _lastHeard[h.Word] = h.Time;
                if (fresh) heard(h);
            }
            while (_alerts.TryDequeue(out var a))
            {
                var sure = a.Kind.StartsWith(TaughtPrefix, StringComparison.Ordinal) && a.Prob >= MatchSure;
                foreach (var c in Alerts) if (c.kind == a.Kind) sure = a.Prob >= 2f * c.min;
                var confirmed = sure || _alertSeen.TryGetValue(a.Kind, out var prev) && a.Time - prev <= Confirm;
                _alertSeen[a.Kind] = a.Time;
                if (confirmed) alert?.Invoke(a);
            }
            if (!Ready || Time.time < _nextRun || _written < Rate * Window || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            _nextRun = Time.time + Every;
            var n = (int)(Rate * Window);
            var clip = new float[n];
            double sum = 0;
            for (var i = 0; i < n; i++) { var s = _ring[(_written - n + i) % _ring.Length]; clip[i] = s; sum += s * s; }
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            var at = (float)(_lastRealtime - offset - Window / 2);
            var rms = (float)Math.Sqrt(sum / n);
            var capturing = Capturing; // teaching hears the quiet windows too (they set the room's level)
            var taught = Taught;
            // A taught sound may be quieter than the built-in ones (a knock down the hall): a lower bar once there are some.
            if (rms < (taught.Length > 0 ? TaughtMinRms : MinRms) && !capturing) { _busy = 0; return; }
            Task.Run(() =>
            {
                try
                {
                    BackgroundThread.Nice(10);
                    lock (_lock)
                    {
                        if (_tagger == null) return;
                        using var stream = _tagger.CreateStream();
                        stream.AcceptWaveform(Rate, clip);
                        // Every class (for the fingerprint); the captions and the built-in alerts read the top 12.
                        var all = _tagger.Compute(stream, Classes527);
                        var events = all.Length > 12 ? all[..12] : all;
                        var probs = new float[Classes527];
                        foreach (var e in all) if (e.Index >= 0 && e.Index < probs.Length) probs[e.Index] = e.Prob;
                        var print = Print(probs);
                        if (capturing) Captured.Enqueue((rms, print, at));
                        string bestWord = null; var best = 0f; var personal = true;
                        foreach (var e in events)
                            foreach (var c in Classes)
                                if (c.cls == e.Name && e.Prob >= c.min && e.Prob / c.min > best)
                                {
                                    best = e.Prob / c.min; bestWord = c.word; personal = c.personal;
                                }
                        if (bestWord != null)
                        {
                            var prob = 0f;
                            foreach (var e in events) foreach (var c in Classes) if (c.cls == e.Name && c.word == bestWord) prob = Math.Max(prob, e.Prob);
                            _found.Enqueue(new Heard { Word = bestWord, Personal = personal, Prob = prob, Time = at });
                        }
                        // The most urgent alert over its bar (ties: the surer one), its probability the best of its classes.
                        var pick = -1; var pickProb = 0f;
                        for (var i = 0; i < Alerts.Length; i++)
                        {
                            var p = 0f;
                            foreach (var e in events) if (Array.IndexOf(Alerts[i].cls, e.Name) >= 0) p = Math.Max(p, e.Prob);
                            if (p < Alerts[i].min) continue;
                            if (pick < 0 || Alerts[i].level > Alerts[pick].level ||
                                Alerts[i].level == Alerts[pick].level && p / Alerts[i].min > pickProb / Alerts[pick].min) { pick = i; pickProb = p; }
                        }
                        // A taught sound: the best match over its bar. It replaces a built-in info alert of the same window
                        // ("Knocking" → "Front door"); danger and warnings still show.
                        Learned match = null; var matchSim = 0f;
                        if (!capturing)
                            foreach (var l in taught)
                                foreach (var ex in l.Examples)
                                {
                                    var sim = Similarity(print, ex);
                                    if (l.Room != null && Similarity(print, l.Room) >= sim) continue; // more like the room than the sound
                                    if (sim >= MatchMin && sim > matchSim) { matchSim = sim; match = l; }
                                }
                        if (match != null)
                        {
                            _alerts.Enqueue(new Alert { Kind = TaughtPrefix + match.Id, Name = match.Name, Advice = "", Glyph = match.Glyph,
                                Source = match.Id, Level = Priority.Info, Prob = matchSim, Time = at });
                            if (pick >= 0 && Alerts[pick].level == Priority.Info) pick = -1;
                        }
                        if (pick >= 0 && capturing && Alerts[pick].level == Priority.Info) pick = -1; // teaching a knock isn't an alert
                        if (pick >= 0)
                        {
                            var a = Alerts[pick];
                            _alerts.Enqueue(new Alert { Kind = a.kind, Name = a.name, Advice = a.advice, Glyph = a.glyph, Level = a.level, Prob = pickProb, Time = at });
                        }
                    }
                }
                catch (Exception e) { Error = $"sounds: {e.Message}"; }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _tagger?.Dispose();
                _tagger = null;
            }
        }
    }
}
