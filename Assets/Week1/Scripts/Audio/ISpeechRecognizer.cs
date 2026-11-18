using System;
using SherpaOnnx;

namespace Week1Bench
{
    /// <summary>
    /// Streaming speech-to-text, swappable. Everything above this (VAD, captions, bubbles) only talks
    /// to this interface, so a bigger or multilingual model can replace the default without other changes.
    /// Calls come from one worker thread; implementations need not be thread-safe.
    /// </summary>
    public interface ISpeechRecognizer : IDisposable
    {
        string Name { get; }
        /// <summary>Feed 16 kHz mono samples and decode as far as possible.</summary>
        void AcceptWaveform(float[] samples);
        /// <summary>Text of the current utterance so far (partial until <see cref="IsEndpoint"/>).</summary>
        string Text { get; }
        /// <summary>When each word of <see cref="Text"/> started, in seconds of audio since the last <see cref="Reset"/>
        /// (one entry per space-separated word). Empty if the model can't tell.</summary>
        float[] WordStarts { get; }
        /// <summary>True when the speaker has paused long enough to finish the utterance.</summary>
        bool IsEndpoint { get; }
        /// <summary>Start a new utterance.</summary>
        void Reset();
        /// <summary>Multilingual models: change the spoken language ("en", "de"… or "auto") from any thread; it applies
        /// from the next audio fed. Others ignore it.</summary>
        void SetLanguage(string language) { }
        /// <summary>Decode a finished clip on its own stream (the live one is untouched), in the current language: to
        /// re-read sentences after a language switch. Null if the model can't.</summary>
        string Decode(float[] audio, out float[] wordStarts) { wordStarts = Array.Empty<float>(); return null; }
        /// <summary>Like <see cref="Decode"/>, a slice at a time (so live audio keeps flowing meanwhile). Null if the
        /// model can't. Same thread as the rest.</summary>
        IClipDecoder BeginDecode() => null;
    }

    /// <summary>A clip read again on its own stream, fed in slices; <see cref="Finish"/> once it has all been fed.</summary>
    public interface IClipDecoder : IDisposable
    {
        void Feed(float[] samples, int offset, int count);
        string Finish(out float[] wordStarts);
    }

    /// <summary>Which recognizer to build. Add new models here and in <see cref="SpeechRecognizers.Create"/>.</summary>
    public enum SpeechModel
    {
        // Scenes save this value as a number: never renumber or reuse 0 (Zipformer EN 20M) or 1 (NeMo FastConformer
        // EN 480 ms), both removed. NeMo 480 was English only and kept up easily, but couldn't do other languages.
        /// <summary>NVIDIA Nemotron Speech Streaming EN 0.6B (cache-aware FastConformer RNNT), int8, 560 ms chunks.
        /// 7.07% average WER on the Open ASR Leaderboard, 1.2% on our clean test sentences, and writes its own
        /// punctuation. About 5× the size of the removed NeMo 480 ms model: SpeechStack skips silence and stale audio so it
        /// keeps up on the headset (asr load and lag in the debug log). Default.</summary>
        NemotronStreamingEn560ms = 2,
        /// <summary>NVIDIA Nemotron 3.5 ASR Streaming 0.6B (the same design with a language prompt), int8, 560 ms chunks:
        /// 32 languages (English, Spanish, French, German, Italian, Portuguese, Dutch, Turkish, Russian, Arabic, Hindi,
        /// Japanese, Korean, Vietnamese, Ukrainian, Polish, Swedish, Mandarin…), told which one per stream. OpenMDW-1.1.
        /// Not the live model on its own: on English it's well behind the English one (5.1% vs 1.2% WER, clean test
        /// sentences), so SpeechStack loads it only for other languages (<see cref="SpeechRecognizers.Multilingual"/>).</summary>
        NemotronMultilingual560ms = 3,
    }

    public static class SpeechRecognizers
    {
        public const SpeechModel Default = SpeechModel.NemotronStreamingEn560ms;

        /// <summary>The model for languages other than English (loaded when one is heard or picked).</summary>
        public const SpeechModel Multilingual = SpeechModel.NemotronMultilingual560ms;

        /// <summary>The model's folder under StreamingAssets/Models (and ModelFiles.Root on the device).</summary>
        public static string Folder(SpeechModel model) => model switch
        {
            SpeechModel.NemotronStreamingEn560ms => "asr-nemotron560",
            SpeechModel.NemotronMultilingual560ms => "asr-nemotron35",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
        };

        /// <param name="language">Multilingual model: the spoken language ("de", "ja"…; "auto" lets it guess, less reliably).</param>
        public static ISpeechRecognizer Create(SpeechModel model, int threads = 2, float endpointTrailingSilence = 0.8f, float blankPenalty = 0f,
            float maxUtteranceSeconds = 20f, string language = "auto") => model switch
        {
            SpeechModel.NemotronStreamingEn560ms => new SherpaStreamingRecognizer("Nemotron Streaming EN 0.6B 560ms", "asr-nemotron560", threads, endpointTrailingSilence, blankPenalty, maxUtteranceSeconds),
            SpeechModel.NemotronMultilingual560ms => new SherpaStreamingRecognizer("Nemotron 3.5 Streaming 0.6B 560ms", "asr-nemotron35", threads,
                endpointTrailingSilence, blankPenalty, maxUtteranceSeconds, string.IsNullOrEmpty(language) ? "auto" : language),
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
        };
    }

    /// <summary>
    /// sherpa-onnx streaming transducer (Nemotron, cache-aware FastConformer). Expects encoder/decoder/joiner.onnx + tokens.txt
    /// in ModelFiles.Root/&lt;folder&gt;/ (copied from StreamingAssets on first launch).
    /// </summary>
    public sealed class SherpaStreamingRecognizer : ISpeechRecognizer
    {
        readonly OnlineRecognizer _recognizer;
        readonly OnlineStream _stream;
        string _text = "";
        float[] _wordStarts = Array.Empty<float>();

        public string Name { get; }
        public string Text => _text;
        public float[] WordStarts => _wordStarts;
        public bool IsEndpoint => _recognizer.IsEndpoint(_stream);

        // Every fresh stream starts with this much silence: a stream whose first sample is already speech loses its
        // first word ("Good morning" → "Morning", "Guten Morgen" → "Morgen", 大家 → 家), and after every endpoint
        // and language switch the next words start a fresh stream. Word times are shifted back to match.
        const float LeadInSeconds = 0.3f;
        static readonly float[] LeadIn = new float[(int)(MicStream.OutputRate * LeadInSeconds)];

        static float[] Unlead(float[] starts)
        {
            for (var i = 0; i < starts.Length; i++) starts[i] = Math.Max(0f, starts[i] - LeadInSeconds);
            return starts;
        }

        string _language;                  // multilingual models: set on the stream (null: the model has no language prompt)
        volatile string _pendingLanguage;  // from SetLanguage, applied on the worker thread

        public SherpaStreamingRecognizer(string name, string folder, int threads, float endpointTrailingSilence, float blankPenalty = 0f,
            float maxUtteranceSeconds = 20f, string language = null)
        {
            Name = name;
            _language = string.IsNullOrEmpty(language) ? null : language;
            var c = new OnlineRecognizerConfig();
            c.FeatConfig.SampleRate = MicStream.OutputRate;
            c.FeatConfig.FeatureDim = 80;
            c.ModelConfig.Transducer.Encoder = ModelFiles.PathOf($"{folder}/encoder.onnx");
            c.ModelConfig.Transducer.Decoder = ModelFiles.PathOf($"{folder}/decoder.onnx");
            c.ModelConfig.Transducer.Joiner = ModelFiles.PathOf($"{folder}/joiner.onnx");
            c.ModelConfig.Tokens = ModelFiles.PathOf($"{folder}/tokens.txt");
            c.ModelConfig.NumThreads = threads;
            c.ModelConfig.Provider = "cpu";
            c.ModelConfig.Debug = 0;
            c.ModelConfig.ModelType = "";
            c.ModelConfig.ModelingUnit = "";
            c.DecodingMethod = "greedy_search";
            c.MaxActivePaths = 4;
            c.EnableEndpoint = 1;
            c.Rule1MinTrailingSilence = 2.4f;
            c.Rule2MinTrailingSilence = endpointTrailingSilence;
            c.Rule3MinUtteranceLength = maxUtteranceSeconds;
            c.BlankPenalty = blankPenalty; // > 0: fewer dropped words on faint audio
            _recognizer = new OnlineRecognizer(c);
            _stream = _recognizer.CreateStream();
            ApplyLanguage();
            _stream.AcceptWaveform(MicStream.OutputRate, LeadIn);
        }

        void ApplyLanguage()
        {
            if (_language != null) _stream.SetOption("language", _language);
        }

        public void SetLanguage(string language)
        {
            if (_language != null && !string.IsNullOrEmpty(language)) _pendingLanguage = language;
        }

        public void AcceptWaveform(float[] samples)
        {
            var pending = _pendingLanguage;
            if (pending != null) { _pendingLanguage = null; _language = pending; ApplyLanguage(); }
            _stream.AcceptWaveform(MicStream.OutputRate, samples);
            var decoded = false;
            while (_recognizer.IsReady(_stream)) { _recognizer.Decode(_stream); decoded = true; }
            if (!decoded) return; // nothing new until a whole chunk (560 ms) is in: the result can't have changed
            var r = _recognizer.GetResult(_stream);
            _text = r.Text?.Trim() ?? "";
            _wordStarts = Unlead(WordStartsOf(r.Tokens, r.Timestamps, _text));
        }

        // Tokens are word pieces; a piece starting with a space begins a new word.
        internal static float[] WordStartsOf(string[] tokens, float[] times, string text)
        {
            if (tokens == null || times == null || tokens.Length != times.Length || text.Length == 0) return Array.Empty<float>();
            var starts = new System.Collections.Generic.List<float>();
            for (var i = 0; i < tokens.Length; i++)
                if (i == 0 || tokens[i].StartsWith(" ") || tokens[i].StartsWith("\u2581")) starts.Add(times[i]);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return starts.Count == words ? starts.ToArray() : Array.Empty<float>();
        }

        public string Decode(float[] audio, out float[] wordStarts)
        {
            using var s = _recognizer.CreateStream();
            var language = _pendingLanguage ?? _language;
            if (language != null) s.SetOption("language", language);
            s.AcceptWaveform(MicStream.OutputRate, LeadIn);
            s.AcceptWaveform(MicStream.OutputRate, audio);
            s.InputFinished();
            while (_recognizer.IsReady(s)) _recognizer.Decode(s);
            var r = _recognizer.GetResult(s);
            var text = r.Text?.Trim() ?? "";
            wordStarts = Unlead(WordStartsOf(r.Tokens, r.Timestamps, text));
            return text;
        }

        public IClipDecoder BeginDecode()
        {
            var s = _recognizer.CreateStream();
            var language = _pendingLanguage ?? _language;
            if (language != null) s.SetOption("language", language);
            s.AcceptWaveform(MicStream.OutputRate, LeadIn);
            return new ClipDecoder(_recognizer, s);
        }

        sealed class ClipDecoder : IClipDecoder
        {
            readonly OnlineRecognizer _r;
            readonly OnlineStream _s;
            public ClipDecoder(OnlineRecognizer r, OnlineStream s) { _r = r; _s = s; }

            public void Feed(float[] samples, int offset, int count)
            {
                var part = new float[count];
                Array.Copy(samples, offset, part, 0, count);
                _s.AcceptWaveform(MicStream.OutputRate, part);
                while (_r.IsReady(_s)) _r.Decode(_s);
            }

            public string Finish(out float[] wordStarts)
            {
                _s.InputFinished();
                while (_r.IsReady(_s)) _r.Decode(_s);
                var r = _r.GetResult(_s);
                var text = r.Text?.Trim() ?? "";
                wordStarts = Unlead(WordStartsOf(r.Tokens, r.Timestamps, text));
                return text;
            }

            public void Dispose() => _s.Dispose();
        }

        public void Reset()
        {
            _recognizer.Reset(_stream);
            ApplyLanguage(); // (kept by Reset in sherpa-onnx 2.0; set again in case a later version clears it)
            _stream.AcceptWaveform(MicStream.OutputRate, LeadIn);
            _text = "";
            _wordStarts = Array.Empty<float>();
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _recognizer?.Dispose();
        }
    }
}
