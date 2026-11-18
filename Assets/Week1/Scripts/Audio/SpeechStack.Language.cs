using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Week1Bench
{
    /// <summary>
    /// Which language the live captions are in. English uses the English model (the most accurate on English); any other
    /// language uses the multilingual one (<see cref="SpeechRecognizers.Multilingual"/>), loaded the first time it's needed.
    /// <see cref="Options.Language"/>:
    ///   "en"   English only: the multilingual model is never loaded.
    ///   "auto" (default): the multilingual model with its language left to guess ("auto" prompt), from the first word
    ///          (the first seconds wait for it to load): on the test videos in seven languages it wrote each as well as
    ///          when told the language, English included. Each finished sentence's language (for translation) comes
    ///          from its text (<see cref="TextLanguage"/>); a sentence too short to tell keeps the running language.
    ///          (An audio language check, Whisper tiny, was removed in Nov 2026: through a loudspeaker it was wrong on
    ///          about one sentence in five, flipping German to English or Swedish, while the text check made no
    ///          mistakes on the same videos.)
    ///   "de", "ja"… that language, on the multilingual model.
    /// The multilingual model's files are copied separately (large), so nothing switches until
    /// <see cref="EnableLanguages"/> says they're on disk.
    /// </summary>
    public partial class SpeechStack
    {
        /// <summary>Languages the multilingual model has a prompt for (two-letter codes).</summary>
        public static readonly HashSet<string> MultilingualLanguages = new()
        {
            "en", "es", "fr", "de", "it", "pt", "nl", "sv", "da", "nb", "fi", "pl", "cs", "sk", "sl", "hr", "hu", "ro", "bg",
            "el", "et", "lt", "lv", "uk", "ru", "tr", "ar", "he", "hi", "th", "vi", "ja", "ko", "zh",
        };

        ISpeechRecognizer _english, _multi;
        volatile ISpeechRecognizer _multiLoaded;  // built on the language thread, taken by the speech thread
        Thread _langThread;
        readonly AutoResetEvent _langSignal = new(false);
        readonly ConcurrentQueue<string> _languageNotes = new();
        volatile bool _languagesReady, _loadMulti;
        volatile string _wantLanguage;   // from SetLanguage / EnableLanguages, for the speech thread
        volatile string _live = "en";    // what the captions are in now
        string _mode;                    // speech thread: "en", "auto" or a language
        string _switchTo;                // speech thread: switch when possible (the multilingual model may still be loading)

        /// <summary>The language the captions are in right now ("en", "de"…).</summary>
        public string LiveLanguage => _live;
        public string LanguageError { get; private set; }
        /// <summary>Things worth logging ("Captions now in de"), read on the main thread.</summary>
        public bool TryGetLanguageNote(out string note) => _languageNotes.TryDequeue(out note);
        bool KeepAudio => _languagesReady;

        /// <summary>"en", "auto" or a language code. Applied on the speech thread between sentences' audio.</summary>
        public void SetLanguage(string language)
        {
            _opt.Language = string.IsNullOrEmpty(language) ? "auto" : language;
            _wantLanguage = _opt.Language;
            if (NeedsMulti(_opt.Language) && _languagesReady) _loadMulti = true; // load now, not once someone talks
            _langSignal.Set();
        }

        // A language was chosen (or "auto"), its model isn't loaded yet but is coming.
        bool HoldForModel => _multi == null && LanguageError == null && _languagesReady &&
                             (_mode == "auto" || _switchTo != null && _switchTo != "en");

        // "auto" on the multilingual model: the recognizer guesses the language itself; only the label changes.
        bool AutoMulti => _mode == "auto" && _multi != null && _asr == _multi;
        string _lastTextLang; // the previous sentence's language by its text (null: it didn't tell)

        static bool NeedsMulti(string language) => language != "en"; // a chosen language, or "auto" (switches mid-sentence otherwise wait ~5 s)

        /// <summary>The multilingual model is on disk (ModelFiles.EnsureLanguages).</summary>
        public void EnableLanguages()
        {
            if (_languagesReady || !_running) return;
            _languagesReady = true;
            _wantLanguage = _opt.Language;
            _loadMulti = NeedsMulti(_opt.Language); // a chosen language: ready before the first words, not 5 s into them
            _langThread = new Thread(LanguageLoop) { IsBackground = true, Name = "SpeechLanguage", Priority = ThreadPriority.BelowNormal };
            _langThread.Start();
        }

        // ---------------- language thread: loads the multilingual model ----------------

        void LanguageLoop()
        {
            MetaStarter.Captions.BackgroundThread.Nice(3); // a moment late is fine, a dropped frame isn't
            while (_running)
            {
                if (_loadMulti && _multiLoaded == null)
                {
                    _loadMulti = false;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        _multiLoaded = SpeechRecognizers.Create(SpeechRecognizers.Multilingual, _opt.AsrThreads, _opt.EndpointTrailingSilence,
                            _opt.BlankPenalty, _opt.MaxUtteranceSeconds, _opt.Language == "auto" || _opt.Language == "en" ? "auto" : _opt.Language);
                        _languageNotes.Enqueue($"Multilingual model loaded in {sw.Elapsed.TotalSeconds:F1} s");
                    }
                    catch (Exception e) { LanguageError = $"multilingual model failed: {e.Message}"; _languageNotes.Enqueue(LanguageError); }
                }
                if (_multiLoaded != null) return; // loaded: nothing else to do here
                _langSignal.WaitOne(200);
            }
        }

        // ---------------- speech thread ----------------

        /// <summary>A finished sentence's language: on "auto", what its text says, else the language the captions are in.</summary>
        string SentenceLanguage(int id, string text)
        {
            if (!AutoMulti) return _live;
            var lang = TextLanguage.Of(text);
            if (lang == null || !MultilingualLanguages.Contains(lang)) { _lastTextLang = null; return _live; }
            // The sentence is labelled by its own text; the running language (live translation of the next words)
            // follows only two sentences in a row, or one long one: the recognizer now and then writes a short
            // sentence in another script ("一个工作 in box" amid Spanish).
            var tokens = text.Split(' ').Length;
            var cjk = 0;
            foreach (var c in text) if (c >= 0x3040 && c <= 0x9FFF || c >= 0xAC00 && c <= 0xD7AF) cjk++;
            var sure = lang == _lastTextLang || tokens >= 8 || cjk >= 12;
            _lastTextLang = lang;
            if (sure && lang != _live)
            {
                _languageNotes.Enqueue($"Captions now in {lang} (was {_live}, by text)");
                _live = lang;
            }
            return lang;
        }

        /// <summary>Before each piece of audio: apply a language change.
        /// <paramref name="utt"/> is the sentence so far, fed again to the new model so it isn't lost.</summary>
        void LanguageStep(List<float> utt)
        {
            if (_multi == null && _multiLoaded != null) _multi = _multiLoaded;
            var want = _wantLanguage;
            if (want != null)
            {
                _wantLanguage = null;
                _mode = want;
                _switchTo = want == "auto" ? null : want;
                if (want != "en" && want != "auto" && _languagesReady) _loadMulti = true;
                _langSignal.Set();
            }
            if (_mode == "auto" && _multi != null && _asr != _multi && LanguageError == null)
            {
                _multi.SetLanguage("auto");
                _asr = _multi;
                _asr.Reset();
                if (utt.Count > 0) _asr.AcceptWaveform(utt.ToArray());
                _switchTo = null;
                _languageNotes.Enqueue($"Captions in any language · {_asr.Name}");
            }
            if (AutoMulti) return; // the text of each sentence says its language (SentenceLanguage)
            if (_switchTo == null || !_languagesReady) return;
            if (_switchTo != "en" && _multi == null)
            {
                if (LanguageError != null) _switchTo = null; // the model didn't load: stay as we are
                else _loadMulti |= _multiLoaded == null;     // still loading
                return;
            }
            SwitchTo(_switchTo, utt);
            _switchTo = null;
        }

        void SwitchTo(string lang, List<float> utt)
        {
            var next = lang == "en" ? _english : _multi;
            if (next != _english) next.SetLanguage(lang);
            var from = _live;
            if (next != _asr || from != lang)
            {
                _asr = next;
                _asr.Reset();
                if (utt.Count > 0) _asr.AcceptWaveform(utt.ToArray());
            }
            _live = lang;
            _languageNotes.Enqueue($"Captions now in {lang} (was {from}) · {_asr.Name}");
        }

        void DisposeLanguages(bool asrDone)
        {
            var langDone = _langThread?.Join(5000) ?? true;
            if (!asrDone || !langDone) return;
            if (_multi != null) _multi.Dispose();
            else _multiLoaded?.Dispose();
        }
    }
}
