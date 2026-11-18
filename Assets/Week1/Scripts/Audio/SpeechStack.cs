using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SherpaOnnx;

namespace Week1Bench
{
    /// <summary>
    /// The on-device audio stack, fed by one shared mic stream and run on a worker thread:
    ///   Silero VAD → streaming speech-to-text (English, or the multilingual model: SpeechStack.Language.cs).
    /// Measures how much CPU time each model needs per second of audio (real-time factor)
    /// and, for Test 3, how long after someone stops speaking the caption text is ready.
    /// </summary>
    public partial class SpeechStack : IDisposable
    {
        public class Options
        {
            public bool Vad = true, Asr = true;
            public int AsrThreads = 2;
            // 0.8 s split sentences at natural pauses with the 480 ms NeMo model; 1.2 s keeps them together.
            public float EndpointTrailingSilence = 1.2f;
            /// <summary>Which speech-to-text model to use (see <see cref="SpeechRecognizers"/>).</summary>
            public SpeechModel Model = SpeechRecognizers.Default;
            /// <summary>"en" (English model only), "auto" (switch by the language heard) or a language code; see
            /// SpeechStack.Language.cs. Other languages need <see cref="EnableLanguages"/>.</summary>
            public string Language = "en";
            /// <summary>The multilingual model's files are already on disk: load it in <see cref="Init"/>, alongside the
            /// English one, instead of after <see cref="EnableLanguages"/> on a low-priority thread (headset, Nov 10:
            /// 3.3 s after listening began, with the first words held until then).</summary>
            public bool PreloadLanguages;

            // ---- Sensitivity: lower = catches quieter speech, but also more false starts from noise ----
            /// <summary>Silero speech probability that counts as speech (sherpa default 0.5). Ends long sentences at a
            /// pause (<see cref="SoftMaxUtteranceSeconds"/>).</summary>
            public float VadThreshold = 0.3f;
            /// <summary>Shortest burst that counts as speech (s). Short quiet words ("yes", "no") are ~0.15–0.2 s.</summary>
            public float VadMinSpeech = 0.15f;
            /// <summary>Recognizer bias against "nothing said" (transducer blank). On faint audio the model prefers
            /// blank and drops words; 0.5–1 brings them back, too high invents words in noise. 0 = off.</summary>
            public float BlankPenalty = 0.5f;
            /// <summary>Energy detector (speech timing only): absolute floor (RMS after gain) and multiple of the
            /// measured noise floor. Was 0.01 (≈ -40 dBFS) and 4×, which missed quiet far-field speech.</summary>
            public float EnergyFloor = 0.003f, EnergyOverNoise = 3f;
            /// <summary>Speech without pauses (a lecture, a video) used to run 20 s before the recognizer cut it. After
            /// this many seconds the utterance ends at the next short pause the voice detector hears (0 = off).</summary>
            public float SoftMaxUtteranceSeconds = 8f;
            /// <summary>Silence fed at such a pause so the word under way comes out before the cut (s).</summary>
            public const float SoftEndFlushSeconds = 0.8f;
            /// <summary>Hard cut even without any pause (s).</summary>
            public float MaxUtteranceSeconds = 12f;

            // ---- Keeping up (live captions; off by default so the Week 1 benchmarks measure every second) ----
            /// <summary>After this long without speech (voice detector and energy both quiet) and no words under way,
            /// the recognizer isn't fed: most of a conversation is pauses, and the 0.6B models cost ~70 % of a
            /// second of CPU per second of audio. The last <see cref="PreRollSeconds"/> are fed first when speech
            /// starts again, so its first word isn't lost. 0 = always feed.</summary>
            public float SkipSilenceAfter = 0f;
            public float PreRollSeconds = 0.6f;
            /// <summary>Live captions this far behind the mic (s) are no use: the sentence under way is finished as it
            /// is, and the queued audio is skipped up to the last <see cref="CatchUpKeepSeconds"/>. 0 = never skip.</summary>
            public float MaxLagSeconds = 0f;
            public float CatchUpKeepSeconds = 0.5f;
            /// <summary>The speech thread's priority (live captions: above the vision models' job threads).</summary>
            public ThreadPriority ThreadPriority = ThreadPriority.Normal;
        }

        public struct Utterance
        {
            public int Id;                // matches Refined.Id (a re-read after a language switch)
            public string Language;       // what the captions were in when it ended ("en", "de"…)
            public double Start;          // realtime of the utterance's first sample
            public string Text;
            public double SpeechSeconds;
            public double PartialLatency; // speech onset → first words on screen
            public double StableLatency;  // end of speech → caption text stops changing
            public double FinalLatency;   // end of speech → recognizer endpoint (bubble "done")
        }

        /// <summary>An utterance read again in the language the captions switched to, with word start times
        /// (Time.realtimeSinceStartup). Replaces the live text of <see cref="Utterance"/> with the same Id.</summary>
        public struct Refined
        {
            public string Language;
            public int Id;
            public string Text;
            public double[] WordTimes;    // one per space-separated word of Text; empty if unknown
            public double AudioSeconds, DecodeSeconds;
        }

        public class Stats
        {
            public double AudioSec, VadSec, AsrSec;
            public int MaxBacklog;
            /// <summary>How far behind the mic the recognizer is now (s), the most it has been, audio skipped to
            /// catch up (s) and silence it wasn't fed (s).</summary>
            public double Lag, MaxLag, SkippedSec, SilentSec;
            public bool VadSpeech;
            public string Partial = "";
            /// <summary>When each word of <see cref="Partial"/> was spoken (Time.realtimeSinceStartup); may be empty.</summary>
            public double[] PartialWordTimes = Array.Empty<double>();
            /// <summary>Finished utterances so far. Updated together with <see cref="Partial"/> going empty, so a reader of
            /// one snapshot never sees a final text while the same words still show as partial.</summary>
            public int Utterances;
            public double VadRtf => AudioSec > 0 ? VadSec / AudioSec : 0;
            public double AsrRtf => AudioSec > 0 ? AsrSec / AudioSec : 0;
            public double TotalRtf => VadRtf + AsrRtf;
        }

        readonly Options _opt;
        readonly ConcurrentQueue<(float[] samples, double realtime)> _queue = new();
        readonly ConcurrentQueue<Utterance> _utterances = new();
        readonly Stats _stats = new();
        readonly object _lock = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _clockOffset; // realtimeSinceStartup - _clock seconds
        Thread _thread;
        volatile bool _running, _transcribe = true;

        /// <summary>Off: the mic is still listened to (voice detection, levels) but nothing is transcribed, e.g. while
        /// the app's menu is up. Turning it off drops the sentence under way.</summary>
        public bool Transcribe { get => _transcribe; set => _transcribe = value; }

        volatile bool _restart;

        /// <summary>Start fresh (a new conversation): audio not yet recognised, the sentence under way, finished
        /// sentences not yet taken and language checks still waiting are all dropped, and <see cref="Stats.Utterances"/>
        /// counts from 0 again. Done on the speech thread; <see cref="Restarting"/> is true until it has been.</summary>
        public void Restart() { if (_running) _restart = true; }

        public bool Restarting => _restart;

        readonly ConcurrentQueue<Refined> _refined = new();
        int _utteranceId;

        VoiceActivityDetector _vad;
        ISpeechRecognizer _asr;

        public string Error { get; private set; }
        public string RecognizerName => _asr?.Name ?? "";

        public SpeechStack(Options options) => _opt = options;

        public bool Init()
        {
            try
            {
                if (_opt.Vad) _vad = new VoiceActivityDetector(VadConfig(_opt), 30);
                if (_opt.Asr)
                {
                    var sw = Stopwatch.StartNew();
                    var multi = _opt.PreloadLanguages && NeedsMulti(_opt.Language)
                        ? System.Threading.Tasks.Task.Run(() => SpeechRecognizers.Create(SpeechRecognizers.Multilingual, _opt.AsrThreads,
                            _opt.EndpointTrailingSilence, _opt.BlankPenalty, _opt.MaxUtteranceSeconds, _opt.Language == "auto" ? "auto" : _opt.Language))
                        : null;
                    _asr = _english = SpeechRecognizers.Create(_opt.Model, _opt.AsrThreads, _opt.EndpointTrailingSilence, _opt.BlankPenalty, _opt.MaxUtteranceSeconds);
                    if (multi != null)
                    {
                        try
                        {
                            _multiLoaded = multi.Result;
                            _languagesReady = true;            // EnableLanguages then has nothing left to do
                            _wantLanguage = _opt.Language;     // a chosen language: switch before the first words
                            _languageNotes.Enqueue($"Multilingual model loaded with the English one ({sw.Elapsed.TotalSeconds:F1} s for both)");
                        }
                        catch (Exception e) { _languageNotes.Enqueue($"Multilingual model didn't preload ({e.GetBaseException().Message}): loading it later"); }
                    }
                }
            }
            catch (Exception e)
            {
                Error = $"sherpa-onnx init failed: {e.GetType().Name}: {e.Message}";
                return false;
            }
            _mode = _opt.Language == "auto" ? "auto" : "en"; // another language: from EnableLanguages
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SpeechStack", Priority = _opt.ThreadPriority };
            _thread.Start();
            return true;
        }

        public void Push(float[] samples, double realtime)
        {
            _clockOffset = realtime - _clock.Elapsed.TotalSeconds;
            // In pieces of ≤ 0.25 s (the mic sends ~20 ms; a test feeding a whole clip at once must not keep the speech
            // thread busy for seconds, or Dispose can't stop it). realtime = time of each piece's last sample.
            const int piece = MicStream.OutputRate / 4;
            if (samples.Length <= piece) { _queue.Enqueue((samples, realtime)); return; }
            for (var s = 0; s < samples.Length; s += piece)
            {
                var e = Math.Min(samples.Length, s + piece);
                var part = new float[e - s];
                Array.Copy(samples, s, part, 0, part.Length);
                _queue.Enqueue((part, realtime - (samples.Length - e) / (double)MicStream.OutputRate));
            }
        }

        /// <summary>How far behind the mic speech-to-text is (s), without the allocation of a <see cref="Snapshot"/>.</summary>
        public double Lag { get { lock (_lock) return _stats.Lag; } }

        public Stats Snapshot()
        {
            lock (_lock)
            {
                return new Stats
                {
                    AudioSec = _stats.AudioSec, VadSec = _stats.VadSec, AsrSec = _stats.AsrSec,
                    MaxBacklog = _stats.MaxBacklog, Lag = _stats.Lag, MaxLag = _stats.MaxLag, SkippedSec = _stats.SkippedSec,
                    SilentSec = _stats.SilentSec, VadSpeech = _stats.VadSpeech, Partial = _stats.Partial,
                    PartialWordTimes = _stats.PartialWordTimes, Utterances = _stats.Utterances
                };
            }
        }

        public bool TryGetUtterance(out Utterance u) => _utterances.TryDequeue(out u);
        public bool TryGetRefined(out Refined r) => _refined.TryDequeue(out r);

        // ---------------- configs ----------------

        static VadModelConfig VadConfig(Options o)
        {
            var c = new VadModelConfig();
            c.SileroVad.Model = ModelFiles.PathOf("vad/silero_vad.onnx");
            c.SileroVad.Threshold = o.VadThreshold;
            c.SileroVad.MinSilenceDuration = 0.25f;
            c.SileroVad.MinSpeechDuration = o.VadMinSpeech;
            c.SileroVad.WindowSize = 512;
            c.SileroVad.MaxSpeechDuration = 20f;
            c.SampleRate = MicStream.OutputRate;
            c.NumThreads = 1;
            c.Provider = "cpu";
            c.Debug = 0;
            return c;
        }

        // ---------------- worker ----------------

        double Now => _clock.Elapsed.TotalSeconds + _clockOffset;

        void Loop()
        {
            // Idle class (the frame first), or normal priority even if started from a lowered pool thread (Linux threads
            // inherit nice): see BackgroundThread.SpeechIdle.
            if (MetaStarter.Captions.BackgroundThread.SpeechIdle) MetaStarter.Captions.BackgroundThread.Idle();
            else MetaStarter.Captions.BackgroundThread.Nice(0);
            var vadBuf = new List<float>(4096);

            // Energy-based speech timing (10 ms frames) for latency measurement.
            var noiseFloor = 0.005f;
            var inUtterance = false;
            double onset = 0, lastVoiced = 0, lastTextChange = 0, firstPartial = -1;
            var lastText = "";
            var segStart = double.NaN; // realtime of the first sample the recognizer saw since its last reset
            var uttAudio = new List<float>(MicStream.OutputRate * 10); // the current utterance (language checks)
            var segAudio = new List<float>(MicStream.OutputRate * 20); // what the recognizer heard since its last reset (re-fed after it)
            string refedWord = null; double refedUntil = 0; // last word kept at an endpoint, and until when its re-fed audio can repeat it
            var hideUntil = 0.0; // after an endpoint: the next utterance's words wait a moment, so the end shows as an empty partial
            var sw = new Stopwatch();
            var preRoll = new List<float>(MicStream.OutputRate); // while silence isn't fed: its last PreRollSeconds
            var lastHeard = double.NegativeInfinity;             // realtime of the last speech (voice detector or energy)
            var rate = (double)MicStream.OutputRate;

            while (_running)
            {
                if (_restart)
                {
                    while (_queue.TryDequeue(out _)) { }
                    while (_utterances.TryDequeue(out _)) { }
                    while (_refined.TryDequeue(out _)) { }
                    _asr?.Reset();
                    vadBuf.Clear();
                    segStart = double.NaN;
                    uttAudio.Clear();
                    segAudio.Clear();
                    preRoll.Clear();
                    lastText = "";
                    inUtterance = false;
                    lock (_lock)
                    {
                        _stats.Partial = "";
                        _stats.PartialWordTimes = Array.Empty<double>();
                        _stats.Utterances = 0;
                        _stats.VadSpeech = false;
                    }
                    _restart = false;
                }
                if (!_queue.TryDequeue(out var item))
                {
                    Thread.Sleep(5);
                    continue;
                }
                var (samples, realtime) = item;
                lock (_lock) _stats.MaxBacklog = Math.Max(_stats.MaxBacklog, _queue.Count);
                // Behind: take what has piled up (up to ~0.5 s) in one go — fewer, larger calls into the models.
                if (!_queue.IsEmpty)
                {
                    var merged = new List<float>(samples);
                    while (merged.Count < MicStream.OutputRate / 2 && _queue.TryDequeue(out var next))
                    {
                        merged.AddRange(next.samples);
                        realtime = next.realtime;
                    }
                    samples = merged.ToArray();
                }
                var audioLen = samples.Length; // (a pre-roll added below was counted when it arrived)
                var lag = Math.Max(0, Now - realtime);
                lock (_lock) { _stats.Lag = lag; _stats.MaxLag = Math.Max(_stats.MaxLag, lag); }

                // ---- too far behind: finish the sentence as it is and skip to (nearly) now ----
                if (_opt.MaxLagSeconds > 0 && lag > _opt.MaxLagSeconds && _transcribe && !HoldForModel)
                {
                    var skipped = samples.Length / rate;
                    var keepFrom = Now - _opt.CatchUpKeepSeconds;
                    while (_queue.TryPeek(out var next) && next.realtime < keepFrom && _queue.TryDequeue(out next))
                        skipped += next.samples.Length / rate;
                    var ended = lastText.Length > 0;
                    if (ended)
                    {
                        var id = ++_utteranceId;
                        _utterances.Enqueue(new Utterance
                        {
                            Id = id, Language = SentenceLanguage(id, lastText), Start = segStart, Text = lastText,
                            SpeechSeconds = -1, PartialLatency = -1, StableLatency = -1, FinalLatency = -1,
                        });
                    }
                    _asr?.Reset();
                    vadBuf.Clear();
                    segStart = double.NaN;
                    uttAudio.Clear();
                    segAudio.Clear();
                    preRoll.Clear();
                    lastText = "";
                    refedWord = null;
                    inUtterance = false;
                    lock (_lock)
                    {
                        _stats.Partial = "";
                        _stats.PartialWordTimes = Array.Empty<double>();
                        if (ended) _stats.Utterances++;
                        _stats.SkippedSec += skipped;
                    }
                    _languageNotes.Enqueue($"Speech-to-text fell {lag:F1} s behind: skipped {skipped:F1} s of audio to catch up");
                    continue;
                }

                // ---- energy frames ----
                for (var f = 0; f + 160 <= samples.Length; f += 160)
                {
                    double e = 0;
                    for (var i = f; i < f + 160; i++) e += samples[i] * samples[i];
                    var rms = (float)Math.Sqrt(e / 160);
                    var voiced = rms > Math.Max(_opt.EnergyFloor, noiseFloor * _opt.EnergyOverNoise);
                    if (!voiced) noiseFloor = noiseFloor * 0.995f + rms * 0.005f;
                    var t = realtime - (samples.Length - (f + 160)) / (double)MicStream.OutputRate;
                    if (voiced)
                    {
                        if (!inUtterance)
                        {
                            inUtterance = true;
                            onset = t;
                            firstPartial = -1;
                            lastTextChange = 0;
                        }
                        lastVoiced = t;
                        lastHeard = Math.Max(lastHeard, t);
                    }
                }

                // ---- VAD ----
                var vadSpeechNow = true; // without a voice detector never split early
                if (_vad != null)
                {
                    sw.Restart();
                    vadBuf.AddRange(samples);
                    var n = vadBuf.Count / 512 * 512;
                    if (n > 0)
                    {
                        _vad.AcceptWaveform(vadBuf.GetRange(0, n).ToArray());
                        vadBuf.RemoveRange(0, n);
                    }
                    var speech = _vad.IsSpeechDetected();
                    vadSpeechNow = speech;
                    if (speech) lastHeard = realtime;
                    while (!_vad.IsEmpty()) _vad.Pop(); // segments are not needed here
                    sw.Stop();
                    lock (_lock) { _stats.VadSec += sw.Elapsed.TotalSeconds; _stats.VadSpeech = speech; }
                }

                // ---- streaming speech-to-text ----
                var transcribe = _transcribe;
                if (_asr != null && !transcribe && !double.IsNaN(segStart))
                {
                    // Paused: forget the sentence under way, so it neither finishes later nor shows as live words.
                    _asr.Reset();
                    segStart = double.NaN;
                    uttAudio.Clear();
                    segAudio.Clear();
                    lastText = "";
                    lock (_lock) { _stats.Partial = ""; _stats.PartialWordTimes = Array.Empty<double>(); }
                }
                if (_asr != null && transcribe)
                {
                    sw.Restart();
                    // ---- silence with nothing under way: don't feed the recognizer, keep the last moment for later ----
                    if (_opt.SkipSilenceAfter > 0 && _vad != null && lastText.Length == 0 && !HoldForModel &&
                        realtime - lastHeard > _opt.SkipSilenceAfter)
                    {
                        if (!double.IsNaN(segStart))
                        {
                            _asr.Reset();
                            segStart = double.NaN;
                            segAudio.Clear();
                            uttAudio.Clear();
                            refedWord = null;
                        }
                        preRoll.AddRange(samples);
                        var max = (int)(_opt.PreRollSeconds * MicStream.OutputRate);
                        if (preRoll.Count > max) preRoll.RemoveRange(0, preRoll.Count - max);
                        if (inUtterance && realtime - lastVoiced > 1.5) inUtterance = false;
                        sw.Stop();
                        lock (_lock) _stats.SilentSec += samples.Length / rate;
                        goto Counted;
                    }
                    if (preRoll.Count > 0)
                    {
                        // Speech again: the recognizer hears the moment before it first (the voice detector is a little late).
                        var withPre = new float[preRoll.Count + samples.Length];
                        preRoll.CopyTo(withPre);
                        Array.Copy(samples, 0, withPre, preRoll.Count, samples.Length);
                        samples = withPre;
                        preRoll.Clear();
                    }
                    if (double.IsNaN(segStart)) segStart = realtime - samples.Length / (double)MicStream.OutputRate;
                    if (_languagesReady) LanguageStep(uttAudio);
                    if (HoldForModel)
                    {
                        // The chosen language's model is still loading: keep the audio for it, rather than let the
                        // English model turn the first words into nonsense ("Gutenberg Susamen") and lose them.
                        if (uttAudio.Count < MicStream.OutputRate * 30) uttAudio.AddRange(samples);
                        sw.Stop();
                        goto Counted;
                    }
                    _asr.AcceptWaveform(samples);
                    segAudio.AddRange(samples);
                    if (segAudio.Count > MicStream.OutputRate * 20) segAudio.RemoveRange(0, segAudio.Count - MicStream.OutputRate * 20);
                    if (KeepAudio && uttAudio.Count < MicStream.OutputRate * 30) uttAudio.AddRange(samples);
                    var text = _asr.Text;
                    var wordTimes = WordTimes(_asr.WordStarts, segStart);
                    if (refedWord != null) DropRepeat(ref text, ref wordTimes, refedWord, refedUntil); DropForeign(ref text, ref wordTimes, _opt.Language);
                    var ended = false;
                    var endpoint = _asr.IsEndpoint;
                    // Long stretch of speech: end it at the first short pause (captions read better in sentences).
                    if (!endpoint && _opt.SoftMaxUtteranceSeconds > 0 && !vadSpeechNow && text.Length > 0 &&
                        realtime - segStart >= _opt.SoftMaxUtteranceSeconds)
                    {
                        // The recognizer runs up to ~0.6 s behind the audio, so at this pause the last word can still
                        // be half out ("en la sala pe|queña"): a moment of silence lets it finish before the cut. Not
                        // kept in segAudio, so it isn't re-fed after the reset.
                        _asr.AcceptWaveform(new float[(int)(MicStream.OutputRate * Options.SoftEndFlushSeconds)]);
                        text = _asr.Text;
                        wordTimes = WordTimes(_asr.WordStarts, segStart);
                        if (refedWord != null) DropRepeat(ref text, ref wordTimes, refedWord, refedUntil); DropForeign(ref text, ref wordTimes, _opt.Language);
                        endpoint = true;
                    }
                    sw.Stop();
                    var now = Now;

                    if (text != lastText)
                    {
                        lastText = text;
                        if (text.Length > 0)
                        {
                            lastTextChange = now;
                            if (inUtterance && firstPartial < 0) firstPartial = now;
                        }
                    }
                    if (endpoint)
                    {
                        // Keep every recognised segment. Timing is only known when the energy detector also
                        // saw the speech (quiet far-field speech can slip under it) — otherwise it is -1.
                        if (text.Length > 0)
                        {
                            var id = ++_utteranceId;
                            var language = SentenceLanguage(id, text);
                            _utterances.Enqueue(new Utterance
                            {
                                Id = id,
                                Language = language,
                                Start = segStart,
                                Text = text,
                                SpeechSeconds = inUtterance ? lastVoiced - onset : -1,
                                PartialLatency = inUtterance && firstPartial > 0 ? firstPartial - onset : -1,
                                StableLatency = inUtterance ? Math.Max(0, lastTextChange - lastVoiced) : -1,
                                FinalLatency = inUtterance ? now - lastVoiced : -1,
                            });
                            ended = true;
                        }
                        // The recognizer emits a word up to a second after it is said, so at an endpoint it has already heard
                        // the start of what comes next — in a conversation, often the next person's first words. A reset
                        // throws that away; hear it again from just after the last word kept (measured on the two-person
                        // clip: 13 words lost per 2 minutes, 8 with this, the rest ordinary re-decoding differences).
                        var starts = _asr.WordStarts;
                        var lastWord = text.Length == 0 ? double.NegativeInfinity
                            : starts.Length > 0 ? segStart + starts[^1] + RefeedAfterWord : realtime - RefeedNoTimes;
                        var tailStart = Math.Max(Math.Max(lastWord, realtime - RefeedMax), segStart);
                        var keep = (int)Math.Min(segAudio.Count, Math.Max(0, (realtime - tailStart) * MicStream.OutputRate));
                        var tail = segAudio.GetRange(segAudio.Count - keep, keep).ToArray();
                        // A long last word can still be under way where the re-fed audio starts: then the next sentence
                        // begins with it again ("können, können"). Remember it, to drop it there.
                        var kept = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        refedWord = kept.Length > 0 ? Plain(kept[^1]) : null;
                        refedUntil = realtime - keep / (double)MicStream.OutputRate + 0.6;
                        _asr.Reset();
                        segAudio.Clear();
                        segAudio.AddRange(tail);
                        segStart = realtime - keep / (double)MicStream.OutputRate;
                        uttAudio.Clear();
                        if (KeepAudio) uttAudio.AddRange(tail);
                        if (keep > 0) _asr.AcceptWaveform(tail);
                        text = "";
                        wordTimes = Array.Empty<double>();
                        lastText = "";
                        inUtterance = false;
                        hideUntil = Now + 0.12;
                    }
                    else if (now < hideUntil)
                    {
                        text = "";
                        wordTimes = Array.Empty<double>();
                        lastText = "";
                    }
                    lock (_lock) { _stats.AsrSec += sw.Elapsed.TotalSeconds; _stats.Partial = text; _stats.PartialWordTimes = wordTimes; if (ended) _stats.Utterances++; }
                }
                else if (inUtterance && realtime - lastVoiced > 1.5)
                {
                    inUtterance = false;
                }

                Counted:
                lock (_lock) _stats.AudioSec += audioLen / rate;
            }
        }

        static string Plain(string w) => new string(Array.FindAll(w.ToCharArray(), char.IsLetterOrDigit)).ToLowerInvariant();

        // The sentence after an endpoint starts with the word the last one ended on, heard again in the re-fed audio:
        // leave it out (also while it is still a partial piece of that word). Only near the start of the re-fed audio.
        // A language is chosen (not auto): words in another writing system aren't that language's (in a café set to
        // Hindi, an Arabic speaker nearby came through in Arabic letters, and the wearer's English in Latin ones). Leave
        // them out. Words without letters (numbers) stay. Within one script (Spanish vs English) words can't be told.
        static void DropForeign(ref string text, ref double[] wordTimes, string language)
        {
            if (string.IsNullOrEmpty(language) || language == "auto" || text.Length == 0) return;
            var want = TextLanguage.ScriptOf(language);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var timed = wordTimes.Length == words.Length;
            var keep = new List<string>(words.Length);
            var times = new List<double>(words.Length);
            for (var i = 0; i < words.Length; i++)
            {
                var script = TextLanguage.ScriptOfText(words[i]);
                if (script != null && script != want) continue;
                keep.Add(words[i]);
                if (timed) times.Add(wordTimes[i]);
            }
            if (keep.Count == words.Length) return;
            text = string.Join(" ", keep);
            wordTimes = timed ? times.ToArray() : Array.Empty<double>();
        }

        static void DropRepeat(ref string text, ref double[] wordTimes, string word, double until)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || word.Length == 0) return;
            var first = Plain(words[0]);
            if (first.Length == 0 || !(first == word || (words.Length == 1 && word.StartsWith(first)))) return;
            if (wordTimes.Length == words.Length && wordTimes[0] + EmitLagSeconds > until) return; // said later: a real repeat
            text = string.Join(" ", words, 1, words.Length - 1);
            if (wordTimes.Length == words.Length) wordTimes = wordTimes[1..];
            else wordTimes = Array.Empty<double>();
        }

        // Re-fed after an endpoint: the audio from this long after the last kept word started (no word timings: this
        // long before the endpoint), at most RefeedMax seconds.
        const double RefeedAfterWord = 0.45, RefeedNoTimes = 0.6, RefeedMax = 1.5;

        // A transducer emits a word ~0.3 s after it starts (measured on our test sentences with NeMo 480 ms).
        const double EmitLagSeconds = 0.3;

        static double[] WordTimes(float[] starts, double segStart)
        {
            if (starts == null || starts.Length == 0) return Array.Empty<double>();
            var t = new double[starts.Length];
            for (var i = 0; i < t.Length; i++) t[i] = segStart + starts[i] - EmitLagSeconds;
            return t;
        }

        public void Dispose()
        {
            _running = false;
            _langSignal.Set();
            // A thread still inside a native decode would use freed memory and take the whole app (or the Editor)
            // down — seen when leaving Play mode right after a long stretch of audio was queued. Wait for it; if it
            // still hasn't stopped, leave the native models it may be using alive rather than crash.
            var asrDone = _thread?.Join(5000) ?? true;
            DisposeLanguages(asrDone);
            if (asrDone) { _english?.Dispose(); _vad?.Dispose(); }
            else UnityEngine.Debug.LogWarning("[SpeechStack] speech thread still busy after 5 s — its models are not freed");
        }
    }
}
