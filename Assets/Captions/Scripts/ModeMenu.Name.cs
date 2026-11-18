using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Your name, called. In the Sounds panel you say your name three times (no keyboard needed); the forms the
    /// recognizer wrote for it are kept. In a conversation, when someone else's words have one of them, an info pill
    /// says who ("Anna called you", or "Someone called you"), controllers buzz, and a pin above the caller's head
    /// ripples; if they're out of view, the edge of the view glows on their side with an arrow pointing to them.
    /// The direction comes from whose lips moved (the mic is single-channel), so with nobody in view there's no arrow.
    /// Your own speech never counts. It works while a mode is on (that's when speech is transcribed).
    /// </summary>
    public partial class ModeMenu
    {
        const string NameKey = "captions.myName", NameFormsKey = "captions.myNameForms";
        const int NameSaysNeeded = 3;
        const float NameListenSeconds = 15f, CallRepeat = 4f;

        static readonly HashSet<string> NameFiller = new()
        {
            "my", "name", "is", "i'm", "im", "i", "am", "it's", "its", "hi", "hello", "the", "a", "and", "uh", "um", "yeah", "okay", "ok",
        };

        readonly List<string> _nameHeard = new();
        string _namePartial = "", _nameGuess;
        float _nameListenAt;
        string[] _nameForms = Array.Empty<string>();
        SoundPin _callPin;
        TrackedPerson _caller;
        int _calledSeg = -1;
        float _calledAt = -99f;
        bool _nameHooked;

        static string MyName => PlayerPrefs.GetString(NameKey, "");

        void HookName()
        {
            if (_nameHooked || director == null) return;
            _nameHooked = true;
            var forms = PlayerPrefs.GetString(NameFormsKey, "");
            _nameForms = forms.Length > 0 ? forms.Split(',') : Array.Empty<string>();
            director.Heard += OnHeardForName;
            director.LiveWords += (who, id, words, settled) => CheckCalled(who, id, words, settled);
            director.FinalCaption += (who, id, text) => CheckCalled(who, id, text.Split(' '), int.MaxValue);
            _callPin = MakePin(new SoundEntry { id = "call", name = "Called you", glyph = Glyphs.Person });
        }

        // ---------------- learning it ----------------

        void StartNameListen()
        {
            _nameHeard.Clear();
            _namePartial = "";
            _nameGuess = null;
            Enter(Stage.NameListen);
        }

        void EnterName(Stage stage)
        {
            switch (stage)
            {
                case Stage.NameListen:
                    _nameListenAt = Time.time;
                    Say(1, 2, "Say your name",
                        "Say just your name, three times, with a short pause between, the way people call you.\n" + NameListenBody(), Glyphs.Person);
                    SetButtons(("Cancel", () => Enter(Stage.SoundList), false));
                    break;
                case Stage.NameConfirm:
                    if (_nameGuess == null)
                    {
                        Say(2, 2, "Didn't catch it",
                            "Try again a little louder, with a pause after each time. It listens to the headset's mic.", Glyphs.Person);
                        SetButtons(("Cancel", () => Enter(Stage.SoundList), false), ("Try again", StartNameListen, true));
                    }
                    else
                    {
                        Say(2, 2, $"“{_nameGuess}”?",
                            $"When someone says {_nameGuess} during a conversation, you'll get a notification saying who.\n" +
                            $"<color=#FFFFFF80>Heard as: {string.Join(", ", KeptForms())}</color>", Glyphs.Person);
                        SetButtons(("Try again", StartNameListen, false), ("Yes", SaveName, true));
                    }
                    break;
            }
        }

        string NameListenBody()
        {
            var said = new List<string>(_nameHeard);
            if (_namePartial.Length > 0) said.Add($"<color=#FFFFFF80>{_namePartial}</color>");
            return said.Count > 0 ? "Heard: " + string.Join(" · ", said) : "<color=#FFFFFF80>Listening…</color>";
        }

        void OnHeardForName(string partial, string final)
        {
            if (!_setup.Open || _stage != Stage.NameListen) return;
            if (final != null)
            {
                var words = NameWords(final);
                if (words.Count > 0) _nameHeard.Add(string.Join(" ", words));
                _namePartial = "";
                Debug.Log($"[Captions] Name heard: \"{final}\"");
            }
            else _namePartial = partial ?? "";
            _bodyText.T.text = "Say just your name, three times, with a short pause between, the way people call you.\n" + NameListenBody();
            // Done once the name has come in twice among three or more lines (a stray line from the room doesn't count),
            // or after five lines.
            var lead = 0;
            foreach (var (_, c) in NameCounts()) lead = Mathf.Max(lead, c);
            if (_nameHeard.Count >= NameSaysNeeded && lead >= 2 || _nameHeard.Count >= 5) FinishNameListen();
        }

        void UpdateNameListen()
        {
            if (_stage == Stage.NameListen && _setup.Open && Time.time - _nameListenAt > NameListenSeconds) FinishNameListen();
        }

        void FinishNameListen()
        {
            // The word in the most lines is the name; the forms kept are those in two lines or more (and it). With
            // several lines but nothing in two of them, it didn't catch it.
            _nameGuess = null;
            var best = 0;
            foreach (var (w, c) in NameCounts()) if (c > best) { best = c; _nameGuess = Cap(w); }
            if (best < 2 && _nameHeard.Count > 1) _nameGuess = null;
            Enter(Stage.NameConfirm);
        }

        void SaveName()
        {
            var forms = KeptForms();
            PlayerPrefs.SetString(NameKey, _nameGuess);
            PlayerPrefs.SetString(NameFormsKey, string.Join(",", forms));
            PlayerPrefs.Save();
            _nameForms = forms.ToArray();
            Debug.Log($"[Captions] Your name: {_nameGuess} (forms {string.Join(", ", forms)})");
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(Stage.SoundList);
        }

        void ForgetName()
        {
            PlayerPrefs.DeleteKey(NameKey);
            PlayerPrefs.DeleteKey(NameFormsKey);
            PlayerPrefs.Save();
            _nameForms = Array.Empty<string>();
        }

        static List<string> NameWords(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = Norm(raw);
                if (w.Length >= 2 && !NameFiller.Contains(w)) list.Add(w);
            }
            return list;
        }

        // In how many of the lines heard each word came (once per line), in the order first heard.
        List<(string word, int count)> NameCounts()
        {
            var list = new List<(string, int)>();
            foreach (var h in _nameHeard)
            {
                var seen = new HashSet<string>();
                foreach (var w in h.Split(' '))
                {
                    if (w.Length == 0 || !seen.Add(w)) continue;
                    var i = list.FindIndex(e => e.Item1 == w);
                    if (i < 0) list.Add((w, 1)); else list[i] = (w, list[i].Item2 + 1);
                }
            }
            return list;
        }

        // The forms of your name to listen for: the name itself and any word heard in two lines or more.
        List<string> KeptForms()
        {
            var forms = new List<string>();
            var guess = _nameGuess?.ToLowerInvariant();
            foreach (var (w, c) in NameCounts()) if (c >= 2 || w == guess) forms.Add(w);
            return forms;
        }

        static string Norm(string w)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in w.ToLowerInvariant()) if (char.IsLetter(ch) || ch == '\'') sb.Append(ch);
            return sb.ToString().Trim('\'');
        }

        static string Cap(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1);

        // ---------------- hearing it ----------------

        bool IsMyName(string word)
        {
            var w = Norm(word);
            if (w.Length < 2) return false;
            foreach (var f in _nameForms)
            {
                if (w == f) return true;
                if (f.Length >= 5 && w.Length >= 4 && Edits(w, f) <= 1) return true; // "Daniela" ↔ "Daniella"
                if (w.EndsWith("'s") && w[..^2] == f) return true;
            }
            return false;
        }

        static int Edits(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 1) return 2;
            var d = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) d[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var prev = d[0]; d[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var t = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = t;
                }
            }
            return d[b.Length];
        }

        void CheckCalled(TrackedPerson who, int id, string[] words, int settled)
        {
            if (_nameForms.Length == 0 || words == null || !director.live) return;
            if (id >= 0 && id == _calledSeg) return;          // once per stretch of speech
            var n = Mathf.Min(settled, words.Length);
            var hit = false;
            for (var i = 0; i < n && !hit; i++) hit = IsMyName(words[i]);
            if (!hit) return;
            _calledSeg = id;
            var again = Time.time - _calledAt < CallRepeat && who == _caller;
            _calledAt = Time.time;
            _caller = who;
            var label = who != null ? who.Label : "Someone";
            Debug.Log($"[Captions] Name called by {label}: \"{string.Join(" ", words)}\"");
            if (who != null)
            {
                if (!again) _callPin.FiredSince = Time.time;
                _callPin.FiredAt = Time.time;
                _callPin.Name.text = $"{label} called you";
            }
            if (!again) Buzz(0.7f, 0.18f);
            OnAlert(new SoundEvents.Alert { Kind = "name", Name = $"{label} called you", Advice = "", Glyph = Glyphs.Person,
                Source = who != null ? "call" : null, Level = SoundEvents.Priority.Info, Prob = 1f, Time = Time.time });
        }

        // Keeps the caller's pin just above their head while they're tracked.
        void UpdateCaller()
        {
            if (_callPin == null) return;
            if (_caller != null && Time.time - _callPin.FiredAt < PinLinger) _callPin.E.pos = _caller.HeadPosition + Vector3.up * 0.28f;
        }
    }
}
