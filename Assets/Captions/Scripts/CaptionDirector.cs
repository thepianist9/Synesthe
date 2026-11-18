using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.InferenceEngine;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Speaker captions: listens (MicStream + SpeechStack), sees people (PersonTracker), and shows each
    /// utterance in a bubble beside the person who said it.
    ///
    /// Who is speaking? The Quest gives apps one mixed mic channel, so audio can't tell direction.
    /// Instead every word is matched to a face by time: the recognizer says when each word was spoken, and the word
    /// goes to whoever's mouth was moving at that moment (captions arrive ~1 s late, so "who is talking now" is the
    /// wrong question). Only faces that are visibly talking (mouth movement ≥ minMouthActivity) count — in videos and
    /// groups the voice is often someone off-screen; then the words go to an "unsure" bubble in front of you.
    /// When another face is clearly more active than the current speaker (by handoverMargin, for handoverHoldSeconds,
    /// or straight after a pause) the utterance is split at that word, so quick turn-taking doesn't need silence.
    ///
    /// Three kinds of evidence, strongest last:
    ///   1. Mouth movement — lip gap from the face mesh (PersonTracker), or BlazeFace's mouth keypoint without it.
    ///   2. Lip sync — does the lip gap rise and fall with the loudness we hear? A listener's nods and smiles don't.
    ///      Mouth movement that is out of step with the sound counts for half.
    ///   3. Voice fingerprints (VoicePrints) — every stretch of speech gets a voice; a voice confidently seen on a face
    ///      is bound to it. Later the voice alone can put words on that face when the lips are unclear, name a speaker
    ///      who is out of view, and split a sentence when the voice changes.
    ///
    /// Look: every detected person gets a small liquid-glass orb beside their face; when they speak the
    /// caption panel grows out of that orb and melts back into it after a few quiet seconds.
    /// </summary>
    public class CaptionDirector : MonoBehaviour
    {
        public MicStream mic;
        public PersonTracker tracker;
        public Transform head;
        [Header("Look")]
        public Material bubbleMaterial;   // MetaStarter/LiquidGlassBubble
        public TMP_FontAsset bodyFont;    // Inter SemiBold
        public TMP_FontAsset nameFont;    // Inter SemiBold
        public Material panelMaterial;    // debug face markers only

        [Header("Speech")]
        public SpeechModel speechModel = SpeechRecognizers.Default;
        /// <summary>Tests (VideoHarness): audio comes from PushAudio only — don't open the microphone.</summary>
        [HideInInspector] public bool noMic;
        [Tooltip("Speech detector threshold (0–1). Lower catches quieter speech, but noise starts captions more often. Launch extra: -e vad 0.3")]
        [Range(0.1f, 0.9f)] public float vadThreshold = 0.3f;
        [Tooltip("Live recognizer bias against \"nothing said\": brings back quiet words; too high invents words in noise. Launch extra: -e blank 0.5")]
        [Range(0f, 3f)] public float blankPenalty = 0.5f;
        const string LanguagePref = "captions.language";

        [Tooltip("What people speak. \"auto\": English, switching to another language when two sentences in a row (or one " +
                 "long one) are heard in it, and back. \"en\": English only. A code (de, es, fr, it, pt, nl, pl, tr, ru, uk, ar, hi, " +
                 "ja, ko, zh, vi…): that language, on the multilingual model. Launch extra: -e lang de")]
        public string speechLanguage = "auto";
        bool _languagesStarted;

        /// <summary>The language the captions are in right now ("en", "de"…).</summary>
        public string LiveLanguage => _stack?.LiveLanguage ?? "en";

        /// <summary>Change the spoken language while running (remembered for next time).</summary>
        public void SetLanguage(string code)
        {
            speechLanguage = string.IsNullOrEmpty(code) ? "auto" : code;
            PlayerPrefs.SetString(LanguagePref, speechLanguage);
            PlayerPrefs.Save();
            _stack?.SetLanguage(speechLanguage);
            if (_stack != null && speechLanguage != "en") StartCoroutine(StartLanguages());
            Note($"Language → {speechLanguage}");
        }

        // The multilingual model and the language check are copied after captions have started (the first time:
        // ~750 MB, some seconds on the Quest) and then handed to the speech stack.
        const string TranslatePref = "captions.translate", MyLanguagePref = "captions.myLanguage";

        /// <summary>Subtitles mode: only the words, in one strip at the bottom of your view like a video's closed
        /// captions. Nobody is tagged or tracked (the face tracker rests), so it's also the lightest mode.</summary>
        public bool Subtitles => mode == CaptionMode.Subtitles;
        /// <summary>Bubbles mode while the headset has lost track of where it is in the room (too dark, too bright,
        /// plain walls; "Continue without tracking"): bubbles can't stay beside people, so captions go to the subtitle
        /// strip until tracking has been steady again for a few seconds. The chosen mode stays Bubbles.</summary>
        public bool TrackingFallback { get; private set; }
        // Captions go in the one strip: Subtitles mode, or Bubbles without tracking.
        bool Strip => Subtitles || TrackingFallback;
        const float TrackingLostAfter = 1.5f, TrackingBackAfter = 3f;
        float _trackingChangedAt = -1f;
        bool _trackedNow = true;
        /// <summary>Tests (Editor): pretend the headset has lost position tracking.</summary>
        [HideInInspector] public bool simTrackingLost;
        [Tooltip("Show finished captions in your language (MyLanguage) instead of as spoken: other languages are translated " +
                 "on the headset with Firefox's translation models (20–60 MB per language; every speech language comes down in the background the first time, about 1.1 GB). Live " +
                 "words stay as spoken.")]
        public bool translate;
        CaptionTranslator _translator;

        /// <summary>The viewer's language, that captions are translated into: the headset's (English if captions can't be
        /// in it), unless set (PlayerPrefs captions.myLanguage, launch extra -e mylang de).</summary>
        public string MyLanguage => PlayerPrefs.GetString(MyLanguagePref, "") is { Length: 2 } l ? l : CaptionTranslator.DeviceLanguage();

        /// <summary>For the menu: getting the model, why translating failed; null when all is well.</summary>
        public string TranslateStatus => _translator?.Status ?? _translator?.Error ?? _translator?.FetchError;

        /// <summary>Captions as spoken (false), or translated into <see cref="MyLanguage"/> (true).</summary>
        public void SetTranslate(bool on)
        {
            translate = on;
            PlayerPrefs.SetInt(TranslatePref, on ? 1 : 0);
            PlayerPrefs.Save();
            if (on && _stack != null) StartCoroutine(StartTranslator());
            if (!on) { _translator?.Dispose(); _translator = null; }
            Note(on ? $"Translating captions into {CaptionTranslator.Name(MyLanguage)}" : "Captions as spoken");
        }

        /// <summary>Auto has settled on a language that isn't English: the menu offers to set it (fixed is faster and
        /// more accurate than auto, which keeps checking and can switch wrongly). At most once per language per run,
        /// and never again for a language you chose to keep Auto for (<see cref="KeepAuto"/>).</summary>
        public event System.Action<string> SuggestLanguage;

        string _autoLang;
        int _autoCaptions, _autoWords;
        float _autoSince;
        float _englishAt = -999f; // the last finished caption in English (or your language)
        // English heard this recently: a mixed conversation (say your friend speaks Spanish and you English), where a
        // fixed language would lose the English: told "es", the multilingual model drops most English sentences in a
        // noisy room (Auto keeps them), so it isn't offered.
        const float MixedSeconds = 300f;
        readonly HashSet<string> _suggested = new();
        const string KeepAutoPref = "captions.keepAuto.";

        /// <summary>"Keep Auto" for that language: it won't be suggested again.</summary>
        public void KeepAuto(string lang)
        {
            PlayerPrefs.SetInt(KeepAutoPref + lang, 1);
            PlayerPrefs.Save();
            Note($"Keeping Auto for {lang}: not asked again");
        }

        // Per finished caption: has Auto been in the same other language long enough to be sure? Three captions, 12 words
        // and 10 s in it, with nothing in between, so one misheard sentence never asks.
        void WatchAutoLanguage(string lang, int words)
        {
            if (lang == "en" || lang == MyLanguage) _englishAt = Time.time;
            if (speechLanguage != "auto" || string.IsNullOrEmpty(lang) || lang != LiveLanguage || lang == "en")
            {
                if (lang != _autoLang) _autoLang = null;
                return;
            }
            if (lang != _autoLang) { _autoLang = lang; _autoCaptions = 0; _autoWords = 0; _autoSince = Time.time; }
            _autoCaptions++;
            _autoWords += words;
            if (_autoCaptions < 3 || _autoWords < 12 || Time.time - _autoSince < 10f) return;
            if (_suggested.Contains(lang) || PlayerPrefs.GetInt(KeepAutoPref + lang, 0) == 1 || SuggestLanguage == null) return;
            if (Time.time - _englishAt < MixedSeconds) return; // mixed: stay on Auto (asked later if it's all lang)
            _suggested.Add(lang);
            Note($"Suggesting {lang}: auto has been in it for {_autoCaptions} captions, {Time.time - _autoSince:F0} s");
            SuggestLanguage(lang);
        }

        /// <summary>The language captions are translated into (Settings › My language).</summary>
        public void SetMyLanguage(string code)
        {
            PlayerPrefs.SetString(MyLanguagePref, code);
            PlayerPrefs.Save();
            Note($"My language → {code}");
            if (_translator != null)
            {
                _translator.Prepare(speechLanguage == "auto" ? "en" : speechLanguage, code);
                _translator.FetchAll(Week1Bench.SpeechStack.MultilingualLanguages, code);
            }
        }

        // The translator, with the likely pair (the speech language, English if it's detected) ready before the first caption.
        IEnumerator StartTranslator()
        {
            if (translate && _translator == null)
            {
                _translator = new CaptionTranslator();
                _translator.Prepare(speechLanguage == "auto" ? "en" : speechLanguage, MyLanguage);
                // Then, in the background, every other language people may speak (one time, about 1.1 GB).
                _translator.FetchAll(Week1Bench.SpeechStack.MultilingualLanguages, MyLanguage);
            }
            yield break;
        }

        // A finished segment in `language`: translate it if it isn't in the viewer's. Your own words stay as you said them.
        // What its live translation already shows stays as it is; only the words after it are translated and added.
        void Translate(Seg seg, string language)
        {
            if (!translate || _translator == null || string.IsNullOrEmpty(language) || language == MyLanguage) return;
            var text = seg.Source ?? seg.Committed;
            if (string.IsNullOrEmpty(text) || seg.Wearer && seg.Who == null || text == seg.Asked) return;
            seg.Asked = text;
            string piece = text, prefix = null;
            var rest = string.IsNullOrEmpty(seg.TrText) ? null : Remainder(seg, text);
            if (rest != null)
            {
                if (rest.Length == 0)
                {
                    _awaiting.RemoveAll(a => a.seg == seg);
                    seg.AskedPiece = null;
                    if (seg.Committed != seg.TrText) ApplyRefined(seg, seg.TrText, new SpeechStack.Refined { Id = seg.Id, Text = seg.TrText, Language = MyLanguage });
                    return;
                }
                piece = rest;
                prefix = seg.TrText;
            }
            seg.AskedPiece = piece;
            seg.AskedPrefix = prefix;
            _awaiting.RemoveAll(a => a.seg == seg);
            _awaiting.Add((seg, Time.time));
            _translator.Request(new CaptionTranslator.Job { Tag = seg, Text = piece, From = language, To = MyLanguage });
        }

        // The words of `source` after the ones its live translation covers ("" none; null: they no longer match).
        static string Remainder(Seg seg, string source)
        {
            var src = Words(source);
            var covered = Words(seg.TrSource).Length;
            // The final text has a word fewer than was translated live (re-decoded): keep what they're reading. More
            // than that went to the next speaker: translate it again without those words.
            if (covered > src.Length) return covered - src.Length <= 1 && Similarity(source, seg.TrSource) >= 0.6f ? "" : null;
            if (Similarity(string.Join(" ", src[..covered]), seg.TrSource) < 0.6f) return null;
            return string.Join(" ", src[covered..]);
        }

        // While translating, a caption never shows the words as spoken (they interrupt reading): a finished one shows its
        // live translation so far, or Waiting, until its translation comes. As spoken only if that fails or takes too long.
        const string Waiting = "…";
        const float TranslateWaitSeconds = 6f;
        readonly List<(Seg seg, float since)> _awaiting = new();

        // What a finished segment shows for `source` (and remembers it as spoken).
        string Shown(Seg seg, string source, string language)
        {
            seg.Source = source;
            if (!LiveTranslating(language) || seg.Wearer && seg.Who == null) return source;
            seg.TrDone = true;
            if (string.IsNullOrEmpty(seg.TrText)) return Waiting;
            return Remainder(seg, source) is "" ? seg.TrText : seg.TrText + " " + Waiting;
        }

        // Its translation failed or is slow: show it as spoken rather than nothing (the translation still replaces it).
        void ShowSource(Seg seg, string why)
        {
            if (string.IsNullOrEmpty(seg.Source) || seg.Committed == null || !seg.Committed.EndsWith(Waiting)) return;
            var rest = string.IsNullOrEmpty(seg.TrText) ? null : Remainder(seg, seg.Source);
            var text = rest != null ? seg.TrText + " " + rest : seg.Source;
            Note($"Caption #{seg.Id}: shown as spoken ({why})");
            ApplyRefined(seg, text, new SpeechStack.Refined { Id = seg.Id, Text = text, Language = LiveLanguage });
        }

        void CheckAwaiting()
        {
            for (var i = _awaiting.Count - 1; i >= 0; i--)
                if (Time.time - _awaiting[i].since > TranslateWaitSeconds)
                {
                    var seg = _awaiting[i].seg;
                    _awaiting.RemoveAt(i);
                    ShowSource(seg, $"no translation after {TranslateWaitSeconds:F0} s");
                }
        }

        // Live translation, for reading comfort: the caption being spoken is translated a piece at a time (a clause, or
        // a few words), each piece once, and added after the last. What you are reading never changes under you; the
        // newest words show as Waiting until their piece is translated (Firefox's models take tens of ms a piece).
        Seg _trSeg;                  // the live segment
        string _trWanted;            // its newest words
        string _trPieceAsked;        // the piece being translated
        bool _trBusy;
        float _trAskedAt;
        const int PieceMin = 3, PieceMax = 5, PieceKeep = 1, FirstPiece = 3; // the first piece goes early so a caption shows fast
        const float SameEnough = 0.75f;

        bool LiveTranslating(string language) =>
            translate && _translator != null && !string.IsNullOrEmpty(language) && language != MyLanguage;

        // What the live caption shows for `text`: its translated pieces, then Waiting for the words not translated yet.
        string LiveText(Seg seg, string text, out bool translated)
        {
            translated = false;
            if (!LiveTranslating(LiveLanguage) || seg.Wearer) return text;
            _trSeg = seg;
            _trWanted = text;
            AskLiveTranslation();
            translated = true;
            return LiveShown(seg);
        }

        string LiveShown(Seg seg)
        {
            if (string.IsNullOrEmpty(seg.TrText)) return Waiting;
            return Words(_trWanted).Length > Words(seg.TrSource).Length ? seg.TrText + " " + Waiting : seg.TrText;
        }

        // How alike two captions read, 0–1: the share of their words in common, in order.
        static float Similarity(string a, string b)
        {
            var x = System.Array.ConvertAll(Words(a), Plain);
            var y = System.Array.ConvertAll(Words(b), Plain);
            if (x.Length == 0 || y.Length == 0) return x.Length == y.Length ? 1f : 0f;
            var d = new int[x.Length + 1, y.Length + 1];
            for (var i = 1; i <= x.Length; i++)
                for (var j = 1; j <= y.Length; j++)
                    d[i, j] = x[i - 1] == y[j - 1] ? d[i - 1, j - 1] + 1 : Mathf.Max(d[i - 1, j], d[i, j - 1]);
            return 2f * d[x.Length, y.Length] / (x.Length + y.Length);
        }

        static bool EndsClause(string word) => word.Length > 0 && ",.?!;:،؟。，、？！".IndexOf(word[^1]) >= 0;

        // How many words after `from` make the next piece: up to the first clause end that a later word confirms,
        // or, in a long clause, all but the last PieceKeep words (the recognizer may still change those). The caption's
        // first piece is shorter (FirstPiece words), so a translation shows about a second after the speaker starts.
        static int NextPiece(string[] words, int from)
        {
            for (var i = from + PieceMin - 1; i < words.Length - 1; i++)
                if (EndsClause(words[i])) return i - from + 1;
            var pending = words.Length - from;
            return pending >= (from == 0 ? FirstPiece : PieceMax) + PieceKeep ? pending - PieceKeep : 0;
        }

        void AskLiveTranslation()
        {
            if (_trBusy && Time.time - _trAskedAt < 3f) return; // (a lost answer doesn't stop it for good)
            if (_trSeg == null || _trSeg.TrDone || _trWanted == null || !LiveTranslating(LiveLanguage)) return;
            if (!_inUtterance || _segs.Count == 0 || _segs[^1] != _trSeg) return;
            var words = Words(_trWanted);
            var from = Words(_trSeg.TrSource).Length;
            if (from >= words.Length) return;
            var n = NextPiece(words, from);
            if (n <= 0) return;
            _trBusy = true;
            _trPieceAsked = string.Join(" ", words[from..(from + n)]);
            _trAskedAt = Time.time;
            _translator.Request(new CaptionTranslator.Job { Tag = _trSeg, Text = _trPieceAsked, From = LiveLanguage, To = MyLanguage, Live = true });
        }

        void OnLiveTranslated(CaptionTranslator.Job job)
        {
            _trBusy = false;
            var seg = (Seg)job.Tag;
            if (seg.TrDone || job.Text != _trPieceAsked || job.Error != null || string.IsNullOrEmpty(job.Result)) return;
            _trPieceAsked = null;
            var result = job.Result.Trim();
            // A piece that ends mid-sentence: the translator tends to close it with a full stop anyway.
            if (!".?!。？！".Contains(job.Text[^1]) && result.EndsWith(".")) result = result[..^1];
            seg.TrSource = string.IsNullOrEmpty(seg.TrSource) ? job.Text : seg.TrSource + " " + job.Text;
            seg.TrText = string.IsNullOrEmpty(seg.TrText) ? result : seg.TrText + " " + result;
            if (_inUtterance && _segs.Count > 0 && _segs[^1] == seg)
                BubbleFor(seg.Who).Show(LabelOf(seg), Pretty(Rolling(seg.Who, LiveShown(seg))), final: false);
            AskLiveTranslation(); // the next piece, if it's ready
        }

        void OnTranslated(CaptionTranslator.Job job)
        {
            if (job.Live) { OnLiveTranslated(job); return; }
            var seg = (Seg)job.Tag;
            if (seg.AskedPiece != job.Text) return; // read again since: that text has its own translation coming
            seg.AskedPiece = null;
            _awaiting.RemoveAll(a => a.seg == seg);
            if (job.Error != null) { Note($"Translation failed: {job.Error}"); ShowSource(seg, "translation failed"); return; }
            if (logEveryWord) Debug.Log($"[Captions] translated {job.From}→{job.To} in {job.Seconds:F2} s: \"{job.Text}\" → \"{job.Result}\"");
            // Already in my language (the spoken-language setting was wrong, e.g. German set for an English talk): the
            // "translation" reads the same as what was said. Keep the caption as shown: swapping in a near-copy a moment
            // after the sentence lands makes the text jump, and the translator mangles words ("haven't" → "have't").
            if (seg.AskedPrefix == null && !string.IsNullOrEmpty(seg.Source) && Similarity(seg.Source, job.Result) >= SameEnough)
            {
                Note($"Caption #{seg.Id} kept as spoken: already reads as {job.To} (spoken language set to {job.From})");
                if (seg.Committed != null && seg.Committed.EndsWith(Waiting)) ShowSource(seg, "already in my language");
                return;
            }
            // The end of a caption whose start is already showing: added after it, the rest stays.
            var result = seg.AskedPrefix != null ? seg.AskedPrefix + " " + job.Result.Trim() : job.Result;
            // A whole new translation of a caption already shown translated: only if it reads clearly differently.
            if (seg.AskedPrefix == null && !seg.Committed.EndsWith(Waiting) && seg.Committed != seg.Source &&
                Similarity(seg.Committed, result) >= SameEnough)
            {
                if (logEveryWord) Debug.Log($"[Captions] kept #{seg.Id} as shown (new translation reads much the same)");
                return;
            }
            ApplyRefined(seg, result, new SpeechStack.Refined { Id = seg.Id, Text = result, Language = job.To,
                DecodeSeconds = job.Seconds });
        }

        IEnumerator StartLanguages()
        {
            if (_languagesStarted) yield break;
            _languagesStarted = true;
            var t0 = Time.realtimeSinceStartup;
            yield return ModelFiles.EnsureLanguages();
            if (!ModelFiles.LanguagesReady) { Note("Other languages aren't in this build: English only"); yield break; }
            ScriptFonts.AddTo(bodyFont, nameFont);
            _stack?.EnableLanguages();
            Note($"Other languages ready ({Time.realtimeSinceStartup - t0:F1} s)");
        }

        [Header("Speaker choice")]
        [Range(0, 1)] public float mouthWeight = 0.6f;
        [Tooltip("People further than this from the centre of view are only picked if nobody else is visible.")]
        public float maxAngleDegrees = 45f;
        [Tooltip("A face must move its mouth at least this much (0–1) to get a caption. A face that is merely visible is not enough: in videos and groups the speaker is often off-screen.")]
        [Range(0, 1)] public float minMouthActivity = 0.25f;
        [Tooltip("Turn change: another face's mouth moves at least this much more than the current speaker's…")]
        [Range(0, 1)] public float handoverMargin = 0.15f;
        [Tooltip("…for this long (seconds of speech). Listeners nod and smile, so it is a comparison, not \"current speaker went still\".")]
        public float handoverHoldSeconds = 0.4f;
        [Tooltip("After a pause this long between words, one word is enough to change speaker.")]
        public float turnPauseSeconds = 0.5f;
        [Tooltip("Mouth movement is judged from this long before a word starts to this long after (seconds).")]
        public Vector2 wordWindow = new(0.1f, 0.6f);
        [Tooltip("Fewer lip samples a second than this in a window: the face counts as not measured there, not as still.")]
        public float minLipSamplesPerSecond = 5f;
        [Tooltip("A person counts as present if their face was seen within this many seconds.")]
        public float recentSeconds = 2f;
        [Tooltip("Only one face in view: it gets the caption whenever its mouth moves at least this much (lip sync is " +
                 "not needed — there is nobody to confuse it with).")]
        [Range(0, 1)] public float singleFaceMouth = 0.12f;
        [Tooltip("…and its lips are at least this much in step with the sound (a listener laughing or nodding moves " +
                 "their mouth too, but out of step). Only needed to take the turn: once talking, they keep it until " +
                 "their mouth goes still.")]
        [Range(-1, 1)] public float singleFaceSync = 0.1f;
        [Tooltip("The speaker's mouth below this (0–1)…")]
        [Range(0, 1)] public float stillMouth = 0.08f;
        [Tooltip("…for this long while words keep coming → someone else (off-screen) is talking.")]
        public float stillSeconds = 1.2f;

        [Header("Bubbles")]
        [Tooltip("A bubble stays open while its person is talking (pauses between sentences too) and this long after " +
                 "they stop, so there is time to read it. New words in that time continue the same bubble.")]
        public float captionHoldSeconds = 2f;
        [Tooltip("Your own speech: with the Quest's own mic, words nobody in view was seen saying and this loud on the raw " +
                 "mic (dBFS, before boost) are yours. They go to a small \"You\" line at the bottom centre of the view, " +
                 "and aren't signed back to you. Other people arrive ~40 dB quieter than the wearer (Week 1 Test 6).")]
        public float wearerRawDb = -50f;
        public bool detectWearer = true;
        [Tooltip("Once anyone is tagged in the setup, only tagged people get captions: other voices nearby (a next " +
                 "table, people passing) are ignored. Nobody tagged: everyone gets captions.")]
        public bool onlyTagged = true;
        [Tooltip("Show your own words in the \"You\" line. Off: what you say isn't shown at all (it's still heard, so it " +
                 "never lands in anyone else's caption). Saved (captions.showMine); launch extra -e mine 0|1.")]
        public bool showMine = true;
        [Tooltip("With someone tagged, in Bubbles mode: only tagged people's words are shown. Words nobody tagged was seen " +
                 "saying (\"Someone\", an unknown voice, people out of view) are dropped instead of going to the subtitle " +
                 "line. Subtitles mode still shows everyone. Saved (captions.taggedOnlyStrict); launch extra -e strays 0|1.")]
        public bool hideStrays;
        const string ShowMinePref = "captions.showMine", HideStraysPref = "captions.taggedOnlyStrict";
        // Words that would go to the subtitle line in Bubbles mode while people are tagged: dropped (hideStrays).
        // Subtitles mode: the subtitle line pinned to a spot in the room (pinch it and drag, e.g. onto a radio or a TV
        // across the room) instead of the bottom of your view. Kept until you leave Subtitles mode.
        Vector3? _stripPin;
        bool _stripHeld;
        /// <summary>The subtitle line, for the menu's pointer.</summary>
        public Transform SubtitleStrip => _fallback != null ? _fallback.transform : null;
        /// <summary>Subtitles mode with words on the line: it can be picked up.</summary>
        public bool SubtitlesShowing => Subtitles && live && _fallback != null && _fallback.IsShowing && !_fallback.Suppressed;
        /// <summary>Subtitles mode is live: the line can be picked up even with no words on it (pointing at its spot shows
        /// a hint there to take hold of).</summary>
        public bool SubtitlesGrabbable => Subtitles && live && _fallback != null && !_fallback.Suppressed;
        /// <summary>Where the subtitle line is (or would be, with no words on it).</summary>
        public Vector3 StripAnchor => _fallback.IsShowing ? _fallback.transform.position
            : _stripPin ?? head.position + Flat(head.forward) * subtitleDistance + Vector3.down * (subtitleDistance * 0.41f);
        /// <summary>Pointing at the empty subtitle spot: show something there to grab.</summary>
        public void HintStrip()
        {
            if (_fallback != null && !_fallback.IsShowing) _fallback.Show("", "Subtitles · pinch and drag to place them", final: true);
        }
        /// <summary>Move the subtitle line to <paramref name="at"/> and keep it there (<paramref name="held"/> while the
        /// pinch is still closed: it follows the hand closely).</summary>
        public void PinSubtitles(Vector3 at, bool held)
        {
            if (!Subtitles) return;
            if (_stripPin == null) Note("Subtitles pinned in the room");
            _stripPin = at;
            _stripHeld = held;
        }
        bool StraysHidden => hideStrays && !Strip && tracker != null && tracker.AnyTagged;
        [Tooltip("Your own voice print (learned in the setup, kept between sessions): speech that sounds like you goes " +
                 "to your strip, speech that clearly doesn't stays with the faces, whatever the loudness says. Works with " +
                 "any mic. Without it, only loudness on the Quest mic (wearerRawDb) can tell.")]
        public bool useWearerVoice = true;
        [Tooltip("Voice-print similarity to you (TitaNet, cosine) at or above which speech is yours.")]
        public float selfSimilar = 0.35f;
        const float StrictSelf = 0.28f; // with your words hidden: this like your voice is enough
        [Tooltip("At or below this it is someone else, even when loud. In between, loudness on the Quest mic decides. " +
                 "EasyCom: every wrong \"yours\" in between was ≤ 0.22, every right one ≥ 0.26.")]
        public float notSelf = 0.25f;
        [Tooltip("Turn change by voice: a 1.5 s stretch this unlike the caption's speaker (cosine) starts a new caption at once…")]
        [Range(-1, 1)] public float voiceTurnBelow = 0.12f;
        [Tooltip("…or two stretches in a row below this.")]
        [Range(-1, 1)] public float voiceTurnTwiceBelow = 0.25f;
        [Tooltip("Turn change by pitch: the last 0.5 s this many semitones above or below the caption's own voice " +
                 "(a man and a woman are ~8–12 apart) starts a new caption within a few tenths of a second.")]
        public bool usePitchTurns = true;
        [Tooltip("The two voice groups (low, high) must be at least this many semitones apart to be used.")]
        public float pitchTurnSemitones = 5f;
        [HideInInspector] public bool loudnessAloneWithVoice; // tests: the old rule (loudness alone still makes a word yours)
        [Tooltip("Seconds of your speech to learn your voice from.")]
        public float enrolSeconds = 8f;
        /// <summary>Tests (VideoHarness): the Quest mic's raw level to use instead of the mic's, in dBFS; NaN = the mic.</summary>
        [HideInInspector] public float simRawDb = float.NaN;

        [Header("Placement")]
        [Tooltip("Where the orb floats relative to the face: metres to the side, and up (at 1.2 m; scales with distance).")]
        public Vector2 orbOffset = new(0.13f, 0.03f);
        [Tooltip("Another face within this angle → the bubble grows away from it instead of towards the centre of view.")]
        public float neighbourDegrees = 90f;
        [Tooltip("A face closer than this (degrees) on a bubble's side makes it move to the other side (bubble closed, after 1 s).")]
        public float crowdDegrees = 25f;
        [Tooltip("Gap between the side of the face and the orb (m at 1.2 m; scales with distance).")]
        public float faceGap = 0.035f;
        public bool showFaceMarkers = false;
        [Tooltip("In the Editor: start with the debug view on (face boxes, lip outlines, talking/lip-sync scores, voices and " +
                 "decisions). On the headset it is the Settings switch (remembered), or the launch extra -e debug 0|1.")]
        public bool showDebug = true;
        [Tooltip("Log every word's speaker evidence (tuning).")]
        public bool logEveryWord = true;

        [Header("Lip sync (needs lip landmarks)")]
        [Tooltip("Correlation of lip gap with loudness that counts as fully in sync. Out-of-sync mouth movement counts for half.")]
        public float fullSync = 0.8f;
        [Tooltip("Share of the mouth movement that still counts when it is not in step with the sound at all.")]
        [Range(0, 1)] public float outOfSyncWeight = 0.1f;
        [Tooltip("1 = mouth movement counts linearly; 0.5 = square root, so a quiet talker's small, in-sync lip movement " +
                 "isn't beaten by a listener laughing with big, out-of-sync movement.")]
        [Range(0.2f, 1f)] public float mouthExponent = 0.5f;
        [Tooltip("Lip sync is judged over this long before and after a word (s); correlation needs ~1.5 s of frames.")]
        public Vector2 syncWindow = new(0.4f, 0.9f);

        [Header("Voice onset ↔ lips (fast speaker binding)")]
        [Tooltip("When the voice starts, give the burst of speech to the face whose lips start moving fast at that moment " +
                 "(lip speed, not lip opening: a mouth just open or closed isn't talking). Decided ~0.45 s after the " +
                 "voice starts, so words go straight to that person.")]
        public bool useOnsets = true;
        public SpeechOnsets.Settings onsetSettings = new();
        [Tooltip("LR-ASD overrules the onset owner when it rates another face at least this much more likely to be talking.")]
        [Range(0, 1)] public float asdOverruleMargin = 0.2f;

        [Header("Combined decision (who is talking, word by word)")]
        [Tooltip("One running belief over who is talking (each face in view, or someone out of view), updated every word " +
                 "from all the evidence at once: LR-ASD or lip movement, whose lips sped up when the voice started, " +
                 "and whose voice it sounds like. A change of speaker is cheap after a pause or a new burst of voice and " +
                 "expensive mid-phrase, so the caption doesn't flicker between faces. Off = the older rule chain.")]
        public bool useFusion = true;
        [Tooltip("Chance the speaker changes between two words of one phrase (no pause, no new burst of voice).")]
        [Range(0.001f, 0.5f)] public float switchPerWord = 0.03f;
        [Tooltip("…after a pause of at least turnPauseSeconds, or at the start of an utterance.")]
        [Range(0.01f, 0.95f)] public float switchAfterPause = 0.35f;
        [Tooltip("…at a new burst of voice (the loudness jumped: a new phrase, or someone cutting in).")]
        [Range(0.01f, 0.95f)] public float switchAtOnset = 0.2f;
        [Tooltip("…between two words of one sentence (the recognizer wrote no punctuation after the first), even after a " +
                 "pause or a new burst of voice: people pause mid-sentence far more often than they hand over mid-sentence.")]
        [Range(0.001f, 0.5f)] public float switchMidSentence = 0.04f;
        [Tooltip("LR-ASD's probability is pulled this far towards 0.5 (1 = as is). It rates every face near speech high, " +
                 "and small differences near 1 would otherwise count as strong evidence.")]
        [Range(0.2f, 1f)] public float asdSharpness = 0.7f;
        [Tooltip("How likely it is that someone out of view talks while every face in view looks silent (relative).")]
        [Range(0.01f, 2f)] public float offscreenWeight = 0.5f;
        [Tooltip("With only one person around (seen in the last few seconds), someone out of view talking is this much " +
                 "less likely: their words go to them unless their lips clearly say otherwise. On the headset, LR-ASD often " +
                 "rates a lone talker 0.2-0.3 and the camera loses their face at the edge of the view, which sent most of " +
                 "their words to the subtitle line.")]
        [Range(0.05f, 1f)] public float soloOffscreenFactor = 0.35f;
        [Tooltip("How far in front of you the subtitle line sits (m), at the same ~22° below eye level whatever the " +
                 "distance. Closer reads bigger and feels more like your own screen.")]
        [Range(0.5f, 1.5f)] public float subtitleDistance = 0.8f;
        [Tooltip("Hand the caption to another face once the belief that they are talking reaches this.")]
        [Range(0.5f, 0.95f)] public float switchBelief = 0.6f;
        [Tooltip("Inside a sentence (the recognizer punctuates and the last word ends no clause) the belief needed to hand the words to someone else, who must also have led for two words in a row.")]
        [Range(0.5f, 0.99f)] public float switchBeliefMidSentence = 0.85f;
        [Tooltip("The person the belief is on stays a candidate this long after their face was last seen (evidence " +
                 "\"unseen\"), so a talker whose face drops out for a moment keeps their words instead of them going to " +
                 "\"out of view\". 0 = off. 2026-11-10 screen A/B with both faces visible ~88%: 4 s → 81%, 76% of words to the " +
                 "right person vs 72%, 72%, 72% off (unsure words 0–5 vs 10–21). Earlier the same day, with a dim screen " +
                 "and faces often lost, it scored lower (60% vs 65%) — re-check with real people.")]
        public float speakerHoldSeconds = 4f;
        [Tooltip("Show startup progress (Starting…, Looking for people…, Ready) in a status bubble. Off: only errors " +
                 "and tracking news are shown; progress goes to the log.")]
        public bool showLoadingStatus = false;
        [Tooltip("Trust the lips by how good the picture of the face is: a dark/flat crop (contrast = grey std, 0–255) " +
                 "or a small face (box side, camera px) moves the face's evidence toward \"not seen\", so the voice " +
                 "decides more. Full trust above the upper values, none at the lower. Launch: -e quality 0 = off.")]
        public bool weighLipsByQuality = true;
        public Vector2 lipContrastRange = new(8f, 24f);
        public Vector2 lipFaceSizePx = new(24f, 56f);
        [Tooltip("Tie anonymous voices to the person whose voice profile they clearly match (VoicePrints.PersonOf). Off: " +
                 "first A/B (poor tracking), voices stayed split (4–5 for 2 people vs 2–3), as a wrong tie blocks merges; " +
                 "evening A/B (good tracking) 71% and 85% vs 72% off — too noisy to call. Launch: -e voiceties 1")]
        public bool voiceProfileTies;
        [Tooltip("Look back: an unsure caption that ended at most this long before a new caption the faces give to " +
                 "someone is theirs too (people rarely hand over within a breath), unless it sounds unlike them.")]
        public float reviseGapSeconds = 1.0f;
        bool _noRevise; // A/B launch extra: also no "unsure between two stretches" merge and no voice ↔ profile ties
        [Tooltip("How much the voice-onset ↔ lips owner counts: their talking evidence moves this far towards 1, " +
                 "everyone else's this far towards 0.")]
        [Range(0, 1)] public float onsetTrust = 0.5f;
        [Tooltip("A word is 'sure' (teaches that person's voice profile) when their talking evidence is at least this…")]
        [Range(0.5f, 1f)] public float sureEvidence = 0.8f;
        [Tooltip("…and every other face's at most this.")]
        [Range(0f, 0.5f)] public float sureOthers = 0.3f;
        [Tooltip("For someone whose voice isn't learned yet: a word is 'sure' at this talking evidence (and the others quiet, " +
                 "and them the likeliest speaker), so tagged people get a voice in the first minute too, not only those whose " +
                 "lips the camera reads very clearly (2026-11-18: only one of three tagged people ever got past 0.8).")]
        [Range(0.3f, 1f)] public float newcomerSureEvidence = 0.5f;
        [Tooltip("With your words hidden: a word isn't taken as yours while a face in view is talking at least this much, " +
                 "unless your voice is clearly yours (selfSimilar + 0.1).")]
        [Range(0.3f, 1f)] public float othersTalkingEvidence = 0.5f;
        public VoiceProfiles.Settings voiceProfileSettings = new();

        [Header("Active speaker detection (LR-ASD)")]
        [Tooltip("LR-ASD encoder (lr_asd_encoder.onnx): face and sound features of each 0.48 s chunk, computed once. " +
                 "With the backend, a model trained to tell talking from laughing, smiling and nodding. Replaces the " +
                 "lip-gap and lip-sync rules wherever it has scored the face.")]
        public ModelAsset activeSpeakerEncoder;
        [Tooltip("LR-ASD backend (lr_asd_backend.onnx): speaking scores from the last 1 s of encoded frames.")]
        public ModelAsset activeSpeakerBackend;
        public bool useActiveSpeaker = true;
        [Tooltip("Speaking probability that counts as talking.")]
        [Range(0, 1)] public float asdThreshold = 0.5f;
        [Tooltip("Below this the current speaker counts as silent (someone off-screen may be talking).")]
        [Range(0, 1)] public float asdStill = 0.2f;
        [Tooltip("Debug: save the model's inputs (face crops as PGM strips) of runs after this many seconds to asdDumpFolder.")]
        public string asdDumpFolder = "";
        public float asdDumpAfter = 5f;
        [Tooltip("Debug: log every LR-ASD speaking logit (one line per face per scored chunk).")]
        public bool logSpeakerScores;
        [Tooltip("Debug: with asdDumpFolder set, save every encoded chunk (inputs and encoder outputs) per person.")]
        public bool asdDumpAll;

        [Header("Voice fingerprints")]
        public bool useVoicePrints = true;
        [Tooltip("Sounds that aren't words in the captions: (laughs), (cries), (sighs), (applause)…")]
        public bool captionSounds = true;
        [Tooltip("Fingerprint the current speaker's voice every this many seconds of speech…")]
        public float voiceCheckSeconds = 2.5f;
        [Tooltip("…from the last this-many seconds of it.")]
        public float voiceWindowSeconds = 3.0f;
        [Tooltip("A visual decision this clear (talking score lead over the next face) teaches the voice which face it belongs to.")]
        public float bindMargin = 0.15f;
        [Tooltip("The voice overrides a visual decision less clear than this.")]
        public float voiceOverridesBelow = 0.3f;

        SpeechStack _stack;
        SpeechOnsets _onsets;
        CaptionBubble _fallback;
        CaptionBubble _status, _notice;
        float _noticeUntil = -99f, _noticeOpenAt = -1f;
        string _noticeLabel, _noticeText;
        const float NoticeOrbSeconds = 0.6f, NoticeShrinkSeconds = 0.7f;

        /// <summary>A notification in the bubble style (a taught sound, your name): a blue orb comes up straight ahead,
        /// grows into a bubble with the text, and when it's over shrinks back into the orb and fades. Its glass is
        /// in <paramref name="color"/>. Calling it again keeps it up.</summary>
        public void Notify(string label, string text, Color color, float seconds)
        {
            if (_notice == null) return;
            _noticeLabel = label;
            _noticeText = text;
            _noticeUntil = Time.time + seconds;
            if (_notice.IsShowing) { _notice.Show(label, text, final: true); return; }
            if (_noticeOpenAt < 0f)
            {
                // First the orb, in colour; it opens a moment later.
                _notice.SetSpeakerColor(color, hue: true);
                _notice.showIdleOrb = true;
                _noticeOpenAt = Time.time + NoticeOrbSeconds;
            }
        }

        void UpdateNotice()
        {
            if (_noticeOpenAt >= 0f && Time.time >= _noticeOpenAt)
            {
                _noticeOpenAt = -1f;
                _notice.Show(_noticeLabel, _noticeText, final: true);
            }
            if (_notice.IsShowing && Time.time > _noticeUntil) _notice.Hide(); // the panel shrinks back into the orb
            else if (!_notice.IsShowing && _noticeOpenAt < 0f && _notice.showIdleOrb && Time.time > _noticeUntil + NoticeShrinkSeconds)
                _notice.showIdleOrb = false; // then the orb fades
            else if (!_notice.showIdleOrb && !_notice.IsShowing && Time.time > _noticeUntil + NoticeShrinkSeconds + 0.5f)
                _notice.SetSpeakerColor(Color.white, hue: false); // ready to glow again next time
            // Straight ahead, the panel just above eye level with its drop hanging under the middle.
            _notice.Target = head.position + Flat(head.forward) * 0.9f + Vector3.up * 0.04f;
        }
        CaptionBubble _you; // the wearer's own words
        readonly List<(float t, float db)> _rawDb = new(); // mic level before boost, each frame (Time.time, dBFS)
        readonly Dictionary<int, CaptionBubble> _bubbles = new();
        readonly Dictionary<int, Transform> _markers = new();
        // The current utterance, split into one segment per speaker (Who null = unsure); words before _decided are assigned.
        class Seg
        {
            public TrackedPerson Who;
            public int Start;
            public float Margin;               // how clearly the face won when the segment started (talking score lead)
            // Talking score lead over the other faces, per word. The median ignores the words around a turn change,
            // where both faces look busy.
            public readonly List<float> Leads = new();
            public float Lead
            {
                get
                {
                    if (Leads.Count == 0) return Margin;
                    var sorted = new List<float>(Leads);
                    sorted.Sort();
                    return sorted[sorted.Count / 2];
                }
            }
            public VoicePrints.Voice Voice;    // whose voice it sounds like, once fingerprinted
            public float[] Print;              // the rolling 1.5 s fingerprint of this caption's own speaker (turn-change check)
            public float PrintTo;              // end of the latest stretch that still sounded like Print
            public int Unlike;                 // stretches in a row that sounded unlike Print
            public float PitchFrom;            // this caption's own voice starts here (Time.time; its pitch is measured from here)
            public int PitchOff;               // pitch checks in a row in the other voice group
            public bool PitchMoved;            // already handed to the person its pitch belongs to
            public float CutAt = -1f;          // a turn change heard here, waiting for its first word to be written
            public float CutSince;
            public string CutWhy;
            public TrackedPerson CutTo;
            public float CheckedUntil;         // speech up to here (Time.time) is fingerprinted
            public bool Checking;
            public int Id = ++_nextSegId;      // for the log: "Caption #12"
            public int StillWord = -1;         // first word of the current stretch where Who's mouth is still
            public bool SawStill;              // unsure segment: a face in view was measured not talking
            public bool Wearer;                // loud on the Quest's own mic: the wearer's own speech (Who is null)
            Seg _prev;
            public Seg Prev                    // the caption before this one (any speaker); its text becomes the accurate one
            {
                get => _prev;
                set { _prev = value; if (value?._prev != null) value._prev._prev = null; } // only a short chain is needed
            }
            public string[] Before => Words(Prev?.Source ?? Prev?.Committed);
            public string[] NextLive;          // the next speaker's first live words, once this turn ended
            public float StartTime;            // when its first word was said (Time.time)
            public float LastWord = float.NegativeInfinity; // when its latest word was said (Time.time)
            public string Committed;           // the words it ended with, as stored in the rolling caption (as shown)
            public string Source;              // the same words as spoken (Committed is their translation, when translating)
            public string Asked;               // the text last sent to be translated
            public string AskedPiece, AskedPrefix; // the part of it sent, and the translation shown before that part
            public string TrSource, TrText;    // live translation: the words translated so far and their translation (fixed)
            public bool TrDone;                // finished: no more live pieces
            public int SureWords;              // words the face evidence was sure about (combined decision)
        }
        // Recently finished utterances, by SpeechStack utterance id: re-read when the captions switch language.
        readonly Dictionary<int, List<Seg>> _finished = new();
        readonly AudioRing _ring = new();
        ActiveSpeakerModel _asd;
        readonly List<TrackedPerson> _bestOf = new(); // per word of the utterance: whose evidence was strongest
        static int _nextSegId;
        readonly List<Seg> _segs = new();
        int _decided;
        int _finalsTaken;
        int _utterance;                        // counts utterances, so late fingerprints know if theirs is still running
        string[] _words = System.Array.Empty<string>();
        float _wordsAt = -99f;              // when the words under way last changed
        const float QuietSeconds = 1f;      // no new words this long: the talker's bubble may melt (after its hold)
        float[] _wordTimes = System.Array.Empty<float>();
        readonly AudioEnvelope _envelope = new();
        VoicePrints _voices;
        VoiceProfiles _profiles;
        readonly Queue<(TrackedPerson who, float time, int skipWords, string label, List<Seg> segs)> _pendingFinals = new();
        string[] _prevSegWords = System.Array.Empty<string>();
        TrackedPerson _challenger;     // someone else who started talking mid-utterance…
        int _challengeWord;            // …from this word on
        readonly Dictionary<int, (float side, float pending, float since)> _sides = new();
        bool _inUtterance;
        string _lastPartial = "";
        float _statusUntil;
        bool _statusError;
        float _nextDiag;

        // Every [Captions] line of this session, kept on the headset (files/captions_log.txt; the session before is
        // captions_log_prev.txt) so a session can be read back after the fact: adb pull …/files/captions_log.txt
        System.IO.StreamWriter _sessionLog;

        void OpenSessionLog()
        {
            try
            {
                var path = System.IO.Path.Combine(Application.persistentDataPath, "captions_log.txt");
                if (System.IO.File.Exists(path)) System.IO.File.Copy(path, path.Replace(".txt", "_prev.txt"), true);
                _sessionLog = new System.IO.StreamWriter(path, false); // flushed every few seconds, not per line (main thread)
                _sessionLog.WriteLine($"session {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Application.logMessageReceived += ToSessionLog;
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] no session log: {e.Message}"); }
        }

        void ToSessionLog(string message, string stack, LogType type)
        {
            if (_sessionLog == null) return;
            if (message.StartsWith("[Captions]")) _sessionLog.WriteLine($"{Time.time:F2}\t{message.Substring(11)}");
            if (type is LogType.Error or LogType.Exception || Time.unscaledTime - _logFlushedAt > 3f) FlushSessionLog();
        }

        float _logFlushedAt;

        void FlushSessionLog()
        {
            _logFlushedAt = Time.unscaledTime;
            try { _sessionLog?.Flush(); } catch (System.Exception) { }
        }

        // The headset going to sleep or the app closing: keep what's written.
        void OnApplicationPause(bool paused) { if (paused) FlushSessionLog(); }

        IEnumerator Start()
        {
            OpenSessionLog();
            _fallback = CaptionBubble.Create("Fallback bubble", transform, head, bubbleMaterial, bodyFont, nameFont, new Color(0.85f, 0.86f, 0.9f));
            _fallback.collapseAfterSeconds = captionHoldSeconds;
            _fallback.showIdleOrb = false;
            _fallback.MakePlain(); // nobody recognised: plain subtitle text, not a bubble floating away from the speaker
            // Your own words: one quiet line at the bottom centre of the view, never in the middle of it.
            _you = CaptionBubble.Create("Your speech", transform, head, bubbleMaterial, bodyFont, nameFont, new Color(0.85f, 0.86f, 0.9f));
            _you.collapseAfterSeconds = 2f;
            _you.showIdleOrb = false;
            _you.MakePlain();
            _you.sizeScale = 0.7f;
            _you.maxLines = 1;
            _you.textOpacity = 0.7f;
            _you.headLocked = true;
            _status = CaptionBubble.Create("Status", transform, head, bubbleMaterial, bodyFont, nameFont, new Color(0.65f, 0.71f, 0.99f));
            _status.showIdleOrb = false;
            _status.collapseAfterSeconds = 999f;
            _notice = CaptionBubble.Create("Notification", transform, head, bubbleMaterial, bodyFont, nameFont, new Color(0.65f, 0.71f, 0.99f));
            _notice.showIdleOrb = false;
            _notice.collapseAfterSeconds = 999f;
            _notice.headLocked = true;
            _notice.MakeCentered();
            LoadingStatus("Starting…");
            Step("Getting ready", 0.05f);

            speechLanguage = PlayerPrefs.GetString(LanguagePref, speechLanguage);
            translate = PlayerPrefs.GetInt(TranslatePref, translate ? 1 : 0) == 1;
            showMine = PlayerPrefs.GetInt(ShowMinePref, showMine ? 1 : 0) == 1;
            hideStrays = PlayerPrefs.GetInt(HideStraysPref, hideStrays ? 1 : 0) == 1;
            ApplyLaunchOverrides();
            Debug.Log($"[Captions] Your words {(showMine ? "shown" : "hidden")} · words of people not tagged {(hideStrays ? "dropped in Bubbles mode once someone is tagged" : "go to the subtitle line")}");
            _defaultWorkers = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            if (jobWorkers > 0) Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = Mathf.Min(jobWorkers, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount);
            Debug.Log($"[Captions] CPU: {SystemInfo.processorCount} cores, job workers {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount} (default {_defaultWorkers}{(jobWorkers > 0 ? ", fixed" : $", {behindWorkers} while speech is behind")}), speech threads {asrThreads}, speech priority {(speechPriority ? "above normal" : "normal")}");
            yield return ModelFiles.EnsureCopied(msg => { LoadingStatus(msg); Step("Unpacking models", 0.12f); }, speechModel);
            if (ModelFiles.Error != null) { ShowStatus(ModelFiles.Error, error: true); yield break; }
            _stack = new SpeechStack(new SpeechStack.Options
            {
                Model = speechModel, Language = speechLanguage, AsrThreads = asrThreads,
                SkipSilenceAfter = skipSilenceAfter, MaxLagSeconds = maxSpeechLag,
                ThreadPriority = speechPriority ? System.Threading.ThreadPriority.AboveNormal : System.Threading.ThreadPriority.Normal,
                VadThreshold = vadThreshold, BlankPenalty = blankPenalty,
                // Another language or "auto": its model loads alongside the English one, so the first words aren't held.
                PreloadLanguages = speechLanguage != "en" && ModelFiles.LanguagesOnDisk(),
            });
            // Loading the speech models (VAD + Nemotron, ~600 MB) takes a second or more: off the main thread, so the
            // passthrough and the "Starting…" status keep moving instead of freezing a frame.
            // Its own thread, not the pool: onnxruntime's workers start here and take this thread's scheduling class.
            var init = BackgroundThread.RunSpeech(() => _stack.Init(), "SpeechInit");
            Step("Loading speech and faces", 0.25f);
            // Meanwhile the vision models that must load on the main thread (LR-ASD, SFace) load one step per frame,
            // now behind "Starting…" rather than the first time a face turns up mid-conversation.
            if (useActiveSpeaker && activeSpeakerEncoder != null && activeSpeakerBackend != null)
            {
                ActiveSpeakerModel asd = null;
                yield return ActiveSpeakerModel.LoadStaged(activeSpeakerEncoder, activeSpeakerBackend, m => asd = m);
                _asd = asd;
                tracker.captureFaceCrops = true;
                tracker.logCropInputs = logSpeakerScores;
                _ = RunActiveSpeaker();
            }
            Step("Loading speech", 0.5f);
            yield return null;
            tracker.PreloadFaceId();
            // Fonts for other scripts now, not only with other spoken languages: a translated caption can be in any.
            ScriptFonts.AddTo(bodyFont, nameFont);
            Note($"Fonts for other scripts: {ScriptFonts.Summary}");
            while (!init.IsCompleted) yield return null;
            if (!init.Result) { ShowStatus(_stack.Error, error: true); yield break; }
            Step("Loading voices", 0.72f);
            if (speechLanguage != "en") StartCoroutine(StartLanguages());
            if (translate) StartCoroutine(StartTranslator());
            if (!string.IsNullOrEmpty(_translateBench))
                CaptionTranslator.Bench(m => Debug.Log("[Captions] trbench " + m));
            if (!noMic)
            {
                mic.StartCapture();
                if (!mic.Running) { ShowStatus(mic.Error, error: true); yield break; }
            }
            if (useVoicePrints)
            {
                _voices = new VoicePrints();
                var vinit = System.Threading.Tasks.Task.Run(() => _voices.Init()); // (TitaNet: off the main thread too)
                while (!vinit.IsCompleted) yield return null;
                if (!vinit.Result) { Debug.LogWarning($"[Captions] {_voices.Error}"); _voices = null; }
                else _voices.Merged += (gone, kept, sim) =>
                {
                    Note($"{gone.Label} is {kept.Label} after all (similarity {sim:F2}) — merged");
                    foreach (var seg in _segs) if (seg.Voice == gone) seg.Voice = kept;
                };
            }
            _soundTags = PlayerPrefs.GetInt(SoundsPref, 1) == 1;
            if (captionSounds) StartSounds(); // always: alerts (smoke alarm, doorbell…) are heard in every mode and the menu
            if (useWearerVoice) _wearerPrint = LoadWearerPrint();
            if (_wearerPrint != null && PlayerPrefs.HasKey(WearerLevelPref)) _wearerLevelDb = PlayerPrefs.GetFloat(WearerLevelPref);
            if (_wearerPrint != null) Debug.Log("[Captions] Your voice print is loaded: your own speech is told apart by voice");
            if (!string.IsNullOrEmpty(_replayFile)) StartCoroutine(Replay(System.IO.Path.Combine(Application.persistentDataPath, _replayFile)));
            else mic.OnChunk += FeedAudio;
            SetDebugView(_debugExtra ?? (Application.isEditor ? showDebug : PlayerPrefs.GetInt(DebugKey, 0) == 1), save: false);
            Note($"Listening · {mic.Status} · {_stack.RecognizerName} · vad {vadThreshold:F2} · blank penalty {blankPenalty:F2} · language {speechLanguage} · voice fingerprints {(_voices != null ? "on" : "off")}");

            Step("Starting the camera", 0.88f);
            while (!tracker.Ready && (tracker.Status == "starting" || tracker.Status.StartsWith("warming")))
            {
                LoadingStatus($"Looking for people… ({tracker.Status})");
                yield return null;
            }
            if (tracker.Ready) LoadingStatus("Ready — captions appear next to whoever speaks");
            else ShowStatus($"Captions only: {tracker.Status}", 4f);
            Step(tracker.Ready ? "Ready" : "Ready · captions only", 1f);
            if (showLoadingStatus || !tracker.Ready) yield return new WaitForSeconds(4f);
            Starting = false;
        }

        /// <summary>The models are still loading (the status bubble shows how far, even over the start screen).</summary>
        public bool Starting { get; private set; } = true;
        /// <summary>Launch option -e welcome 1: open the welcome screens even when already set up (testing).</summary>
        [System.NonSerialized] public bool welcomeNow;
        /// <summary>For the start screens' "Getting ready" pill: what is loading now, how far along (0–1) and since when.
        /// StartupFailed: an error stopped the startup (the status bubble says which).</summary>
        public string StartupStep { get; private set; } = "Getting ready";
        public float StartupProgress { get; private set; }
        public float StartupStepAt { get; private set; }
        public bool StartupFailed { get; private set; }
        void Step(string text, float progress)
        {
            if (text != StartupStep || progress != StartupProgress) StartupStepAt = Time.time;
            StartupStep = text; StartupProgress = Mathf.Max(StartupProgress, progress);
        }

        /// <summary>
        /// Testing aid: adb shell am start … -e mic unprocessed|mic|camcorder|unity -e maxgain 55
        /// -e vision 0: no face tracking (frame-rate tests; read by PersonTracker)
        /// -e vad 0.3 -e blank 0.5: speech sensitivity (see vadThreshold, blankPenalty)
        /// -e lang auto|en|de|…: spoken language (see speechLanguage) -e asrthreads 3: speech-to-text decoding threads (default 2)
        /// -e translate 1 -e mylang en: translate finished captions into that language (see translate)
        /// -e start 1: start the conversation straight away (no start screen), for tests without wearing the headset
        /// -e trbench 1: time Firefox's translation models on test sentences (log "trbench")
        /// -e replay mic_post.wav: feed that recording (32-bit float, 16 kHz, in the app's files folder) instead of the mic
        /// </summary>
        void ApplyLaunchOverrides()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                var src = intent.Call<string>("getStringExtra", "mic");
                if (!string.IsNullOrEmpty(src))
                    mic.androidSource = src switch
                    {
                        "unprocessed" => MicStream.SourceUnprocessed, "camcorder" => MicStream.SourceCamcorder,
                        "unity" => MicStream.SourceUnity, "performance" => 10, "default" => 0, "communication" => 7,
                        _ => int.TryParse(src, out var n) ? n : MicStream.SourceMic,
                    };
                if (int.TryParse(intent.Call<string>("getStringExtra", "micch") ?? "", out var micCh)) mic.channels = micCh; // -e micch 3: ask for 3 mic channels (test)
                if ((Extra("quality") ?? 1f) == 0f) weighLipsByQuality = false;                        // -e quality 0: lips trusted whatever the picture
                var rec = intent.Call<string>("getStringExtra", "record");
                if (!string.IsNullOrEmpty(rec) && float.TryParse(rec, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var recSec)) mic.RecordDebug(recSec);
                _replayFile = intent.Call<string>("getStringExtra", "replay");
                var dbg = intent.Call<string>("getStringExtra", "debug");
                if (!string.IsNullOrEmpty(dbg)) _debugExtra = dbg != "0";
                float? Extra(string key) => float.TryParse(intent.Call<string>("getStringExtra", key) ?? "",
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                // -e mine 0|1: show your own words; -e strays 0|1: drop untagged words in Bubbles mode (both saved).
                var mine = intent.Call<string>("getStringExtra", "mine");
                if (!string.IsNullOrEmpty(mine)) { showMine = mine != "0"; PlayerPrefs.SetInt(ShowMinePref, showMine ? 1 : 0); PlayerPrefs.Save(); }
                var strays = intent.Call<string>("getStringExtra", "strays");
                if (!string.IsNullOrEmpty(strays)) { hideStrays = strays != "0"; PlayerPrefs.SetInt(HideStraysPref, hideStrays ? 1 : 0); PlayerPrefs.Save(); }
                var lang = intent.Call<string>("getStringExtra", "lang");
                if (!string.IsNullOrEmpty(lang)) speechLanguage = lang;
                // Translation: -e translate 1|0, -e mylang de (the language to translate into).
                var tr = intent.Call<string>("getStringExtra", "translate");
                if (!string.IsNullOrEmpty(tr)) translate = tr != "0";
                _translateBench = intent.Call<string>("getStringExtra", "trbench");
                if (intent.Call<string>("getStringExtra", "forgetkey") == "1") OnlineSummary.ForgetKey();   // -e forgetkey 1
                if (OnlineSummary.DevUnlocked && intent.Call<string>("getStringExtra", "sumtest") == "1")
                    StartCoroutine(OnlineSummary.SelfTest()); // -e onlinesum 1 -e sumtest 1
                startNow = intent.Call<string>("getStringExtra", "start") == "1"; // -e start 1: no start screen
                welcomeNow = intent.Call<string>("getStringExtra", "welcome") == "1"; // -e welcome 1: the welcome screens again
                // Testing tagging without hands: -e tag auto|both|hand (tagging mode), -e name Anna (tag the first
                // person in view as if pointed at and named).
                tagExtra = intent.Call<string>("getStringExtra", "tag");
                nameExtra = intent.Call<string>("getStringExtra", "name");
                alertExtra = intent.Call<string>("getStringExtra", "alert"); // -e alert smoke: show that alert 4 s in
                if (!string.IsNullOrEmpty(alertExtra)) _testAlertAt = Time.time + 4f;
                var my = intent.Call<string>("getStringExtra", "mylang");
                if (!string.IsNullOrEmpty(my)) PlayerPrefs.SetString(MyLanguagePref, my);
                asrThreads = (int)(Extra("asrthreads") ?? asrThreads); // A/B: -e asrthreads 3
                skipSilenceAfter = Extra("skipsilence") ?? skipSilenceAfter; // -e skipsilence 0: feed silence too
                maxSpeechLag = Extra("maxlag") ?? maxSpeechLag;              // -e maxlag 0: never skip audio
                adaptVision = (Extra("adaptvision") ?? 1f) != 0f;           // -e adaptvision 0: faces always at full rate
                speechPriority = (Extra("asrprio") ?? (speechPriority ? 1f : 0f)) != 0f; // -e asrprio 0|1
                BackgroundThread.SpeechIdle = (Extra("speechidle") ?? 1f) != 0f; // A/B: -e speechidle 0: speech at normal priority
                // -e jobworkers 2: Unity job threads (the vision models run on them, spread over every core).
                var jw = Extra("jobworkers");
                if (jw.HasValue && jw.Value > 0) jobWorkers = (int)jw.Value;
                behindWorkers = (int)(Extra("behindworkers") ?? behindWorkers);
                behindLag = Extra("behindlag") ?? behindLag;                 // -e behindlag 1: old threshold
                if ((Extra("revise") ?? 1f) == 0f)                           // -e revise 0: no hold / look-back / voice ties (A/B)
                { speakerHoldSeconds = 0f; reviseGapSeconds = -1f; _noRevise = true; }
                speakerHoldSeconds = Extra("hold") ?? speakerHoldSeconds;    // -e hold 4: keep a talker 4 s after their face is lost
                voiceProfileTies = (Extra("voiceties") ?? (voiceProfileTies ? 1f : 0f)) != 0f;
                // Tests: -e mode bubbles|signing|signtospeech|subtitles (for this launch only, not saved).
                var m = intent.Call<string>("getStringExtra", "mode");
                if (!string.IsNullOrEmpty(m) && System.Enum.TryParse<CaptionMode>(m, true, out var cm)) mode = cm;
                vadThreshold = Extra("vad") ?? vadThreshold;
                blankPenalty = Extra("blank") ?? blankPenalty;
                minLipSamplesPerSecond = Extra("minlip") ?? minLipSamplesPerSecond; // A/B: -e minlip 2
                var g = intent.Call<string>("getStringExtra", "maxgain");
                if (!string.IsNullOrEmpty(g) && float.TryParse(g, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var gain)) mic.maxAutoGainDb = gain;
                // Release: the per-word and face-ID log lines only in test sessions (-e log 1, or the debug view on).
                var verbose = Extra("log") is { } lg ? lg != 0f : _debugExtra ?? PlayerPrefs.GetInt(DebugKey, 0) == 1;
                logEveryWord = verbose;
                if (tracker != null) tracker.logFaceId = verbose;
            }
            catch (System.Exception e) { Debug.LogWarning($"[Captions] launch extras: {e.Message}"); }
#endif
        }

        // Speech-to-text and face tracking share the CPU. On the headset, with both at full rate, non-stop speech fell
        // behind and 20 % of it was skipped to catch up; without vision it kept up. So while speech is behind, faces
        // update less often (down to a quarter), and come back gradually once it has caught up.
        void AdaptVision()
        {
            if (tracker == null) return;
            if (!adaptVision) { tracker.visionShare = 1f; return; }
            var lag = _stack.Lag;
            if (lag > behindLag)
            {
                tracker.visionShare = lag > behindLag + 1f ? 0.25f : Mathf.Min(tracker.visionShare, 0.5f);
                _speechBehindAt = Time.time;
            }
            else if (Time.time - _speechBehindAt > 3f)
                tracker.visionShare = Mathf.Min(1f, tracker.visionShare + Time.deltaTime * 0.1f); // full rate again in ≤ 7.5 s
            // The vision models (Unity's CPU backend) spread each run over every job thread, which starves the speech
            // thread however rarely they run. While speech is behind they get fewer threads; frames drop a little
            // more then (measured: 1 thread kept up best but dropped below 60 fps 2.4× as often as 4), so only then.
            if (jobWorkers > 0 || _defaultWorkers <= behindWorkers) return;
            var behind = Time.time - _speechBehindAt < 3f;
            var want = behind ? behindWorkers : _defaultWorkers;
            if (Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount != want) Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = want;
        }
        int _defaultWorkers;
        /// <summary>Job threads left to the vision models while speech-to-text is behind (tests: -e behindworkers 1).</summary>
        [System.NonSerialized] public int behindWorkers = 2;
        float _speechBehindAt = -10f;
        /// <summary>Lag (s) above which speech counts as behind. Nemotron's own delay is ~1.2 s (560 ms chunks plus
        /// decoding), so at 1 s it always counted as behind and the speaker model was paused half the time
        /// (tests: -e behindlag 1).</summary>
        [System.NonSerialized] public float behindLag = 2f;
        /// <summary>Slow face tracking while speech-to-text is behind (tests: -e adaptvision 0).</summary>
        [System.NonSerialized] public bool adaptVision = true;
        /// <summary>Speech thread above normal priority, and the Unity job threads the vision models run on
        /// (0 = Unity's default, one per core but one).</summary>
        [System.NonSerialized] public bool speechPriority = true;
        [System.NonSerialized] public int jobWorkers;

        string _replayFile, _translateBench;
        /// <summary>Speech-to-text decoding threads (tests: -e asrthreads 3).</summary>
        [System.NonSerialized] public int asrThreads = 2;
        /// <summary>Keeping up (SpeechStack.Options): don't feed silence after this long (s), and skip audio when the
        /// captions are this far behind (s). Tests: -e skipsilence 0, -e maxlag 0 turn them off.</summary>
        [System.NonSerialized] public float skipSilenceAfter = 1f, maxSpeechLag = 4f;
        /// <summary>Testing aid (-e start 1): the menu starts the conversation at once, as if Start was pressed.</summary>
        [HideInInspector] public bool startNow;
        [System.NonSerialized] public string tagExtra, nameExtra;

        /// <summary>Tests: stop listening to the mic; audio comes from <see cref="PushAudio"/> instead.</summary>
        public void UseExternalAudio() { if (mic != null) mic.OnChunk -= FeedAudio; }
        public void PushAudio(float[] samples, double realtime) => FeedAudio(samples, realtime);
        public bool Listening => _stack != null;

        /// <summary>How speech is shown: speech bubbles beside each speaker, or a signing avatar (<see cref="SignAvatar"/>)
        /// with the bubbles hidden. Chosen on the start screen (<see cref="ModeMenu"/>).</summary>
        public enum CaptionMode { Bubbles, Signing, SignToSpeech, Subtitles }
        public CaptionMode mode = CaptionMode.Bubbles;
        /// <summary>False while the conversation setup runs (<see cref="ModeMenu"/>): speech is still recognised, but no
        /// caption or sign is shown until the viewer presses Start conversation.</summary>
        [HideInInspector] public bool live = true;
        /// <summary>Faces are needed although captions aren't live: a setup step that tags people. Otherwise, outside a
        /// conversation the face tracker rests (<see cref="PersonTracker.paused"/>).</summary>
        [System.NonSerialized] public bool facesWanted;
        /// <summary>A finished caption (the live recognizer's final text of one speaker's stretch): who said it (null =
        /// nobody identified), the stretch's id (as in <see cref="LiveWords"/>; -1 = none) and the words.</summary>
        public event System.Action<TrackedPerson, int, string> FinalCaption;
        /// <summary>Live text of one speaker's stretch while it is being said: who, the stretch's id, its words so far,
        /// and how many of them (from the start) have settled — the recognizer won't change those any more.</summary>
        public event System.Action<TrackedPerson, int, string[], int> LiveWords;
        /// <summary>Every finished line of the conversation, for the saved transcript (<see cref="ConversationLog"/>):
        /// its id, who said it (null = nobody identified), whether it was the wearer, and the words. The same id again
        /// means that line was read again (after a language switch): the new text replaces the old.</summary>
        public event System.Action<int, TrackedPerson, bool, string> TranscriptLine;
        /// <summary>Outside a conversation, transcribe for the menu only (saying your name): <see cref="Heard"/> gets the
        /// words (live text, or a finished line), nothing is captioned.</summary>
        [System.NonSerialized] public bool hearOnly;
        public event System.Action<string, string> Heard;
        Seg _liveSeg;
        bool _freshPending;

        /// <summary>
        /// Start fresh: a mode starts, or it's back to the main menu. Nothing from before carries over: audio the
        /// recognizer hasn't got to yet, the sentence under way, captions waiting for their translation and the text
        /// on screen are all dropped, so a new conversation starts with what's said from now on.
        /// </summary>
        public void StartFresh()
        {
            _stack?.Restart();
            _translator?.Clear();
            _awaiting.Clear();
            _freshPending = _stack != null;
            _segs.Clear();
            _inUtterance = false;
            _decided = 0;
            _words = System.Array.Empty<string>();
            _wordTimes = System.Array.Empty<float>();
            _lastPartial = "";
            _pendingFinals.Clear();
            _finished.Clear();
            _committed.Clear();
            _prevSegWords = System.Array.Empty<string>();
            _bestOf.Clear();
            _challenger = null;
            _lastSeg = null;
            _liveSeg = null;
            _trSeg = null;
            _trWanted = _trPieceAsked = null;
            _trBusy = false;
            foreach (var b in _bubbles.Values) b.Hide();
            _fallback?.Hide();
            _you?.Hide();
        }

        /// <summary>The side of a person their caption sits on (+1 = the viewer's right of their face), kept steady
        /// the same way as the bubbles'.</summary>
        public float SideOf(TrackedPerson p) => _sides.TryGetValue(p.Id, out var s) ? s.side : DesiredSide(p);

        // Repeatable tests on the headset: play a recording into the pipeline in real time, as if it came from the mic.
        IEnumerator Replay(string path)
        {
            float[] samples;
            try
            {
                var b = System.IO.File.ReadAllBytes(path);
                var i = 12;
                while (System.Text.Encoding.ASCII.GetString(b, i, 4) != "data") i += 8 + System.BitConverter.ToInt32(b, i + 4);
                var n = System.BitConverter.ToInt32(b, i + 4) / 4;
                samples = new float[n];
                System.Buffer.BlockCopy(b, i + 8, samples, 0, n * 4);
            }
            catch (System.Exception e) { Note($"Replay failed: {e.Message}"); yield break; }
            Note($"Replaying {System.IO.Path.GetFileName(path)} ({samples.Length / (float)MicStream.OutputRate:F1} s) instead of the mic");
            yield return new WaitForSeconds(2f);
            var t0 = Time.realtimeSinceStartupAsDouble;
            var pos = 0;
            while (pos < samples.Length)
            {
                var target = System.Math.Min(samples.Length, (int)((Time.realtimeSinceStartupAsDouble - t0) * MicStream.OutputRate));
                if (target > pos)
                {
                    var chunk = new float[target - pos];
                    System.Array.Copy(samples, pos, chunk, 0, chunk.Length);
                    pos = target;
                    FeedAudio(chunk, Time.realtimeSinceStartupAsDouble);
                }
                yield return null;
            }
            for (var k = 0; k < 150; k++) { FeedAudio(new float[MicStream.OutputRate / 50], Time.realtimeSinceStartupAsDouble); yield return new WaitForSeconds(0.02f); }
            mic.OnChunk += FeedAudio; // back to the live mic, so a test run never leaves the app deaf
            Note("Replay finished — listening to the mic again");
        }

        // One mic stream feeds speech-to-text, the loudness envelope (lip sync) and the voice fingerprints.
        void FeedAudio(float[] samples, double realtime)
        {
            _stack.Push(samples, realtime);
            _envelope.Push(samples, realtime);
            _ring.Push(samples, realtime);
            _voices?.Push(samples, realtime);
            _sounds?.Push(samples, realtime);
            _pitch.Push(samples, realtime);
        }

        // ---------------- debug view (DebugOverlay reads these) ----------------

        const string DebugKey = "captions.debug";
        bool? _debugExtra;
        DebugOverlay _overlay;

        /// <summary>Face boxes, lip outlines and scores are showing.</summary>
        /// <summary>How long a caption stays after its person stops talking (the conversation menu's slider): for the
        /// captions on screen now too.</summary>
        public void SetCaptionHold(float seconds)
        {
            captionHoldSeconds = seconds;
            foreach (var b in _bubbles.Values) b.collapseAfterSeconds = seconds;
            if (_fallback != null) _fallback.collapseAfterSeconds = seconds;
        }

        public bool DebugView => _overlay != null && _overlay.enabled;

        /// <summary>Show or hide the debug view; <paramref name="save"/> remembers the choice for next time.</summary>
        public void SetDebugView(bool on, bool save = true)
        {
            if (on && _overlay == null)
            {
                _overlay = gameObject.AddComponent<DebugOverlay>();
                _overlay.director = this;
                _overlay.tracker = tracker;
                _overlay.mic = mic;
                _overlay.head = head;
                _overlay.font = bodyFont;
                _overlay.lineMaterial = panelMaterial;
            }
            if (_overlay != null) _overlay.enabled = on;
            tracker.keepDebugGeometry = on;
            if (!save) return;
            PlayerPrefs.SetInt(DebugKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        readonly List<(float time, string text)> _notes = new();
        public IReadOnlyList<(float time, string text)> RecentNotes => _notes;
        public AudioEnvelope Envelope => _envelope;
        public VoicePrints Voices => _voices;
        public bool VadSpeech { get; private set; }

        // Every decision goes to the log and to the last few lines of the debug view.
        public void Note(string text)
        {
            Debug.Log("[Captions] " + text);
            _notes.Add((Time.time, text));
            if (_notes.Count > 4) _notes.RemoveAt(0);
        }

        /// <summary>The speaker of the words being captioned right now (null: nobody or unsure).</summary>
        public TrackedPerson CurrentSpeaker => _inUtterance && _segs.Count > 0 ? _segs[^1].Who : null;

        /// <summary>Talking evidence for a face, as judged for a word said just long enough ago to have the full window.</summary>
        public float DebugTalking(TrackedPerson p, out float mouth, out float sync)
        {
            var t = Time.time - Mathf.Max(wordWindow.y, syncWindow.y);
            mouth = Mouth(p, t);
            return Talking(p, t, out sync);
        }

        /// <summary>The voice tied to this face, if any.</summary>
        public VoicePrints.Voice VoiceOf(TrackedPerson p) => _voices?.Voices.Find(v => v.BoundPerson() == p.Id);

        /// <summary>The current utterance, each word coloured by the face it went to (grey: not decided yet).</summary>
        public string DebugUtterance(int maxWords = 16)
        {
            if (!_inUtterance || _words.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            var from = Mathf.Max(0, _words.Length - maxWords);
            if (from > 0) sb.Append("… ");
            for (var i = from; i < _words.Length; i++)
            {
                Seg seg = null;
                foreach (var g in _segs) if (g.Start <= i) seg = g;
                if (i > 0 && seg != null && seg.Start == i && i > from) sb.Append("<color=#FFFFFF>| </color>");
                var color = i >= _decided || seg == null ? "#8A8A8A" : seg.Who != null ? "#" + ColorUtility.ToHtmlStringRGB(seg.Who.Color) : "#D8D8E0";
                sb.Append($"<color={color}>{_words[i].ToLowerInvariant()}</color> ");
            }
            return sb.ToString();
        }

        // Progress and errors, in a bubble low in the view. Progress waits while the start screen and setup are up
        // (captions are paused then, and it would sit over them); errors always show.
        // Position tracking, with some patience both ways: a moment's loss (a hand over the cameras) changes nothing,
        // and bubbles only come back once tracking has held for a few seconds (no flicker in a dim room).
        void UpdateTrackingFallback()
        {
            var tracked = !simTrackingLost;
#if UNITY_ANDROID && !UNITY_EDITOR
            tracked &= OVRPlugin.positionTracked;
#endif
            if (tracked != _trackedNow) { _trackedNow = tracked; _trackingChangedAt = Time.time; }
            var steadyFor = _trackingChangedAt < 0f ? float.MaxValue : Time.time - _trackingChangedAt;
            var want = mode == CaptionMode.Bubbles && live
                ? (TrackingFallback ? !(tracked && steadyFor >= TrackingBackAfter) : !tracked && steadyFor >= TrackingLostAfter)
                : false;
            if (want == TrackingFallback) return;
            TrackingFallback = want;
            if (want)
            {
                Debug.Log("[Captions] Position tracking lost: captions in the subtitle strip until it's back");
                ShowStatus("Lost track of the room (too dark or bright?): captions in subtitles for now.", 8f);
            }
            else if (tracked && live)
            {
                Debug.Log("[Captions] Position tracking back: bubbles beside people again");
                ShowStatus("Tracking is back: bubbles beside people again.", 4f);
            }
        }

        // Startup progress ("Starting…", "Looking for people…", "Ready"): only in the log unless showLoadingStatus is
        // on — a bubble popping up and changing while you look at someone reads as a caption.
        string _lastLoading;
        void LoadingStatus(string text)
        {
            if (showLoadingStatus) { ShowStatus(text, text.StartsWith("Ready") ? 4f : 999f); return; }
            if (text != _lastLoading) Debug.Log($"[Captions] status · {text}");
            _lastLoading = text;
        }

        void ShowStatus(string text, float seconds = 999f, bool error = false)
        {
            if (_status == null) return;
            _status.Show(ModeMenu.AppName, text, final: true);
            _statusUntil = Time.time + seconds;
            _statusError = error;
            if (error && Starting) StartupFailed = true;
        }

        // Frame timing for the diag line: frames in the window, frames over 1.5× the display's budget (a visible
        // hitch in the headset), and the longest frame.
        int _frames, _hitches;
        float _worstFrame, _framesSince;

        void Update()
        {
            _frames++;
            var dt = Time.unscaledDeltaTime;
            if (dt > 1.5f / Mathf.Max(60f, (float)Screen.currentResolution.refreshRateRatio.value)) _hitches++;
            _worstFrame = Mathf.Max(_worstFrame, dt);
            if (head == null) return;
            if (_status.IsShowing && Time.time > _statusUntil) _status.Hide();
            UpdateNotice();
            // Loading shows over the start screen too (it's the first thing you see); other news waits for captions.
            _status.Suppressed = !live && !_statusError && !Starting;
            _status.Target = head.position + Flat(head.forward) * 1.1f + Vector3.down * 0.42f + Vector3.Cross(Vector3.up, Flat(head.forward)) * -0.08f;
            // Bottom centre of view, like TV subtitles (the panel grows upwards from here): ~22° below eye level.
            if (!Subtitles) { _stripPin = null; _stripHeld = false; }
            _fallback.headLocked = _stripHeld;
            _fallback.Target = _stripPin ?? head.position + Flat(head.forward) * subtitleDistance + Vector3.down * (subtitleDistance * 0.41f);
            // Bottom centre of the view, ~30° below where you look (with your head's pitch, so looking down at people
            // doesn't lift it into the middle). It follows your head; other people's bubbles stay with them.
            _you.Target = head.position + ViewTilt() * (Quaternion.Euler(30f, 0f, 0f) * Vector3.forward) * 0.9f;
            _you.Suppressed = !live || !showMine;
            // (Unity's own mic path, e.g. in the Editor, has no raw level: the boosted level less the boost.)
            _rawDb.Add((Time.time, !float.IsNaN(simRawDb) ? simRawDb : mic.RawLevel > 0 ? 20f * Mathf.Log10(mic.RawLevel)
                : mic.Level > 0 ? 20f * Mathf.Log10(mic.Level) - mic.CurrentGainDb : -120f));
            while (_rawDb.Count > 0 && Time.time - _rawDb[0].t > 20f) _rawDb.RemoveAt(0);
            UpdateMarkers();
            UpdateTrackingFallback();
            if (tracker != null) tracker.paused = (!live || Strip) && !facesWanted; // nothing looks for faces in the menu
            if (_stack == null) return;
            _stack.Transcribe = live || hearOnly; // nothing is transcribed while the menu or the setup is up (unless it listens for your name)
            AdaptVision();
            if (_freshPending)
            {
                if (_stack.Restarting) return; // the speech thread hasn't dropped the old audio yet
                _freshPending = false;
                _finalsTaken = 0;
            }

            var snap = _stack.Snapshot();
            if (useOnsets)
            {
                _onsets ??= new SpeechOnsets(onsetSettings);
                _onsets.Update(_envelope, Speakers, Time.time, logEveryWord ? (System.Action<string>)(m => Debug.Log("[Captions] " + m)) : null);
            }
            VadSpeech = snap.VadSpeech;
            UpdateEnrol(snap.VadSpeech);
            var partial = snap.Partial ?? "";
            if (Time.time >= _nextDiag)
            {
                _nextDiag = Time.time + 2f;
                var db = mic.Level > 0 ? 20f * Mathf.Log10(mic.Level) : -120f;
                var rawDb = mic.RawLevel > 0 ? 20f * Mathf.Log10(mic.RawLevel) : -120f;
                Debug.Log($"[Captions] diag · mic raw {rawDb:F0} → {db:F0} dBFS (gain +{mic.CurrentGainDb:F0} dB) · speech {(snap.VadSpeech ? "yes" : "no")} · text {partial.Length} · people {tracker.People.Count} ({tracker.People.FindAll(p => p.Visible).Count} visible, {(tracker.AnyTagged ? $"captioning {Speakers.Count} tagged" : "nobody tagged")}) · faces {tracker.DetectionsPerSecond:F0}/s, mesh-tracked {tracker.MeshTrackShare:P0} (depth {tracker.DepthShare:P0} {tracker.DepthStatus}, detect {tracker.DetectMs:F0} ms, main thread {tracker.DetectMainMs:F1}) · fps {_frames / Mathf.Max(0.001f, Time.realtimeSinceStartup - _framesSince):F0} ({_hitches} hitches, worst {_worstFrame * 1000f:F0} ms) · lips {(tracker.LipLandmarks ? $"mesh {tracker.LandmarkMs:F0} ms/face, main thread {tracker.LandmarkMainMs:F1}" : "proxy")} · speaker model {(_asd != null ? $"{_asdRuns} runs, {_asd.LastMs:F0} ms, {_asdSkip}, audio lag {_asdAudioLagMs:F0} ms, frame age {tracker.CropFrameAgeMs:F0} ms, crop frames lost {tracker.CropFramesLost}" : "off")} · face quality {string.Join(" ", tracker.People.FindAll(p => p.Visible).ConvertAll(p => $"P{p.Id}: c{p.CropContrast:F0}/{p.FaceBoxPx.width:F0}px={FaceQuality(p):F2}"))} · voices {_voices?.Voices.Count ?? 0} ({_voices?.ClipCount ?? 0} prints, regroup {_voices?.LastRegroupMs ?? 0:F1} ms) · asr load {snap.AsrRtf:P0} backlog {snap.MaxBacklog} lag {snap.Lag:F1} s (max {snap.MaxLag:F1}) silence not fed {snap.SilentSec:F0} s skipped {snap.SkippedSec:F1} s · vision {(tracker != null ? tracker.visionShare : 1f):P0}, job workers {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}");
                _frames = _hitches = 0; _worstFrame = 0f; _framesSince = Time.realtimeSinceStartup;
            }

            _voices?.Update(); // finished fingerprints → OnVoice
            _sounds?.Update(OnSound, live && mode != CaptionMode.Signing && _soundTags, OnAlert);
            if (_testAlertAt > 0f && Time.time >= _testAlertAt) { _testAlertAt = -1f; TestAlert(alertExtra); }
            if (hearOnly && !live)
            {
                // Learning your name (ModeMenu.Sounds.cs): the words go to the menu, not into captions.
                Heard?.Invoke(partial, null);
                while (_finalsTaken < snap.Utterances && _stack.TryGetUtterance(out var hu)) { _finalsTaken++; Heard?.Invoke(null, hu.Text); }
                return;
            }
            if (useFusion && _voices != null)
            {
                if (_profiles == null)
                {
                    _profiles = new VoiceProfiles(_voices, voiceProfileSettings);
                    // People remembered by face bring their voice back too (Remember names keeps both).
                    var seeded = 0;
                    foreach (var (id, sum, count) in tracker.SavedVoices()) { _profiles.Seed(id, sum, count); seeded++; }
                    if (seeded > 0) Note($"Voice profiles from earlier sessions: {seeded}");
                    tracker.VoiceOf = _profiles.Export;
                    // Only people around now: a profile remembered from another day must not claim a voice here.
                    if (!_noRevise && voiceProfileTies) _voices.PersonOf = e => _profiles.PersonOf(e, id => Speakers.Exists(p => p.Id == id && Time.time - p.LastSeen < 30f));
                }
                _profiles.Update(Time.time, snap.VadSpeech, SureOwner, logEveryWord ? (System.Action<string>)Note : null);
            }
            KeepTalkerOpen(snap.VadSpeech);
            var words = Words(partial);
            var times = WordTimes(snap.PartialWordTimes, words.Length);
            if (words.Length != _words.Length) _wordsAt = Time.time;
            _words = words;
            _wordTimes = times;

            if (!_inUtterance && words.Length > 0)
            {
                _inUtterance = true;
                _utterance++;
                _segs.Clear();
                _bestOf.Clear();
                _decided = 0;
                _challenger = null;
                _prevSegWords = System.Array.Empty<string>();
            }
            if (_inUtterance && words.Length > 0 && AssignWords(words, times))
                _lastPartial = ""; // speaker changed → redraw
            if (_inUtterance && words.Length > 0) CheckVoice(times);
            CheckSelf(snap.VadSpeech);
            PitchTurn(); // before the live text below, so a new speaker's first word never shows in the last one's bubble

            // Live text goes to the latest segment's speaker; words not yet matched to a face are dimmed.
            if (_inUtterance && _segs.Count > 0 && partial != _lastPartial)
            {
                var cur = _segs[^1];
                if (cur.Who == null) MarkWearer(cur, times, words.Length);
                // The speaker changed: the previous stretch's words are all theirs now (it gets no final text of its own).
                if (_liveSeg != null && _liveSeg != cur && _segs.Contains(_liveSeg) && cur.Start > _liveSeg.Start && cur.Start <= words.Length)
                {
                    var done = words[_liveSeg.Start..cur.Start];
                    if (!_liveSeg.Wearer) LiveWords?.Invoke(_liveSeg.Who, _liveSeg.Id, done, done.Length);
                }
                _liveSeg = cur;
                var seg = Slice(words, cur.Start);
                if (seg.Length > 0)
                {
                    var unstable = Mathf.Max(seg.Length - CommonPrefix(seg, _prevSegWords), words.Length - Mathf.Max(_decided, cur.Start));
                    if (cur.Wearer) _you.Show("You", Pretty(string.Join(" ", seg)), final: false, unstableWords: Mathf.Min(unstable, seg.Length));
                    else
                    {
                        var shown = LiveText(cur, string.Join(" ", seg), out var translated);
                        BubbleFor(cur.Who).Show(LabelOf(cur), Pretty(Rolling(cur.Who, shown)), final: false,
                            unstableWords: translated ? 0 : Mathf.Min(unstable, seg.Length));
                        LiveWords?.Invoke(cur.Who, cur.Id, seg, seg.Length - Mathf.Min(unstable, seg.Length));
                    }
                    _prevSegWords = seg;
                }
            }
            _lastPartial = _segs.Count > 0 ? partial : "";

            // The recognizer resets after an endpoint; partial is empty again → utterance over.
            // Remember who said it (and which words were already handed to an earlier speaker): the final text can arrive a frame later.
            if (_inUtterance && words.Length == 0)
            {
                _inUtterance = false;
                if (_segs.Count > 0)
                {
                    var last = _segs[^1];
                    _lastSeg = last;
                    Learn(last);
                    _pendingFinals.Enqueue((last.Who, Time.time, last.Start, LabelOf(last), new List<Seg>(_segs)));
                }
                else _pendingFinals.Enqueue((null, Time.time, 0, "Someone", new List<Seg> { new() { StartTime = float.MinValue } }));
                _segs.Clear();
            }
            // Only finals this snapshot already counts: their words have left the partial text above.
            while (_finalsTaken < snap.Utterances && _stack.TryGetUtterance(out var u))
            {
                _finalsTaken++;
                if (logEveryWord) Debug.Log($"[Captions] final #{u.Id}: \"{u.Text}\""); // what the caption settles on (word lines are partials)
                // Drop endings whose final text never came (too quiet to count as an utterance).
                while (_pendingFinals.Count > 1 && Time.time - _pendingFinals.Peek().time > 1.5f) _pendingFinals.Dequeue();
                var (who, _, skip, label, segs) = _pendingFinals.Count > 0 ? _pendingFinals.Dequeue() : (null, 0f, 0, "Someone", null);
                var segWords = Slice(Words(u.Text), skip);
                if (segs != null && segs.Count > 0)
                {
                    segs[0].StartTime = float.MinValue; // re-read words said before the first live word belong to the first speaker
                    if (segWords.Length > 0) segs[^1].Committed = Shown(segs[^1], string.Join(" ", segWords), u.Language);
                    _finished[u.Id] = segs;
                    if (_finished.Count > 8) _finished.Remove(Mathf.Min(new List<int>(_finished.Keys).ToArray()));
                }
                if (segWords.Length == 0) continue; // everything was already handed to an earlier speaker
                var seg = string.Join(" ", segWords);
                if (who == null && segs != null && segs.Count > 0 && segs[^1].Wearer)
                {
                    // Your own words: shown back to you quietly, not added to anyone's caption, not signed.
                    _you.Show("You", Pretty(seg), final: true);
                    TranscriptLine?.Invoke(segs[^1].Id, null, true, seg);
                    Note($"Caption #{segs[^1].Id} You: \"{seg}\" (live)");
                    Note($"You: \"{seg}\"" + (u.StableLatency >= 0 ? $" (caption settled {u.StableLatency:F2} s after speech)" : ""));
                    continue;
                }
                Commit(who, segs != null && segs.Count > 0 ? segs[^1].Committed : seg);
                FinalCaption?.Invoke(who, segs != null && segs.Count > 0 ? segs[^1].Id : -1, seg);
                TranscriptLine?.Invoke(segs != null && segs.Count > 0 ? segs[^1].Id : ++_nextSegId, who, false, seg);
                if (segs != null && segs.Count > 0) Note($"Caption #{segs[^1].Id} {label}: \"{seg}\" (live)");
                // Only confirms words already shown → don't restart the 1.5 s collapse timer; corrected words → do.
                var changed = segWords.Length != CommonPrefix(segWords, _prevSegWords) || segWords.Length != _prevSegWords.Length;
                BubbleFor(who).Show(label, Pretty(Rolling(who, "")), final: true, refresh: changed);
                Note($"{label}: \"{seg}\"" + (u.StableLatency >= 0 ? $" (caption settled {u.StableLatency:F2} s after speech)" : ""));
                if (segs != null) foreach (var s in segs) Translate(s, u.Language);
                WatchAutoLanguage(u.Language, segWords.Length);
            }
            while (_pendingFinals.Count > 0 && Time.time - _pendingFinals.Peek().time > 3f) _pendingFinals.Dequeue();
            while (_stack.TryGetRefined(out var r)) OnRefined(r);
            while (_stack.TryGetLanguageNote(out var ln)) Note(ln);
            while (_translator != null && _translator.TryGet(out var tj)) OnTranslated(tj);
            AskLiveTranslation();
            CheckAwaiting();

            // Every person gets an idle orb beside their face; keep it there.
            foreach (var p in Speakers)
            {
                if (Strip) break; // no bubbles beside people: everything is in the subtitle strip
                var b = BubbleFor(p);
                b.Target = BesideHead(p, b.IsShowing, out var side);
                b.Side = side;
                b.Suppressed = mode == CaptionMode.Signing || !live || Ignored(p);
                b.SetSpeakerColor(p.Color, hue: p.Tag != null);
            }
            _fallback.Suppressed = mode == CaptionMode.Signing || !live || StraysHidden;
            foreach (var id in new List<int>(_bubbles.Keys))
                if (Strip || !Speakers.Exists(p => p.Id == id)) { Destroy(_bubbles[id].gameObject); _bubbles.Remove(id); }
        }

        // Whoever is talking keeps their bubble (and their rolling text) open: during an utterance that's the current
        // segment's speaker; in the pause between sentences, the last speaker while the voice detector still hears speech.
        // Only while their words keep coming: noise, music or your own (hidden) voice keeps the voice detector and the
        // utterance going without new words for them, and their bubble then never melted.
        void KeepTalkerOpen(bool speech)
        {
            Seg talking = _inUtterance && _segs.Count > 0 ? _segs[^1] : speech ? _lastSeg : null;
            if (talking == null || Time.time - _wordsAt > QuietSeconds) return;
            if (talking.Wearer && talking.Who == null) { _you.KeepOpen(); return; }
            BubbleFor(talking.Who).KeepOpen();
            var key = KeyOf(talking.Who);
            if (_committed.TryGetValue(key, out var c) && Time.time - c.time < TurnGapSeconds) _committed[key] = (c.text, Time.time);
        }

        // Live captions roll: a speaker's finished segments stay in their bubble and new words are appended,
        // so a sentence split by a pause still reads as one. A new turn starts after a few quiet seconds.
        readonly Dictionary<int, (string text, float time)> _committed = new();
        float TurnGapSeconds => captionHoldSeconds; // same as the bubble's hold: a melted bubble starts fresh
        const int MaxCommittedChars = 600; // a turn's text, of which the bubble shows the last few lines at reading pace

        int KeyOf(TrackedPerson p) => Strip ? -1 : p?.Id ?? -1; // subtitles: one running text for everyone

        string Rolling(TrackedPerson p, string partial)
        {
            var key = KeyOf(p);
            var prefix = _committed.TryGetValue(key, out var c) && Time.time - c.time < TurnGapSeconds ? c.text : "";
            return (prefix + " " + partial).Trim();
        }

        // The latest finished caption, whoever said it (the previous caption of the next one).
        Seg _lastSeg;

        void Commit(TrackedPerson p, string segment)
        {
            var text = Rolling(p, segment);
            if (text.Length > MaxCommittedChars)
            {
                var cut = text.IndexOf(' ', text.Length - MaxCommittedChars); // whole words: the bubble finds its top line by its text
                text = cut > 0 ? text.Substring(cut + 1) : text.Substring(text.Length - MaxCommittedChars);
            }
            _committed[KeyOf(p)] = (text, Time.time);
        }

        // Match words to faces, oldest first, once the camera has seen the moment each word was said.
        // Returns true if the speaker of the shown text changed.
        bool AssignWords(string[] words, float[] times)
        {
            var changed = false;
            while (_decided < words.Length && EvidenceReady(times[_decided]))
            {
                var k = _decided++;
                var t = times[k];
                var seg = _segs.Count > 0 ? _segs[^1] : null;
                var cur = seg?.Who;
                if (WearerWord(words, times, k, seg)) { changed = true; continue; }
                seg = _segs.Count > 0 ? _segs[^1] : null; // a quiet stretch after yours may have started a new segment
                cur = seg?.Who;
                if (useFusion)
                {
                    changed |= FuseWord(words, times, k, seg, cur);
                    continue;
                }
                var best = Talker(t, cur, out var bestScore, out var curScore, out var margin);
                while (_bestOf.Count <= k) _bestOf.Add(null);
                _bestOf[k] = best;
                if (logEveryWord) Debug.Log($"[Captions] word \"{words[k]}\" at {t:F2} → {(best != null ? best.Label : "unsure")} · {Scores(t)}");

                if (seg == null)
                {
                    _segs.Add(new Seg { Who = best, Start = k, Margin = margin, CheckedUntil = t, StartTime = t, Prev = _lastSeg });
                    changed = true;
                    Note($"Utterance → {(best != null ? best.Label : "unsure (nobody visibly talking)")} · {Scores(t)}");
                }
                else if (cur == null)
                {
                    if (best == null)
                    {
                        foreach (var p in Speakers) if (Mouth(p, t) >= 0) seg.SawStill = true;
                        continue;
                    }
                    // The unsure words only move onto a person who is seen talking — not just because a face appeared.
                    // All of them if no face was seen still before (the face just wasn't measured yet).
                    if (k - seg.Start <= 2 || !seg.SawStill) MoveSeg(seg, best, margin, $"seen talking · {Scores(t)}");
                    else Split(words, k, best, margin, $"seen talking · {Scores(t)}");
                    changed = true;
                }
                else if (!Speakers.Contains(cur) || Time.time - cur.LastSeen > recentSeconds)
                {
                    Split(words, k, best, margin, "speaker left the view");
                    changed = true;
                }
                else if (best != null && best != cur && OnsetOwnerOf(t) == best && (k == 0 || OnsetOwnerOf(times[k - 1]) != best))
                {
                    // A new burst of voice started and another person's lips sped up with it: their turn, from this word.
                    Split(words, k, best, margin, $"voice started with {best.Label}'s lips");
                    _challenger = null;
                    changed = true;
                }
                else if (best != null && best != cur && bestScore - curScore >= handoverMargin)
                {
                    if (_challenger != best) { _challenger = best; _challengeWord = k; }
                    var afterPause = _challengeWord > 0 && times[_challengeWord] - times[_challengeWord - 1] >= turnPauseSeconds;
                    if (afterPause || t - times[_challengeWord] >= handoverHoldSeconds)
                    {
                        Split(words, afterPause ? _challengeWord : TurnStart(times, seg.Start, _challengeWord, cur), best, bestScore - curScore, $"{(afterPause ? "after a pause" : "more active")} · {Scores(t)}");
                        changed = true;
                    }
                }
                else
                {
                    _challenger = null;
                    // Words keep coming but the speaker's mouth is still → it's someone we can't see.
                    if (!IsStill(cur, t)) seg.StillWord = -1;
                    else
                    {
                        if (seg.StillWord < 0) seg.StillWord = k;
                        if (t - times[seg.StillWord] >= stillSeconds)
                        {
                            Split(words, seg.StillWord, null, 0f, $"{cur.Label}'s mouth is still · {Scores(t)}");
                            changed = true;
                        }
                    }
                }

                // How clearly the segment's face is the talker, word by word (teaches the voice fingerprints).
                var last = _segs[^1];
                if (last.Who != null)
                {
                    last.Leads.Add(last.Who == best ? margin : last.Who == cur ? curScore - bestScore : 0f);
                }
            }
            return changed;
        }

        // The evidence lags a turn change by a word or two (lip sync needs a window). People leave a small gap
        // when they take the turn, so cut at the longest gap between words in the 1.5 s before the evidence switched.
        // Never cut before a word the evidence gave to the current speaker.
        int TurnStart(float[] times, int segStart, int challengeWord, TrackedPerson cur)
        {
            var best = challengeWord;
            var bestGap = 0f;
            for (var j = challengeWord; j > segStart + 1 && times[challengeWord] - times[j] <= 1.5f; j--)
            {
                if (j < challengeWord && _bestOf[j] == cur) break;
                var gap = times[j] - times[j - 1];
                if (gap > bestGap) { bestGap = gap; best = j; }
            }
            return bestGap >= 0.35f ? best : challengeWord;
        }

        // Words before `at` stay with the old speaker (their bubble then melts on its own); the rest go to the next one.
        /// <param name="force">Split even when the words stay with the same person (or nobody): a new voice took over.</param>
        void Split(string[] words, int at, TrackedPerson next, float margin, string why, bool wearer = false, bool force = false)
        {
            var old = _segs[^1];
            _challenger = null;
            if ((!force && next == old.Who && wearer == old.Wearer) || at <= old.Start) return;
            // A few unsure words between two stretches of the same person, in one utterance: theirs (the pitch or
            // the lips wavered; 2026-11-10 screen test: most "Someone" captions were 2–6 words inside a person's turn).
            if (!_noRevise && !wearer && next != null && old.Who == null && !old.Wearer && old.Prev != null && old.Prev.Who == next
                && at - old.Start <= 6 && _segs.Contains(old.Prev))
            {
                MoveSeg(old, next, margin, $"{why}; the unsure words between two stretches of {next.Label} are theirs");
                return;
            }
            var seg = words[old.Start..at];
            old.Committed = Shown(old, string.Join(" ", seg), LiveLanguage);
            TranscriptLine?.Invoke(old.Id, old.Who, old.Wearer && old.Who == null, old.Source);
            if (old.Wearer && old.Who == null) _you.Show("You", Pretty(old.Committed), final: true); // yours: not in anyone's caption
            else
            {
                Commit(old.Who, old.Committed);
                BubbleFor(old.Who).Show(LabelOf(old), Pretty(Rolling(old.Who, "")), final: true, refresh: false);
            }
            Note($"Caption #{old.Id} {LabelOf(old)}: \"{old.Source}\" (live)");
            Translate(old, LiveLanguage);
            old.NextLive = words[at..Mathf.Min(words.Length, at + 6)];
            Learn(old);
            Note($"Handover {LabelOf(old)} → {(wearer ? "You" : next?.Label ?? "unsure")} after {seg.Length} words, from \"{string.Join(" ", words[at..Mathf.Min(words.Length, at + 3)])}…\" ({why})");
            var t = at < _wordTimes.Length ? _wordTimes[at] : Time.time;
            _segs.Add(new Seg { Who = next, Wearer = wearer, Start = at, Margin = margin, CheckedUntil = t, StartTime = t, Prev = old });
            _prevSegWords = System.Array.Empty<string>();
            _lastPartial = "";
        }

        // The segment's words belong to someone else after all: take them back from the old bubble.
        void MoveSeg(Seg seg, TrackedPerson to, float margin, string why)
        {
            var from = seg.Who;
            if (from == null) _fallback.Hide();
            else
            {
                var kept = Rolling(from, "");
                if (kept.Length == 0) BubbleFor(from).Hide();
                else BubbleFor(from).Show(from.Label, Pretty(kept), final: true, refresh: false);
            }
            seg.Who = to;
            seg.Margin = margin;
            seg.Leads.Clear();
            _prevSegWords = System.Array.Empty<string>();
            _lastPartial = "";
            Note($"Utterance moved {(from?.Label ?? "unsure")} → {to.Label} ({why})");
            if (from == null) Revise(seg, to);
        }

        // Look back: the caption before `seg` was finished unsure ("Someone"), and `seg` — said within reviseGapSeconds
        // of it — was just given to `to`. People rarely hand over within a breath, so it was `to` too: move its words
        // from the unsure subtitle into their caption and correct the saved line. Not when the voice there sounds unlike
        // them, or when it was the wearer's own.
        void Revise(Seg seg, TrackedPerson to)
        {
            var prev = seg.Prev;
            if (Strip || prev == null || prev.Who != null || prev.Wearer || string.IsNullOrEmpty(prev.Committed) || _segs.Contains(prev)) return;
            var gap = seg.StartTime - prev.LastWord;
            if (gap < 0f || gap > reviseGapSeconds) { Note($"Caption #{prev.Id} stays unsure: {gap:F2} s before {to.Label}'s words"); return; }
            if (prev.Voice != null && prev.Voice.BoundPerson() >= 0 && prev.Voice.BoundPerson() != to.Id)
            { Note($"Caption #{prev.Id} stays unsure: its voice is Person {prev.Voice.BoundPerson()}'s"); return; }
            var ids = new List<int>();
            foreach (var p in Speakers) ids.Add(p.Id);
            var voice = _profiles != null ? _profiles.Factor(to.Id, prev.LastWord, ids, out _) : 1f;
            if (voice < 0.8f) { Note($"Caption #{prev.Id} stays unsure: sounds unlike {to.Label} (×{voice:F2})"); return; }
            if (ReplaceCommitted(null, prev.Committed, "", out var rest))
            {
                rest = rest.Trim();
                _committed[KeyOf(null)] = (rest, Time.time);
                if (rest.Length == 0) _fallback.Hide();
                else _fallback.Show("Someone", Pretty(rest), final: true, refresh: false);
            }
            else if (_fallback.IsShowing) _fallback.Hide();
            prev.Who = to;
            Commit(to, prev.Committed);
            TranscriptLine?.Invoke(prev.Id, to, false, prev.Source ?? prev.Committed);
            Note($"Caption #{prev.Id} revised unsure → {to.Label} (said {gap:F2} s before their next words, voice ×{voice:F2}): \"{prev.Committed}\"");
        }

        // ---------------- language switch: sentences read again ----------------

        // A finished utterance read again in the language the captions just switched to: split it between its speakers
        // by word time and swap it in for the live words in each speaker's bubble.
        void OnRefined(SpeechStack.Refined r)
        {
            var words = Words(r.Text);
            if (logEveryWord) Debug.Log($"[Captions] read again in {r.Language} #{r.Id}: \"{r.Text}\" ({r.AudioSeconds:F1} s)");
            if (!_finished.Remove(r.Id, out var segs)) return; // too old: its caption has gone
            var timed = r.WordTimes != null && r.WordTimes.Length == words.Length;
            if (!timed && segs.Count > 1) { Note($"Read again in {r.Language}: no word times, kept the live text ({r.Text})"); return; }
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            // Cut points: where each later segment starts. By time first (the two models time words slightly differently),
            // then moved to the nearest word that matches the segment's first live word.
            var cuts = new int[segs.Count + 1];
            cuts[segs.Count] = words.Length;
            for (var i = 1; i < segs.Count; i++)
            {
                var c = 0;
                if (timed)
                    while (c < words.Length && (float)(r.WordTimes[c] - offset) < segs[i].StartTime - 0.2f) c++;
                var first = FirstWord(segs[i].Source ?? segs[i].Committed);
                var bestDist = int.MaxValue;
                for (var j = Mathf.Max(0, c - 3); j <= Mathf.Min(words.Length - 1, c + 3); j++)
                    if (first != null && Plain(words[j]) == first && Mathf.Abs(j - c) < bestDist) { bestDist = Mathf.Abs(j - c); cuts[i] = j; }
                if (bestDist == int.MaxValue) cuts[i] = c;
                cuts[i] = Mathf.Clamp(cuts[i], cuts[i - 1], words.Length);
            }
            var parts = new List<string>[segs.Count];
            for (var i = 0; i < parts.Length; i++)
            {
                var part = words[cuts[i]..cuts[i + 1]];
                // Context audio before the utterance, or a cut a word off: don't repeat the previous caption's words.
                var before = i == 0 ? segs[0].Before : Words(segs[i - 1].Source ?? segs[i - 1].Committed);
                parts[i] = new List<string>(part[Overlap(before, part)..]);
            }
            // A turn already re-read on its own is replaced only if the whole-utterance read found more words in it
            // (its own clip can miss the words right before the next speaker).
            for (var i = 0; i < segs.Count; i++)
                if (parts[i].Count > 0)
                {
                    var text = string.Join(" ", parts[i]);
                    if (LiveTranslating(r.Language) && !(segs[i].Wearer && segs[i].Who == null))
                    {
                        // Translating: the re-read words aren't shown, their translation replaces the caption when it comes.
                        var was = segs[i].Source;
                        segs[i].Source = text;
                        // Nearly the same words: keep the translation they're reading.
                        if (was != null && segs[i].Committed != null && !segs[i].Committed.EndsWith(Waiting) && Similarity(was, text) >= 0.8f) continue;
                        Translate(segs[i], r.Language);
                    }
                    else { ApplyRefined(segs[i], text, r); segs[i].Source = text; }
                }
        }

        void ApplyRefined(Seg seg, string text, SpeechStack.Refined r)
        {
            if (string.IsNullOrEmpty(seg.Committed) || text == seg.Committed) return;
            if (seg.Wearer && seg.Who == null)
            {
                _you.Show("You", Pretty(text), final: true);
                TranscriptLine?.Invoke(seg.Id, null, true, text);
                Note($"Caption #{seg.Id} You: \"{text}\" (in {r.Language}; live was \"{seg.Committed}\", {r.DecodeSeconds:F2} s for {r.AudioSeconds:F1} s)");
                seg.Committed = text;
                return;
            }
            if (!ReplaceCommitted(seg.Who, seg.Committed, text, out var full))
            {
                // The caption already moved on. Unless this person has started a new sentence, show the corrected
                // one again — right words late beat wrong words on time.
                if (CurrentSpeaker != seg.Who || seg.Who == null && !_inUtterance)
                {
                    var key = KeyOf(seg.Who);
                    full = text;
                    _committed[key] = (full, Time.time);
                    BubbleFor(seg.Who).Show(LabelOf(seg), Pretty(full), final: true);
                    Note($"Caption #{seg.Id} {LabelOf(seg)}: \"{text}\" (in {r.Language}, shown again; live was \"{seg.Committed}\")");
                    seg.Committed = text;
                    TranscriptLine?.Invoke(seg.Id, seg.Who, false, text);
                }
                else Note($"Read again for #{seg.Id} came too late (speaker already talking again): \"{text}\"");
                return;
            }
            BubbleFor(seg.Who).Show(LabelOf(seg), Pretty(full), final: true);
            Note($"Caption #{seg.Id} {LabelOf(seg)}: \"{text}\" (in {r.Language}; live was \"{seg.Committed}\", {r.DecodeSeconds:F2} s for {r.AudioSeconds:F1} s)");
            seg.Committed = text;
            TranscriptLine?.Invoke(seg.Id, seg.Who, false, text);
        }

        // How many words at the end of `prev` are repeated at the start of `next`. One short word ("I", "a") alone
        // isn't enough.
        static int Overlap(string[] prev, string[] next)
        {
            if (prev == null || next == null) return 0;
            for (var n = Mathf.Min(6, Mathf.Min(prev.Length, next.Length)); n >= 1; n--)
            {
                var same = true;
                for (var i = 0; i < n && same; i++) same = Plain(prev[prev.Length - n + i]) == Plain(next[i]);
                if (same && (n > 1 || Plain(next[0]).Length >= 3)) return n;
            }
            return 0;
        }

        static string Plain(string w) => new string(System.Array.FindAll(w.ToLowerInvariant().ToCharArray(), char.IsLetterOrDigit));
        static string FirstWord(string text) { var w = Words(text); return w.Length > 0 ? Plain(w[0]) : null; }


        bool ReplaceCommitted(TrackedPerson p, string old, string replacement, out string full)
        {
            full = null;
            var key = KeyOf(p);
            if (!_committed.TryGetValue(key, out var c)) return false;
            // Several captions can be waiting for their translation: they come in order, so the oldest Waiting goes first.
            var at = old == Waiting ? c.text.IndexOf(old, System.StringComparison.Ordinal) : c.text.LastIndexOf(old, System.StringComparison.Ordinal);
            var len = old.Length;
            if (at < 0)
            {
                // The rolling caption keeps only its last MaxCommittedChars, so a long sentence may have lost its start.
                // Replace from the longest word-aligned tail of it that is still there (at least ~4 words).
                for (var i = old.IndexOf(' '); i >= 0 && old.Length - i >= 20; i = old.IndexOf(' ', i + 1))
                {
                    var tail = old.Substring(i + 1);
                    at = c.text.LastIndexOf(tail, System.StringComparison.Ordinal);
                    if (at >= 0) { len = tail.Length; break; }
                }
                if (at < 0) return false;
            }
            full = c.text.Substring(0, at) + replacement + c.text.Substring(at + len);
            if (full.Length > MaxCommittedChars)
            {
                var cut = full.IndexOf(' ', full.Length - MaxCommittedChars);
                full = cut > 0 ? full.Substring(cut + 1) : full.Substring(full.Length - MaxCommittedChars);
            }
            _committed[key] = (full, c.time);
            return true;
        }

        // ---------------- voice fingerprints ----------------

        // Every voiceCheckSeconds of new speech, fingerprint the latest stretch of the current segment.
        void CheckVoice(float[] times)
        {
            if (_voices == null || _segs.Count == 0 || _decided == 0) return;
            var seg = _segs[^1];
            if (seg.Checking || _decided <= seg.Start) return;
            var end = Mathf.Min(times[_decided - 1] + 0.4f, Time.time - 0.1f);
            var from = Mathf.Max(times[seg.Start] - 0.1f, end - voiceWindowSeconds);
            if (end - seg.CheckedUntil < voiceCheckSeconds || end - from < _voices.MinSeconds) return;
            var utterance = _utterance;
            if (_voices.Request(from, end, (v, sim) => OnVoice(seg, utterance, from, v, sim)))
            {
                seg.Checking = true;
                seg.CheckedUntil = end;
            }
        }

        void OnVoice(Seg seg, int utterance, float from, VoicePrints.Voice v, float sim)
        {
            seg.Checking = false;
            if (v == null) return;
            if (seg.Wearer) { seg.Voice ??= v; return; } // loud on the Quest mic: yours, whatever it sounds like
            var live = utterance == _utterance && _inUtterance && _segs.Count > 0 && _segs[^1] == seg;
            var bound = BoundPresent(v);

            if (seg.Voice == null || seg.Voice == v)
            {
                var first = seg.Voice == null;
                if (first) Note($"{LabelOf(seg)} sounds like {v.Label} (similarity {sim:F2}{(bound != null ? $", known from {bound.Label}" : "")})");
                seg.Voice = v;
                if (!live) { Learn(seg); return; }
                // The voice belongs to a face we can see, and the lips didn't clearly say otherwise → trust the voice.
                if (bound != null && bound != seg.Who && (seg.Who == null || seg.Lead < voiceOverridesBelow)
                    && MouthOver(bound, from, Time.time - 0.2f) >= stillMouth)
                    MoveSeg(seg, bound, 0f, $"voice {v.Label}, lips unclear (lead {seg.Lead:F2})");
                return;
            }

            // A different voice than at the start of this segment: someone else took over without a pause. Always
            // start a new caption there, so two people never share one bubble (2026-11-09: a man and a woman talking
            // back to back landed in one caption, because the new voice wasn't tied to a face yet and nothing split).
            // The new caption goes to the face the voice is tied to, else to whoever else in view is moving their
            // lips, else to nobody ("unsure") until the lips decide.
            if (!live) return;
            // Both voices are tied to the same person (by face, or by their voice profile): their own voice, heard
            // a little differently — not a new speaker (the two voices merge once their prints agree).
            var was = seg.Voice.BoundPerson();
            if (!_noRevise && was >= 0 && was == v.BoundPerson())
            {
                Note($"{seg.Voice.Label} → {v.Label} in {LabelOf(seg)}'s turn: both are Person {was}'s voice, no split");
                seg.Voice = v;
                return;
            }
            Note($"Voice changed {seg.Voice.Label} → {v.Label} in {LabelOf(seg)}'s turn (similarity {_voices.Similarity(seg.Voice, v):F2})");
            if (bound != null && bound == seg.Who) return; // the new print is this person's own: their first print was off
            var now = Time.time - 0.2f;
            var next = bound != null && MouthOver(bound, from, now) >= stillMouth ? bound : null;
            if (next == null)
            {
                var best = stillMouth;
                foreach (var p in Speakers)
                {
                    if (p == seg.Who || Time.time - p.LastSeen > recentSeconds) continue;
                    var m = MouthOver(p, from, now);
                    if (m > best) { best = m; next = p; }
                }
            }
            var at = seg.Start + 1;
            while (at < _decided && _wordTimes[at] < from) at++;
            if (at >= _decided) return;
            Split(_words, at, next, 0f, $"voice changed to {v.Label}", force: true);
            _segs[^1].Voice = v;
        }

        // A segment the face clearly won teaches its voice which face it belongs to.
        void Learn(Seg seg)
        {
            if (_voices == null || seg.Who == null || seg.Voice == null) return;
            if (useFusion ? seg.SureWords < 3 || seg.SureWords < 0.5f * seg.Leads.Count : seg.Lead < bindMargin)
            {
                Note(useFusion
                    ? $"{seg.Voice.Label} not tied to {seg.Who.Label}: turn not clear enough ({seg.SureWords} of {seg.Leads.Count} words sure, need 3 and half)"
                    : $"{seg.Voice.Label} not tied to {seg.Who.Label}: turn not clear enough (lead {seg.Lead:F2} < {bindMargin:F2})");
                return;
            }
            var before = seg.Voice.BoundPerson();
            _voices.Bind(seg.Voice, seg.Who.Id);
            var after = seg.Voice.BoundPerson();
            if (after != before)
                Note($"{seg.Voice.Label} ↔ {(after >= 0 ? $"Person {after}" : "nobody yet")} (clear turn by {seg.Who.Label}, lead {seg.Lead:F2})");
        }

        // The person a voice belongs to, if they are in view right now.
        TrackedPerson BoundPresent(VoicePrints.Voice v)
        {
            var id = v.BoundPerson();
            if (id < 0) return null;
            var p = Speakers.Find(x => x.Id == id);
            return p != null && Time.time - p.LastSeen <= recentSeconds ? p : null;
        }

        // Bubble name: the person; for an unsure segment, whose voice it is ("Person 2 · out of view", "Voice 3").
        // An unsure stretch is the wearer's when the Quest's own mic heard it loud: the median word, at its loudest
        // within ±0.15 s, is over wearerRawDb. Not with an external mic: one clipped on the other person hears them
        // louder than you. Once a stretch is yours it stays yours.
        void MarkWearer(Seg seg, float[] times, int end)
        {
            if (seg.Wearer || _wearerPrint != null || !detectWearer || mic.preferredDeviceId != 0 || end - seg.Start < 1) return;
            var loud = new List<float>();
            for (var k = seg.Start; k < end && k < times.Length; k++)
            {
                var max = float.NegativeInfinity;
                foreach (var (t, db) in _rawDb) if (Mathf.Abs(t - times[k]) <= 0.15f) max = Mathf.Max(max, db);
                if (!float.IsNegativeInfinity(max)) loud.Add(max);
            }
            if (loud.Count == 0) return;
            loud.Sort();
            var median = loud[loud.Count / 2];
            if (median < wearerRawDb) return;
            seg.Wearer = true;
            // Its first words may have shown in the unsure subtitle before we could tell: put back what was there before.
            var before = Rolling(null, "");
            if (before.Length > 0) _fallback.Show("Someone", Pretty(before), final: true, refresh: false);
            else if (_fallback.IsShowing) _fallback.Hide();
            Note($"Utterance → You (your own voice: {median:F0} dBFS on the Quest mic)");
        }

        // ---- Sounds that aren't words: (laughs), (cries), (applause)… ----------------------------------------------

        const string SoundsPref = "captions.sounds";
        SoundEvents _sounds;

        bool _soundTags = true;

        /// <summary>Show laughing, crying… in the captions (remembered). Alerts don't depend on it: they're always on.</summary>
        public bool SoundsOn => _soundTags;

        /// <summary>The sound tagger once it has started (null before): taught sounds go in here.</summary>
        public SoundEvents Sounds => _sounds;

        public void SetSounds(bool on)
        {
            _soundTags = on;
            PlayerPrefs.SetInt(SoundsPref, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        // ---- Alerts: smoke alarm, siren, doorbell… (SoundEvents), always on; ModeMenu shows them ----

        /// <summary>A confirmed alert window (again every second while it sounds).</summary>
        public event System.Action<SoundEvents.Alert> AlertHeard;
        [System.NonSerialized] public string alertExtra;   // -e alert smoke: that alert 4 s after launch (tests, demos)
        float _testAlertAt = -1f;
        readonly System.Collections.Generic.Dictionary<string, float> _alertLogged = new();

        /// <summary>Shows an alert as if it were heard (tests: -e alert smoke|alarm|siren|doorbell|…, the editor's A key).</summary>
        public void TestAlert(string kind)
        {
            var a = SoundEvents.Make(string.IsNullOrEmpty(kind) ? "smoke" : kind);
            if (a.HasValue) OnAlert(a.Value);
            else Debug.LogWarning($"[Captions] No alert '{kind}'. Kinds: {string.Join(", ", SoundEvents.Kinds)}");
        }

        void OnAlert(SoundEvents.Alert a)
        {
            // Into the log and transcript once per sounding (it repeats every second while it lasts).
            if (!_alertLogged.TryGetValue(a.Kind, out var last) || Time.time - last > 30f)
            {
                Note($"Alert: {a.Name} ({a.Level}, {a.Prob:F2})");
                if (live) TranscriptLine?.Invoke(++_nextSegId, null, false, $"[{a.Name.ToLowerInvariant()}]");
            }
            _alertLogged[a.Kind] = Time.time;
            AlertHeard?.Invoke(a);
        }

        void StartSounds()
        {
            var s = new SoundEvents();
            System.Threading.Tasks.Task.Run(() => s.Init()).ContinueWith(t =>
            {
                if (t.Result) _sounds = s;
                else Debug.LogWarning($"[Captions] {s.Error}");
            });
        }

        // Who made it: whoever is talking now, else whoever just finished, else the face whose mouth moved most (a laugh
        // moves it); nobody clear → "Someone". Applause and cheering belong to the room.
        void OnSound(SoundEvents.Heard h)
        {
            if (!live || mode == CaptionMode.Signing) return;
            var tag = $"({h.Word})";
            TrackedPerson who = null;
            var wearer = false;
            if (h.Personal)
            {
                var seg = _inUtterance && _segs.Count > 0 ? _segs[^1] : _lastSeg;
                var recent = seg != null && (_inUtterance && _segs.Count > 0 ||
                             _committed.TryGetValue(KeyOf(seg.Who), out var c) && Time.time - c.time < 3f);
                if (recent) { who = seg.Who; wearer = seg.Wearer && seg.Who == null; }
                else
                {
                    var best = minMouthActivity * 0.5f;
                    foreach (var p in Speakers)
                    {
                        if (!p.Visible || Ignored(p)) continue;
                        var m = Mouth(p, h.Time);
                        if (m > best) { best = m; who = p; }
                    }
                }
            }
            Note($"Sound: {h.Word} ({h.Prob:F2}) → {(wearer ? "You" : who?.Label ?? (h.Personal ? "Someone" : "the room"))}");
            TranscriptLine?.Invoke(++_nextSegId, who, wearer, tag);
            if (wearer)
            {
                var before = _you.IsShowing ? _you.Text : "";
                _you.Show("You", $"{before} {tag}".Trim(), final: true);
                return;
            }
            Commit(who, tag);
            var b = BubbleFor(who);
            b.Show(who != null ? who.Label : h.Personal ? "Someone" : "Sound", Pretty(Rolling(who, "")), final: true);
        }

        string LabelOf(Seg seg)
        {
            if (seg.Who != null) return seg.Who.Label;
            if (seg.Wearer) return "You";
            if (seg.Voice == null) return "Someone";
            var id = seg.Voice.BoundPerson();
            if (tracker.AnyTagged)
            {
                // Only tagged people are named: their voice, heard while they're out of view, keeps their name.
                var p = id >= 0 ? tracker.People.Find(x => x.Id == id) : null;
                return p is { Tag: not null } ? $"{p.Label} · out of view" : "Someone";
            }
            return id >= 0 ? $"Person {id} · out of view" : seg.Voice.Label;
        }

        // Word times from the recognizer (realtime clock) → Time.time, which the tracker uses.
        // Without them, assume new words were said ~1 s ago (typical caption delay).
        readonly List<float> _times = new();
        float[] WordTimes(double[] realtimes, int count)
        {
            var offset = Time.realtimeSinceStartupAsDouble - Time.timeAsDouble;
            if (realtimes != null && realtimes.Length == count)
            {
                var r = new float[count];
                for (var i = 0; i < count; i++) r[i] = (float)(realtimes[i] - offset);
                _times.Clear();
                _times.AddRange(r);
                return r;
            }
            if (count < _times.Count) _times.RemoveRange(count, _times.Count - count);
            while (_times.Count < count) _times.Add(Time.time - 1f);
            return _times.ToArray();
        }

        static string[] Words(string s) => string.IsNullOrWhiteSpace(s) ? System.Array.Empty<string>() : s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        static string[] Slice(string[] w, int from) => from <= 0 ? w : from >= w.Length ? System.Array.Empty<string>() : w[from..];
        static int CommonPrefix(string[] a, string[] b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
            return n;
        }

        // Who was visibly talking when the word starting at t was said? Talking score first, centred in view breaks ties.
        // margin = how far the winner's talking score is ahead of the next face (or of nobody).
        TrackedPerson Talker(float t, TrackedPerson cur, out float bestTalk, out float curTalk, out float margin)
        {
            // Strongest and fastest evidence: whose lips sped up when this burst of voice started.
            var burst = useOnsets ? _onsets?.At(t) : null;
            if (burst?.Owner != null && Time.time - burst.Owner.LastSeen < recentSeconds && !AsdDisagrees(burst.Owner, t))
            {
                bestTalk = 1f;
                curTalk = cur == burst.Owner ? 1f : 0f;
                margin = Mathf.Clamp01(0.5f + burst.Lead);
                return burst.Owner;
            }

            // One face in view: no other face to compare with, so it's an absolute question — is this mouth moving
            // with the sound? Mouth moving in step → them; still or out of step → someone we can't see.
            var only = OnlyFace();
            if (only != null)
            {
                if (SpeakingProbability(only, t, out var pr))
                {
                    bestTalk = margin = pr;
                    curTalk = only == cur ? pr : 0;
                    return pr >= asdThreshold ? only : null;
                }
                var m = Mouth(only, t);
                var talk = Talking(only, t, out var sync);
                bestTalk = margin = Mathf.Max(0, talk);
                curTalk = only == cur ? bestTalk : 0;
                return m >= singleFaceMouth && (float.IsNaN(sync) || sync >= singleFaceSync) ? only : null;
            }

            TrackedPerson best = null;
            var bestScore = float.MinValue;
            bestTalk = 0;
            curTalk = 0;
            var second = 0f;
            foreach (var p in Speakers)
            {
                var talk = Talking(p, t, out _);
                if (p == cur) curTalk = Mathf.Max(0, talk);
                if (talk < (SpeakingProbability(p, t, out _) ? asdThreshold : minMouthActivity)) { second = Mathf.Max(second, talk); continue; } // not visibly talking
                var angle = Vector3.Angle(head.forward, p.HeadPosition - head.position);
                var centred = Mathf.Clamp01(1f - angle / maxAngleDegrees);
                var score = mouthWeight * talk + (1f - mouthWeight) * centred - (angle > maxAngleDegrees ? 1f : 0f);
                if (score > bestScore)
                {
                    if (best != null) second = Mathf.Max(second, bestTalk);
                    bestScore = score; best = p; bestTalk = talk;
                }
                else second = Mathf.Max(second, talk);
            }
            margin = best != null ? bestTalk - second : 0f;
            return best;
        }

        // ---------------- who can be captioned ----------------

        // Once anyone is tagged, only tagged people are speakers: untagged faces, passers-by and false detections
        // (posters, screens) get no bubble and never take words. Nobody tagged → everyone the tracker sees.
        readonly List<TrackedPerson> _speakers = new();
        int _speakersFrame = -1;
        List<TrackedPerson> Speakers
        {
            get
            {
                if (_speakersFrame == Time.frameCount) return _speakers;
                _speakersFrame = Time.frameCount;
                _speakers.Clear();
                var tagged = tracker.AnyTagged;
                foreach (var p in tracker.People) if (!tagged || p.Tag != null) _speakers.Add(p);
                return _speakers;
            }
        }

        // Your own voice comes first, whatever the faces show (a tagged person nodding along, the lips model guessing):
        // a word is yours when it sounds like your voice print, or (no print, or the print undecided) when it is loud
        // on the Quest's own mic. Faces only decide the rest. The voice print is decisive at once; loudness needs two
        // loud words in a row to take the turn for you, two quiet ones to give it back.
        int _loudRun, _quietRun;

        // Loudest raw Quest-mic level from just before the word's start into its vowel (t is where it starts, often on
        // a quiet consonant: "s", "k"); NaN if not measured then.
        float RawDbAt(float t)
        {
            var max = float.NegativeInfinity;
            foreach (var (when, db) in _rawDb) if (when >= t - 0.1f && when <= t + 0.3f) max = Mathf.Max(max, db);
            return float.IsNegativeInfinity(max) ? float.NaN : max;
        }

        // True if word k was handled as yours (the face evidence is skipped).
        bool WearerWord(string[] words, float[] times, int k, Seg seg)
        {
            var sim = SelfAt(times[k]);
            var byVoice = !float.IsNaN(sim);
            var db = detectWearer && mic.preferredDeviceId == 0 ? RawDbAt(times[k]) : float.NaN; // not with an external mic
            var byLevel = !float.IsNaN(db);
            // With your voice print learned, loudness alone never makes a word yours: someone close by or a raised
            // voice is loud too (EasyCom: every loudness-only switch to You after the setup was someone else's words).
            // Your words hidden (showMine off): strict. Your voice must never land in someone's bubble, so loudness counts
            // even with a voice print, and a voice only somewhat like yours is yours. (Costs: now and then a loud word
            // of theirs is hidden as yours.)
            var strict = !showMine;
            if (!byVoice && ((_wearerPrint != null && !loudnessAloneWithVoice && !strict) || !byLevel)) return seg is { Wearer: true }; // nothing to go on: keep the turn as it is
            var loud = byLevel && db >= (float.IsNaN(_wearerLevelDb) ? wearerRawDb : _wearerLevelDb);
            var you = byVoice ? sim >= (strict ? StrictSelf : selfSimilar) || ((strict || sim > notSelf) && loud) : loud;
            // Strict, but a face in view is clearly talking: theirs unless the voice is clearly yours (2026-11-18: your
            // print matched them about as well as you, 0.3, and 123 of their turns were hidden as yours).
            if (you && strict && !(byVoice && sim >= selfSimilar + 0.1f) && OthersTalking(times[k])) you = false;
            if (k == 0) _loudRun = _quietRun = 0;
            if (you) { _loudRun = byVoice ? 2 : _loudRun + 1; _quietRun = 0; }
            else { _quietRun = byVoice && sim <= notSelf ? 2 : _quietRun + 1; _loudRun = 0; }
            var how = byVoice ? $"voice {sim:F2} like yours" + (byLevel ? $", {db:F0} dBFS" : "") : $"{db:F0} dBFS on the Quest mic";
            while (_bestOf.Count <= k) _bestOf.Add(null);

            if (seg is { Wearer: true })
            {
                if (you || _quietRun < 2) { _sure.Add((times[k], -1)); return true; }
                // Not you (by voice, or quiet for two words): someone else talks from here; the faces decide who.
                Split(words, byVoice ? k : k - 1, null, 0f, $"not you ({how})");
                return _segs[^1].Wearer; // (not split: too short to cut — stays yours)
            }
            if (!you) return false;
            if (seg == null)
            {
                _segs.Add(new Seg { Wearer = true, Start = k, CheckedUntil = times[k], StartTime = times[k], Prev = _lastSeg });
                Note($"Utterance → You ({how})");
                _sure.Add((times[k], -1));
                return true;
            }
            if (_loudRun < 2 && k > seg.Start && !strict) return false;
            var at = !byVoice && _loudRun >= 2 ? k - 1 : k;
            if (at <= seg.Start) ToWearer(seg, how);
            else Split(words, at, null, 0f, how, wearer: true);
            _sure.Add((times[k], -1));
            return true;
        }

        // The whole segment is yours after all: take its words back from whoever showed them.
        void ToWearer(Seg seg, string why)
        {
            var from = seg.Who;
            if (from == null)
            {
                var before = Rolling(null, "");
                if (before.Length > 0) _fallback.Show("Someone", Pretty(before), final: true, refresh: false);
                else if (_fallback.IsShowing) _fallback.Hide();
            }
            else
            {
                var kept = Rolling(from, "");
                if (kept.Length == 0) BubbleFor(from).Hide();
                else BubbleFor(from).Show(from.Label, Pretty(kept), final: true, refresh: false);
            }
            seg.Who = null;
            seg.Wearer = true;
            seg.Leads.Clear();
            _prevSegWords = System.Array.Empty<string>();
            _lastPartial = "";
            Note($"Utterance moved {(from?.Label ?? "unsure")} → You ({why})");
        }

        // ---------------- your voice print ----------------

        const string WearerVoicePref = "captions.wearerVoice", WearerLevelPref = "captions.wearerLevelDb";
        float[] _wearerPrint;
        float[] _wearerToday;           // your voice this session (see OnSelf)
        int _wearerTodayCount;
        const float LearnSelf = 0.5f;
        readonly PitchTrack _pitch = new();
        float _nextPitchCheck;
        // Each person's voice group, learned from the speech their captions got: id → (low, high) seconds.
        readonly Dictionary<int, Vector2> _pitchVotes = new();

        // The one person seen lately whose voice is clearly in this group (and nobody else seen lately is), else null.
        TrackedPerson PitchOwner(int group, TrackedPerson not = null)
        {
            if (group == 0) return null;
            TrackedPerson found = null;
            foreach (var p in Speakers)
            {
                if (p == not || Time.time - p.LastSeen > 30f || !_pitchVotes.TryGetValue(p.Id, out var v)) continue;
                var total = v.x + v.y;
                if (total < 3f) continue; // 3 s of their speech
                var share = (group < 0 ? v.x : v.y) / total;
                if (share >= 0.75f) { if (found != null) return null; found = p; } // two people in this group: can't tell
            }
            return found != null && Time.time - found.LastSeen <= recentSeconds ? found : null;
        }
        // Loudness bar for "you" learned with your voice: your typical word level on the Quest mic minus 6 dB
        // (EasyCom, 12 sessions: catches 99% of your speech, takes <1% of partners'; a fixed bar doesn't carry over
        // between mics and voices). NaN: use wearerRawDb.
        float _wearerLevelDb = float.NaN;
        readonly List<float> _enrolLevels = new();
        [HideInInspector] public bool enrolHold; // harness: speech now isn't the wearer reading (not learned)
        bool _enrolling;
        float _enrolHeard, _enrolFrom = -1f;
        readonly List<float[]> _enrolPrints = new();
        readonly List<(float from, float to, float sim)> _selfSims = new();
        bool _selfPending;
        float _nextSelfCheck;

        /// <summary>Your voice print is known (learned in the setup, or kept from an earlier session).</summary>
        public bool HasWearerVoice => _wearerPrint != null;
        /// <summary>The voice-print model is running, so your voice can be learned.</summary>
        public bool CanLearnVoice => useWearerVoice && _voices != null;
        public bool LearningVoice => _enrolling;
        /// <summary>0..1: how much of your speech has been heard while learning your voice.</summary>
        public float VoiceLearnProgress => Mathf.Clamp01(_enrolHeard / enrolSeconds);

        /// <summary>Setup: start listening to you. Every stretch of speech from now is yours, until enough is heard.</summary>
        public void StartLearningVoice()
        {
            if (!CanLearnVoice) return;
            _enrolling = true;
            _enrolHeard = 0f;
            _enrolFrom = -1f;
            _enrolPrints.Clear();
            _enrolLevels.Clear();
            Debug.Log("[Captions] Learning your voice…");
        }

        public void StopLearningVoice() { _enrolling = false; _enrolFrom = -1f; }

        public void ForgetWearerVoice()
        {
            _wearerPrint = null;
            _wearerLevelDb = float.NaN;
            PlayerPrefs.DeleteKey(WearerVoicePref);
            PlayerPrefs.DeleteKey(WearerLevelPref);
            PlayerPrefs.Save();
        }

        // While learning: cut your speech (by the VAD) into 1.2–3 s stretches and fingerprint each.
        void UpdateEnrol(bool speech)
        {
            if (!_enrolling || _voices == null) return;
            if (enrolHold) speech = false;
            if (speech)
            {
                if (_enrolFrom < 0) _enrolFrom = Time.time - 0.2f; // the VAD notices speech a little late
                if (Time.time - _enrolFrom >= 3f) { EnrolStretch(_enrolFrom, Time.time); _enrolFrom = Time.time; }
            }
            else if (_enrolFrom >= 0)
            {
                if (Time.time - _enrolFrom >= 1.2f) EnrolStretch(_enrolFrom, Time.time);
                _enrolFrom = -1f;
            }
        }

        void EnrolStretch(float from, float to)
        {
            // Word-level loudness as WearerWord measures it (loudest frame over 0.4 s), sampled through the stretch.
            var levels = new List<float>();
            for (var t = from; t + 0.3f <= to; t += 0.1f) { var d = RawDbAt(t); if (!float.IsNaN(d)) levels.Add(d); }
            _voices.Embed(from, to, e =>
            {
                if (!_enrolling || e == null) return;
                _enrolPrints.Add(e);
                _enrolLevels.AddRange(levels);
                _enrolHeard += to - from;
                if (_enrolHeard < enrolSeconds) return;
                _enrolling = false;
                // Drop a stretch that doesn't match the rest (a cough, someone else talking), then average.
                var mean = MeanPrint(_enrolPrints);
                if (_enrolPrints.Count >= 3) _enrolPrints.RemoveAll(p => Dot(p, mean) < 0.3f);
                if (_enrolPrints.Count == 0) { Debug.LogWarning("[Captions] Couldn't learn your voice: the stretches didn't match"); return; }
                _wearerPrint = MeanPrint(_enrolPrints);
                PlayerPrefs.SetString(WearerVoicePref, string.Join(",", System.Array.ConvertAll(_wearerPrint, v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                if (_enrolLevels.Count > 0)
                {
                    _enrolLevels.Sort();
                    _wearerLevelDb = Mathf.Clamp(_enrolLevels[_enrolLevels.Count / 2] - 6f, wearerRawDb - 10f, wearerRawDb + 10f);
                    PlayerPrefs.SetFloat(WearerLevelPref, _wearerLevelDb);
                }
                PlayerPrefs.Save();
                var agree = 0f;
                foreach (var p in _enrolPrints) agree += Dot(p, _wearerPrint) / _enrolPrints.Count;
                Note($"Learned your voice from {_enrolHeard:F1} s ({_enrolPrints.Count} stretches agree {agree:F2})" +
                     (float.IsNaN(_wearerLevelDb) ? "" : $", loud from {_wearerLevelDb:F0} dBFS"));
            });
        }

        static float[] MeanPrint(List<float[]> prints)
        {
            var m = new float[prints[0].Length];
            foreach (var p in prints) for (var i = 0; i < m.Length; i++) m[i] += p[i];
            var n = 0f;
            foreach (var v in m) n += v * v;
            n = Mathf.Sqrt(n);
            for (var i = 0; i < m.Length; i++) m[i] /= n > 0 ? n : 1f;
            return m;
        }

        static float Dot(float[] a, float[] b)
        {
            var s = 0f;
            for (var i = 0; i < Mathf.Min(a.Length, b.Length); i++) s += a[i] * b[i];
            return s;
        }

        static float[] LoadWearerPrint()
        {
            var text = PlayerPrefs.GetString(WearerVoicePref, "");
            if (text.Length == 0) return null;
            try { return System.Array.ConvertAll(text.Split(','), v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)); }
            catch (System.FormatException) { return null; }
        }

        // During speech, every ~0.6 s: how much do the last 1.5 s sound like you, and like the caption's own speaker?
        void CheckSelf(bool speech)
        {
            if (_voices == null || _enrolling || !speech || _selfPending || Time.time < _nextSelfCheck) return;
            var to = Time.time - 0.05f;
            var from = to - 1.5f;
            if (!_voices.Embed(from, to, e => OnSelf(from, to, e))) return;
            _selfPending = true;
            _nextSelfCheck = Time.time + 0.6f;
        }

        void OnSelf(float from, float to, float[] e)
        {
            _selfPending = false;
            if (e == null) return;
            // A new voice cuts the caption first; then (cut or not) is this stretch yours? Skipping that after a cut
            // left your words, when you answered someone mid-utterance, to the faces, so they went into the other
            // person's bubble.
            VoiceTurn(from, to, e);
            if (_wearerPrint == null) return;
            var sim = Dot(e, _wearerPrint);
            // Your voice as it sounds today, here (noise, the mic's fit, a cold): learned from stretches that are surely
            // yours (clearly like your print and loud on the Quest mic), and asked as well as the print. Not saved: a
            // wrong stretch can't follow you to the next session.
            if (_wearerToday != null) sim = Mathf.Max(sim, Dot(e, _wearerToday));
            var db = mic != null && mic.preferredDeviceId == 0 ? RawDbAt((from + to) * 0.5f) : float.NaN;
            if (sim >= LearnSelf && !float.IsNaN(db) && db >= (float.IsNaN(_wearerLevelDb) ? wearerRawDb : _wearerLevelDb))
            {
                _wearerToday = _wearerToday == null ? e : Blend(_wearerToday, e);
                if (++_wearerTodayCount == 5) Note("Learning how your voice sounds here");
            }
            _selfSims.Add((from, to, sim));
            _selfSims.RemoveAll(x => Time.time - x.to > 30f);
            if (logEveryWord) Debug.Log($"[Captions] voice {from:F2}–{to:F2} like yours {sim:F2}");
            // Words already decided in this stretch: put them right.
            if (!_inUtterance || _segs.Count == 0 || _words == null) return;
            var seg = _segs[^1];
            var k = seg.Start;
            while (k < _decided && k < _wordTimes.Length && _wordTimes[k] < from + 0.1f) k++;
            if (k >= _decided || k >= _words.Length) return;
            if (sim >= (showMine ? selfSimilar : StrictSelf) && !seg.Wearer
                && (showMine || sim >= selfSimilar + 0.1f || !OthersTalking((from + to) * 0.5f)))
            {
                if (k <= seg.Start) ToWearer(seg, $"voice {sim:F2} like yours");
                else Split(_words, k, null, 0f, $"voice {sim:F2} like yours", wearer: true);
                _loudRun = 2; _quietRun = 0;
            }
            else if (sim <= notSelf && seg.Wearer)
            {
                if (k <= seg.Start) { seg.Wearer = false; _you.Hide(); Note($"Utterance isn't yours after all (voice {sim:F2})"); }
                else Split(_words, k, null, 0f, $"not you (voice {sim:F2})");
                _quietRun = 2; _loudRun = 0;
            }
        }

        // A new voice in the middle of a caption: start the next speaker's caption within ~1.5 s of the switch, rather
        // than waiting for the lips (several words) or the 2.5 s voice check (2026-11-09: a man and a woman talking back
        // to back stayed in one bubble for seconds). Each caption keeps a rolling fingerprint of its own speaker; a
        // stretch that sounds clearly unlike it cuts the caption where the last like-sounding stretch ended.
        // True when it cut (the stretch is then the new caption's fingerprint).
        bool VoiceTurn(float from, float to, float[] e)
        {
            if (!_inUtterance || _segs.Count == 0 || _words == null || _wordTimes == null) return false;
            var seg = _segs[^1];
            if (seg.Wearer || from < seg.StartTime - 0.2f) return false; // yours (CheckSelf's), or reaching into the turn before
            // Only stretches full of speech: one that is mostly pause fingerprints the room, not a person.
            var n = 0;
            for (var i = seg.Start; i < _wordTimes.Length && i < _words.Length; i++)
                if (_wordTimes[i] >= from && _wordTimes[i] <= to) n++;
            if (n < 2) return false;
            if (seg.Print == null) { seg.Print = e; seg.PrintTo = to; return false; }
            var sim = Dot(e, seg.Print);
            if (logEveryWord) Debug.Log($"[Captions] voice {from:F2}–{to:F2} like {LabelOf(seg)}'s {sim:F2}");
            if (sim >= voiceTurnTwiceBelow)
            {
                seg.Unlike = 0;
                seg.PrintTo = to;
                if (sim >= 0.4f) seg.Print = Blend(seg.Print, e); // follows the speaker's voice as it goes
                return false;
            }
            seg.Unlike++;
            // The switch came after the last like-sounding stretch's middle: the words from there go to the new caption.
            var cut = Mathf.Max(seg.PrintTo - 0.75f, from);
            // Clearly unlike → cut. Somewhat unlike → cut if another face's lips are moving now (voice and lips agree),
            // else wait for one more stretch.
            if (sim >= voiceTurnBelow && seg.Unlike < 2 && OtherTalker(seg, cut) == null) return false;
            if (!TurnCut(seg, cut, $"voice changed (similarity {sim:F2} to {LabelOf(seg)}'s)")) return false;
            _segs[^1].Print = e;
            _segs[^1].PrintTo = to;
            return true;
        }

        // The face in view (other than the caption's own) whose lips moved most since t, or null if none clearly did.
        TrackedPerson OtherTalker(Seg seg, float t)
        {
            TrackedPerson next = null;
            var best = stillMouth;
            var now = Time.time - 0.2f;
            foreach (var p in Speakers)
            {
                if (p == seg.Who || Time.time - p.LastSeen > recentSeconds) continue;
                var m = MouthOver(p, Mathf.Min(t, now - 0.3f), now);
                if (m > best) { best = m; next = p; }
            }
            return next;
        }

        // Another voice took over at time t: the words from t go to a new caption, for the face whose lips are moving
        // (else nobody yet: the lips decide later). If no word from t is written yet, the cut waits for it (PitchTurn).
        bool TurnCut(Seg seg, float t, string why, TrackedPerson next = null)
        {
            var k = seg.Start + 1;
            while (k < _words.Length && k < _wordTimes.Length && _wordTimes[k] < t) k++;
            if (k >= _words.Length || k >= _wordTimes.Length)
            {
                if (seg.CutAt < 0) { seg.CutAt = t; seg.CutSince = Time.time; seg.CutWhy = why; seg.CutTo = next; }
                return false;
            }
            seg.CutAt = -1f;
            Split(_words, k, next ?? OtherTalker(seg, t), 0f, why, force: true);
            if (_segs[^1] == seg) return false;
            if (k > _decided) _decided = k; // words before the cut stay in the old caption
            _segs[^1].PitchFrom = t;
            return true;
        }

        // Every 0.1 s during speech: has the pitch jumped away from this caption's own voice (a low voice → a high one,
        // or back)? Then cut where the jump began. Also applies a cut waiting for its first word.
        void PitchTurn()
        {
            if (!_inUtterance || _segs.Count == 0 || _words == null || _wordTimes == null) return;
            var seg = _segs[^1];
            if (seg.Wearer) return;
            if (seg.CutAt >= 0)
            {
                if (Time.time - seg.CutSince > 3f) seg.CutAt = -1f; // the words never came
                else { TurnCut(seg, seg.CutAt, seg.CutWhy, seg.CutTo); return; }
            }
            if (!usePitchTurns || Time.time < _nextPitchCheck) return;
            _nextPitchCheck = Time.time + 0.1f;
            if (!_pitch.Learn(pitchTurnSemitones)) return; // one voice so far, or voices alike in pitch: lips and fingerprints only
            var now = Time.time;
            var start = Mathf.Max(seg.StartTime - 0.1f, seg.PitchFrom);
            var own = _pitch.Group(start, now - 0.5f, 15);   // the caption's own voice (≥ 0.3 s voiced)
            var latest = _pitch.Group(now - 0.4f, now, 8);   // the last 0.4 s (≥ 0.16 s voiced)
            if (latest != 0 && seg.Who != null && latest == own)
            {
                // Teach the caption's person their voice group (0.1 s per check).
                _pitchVotes.TryGetValue(seg.Who.Id, out var v);
                if (latest < 0) v.x += 0.1f; else v.y += 0.1f;
                _pitchVotes[seg.Who.Id] = v;
            }
            // The caption's voice belongs to someone else in view (their group, and only theirs): move it to them.
            if (own != 0 && !seg.PitchMoved && seg.Leads.Count < 6)
            {
                var owner = PitchOwner(own);
                if (owner != null && owner != seg.Who && (seg.Who == null || PitchOwner(-own) == seg.Who))
                {
                    seg.PitchMoved = true;
                    MoveSeg(seg, owner, 0f, $"pitch: {(own < 0 ? "low" : "high")} voice is {owner.Label}'s");
                }
            }
            if (own == 0 || latest == 0 || latest == own) { seg.PitchOff = 0; return; }
            if (++seg.PitchOff < 3) return; // 0.3 s in the other group
            // Where it began: back from now over the voiced frames in the new group (stop at the first in the old one).
            var at = now;
            var frames = _pitch.Frames;
            for (var i = frames.Count - 1; i >= 0 && frames[i].t > start; i--)
            {
                var g = _pitch.GroupOf(frames[i].f0);
                if (g == own) break;
                if (g == latest) at = frames[i].t;
            }
            seg.PitchOff = 0;
            var name = latest < 0 ? "low" : "high";
            if (logEveryWord) Debug.Log($"[Captions] pitch: {name} voice from {at:F2} (groups {_pitch.LowHz:F0} / {_pitch.HighHz:F0} Hz)");
            var next = PitchOwner(latest, seg.Who);
            TurnCut(seg, at - 0.1f, $"pitch: {name} voice", next);
        }

        static float[] Blend(float[] a, float[] b)
        {
            var r = new float[a.Length];
            var len = 0f;
            for (var i = 0; i < a.Length; i++) { r[i] = 0.8f * a[i] + 0.2f * b[i]; len += r[i] * r[i]; }
            len = Mathf.Sqrt(len);
            if (len > 1e-6f) for (var i = 0; i < r.Length; i++) r[i] /= len;
            return r;
        }

        // Similarity to your voice of the latest stretch that covers time t (NaN: not measured, or no print).
        float SelfAt(float t)
        {
            if (_wearerPrint == null) return float.NaN;
            for (var i = _selfSims.Count - 1; i >= 0; i--)
                if (t >= _selfSims[i].from - 0.1f && t <= _selfSims[i].to + 0.1f) return _selfSims[i].sim;
            return float.NaN;
        }

        // ---------------- combined decision ----------------

        // Belief that each person is the one talking (id; -1 = someone out of view). Kept across utterances: whoever
        // talked last most likely goes on.
        readonly Dictionary<int, float> _belief = new();
        bool _punctuated; // the recognizer writes punctuation (some models don't: then every pause may be a turn)
        // Every decided word: (time, person the faces were sure about, or -1). Teaches the voice profiles.
        readonly List<(float t, int id)> _sure = new();

        bool FuseWord(string[] words, float[] times, int k, Seg seg, TrackedPerson cur)
        {
            var t = times[k];
            var best = Fuse(t, SwitchChance(words, times, k), out var belief, out var second, out var sureFor, out var why);
            var margin = belief - second;
            while (_bestOf.Count <= k) _bestOf.Add(null);
            _bestOf[k] = best;
            _sure.Add((t, sureFor != null ? sureFor.Id : -1)); // every decided word; -1 = not sure
            _sure.RemoveAll(s => Time.time - s.t > 30f);
            if (logEveryWord) Debug.Log($"[Captions] word \"{words[k]}\" at {t:F2} → {(best != null ? best.Label : "unsure")} ({belief:F2}) · {why}");

            var changed = false;
            if (seg == null)
            {
                _segs.Add(new Seg { Who = best, Start = k, Margin = margin, CheckedUntil = t, StartTime = t, Prev = _lastSeg });
                Note($"Utterance → {(best != null ? best.Label : "unsure (nobody visibly talking)")} · {why}");
                if (best != null) Revise(_segs[^1], best);
                changed = true;
            }
            else if (best != cur && belief >= switchBelief && (!MidSentence(words, k)
                         || (belief >= switchBeliefMidSentence && k - 1 > seg.Start && _bestOf[k - 1] == best)))
            {
                if (cur == null && (k - seg.Start <= 2 || !seg.SawStill)) MoveSeg(seg, best, margin, why);
                else
                {
                    // The turn changed where the evidence first leaned to the new speaker (or at a pause just before).
                    var at = k;
                    while (at - 1 > seg.Start && _bestOf[at - 1] == best) at--;
                    Split(words, cur == null ? at : TurnStart(times, seg.Start, at, cur), best, margin, why);
                }
                changed = true;
            }
            else if (cur == null && best == null)
            {
                foreach (var p in Speakers) if (Mouth(p, t) >= 0) seg.SawStill = true;
            }

            var last = _segs[^1];
            last.LastWord = t;
            if (last.Who != null)
            {
                last.Leads.Add(last.Who == best ? margin : -margin);
                if (sureFor == last.Who) last.SureWords++;
            }
            return changed;
        }

        bool MidSentence(string[] words, int k) => _punctuated && k > 0 && !EndsClause(words[k - 1]);

        // Chance the speaker changes at word k: low mid-phrase, high after a pause or when a new burst of voice starts.
        float SwitchChance(string[] words, float[] times, int k)
        {
            var chance = switchPerWord;
            if (k == 0 || times[k] - times[k - 1] >= turnPauseSeconds) chance = switchAfterPause;
            var burst = useOnsets ? _onsets?.At(times[k]) : null;
            if (burst != null && (k == 0 || burst.Start > times[k - 1])) chance = Mathf.Max(chance, switchAtOnset);
            // The text knows where sentences end (when the recognizer punctuates): a turn rarely changes inside one,
            // and often right after a question or a full stop.
            if (!_punctuated && k > 0 && EndsClause(words[k - 1])) _punctuated = true;
            if (k > 0 && _punctuated)
            {
                var prev = words[k - 1];
                var last = prev.Length > 0 ? prev[^1] : ' ';
                if (".?!。？！؟".IndexOf(last) >= 0) chance = Mathf.Max(chance, switchAfterPause);
                else if (",;:،，、".IndexOf(last) < 0) chance = Mathf.Min(chance, switchMidSentence);
            }
            return chance;
        }

        // One step of the belief: prior (last belief, with the switch chance) × how well "only P talks" explains the
        // evidence at t. Returns the most likely talker (null = someone out of view).
        bool OthersTalking(float t)
        {
            foreach (var p in Speakers)
                if (Time.time - p.LastSeen <= recentSeconds && Evidence(p, t, out _) >= othersTalkingEvidence) return true;
            return false;
        }

        TrackedPerson Fuse(float t, float switchChance, out float belief, out float second, out TrackedPerson sureFor, out string why)
        {
            var people = new List<TrackedPerson>();
            foreach (var p in Speakers)
                if (Time.time - p.LastSeen <= recentSeconds
                    || Time.time - p.LastSeen <= speakerHoldSeconds && _belief.TryGetValue(p.Id, out var held) && held >= 0.5f)
                    people.Add(p);
            var n = people.Count;
            var ids = new List<int>();
            foreach (var p in people) ids.Add(p.Id);

            var e = new float[n];
            var allSilent = 1f;
            var parts = new System.Text.StringBuilder();
            for (var i = 0; i < n; i++)
            {
                e[i] = Evidence(people[i], t, out var src);
                allSilent *= 1f - e[i];
                parts.Append($"{people[i].Label} {e[i]:F2} {src} · ");
            }
            var like = new float[n + 1];
            for (var i = 0; i < n; i++)
            {
                var voice = _profiles != null ? _profiles.Factor(ids[i], t, ids, out var sim) : 1f;
                like[i] = e[i] * (allSilent / (1f - e[i])) * voice;
                if (voice != 1f) parts.Append($"voice {people[i].Label} ×{voice:F2} · ");
            }
            like[n] = allSilent * offscreenWeight * (n == 1 ? soloOffscreenFactor : 1f);

            var post = new float[n + 1];
            var total = 0f;
            for (var s = 0; s <= n; s++)
            {
                var id = s < n ? ids[s] : -1;
                var b = _belief.TryGetValue(id, out var x) ? x : 0f;
                post[s] = (b * (1f - switchChance) + switchChance / (n + 1)) * like[s];
                total += post[s];
            }
            _belief.Clear();
            var bestS = n; belief = 0; second = 0;
            for (var s = 0; s <= n; s++)
            {
                post[s] = total > 0 ? post[s] / total : 1f / (n + 1);
                _belief[s < n ? ids[s] : -1] = post[s];
                if (post[s] > belief) { second = belief; belief = post[s]; bestS = s; }
                else second = Mathf.Max(second, post[s]);
            }

            sureFor = null;
            var newcomer = bestS < n && _profiles != null && _profiles.Count(ids[bestS]) < voiceProfileSettings.minWindows;
            if (bestS < n && (e[bestS] >= sureEvidence || newcomer && e[bestS] >= newcomerSureEvidence && post[bestS] >= 0.5f))
            {
                var othersQuiet = true;
                for (var i = 0; i < n; i++) if (i != bestS && e[i] > sureOthers) othersQuiet = false;
                if (othersQuiet) sureFor = people[bestS];
            }
            parts.Append($"out of view {post[n]:F2}");
            why = parts.ToString();
            return bestS < n ? people[bestS] : null;
        }

        // Probability that this face is talking at t, from what we can see: LR-ASD where it scored the face,
        // otherwise lip movement (in step with the sound); then the voice-onset ↔ lips owner pulls it up or down.
        float Evidence(TrackedPerson p, float t, out string source)
        {
            float e;
            if (SpeakingProbability(p, t, out var prob)) { e = 0.5f + asdSharpness * (prob - 0.5f); source = "asd"; }
            else
            {
                var talk = Talking(p, t, out _);
                if (talk < 0) { e = 0.3f; source = "unseen"; }
                else { e = Mathf.Clamp01(talk); source = "lips"; }
            }
            if (source != "unseen" && weighLipsByQuality)
            {
                var q = FaceQuality(p);
                if (q < 0.99f) { e = 0.3f + q * (e - 0.3f); source += $" q{q:F1}"; }
            }
            var burst = useOnsets ? _onsets?.At(t) : null;
            if (burst?.Owner != null)
            {
                if (burst.Owner == p) { e = 1f - (1f - e) * (1f - onsetTrust); source += "+onset"; }
                else e *= 1f - onsetTrust;
            }
            return Mathf.Clamp(e, 0.02f, 0.98f);
        }

        // How far the picture of this face can be trusted for lip reading, 0–1: contrast of its crops × its size.
        float FaceQuality(TrackedPerson p)
        {
            var c = p.CropContrast < 0 ? 1f : Mathf.InverseLerp(lipContrastRange.x, lipContrastRange.y, p.CropContrast);
            var side = p.FaceBoxPx.width;
            var s = side <= 0 ? 1f : Mathf.InverseLerp(lipFaceSizePx.x, lipFaceSizePx.y, side);
            return c * s;
        }

        // The person the faces were sure about for the words between two times: -1 mixed / none, -2 not decided yet.
        int SureOwner(float from, float to)
        {
            if (_inUtterance && _decided < _wordTimes.Length && _wordTimes[_decided] <= to) return -2;
            int owner = -1, sure = 0, total = 0;
            foreach (var (t, id) in _sure)
            {
                if (t < from || t > to - 0.1f) continue;
                total++;
                if (id < 0) continue;
                if (owner >= 0 && id != owner) return -1;
                owner = id;
                sure++;
            }
            return sure > 0 && sure >= voiceProfileSettings.sureShare * total ? owner : -1;
        }

        // ---------------- active speaker detection ----------------

        // LR-ASD's speaking probability for this face around the word at t, if it has scored that moment.
        bool SpeakingProbability(TrackedPerson p, float t, out float prob)
        {
            prob = 0;
            if (_asd == null) return false;
            var logit = p.SpeakingBetween(t - 0.05f, t + 0.35f);
            if (float.IsNaN(logit)) return false;
            prob = 1f / (1f + Mathf.Exp(-logit));
            return true;
        }

        // With several faces scored, LR-ASD rates everyone near speech fairly high; what it gets right is who is
        // higher. So a face's talking score is its lead over the strongest other face (two faces: their softmax share).
        // A face that is clearly silent on its own keeps its low score.
        float SpeakingShare(TrackedPerson p, float t, float prob)
        {
            if (prob < asdStill) return prob;
            var mine = p.SpeakingBetween(t - 0.05f, t + 0.35f);
            var other = float.NegativeInfinity;
            foreach (var q in Speakers)
            {
                if (q == p || Time.time - q.LastSeen > recentSeconds) continue;
                var l = q.SpeakingBetween(t - 0.05f, t + 0.35f);
                if (!float.IsNaN(l)) other = Mathf.Max(other, l);
            }
            return float.IsNegativeInfinity(other) ? prob : 1f / (1f + Mathf.Exp(other - mine));
        }

        // Words keep coming, but is the current speaker silent? Unknown (face not measured) counts as not still.
        bool IsStill(TrackedPerson p, float t)
        {
            if (SpeakingProbability(p, t, out var prob)) return prob < asdStill;
            var m = Mouth(p, t);
            return m >= 0 && m < stillMouth;
        }

        // A word can be judged once every face in view has been scored past it (LR-ASD), or once the lip windows
        // have passed. Never later than 1.2 s, so captions don't stall when a face stops being scored.
        TrackedPerson OnsetOwnerOf(float t) => useOnsets ? _onsets?.At(t)?.Owner : null;

        // LR-ASD clearly says the onset owner is silent while someone else clearly talks → don't trust the onset.
        bool AsdDisagrees(TrackedPerson owner, float t)
        {
            if (!SpeakingProbability(owner, t, out var po)) return false;
            foreach (var q in Speakers)
                if (q != owner && SpeakingProbability(q, t, out var pq) && pq > po + asdOverruleMargin) return true;
            return false;
        }

        bool EvidenceReady(float t)
        {
            if (Strip) return true; // nobody to match words to: show them as they come
            if (useOnsets && _onsets != null)
            {
                if (_onsets.At(t)?.Owner != null) return true;    // the voice's owner is known: show the word now
                if (_onsets.Pending(t) && Time.time < t + 1.2f) return false; // decision due within ~0.45 s
            }
            if (Time.time >= t + 1.2f) return true;
            var lipRule = Time.time >= t + Mathf.Max(wordWindow.y, syncWindow.y);
            if (_asd == null) return lipRule;
            foreach (var p in Speakers)
            {
                if (Time.time - p.LastSeen > recentSeconds) continue;
                // Wait for LR-ASD only on faces it is actually scoring (scored within the last second);
                // otherwise a face it can't score would hold every word back for the full 1.2 s.
                var scoring = p.SpeakingUntil > Time.time - 1f;
                if (scoring ? p.SpeakingUntil < t + 0.35f : !lipRule) return false;
            }
            return true;
        }

        // Each visible face is encoded once per 0.48 s chunk (12 crops resampled to the model's 25 fps + the audio),
        // as soon as the chunk is complete; the features are kept, and every new chunk is scored with the 1 s of
        // features before it. Offline (AVA clips) this beats re-running the whole model on overlapping 0.6 s windows
        // (94% vs 91% with LR-ASD) for less work, and needs no look-ahead.
        // Diagnostics (diag log line): encoder runs, time per chunk, and why the last chunk wasn't scored.
        int _asdRuns;
        float _asdAudioLagMs;
        string _asdSkip = "not started";

        async Awaitable RunActiveSpeaker()
        {
            // An exception inside an un-awaited async method vanishes and the loop silently stops — log it and go on.
            while (this != null && _asd != null)
            {
                try { await ActiveSpeakerLoop(); return; }
                catch (System.Exception e)
                {
                    _asdSkip = $"error {e.GetType().Name}";
                    Debug.LogException(e);
                    Note($"Speaker model error: {e.GetType().Name}: {e.Message} — restarting it");
                    await Awaitable.WaitForSecondsAsync(1f);
                }
            }
        }

        async Awaitable ActiveSpeakerLoop()
        {
            const int n = ActiveSpeakerModel.CropSize * ActiveSpeakerModel.CropSize;
            const int chunk = ActiveSpeakerModel.Chunk, context = ActiveSpeakerModel.Context, feat = ActiveSpeakerModel.Features;
            var faces = new float[chunk * n];
            var samples = new float[ActiveSpeakerModel.Samples];
            var ctxAudio = new float[context * feat];
            var ctxVisual = new float[context * feat];
            while (this != null && _asd != null)
            {
                await Awaitable.WaitForSecondsAsync(0.1f); // check often: a chunk is encoded as soon as it is complete
                if (!live) { _asdSkip = "not live"; continue; }
                // The speaker model is the heaviest vision model (~200 ms a run on the headset): while speech-to-text
                // is behind, captions come first and who-said-what falls back on lips and voices for a moment.
                if (adaptVision && _stack != null && _stack.Lag > behindLag) { _asdSkip = "speech behind"; continue; }
                // Frames that are complete in BOTH streams. Audio can reach the ring a few hundred ms late, so end at
                // the newest audio, not the clock. Frame k covers [k/25, (k+1)/25) s of Time.time.
                _asdAudioLagMs = (Time.time - (float)_ring.LastTime) * 1000f;
                var end = Mathf.Min(Time.time - 0.1f, (float)_ring.LastTime - 0.02f);
                var lastFrame = Mathf.FloorToInt(end * 25f) - 1;
                if (Speakers.Count == 0) _asdSkip = "nobody tracked";
                foreach (var p in Speakers.ToArray())
                {
                    if (Time.time - p.LastSeen > 0.5f || p.Crops.Count == 0) continue;
                    // Crops trail the camera (cut once the next detection is in): end at this face's newest crop.
                    var last = Mathf.Min(lastFrame, Mathf.FloorToInt(p.Crops[^1].t * 25f - 0.5f));
                    // New face, or back after a gap: start afresh at the newest chunk.
                    if (p.AsdNext < last - 2 * chunk) { p.AsdNext = last - chunk + 1; p.AsdFeatures.Clear(); }
                    var fresh = false;
                    while (p.AsdNext + chunk - 1 <= last)
                    {
                        var k0 = p.AsdNext;
                        p.AsdNext += chunk;
                        var start = k0 / 25.0;
                        var audio = _ring.Get(start, start + ActiveSpeakerModel.Samples / (double)MicStream.OutputRate);
                        if (audio == null || audio.Length < ActiveSpeakerModel.Samples * 0.9f)
                        {
                            _asdSkip = audio == null ? "no audio" : $"short audio {audio.Length}/{ActiveSpeakerModel.Samples}";
                            continue;
                        }
                        System.Array.Clear(samples, 0, samples.Length);
                        for (var i = 0; i < samples.Length && i < audio.Length; i++) samples[i] = audio[i] * 32768f;
                        var ok = true;
                        for (var f = 0; f < chunk && ok; f++)
                        {
                            var tf = (k0 + f + 0.5f) / 25f;
                            byte[] best = null; var bestDt = 0.25f; // nearest crop (the camera runs at ~14 Hz)
                            foreach (var c in p.Crops)
                                if (Mathf.Abs(c.t - tf) < bestDt) { bestDt = Mathf.Abs(c.t - tf); best = c.grey; }
                            if (best == null) { ok = false; break; }
                            for (var i = 0; i < n; i++) faces[f * n + i] = best[i];
                        }
                        if (!ok) { _asdSkip = "crops don't cover the chunk"; continue; }
                        if (!string.IsNullOrEmpty(asdDumpFolder) && Time.time > asdDumpAfter) DumpInputs(p, faces, samples);
                        _asdSkip = "running";
                        var (a, v) = await _asd.Encode(samples, faces);
                        if (asdDumpAll && !string.IsNullOrEmpty(asdDumpFolder)) DumpChunk(p, k0, faces, samples, a, v);
                        for (var f = 0; f < chunk; f++)
                            p.AsdFeatures.Add((k0 + f, a[(f * feat)..((f + 1) * feat)], v[(f * feat)..((f + 1) * feat)]));
                        _asdRuns++;
                        fresh = true;
                    }
                    if (!fresh) continue;
                    // Score the newest second. A frame that couldn't be encoded (no crop or audio) borrows the nearest
                    // encoded one, as does the time before a face's first chunk.
                    var newest = p.AsdFeatures[^1].k;
                    var cut = 0;
                    while (cut < p.AsdFeatures.Count && p.AsdFeatures[cut].k <= newest - context) cut++;
                    if (cut > 0) p.AsdFeatures.RemoveRange(0, cut);
                    for (var s = 0; s < context; s++)
                    {
                        var k = newest - context + 1 + s;
                        var near = p.AsdFeatures[0];
                        foreach (var e in p.AsdFeatures)
                            if (Mathf.Abs(e.k - k) < Mathf.Abs(near.k - k)) near = e;
                        System.Array.Copy(near.audio, 0, ctxAudio, s * feat, feat);
                        System.Array.Copy(near.visual, 0, ctxVisual, s * feat, feat);
                    }
                    var logits = await _asd.Score(ctxAudio, ctxVisual);
                    _asdSkip = "ok";
                    var from = p.SpeakingUntil;
                    var logged = logSpeakerScores ? new System.Text.StringBuilder() : null;
                    for (var s = 0; s < context; s++)
                    {
                        var k = newest - context + 1 + s;
                        var tf = (k + 0.5f) / 25f;
                        if (tf > from && p.AsdFeatures.Exists(e => e.k == k))
                        {
                            p.Speaking.Add((tf, logits[s]));
                            logged?.Append($" {tf:F2}:{logits[s]:F2}");
                        }
                    }
                    if (logged != null && logged.Length > 0) Debug.Log($"[Captions] asd {p.Label}{logged}");
                    p.SpeakingUntil = Mathf.Max(p.SpeakingUntil, (newest + 1) / 25f);
                    cut = 0;
                    while (cut < p.Speaking.Count && Time.time - p.Speaking[cut].t > 12f) cut++;
                    if (cut > 0) p.Speaking.RemoveRange(0, cut);
                }
            }
        }

        // Debug: every encoded chunk, appended per person: k0 (int), crops (bytes), audio (floats), encoder audio and
        // visual features (floats) — to re-run the same inputs offline (tools: onnxruntime vs Unity's Inference Engine).
        void DumpChunk(TrackedPerson p, int k0, float[] faces, float[] samples, float[] a, float[] v)
        {
            System.IO.Directory.CreateDirectory(asdDumpFolder);
            using var w = new System.IO.BinaryWriter(System.IO.File.Open(System.IO.Path.Combine(asdDumpFolder,
                $"{p.Label.Replace(' ', '_')}_chunks.bin"), System.IO.FileMode.Append));
            w.Write(k0);
            foreach (var f in faces) w.Write((byte)f);
            foreach (var s in samples) w.Write(s);
            foreach (var x in a) w.Write(x);
            foreach (var x in v) w.Write(x);
        }

        // Debug: one strip of the crops (PGM) and the audio window per person, once.
        readonly HashSet<int> _dumped = new();
        void DumpInputs(TrackedPerson p, float[] faces, float[] samples)
        {
            if (!_dumped.Add(p.Id)) return;
            const int n = ActiveSpeakerModel.CropSize, frames = ActiveSpeakerModel.Chunk;
            System.IO.Directory.CreateDirectory(asdDumpFolder);
            var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{n * frames} {n}\n255\n");
            var px = new byte[n * n * frames];
            for (var f = 0; f < frames; f++)
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++) px[y * n * frames + f * n + x] = (byte)faces[f * n * n + y * n + x];
            var all = new byte[header.Length + px.Length];
            header.CopyTo(all, 0); px.CopyTo(all, header.Length);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(asdDumpFolder, $"{p.Label.Replace(' ', '_')}_crops.pgm"), all);
            var crops = p.Crops.ConvertAll(c => c.t.ToString("F3"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(asdDumpFolder, $"{p.Label.Replace(' ', '_')}_info.txt"),
                $"time {Time.time:F2} crops {p.Crops.Count}: {string.Join(" ", crops)}\naudio rms {Mathf.Sqrt(System.Linq.Enumerable.Average(samples, v => v * v)):F0}");
        }

        // The one face seen recently, or null if there are none or several.
        TrackedPerson OnlyFace()
        {
            TrackedPerson only = null;
            foreach (var p in Speakers)
            {
                if (Time.time - p.LastSeen > recentSeconds) continue;
                if (only != null) return null;
                only = p;
            }
            return only;
        }

        // Talking score 0..1 around the word at t: mouth movement above this face's own resting jitter, cut to 30% when
        // it is out of step with the sound. -1 if the face wasn't tracked then.
        float Talking(TrackedPerson p, float t, out float sync)
        {
            if (SpeakingProbability(p, t, out var prob)) { sync = float.NaN; return SpeakingShare(p, t, prob); }
            var mouth = Mouth(p, t);
            sync = LipSync.Score(p, _envelope, t - syncWindow.x, t + syncWindow.y);
            if (mouth < 0 || float.IsNaN(sync)) return mouth;
            return Mathf.Pow(mouth, mouthExponent) * (outOfSyncWeight + (1f - outOfSyncWeight) * Mathf.Clamp01(sync / fullSync));
        }

        // Mouth movement over a stretch, from this face's baseline; -1 if not tracked then.
        // Too few lip samples (the face models slowed to ~4 a second under load) see no syllable-rate movement and read
        // a talking face as still (EasyGerman, 2026-11-07: the speaker in view at "lips 0.02", her words "out of view"):
        // then it counts as not measured.
        float MouthOver(TrackedPerson p, float from, float to)
        {
            var raw = p.MouthActivityBetween(from, to, Mathf.CeilToInt((to - from) * minLipSamplesPerSecond));
            if (raw < 0) return raw;
            var b = p.MouthBaseline();
            return Mathf.Clamp01((raw - b) / Mathf.Max(0.3f, 1f - b));
        }

        // Mouth movement around t, measured from this face's baseline (a jittery face's "still" level counts as 0).
        float Mouth(TrackedPerson p, float t) => MouthOver(p, t - wordWindow.x, t + wordWindow.y);

        // Every face's evidence around time t, for the log: "Person 1 talk 0.72 (mouth 0.80 sync 0.45) · Person 2 …".
        string Scores(float t)
        {
            var parts = new List<string>();
            foreach (var p in Speakers)
            {
                if (SpeakingProbability(p, t, out var prob)) { parts.Add($"{p.Label} asd {SpeakingShare(p, t, prob):F2} (alone {prob:F2})"); continue; }
                var talk = Talking(p, t, out var sync);
                if (talk < 0) continue;
                var mouth = Mouth(p, t);
                parts.Add(float.IsNaN(sync) ? $"{p.Label} {talk:F2}" : $"{p.Label} talk {talk:F2} (mouth {mouth:F2} rest {p.MouthBaseline():F2} sync {sync:F2})");
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : "no faces";
        }

        // Not part of the conversation: someone untagged while others are tagged (see onlyTagged).
        bool Ignored(TrackedPerson p) => onlyTagged && p != null && p.Tag == null && tracker != null && tracker.AnyTagged;

        CaptionBubble BubbleFor(TrackedPerson p)
        {
            var hidden = mode == CaptionMode.Signing || !live || Ignored(p); // set before any Show() can draw it this frame
            if (p == null || Strip) { _fallback.Suppressed = hidden || p == null && StraysHidden; return _fallback; } // subtitles: one strip for everyone
            if (!_bubbles.TryGetValue(p.Id, out var b))
            {
                b = CaptionBubble.Create($"Bubble {p.Label}", transform, head, bubbleMaterial, bodyFont, nameFont, p.Color);
                b.collapseAfterSeconds = captionHoldSeconds;
                b.Target = BesideHead(p, false, out var side);
                b.Side = side;
                _bubbles[p.Id] = b;
            }
            b.Suppressed = hidden;
            return b;
        }

        // Orb beside the face; the panel grows further that way. The side is chosen once, when the person first
        // appears (away from a face next to them, otherwise towards the centre of view), and then kept — turning
        // your head must not make it jump across the face. It only changes, with the bubble closed, when another
        // face has sat on that side for a second (the caption would cover them) and the other side is free.
        Vector3 BesideHead(TrackedPerson p, bool open, out float side)
        {
            var toPerson = p.HeadPosition - head.position;
            var right = Vector3.Cross(Vector3.up, Flat(toPerson)).normalized;
            if (!_sides.TryGetValue(p.Id, out var s)) s = (DesiredSide(p), 0f, 0f); // (side, unused, crowded since)
            var crowded = !open && Crowded(p, s.side) && !Crowded(p, -s.side);
            if (!crowded) s.since = 0f;
            else if (s.since == 0f) s.since = Time.time;
            else if (Time.time - s.since > 1f) s = (-s.side, 0f, 0f);
            _sides[p.Id] = s;
            side = s.side;
            var scale = Mathf.Clamp(toPerson.magnitude / 1.2f, 0.6f, 3f); // bubble grows with distance; keep the gap proportional
            // Never on the face: at least half a face width (faces are placed assuming ~16 cm) plus a gap from its centre.
            var sideways = Mathf.Max(orbOffset.x * scale, tracker.faceWidthMeters * 0.5f + faceGap * scale);
            return p.HeadPosition + right * side * sideways + Vector3.up * orbOffset.y * scale;
        }

        float DesiredSide(TrackedPerson p)
        {
            var angle = SignedAngle(p);
            TrackedPerson nearest = null;
            var nearestGap = neighbourDegrees;
            foreach (var o in tracker.People)
            {
                if (o == p || Time.time - o.LastSeen > recentSeconds) continue;
                var gap = Mathf.Abs(SignedAngle(o) - angle);
                if (gap < nearestGap) { nearestGap = gap; nearest = o; }
            }
            if (nearest != null) return SignedAngle(nearest) > angle ? -1f : 1f; // neighbour on the right → grow left
            return angle > 8f ? -1f : 1f; // person right of centre → orb and panel on their left
        }

        // Another face close beside p on that side (+1 = the viewer's right), where p's caption would cover it.
        bool Crowded(TrackedPerson p, float side)
        {
            var angle = SignedAngle(p);
            foreach (var o in tracker.People)
            {
                if (o == p || Time.time - o.LastSeen > recentSeconds) continue;
                var gap = (SignedAngle(o) - angle) * side;
                if (gap > 0 && gap < crowdDegrees) return true;
            }
            return false;
        }

        float SignedAngle(TrackedPerson p) => Vector3.SignedAngle(Flat(head.forward), Flat(p.HeadPosition - head.position), Vector3.up);

        void UpdateMarkers()
        {
            if (!showFaceMarkers || tracker == null) return;
            foreach (var p in tracker.People)
            {
                if (!_markers.TryGetValue(p.Id, out var m))
                {
                    var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Destroy(q.GetComponent<Collider>());
                    q.name = $"Marker {p.Label}";
                    q.transform.SetParent(transform, false);
                    q.transform.localScale = Vector3.one * 0.03f;
                    if (panelMaterial != null) q.GetComponent<MeshRenderer>().sharedMaterial = new Material(panelMaterial) { color = p.Color };
                    m = q.transform;
                    _markers[p.Id] = m;
                }
                m.gameObject.SetActive(p.Visible);
                m.position = p.HeadPosition;
                m.rotation = Quaternion.LookRotation(p.HeadPosition - head.position, Vector3.up);
            }
            foreach (var id in new List<int>(_markers.Keys))
                if (!tracker.People.Exists(p => p.Id == id)) { Destroy(_markers[id].gameObject); _markers.Remove(id); }
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        /// <summary>Where the head looks: yaw and pitch (pitch limited to ±40°), no roll, so view-anchored UI stays
        /// level when you tilt your head.</summary>
        Quaternion ViewTilt()
        {
            var f = head.forward;
            var pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -40f, 40f);
            return Quaternion.LookRotation(Flat(f), Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
        }

        // The Nemotron models write proper case and punctuation, which is kept (older models wrote plain lower case).
        static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var lower = (s == s.ToUpperInvariant() ? s.ToLowerInvariant() : s).Replace(" i ", " I ").Replace(" i'", " I'");
            if (lower.StartsWith("i ") || lower.StartsWith("i'")) lower = "I" + lower.Substring(1);
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= ToSessionLog;
            _sessionLog?.Dispose();
            if (mic != null && _stack != null) mic.OnChunk -= FeedAudio;
            mic?.StopCapture();
            _stack?.Dispose();
            _translator?.Dispose();
            ScriptFonts.Remove();
            _voices?.Dispose();
            _sounds?.Dispose();
            _asd?.Dispose();
            _asd = null;
        }
    }
}
