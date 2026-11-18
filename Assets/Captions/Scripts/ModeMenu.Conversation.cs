using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The conversation menu, laid out like iOS Control Center: modules on one dark frosted pane, wider than tall.
    ///   Left: the live card (who is talking, words and minutes so far, a level meter, Pause and Save) and the language
    ///   tile (the spoken language, and the one translations go into), each row opening in place into a sheet of chips.
    ///   Middle: four round toggles (Translate, Debug view, Microphone, Past conversations) and the way back to the menu.
    ///   Right: three tall capsule sliders: caption size, how many lines a caption shows, and how long captions stay.
    /// </summary>
    public partial class ModeMenu
    {
        const float QuickW = 0.44f, QuickH = 0.29f;
        const string HoldKey = "captions.holdSeconds", RecentKey = "captions.recentSpoken", RecentMineKey = "captions.recentMine";
        const float MinHold = 2f, MaxHold = 10f;
        const string LinesKey = "captions.lines";
        const int MinLines = 3, MaxLines = 6;
        // Chips in the language sheet after Auto, English and the recent ones (when the recogniser knows them).
        static readonly string[] CommonSpoken = { "es", "fr", "de", "zh", "hi", "ar", "pt", "it", "ja", "ru", "ko" };

        sealed class RoundToggle { public Glass G; public Label Icon, Name; public System.Func<bool> On; public Color OnColor; }

        Group _quickMain, _langSheet;
        readonly List<RoundToggle> _roundToggles = new();
        Label _quickStatus, _talkerName, _talkerState, _liveStats, _pauseIcon, _saveIcon, _saveText, _langTitle, _mineTitle, _modeName;
        Label _sheetTitle, _sheetHint;
        bool _sheetMine; // the sheet picks the language translations go into (else the spoken one)
        Glass _talkerDot, _saveButton, _pauseButton, _langTile, _sheetCard;
        readonly Glass[] _bars = new Glass[5];
        readonly float[] _barLevel = new float[5];
        Label _sizeValue, _holdValue, _linesValue, _modeIcon;
        Glass _modeBadge;
        Slider _sizeSlider, _holdSlider, _linesSlider;
        Rect _tileRect, _sheetRect;
        float _langOpen;      // 0 = the tile, 1 = the sheet; eases between
        bool _langWanted;
        const int SheetChips = 12;
        readonly (Glass g, Label l, BoxCollider c)[] _chips = new (Glass, Label, BoxCollider)[SheetChips];
        readonly List<string> _chipCodes = new();

        // What the live card shows: who spoke last, and the words of each finished line (a line read again replaces).
        TrackedPerson _talker;
        bool _talkerYou;
        float _talkerAt = -99f;
        readonly Dictionary<int, int> _lineWords = new();
        string _countFor;
        bool _paused; // listening paused from the live card: no captions until it's resumed

        void BuildQuick()
        {
            var root = new GameObject("Conversation menu").transform;
            root.SetParent(transform, false);
            _building = _quick = new Panel { Root = root, Dist = 0.62f, Drop = 0.12f };
            _group = null;
            AddGlass(root, Vector2.zero, new Vector2(QuickW, QuickH), 0.04f, PanelTint, 0, bevel: 0.007f).Dim = PanelDim;
            AddBacking(root, new Vector2(QuickW, QuickH));
            const float pad = 0.018f, gap = 0.012f;
            float left = -QuickW / 2 + pad, right = QuickW / 2 - pad, top = QuickH / 2, bottom = -QuickH / 2 + pad;
            AddLabel(root, "This conversation", new Vector2(left + 0.004f, top - 0.03f), 0.019f, Primary, TextAlignmentOptions.MidlineLeft, 0.2f);
            _quickStatus = AddLabel(root, "", new Vector2(right - 0.004f, top - 0.03f), 0.0115f, Secondary, TextAlignmentOptions.MidlineRight, 0.2f);
            _quickStatus.T.rectTransform.pivot = new Vector2(1, 0.5f);

            float cTop = top - 0.057f, split = cTop - 0.122f;    // the two rows of modules: tall ones above, short below
            float aL = left, aR = left + 0.19f;                    // column A: live card, language
            float bL = aR + gap, bR = bL + 0.104f;                 // column B: toggles, menu
            float cL = bR + gap;                                   // column C: sliders

            _group = _quickMain = new Group();
            BuildLiveCard(root, aL, aR, cTop, split);
            BuildToggleGrid(root, bL, bR, cTop, split);
            BuildLanguageTile(root, aL, aR, split - 0.01f, bottom);
            BuildMenuTile(root, bL, bR, split - 0.01f, bottom);
            _group = null;
            BuildSliders(root, cL, right, cTop, bottom);
            _sheetRect = Rect.MinMaxRect(aL, bottom, bR, cTop);
            BuildLanguageSheet(root);
            AddGrabBar(root, QuickW, QuickH, CloseQuick);

            director.LiveWords += (p, _, words, _) =>
            {
                if (words == null || words.Length == 0) return;
                _talker = p; _talkerYou = false; _talkerAt = Time.time;
            };
            director.TranscriptLine += (id, p, you, text) =>
            {
                _lineWords[id] = string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries).Length;
                if (you) { _talker = null; _talkerYou = true; _talkerAt = Time.time; }
            };
            root.gameObject.SetActive(false);
        }

        // Now Playing, for a conversation: who is talking (their colour), words and minutes, a level meter, Pause and Save.
        void BuildLiveCard(Transform root, float l, float r, float t, float b)
        {
            AddGlass(root, new Vector2((l + r) / 2, (t + b) / 2), new Vector2(r - l, t - b), 0.03f, CardTint, 1, bevel: 0.005f);
            var y = t - 0.025f;
            _talkerDot = AddGlass(root, new Vector2(l + 0.022f, y), Vector2.one * 0.012f, 0.006f, Primary, 2, bevel: 0.0015f);
            _talkerDot.Dim = 0f;
            _talkerName = AddLabel(root, "", new Vector2(l + 0.036f, y + 0.001f), 0.0165f, Primary, TextAlignmentOptions.MidlineLeft, 0.1f, layer: 3);
            _talkerState = AddLabel(root, "", new Vector2(l + 0.036f, y - 0.019f), 0.0112f, Secondary, TextAlignmentOptions.MidlineLeft, 0.1f, layer: 3);
            // The level meter: five bars that follow the microphone, like Now Playing's.
            for (var i = 0; i < _bars.Length; i++)
            {
                _bars[i] = AddGlass(root, new Vector2(r - 0.054f + i * 0.0085f, y - 0.008f), new Vector2(0.0042f, 0.008f), 0.0021f,
                    Secondary, 2, bevel: 0.001f);
                _bars[i].Dim = 0f;
            }
            _liveStats = AddLabel(root, "", new Vector2(l + 0.016f, b + 0.058f), 0.0112f, Tertiary, TextAlignmentOptions.MidlineLeft, r - l - 0.03f, layer: 3);

            // Pause (round) and Save (a capsule with the record dot: it turns red while saving).
            var by = b + 0.026f;
            var px = l + 0.016f + 0.018f;
            _pauseButton = AddGlass(root, new Vector2(px, by), Vector2.one * LargeBtn, LargeBtn / 2, Circle, 2, bevel: 0.003f, round: true);
            _pauseIcon = AddIcon(root, Glyphs.Pause, new Vector2(px, by), 0.019f, Primary, 3);
            AddHit(root, new Vector2(px, by), Vector2.one * 0.042f, TogglePause, _pauseButton);
            var sl = px + 0.018f + 0.01f;
            var sw = r - 0.016f - sl;
            _saveButton = AddGlass(root, new Vector2(sl + sw / 2, by), new Vector2(sw, LargeBtn), LargeBtn / 2, Circle, 2, bevel: 0.003f, round: true);
            _saveIcon = AddIcon(root, Glyphs.Record, new Vector2(sl + 0.019f, by), 0.017f, Primary, 3);
            _saveText = AddLabel(root, "Save", new Vector2(sl + 0.033f, by), 0.013f, Primary, TextAlignmentOptions.MidlineLeft, sw - 0.04f, layer: 3);
            AddHit(root, new Vector2(sl + sw / 2, by), new Vector2(sw, 0.042f), ToggleSave, _saveButton);
        }

        // Control Center's round toggles, two by two on one platter: filled when on.
        void BuildToggleGrid(Transform root, float l, float r, float t, float b)
        {
            AddGlass(root, new Vector2((l + r) / 2, (t + b) / 2), new Vector2(r - l, t - b), 0.03f, CardTint, 1, bevel: 0.005f);
            float x0 = l + (r - l) / 4, x1 = l + 3 * (r - l) / 4;
            float y0 = t - 0.03f, y1 = y0 - 0.058f;
            RoundButton(root, new Vector2(x0, y0), Glyphs.Translate, "Translate", ToggleTranslate, () => director.translate, Accent);
            // Name or rename people without leaving the conversation (the debug view is in Settings).
            RoundButton(root, new Vector2(x1, y0), Glyphs.PersonAdd, "People", NamePeople, null, default);
            RoundButton(root, new Vector2(x0, y1), Glyphs.Mic, "Mic", () => { _quick.Open = false; OpenMic(); }, null, default);
            RoundButton(root, new Vector2(x1, y1), Glyphs.History, "History", PastConversations, null, default);
        }

        void RoundButton(Transform root, Vector2 at, string glyph, string name, System.Action act, System.Func<bool> on, Color onColor)
        {
            var g = AddGlass(root, at, Vector2.one * LargeBtn, LargeBtn / 2, Circle, 2, bevel: 0.003f, round: true);
            var icon = AddIcon(root, glyph, at, 0.019f, Primary, 3);
            var label = AddLabel(root, name, at + new Vector2(0, -0.027f), 0.0098f, Secondary, TextAlignmentOptions.Midline, 0.05f, layer: 3);
            AddHit(root, at + new Vector2(0, -0.006f), new Vector2(0.048f, 0.05f), act, g);
            if (on != null) _roundToggles.Add(new RoundToggle { G = g, Icon = icon, Name = label, On = on, OnColor = onColor });
        }

        // Two rows: the spoken language (what it hears now) and the language translations go into. Tap a row: the tile
        // opens in place into that row's language sheet.
        void BuildLanguageTile(Transform root, float l, float r, float t, float b)
        {
            _tileRect = Rect.MinMaxRect(l, b, r, t);
            _langTile = AddGlass(root, _tileRect.center, _tileRect.size, 0.03f, CardTint, 1, bevel: 0.005f);
            var rowH = (t - b) / 2;
            for (var k = 0; k < 2; k++)
            {
                var mine = k == 1;
                var y = t - rowH / 2 - k * rowH;
                AddGlass(root, new Vector2(l + 0.026f, y), Vector2.one * 0.026f, 0.007f, mine ? Accent : SysGray, 2, bevel: 0.002f).Dim = 0f;
                AddIcon(root, mine ? Glyphs.Translate : Glyphs.Language, new Vector2(l + 0.026f, y), 0.016f, Primary, 3);
                AddLabel(root, mine ? "Translate into" : "Spoken language", new Vector2(l + 0.047f, y + 0.008f), 0.0098f, Secondary,
                    TextAlignmentOptions.MidlineLeft, r - l - 0.08f, layer: 3);
                var value = AddLabel(root, "", new Vector2(l + 0.047f, y - 0.0075f), 0.0145f, Primary, TextAlignmentOptions.MidlineLeft, r - l - 0.075f, layer: 3);
                if (mine) _mineTitle = value; else _langTitle = value;
                AddIcon(root, Glyphs.ExpandMore, new Vector2(r - 0.02f, y), 0.016f, Secondary, 3);
                AddHit(root, new Vector2((l + r) / 2, y), new Vector2(r - l, rowH), () =>
                {
                    _sheetMine = mine;
                    _langWanted = true;
                    UiFeedback.Play(UiSound.Pick, _quick.Root.position);
                }, _langTile);
            }
        }

        // Back to the start screen (modes, microphone, settings); the conversation waits there.
        void BuildMenuTile(Transform root, float l, float r, float t, float b)
        {
            var c = new Vector2((l + r) / 2, (t + b) / 2);
            var card = AddGlass(root, c, new Vector2(r - l, t - b), 0.03f, CardTint, 1, bevel: 0.005f);
            var at = new Vector2(l + 0.026f, t - 0.026f);
            _modeBadge = AddGlass(root, at, Vector2.one * 0.03f, 0.008f, Accent, 2, bevel: 0.002f);
            _modeBadge.Dim = 0f;
            _modeIcon = AddIcon(root, Glyphs.Bubbles, at, 0.017f, Primary, 3);
            _modeName = AddLabel(root, "", new Vector2(l + 0.014f, b + 0.03f), 0.0145f, Primary, TextAlignmentOptions.MidlineLeft, r - l - 0.02f, layer: 3);
            AddLabel(root, "Menu", new Vector2(l + 0.014f, b + 0.013f), 0.0105f, Secondary, TextAlignmentOptions.MidlineLeft, r - l - 0.02f, layer: 3);
            AddHit(root, c, new Vector2(r - l, t - b), BackToMenu, card);
        }

        // Tall capsules, as Control Center's brightness and volume: caption size, lines shown, and how long captions stay.
        void BuildSliders(Transform root, float l, float r, float t, float b)
        {
            var w = (r - l - 0.024f) / 3;
            _sizeSlider = TallSlider(root, l + w / 2, w, t, b, Glyphs.TextSize, out _sizeValue,
                () => Mathf.InverseLerp(MinText, MaxText, CaptionBubble.TextScale), v => SetTextScale(Mathf.Lerp(MinText, MaxText, v)), SaveTextScale);
            _holdSlider = TallSlider(root, r - w / 2, w, t, b, Glyphs.Timelapse, out _holdValue,
                () => Mathf.InverseLerp(MinHold, MaxHold, director.captionHoldSeconds), v =>
                {
                    var s = Mathf.Round(Mathf.Lerp(MinHold, MaxHold, v) * 2f) / 2f;
                    if (!Mathf.Approximately(s, director.captionHoldSeconds)) UiFeedback.Play(UiSound.Tick, _quick.Root.position);
                    director.SetCaptionHold(s);
                }, () => { PlayerPrefs.SetFloat(HoldKey, director.captionHoldSeconds); PlayerPrefs.Save(); });
            _linesSlider = TallSlider(root, (l + r) / 2, w, t, b, Glyphs.Subtitles, out _linesValue,
                () => Mathf.InverseLerp(MinLines, MaxLines, CaptionBubble.Lines), v =>
                {
                    var n = Mathf.RoundToInt(Mathf.Lerp(MinLines, MaxLines, v));
                    if (n != CaptionBubble.Lines) UiFeedback.Play(UiSound.Tick, _quick.Root.position);
                    CaptionBubble.Lines = n;
                }, () => { PlayerPrefs.SetInt(LinesKey, CaptionBubble.Lines); PlayerPrefs.Save(); });
        }

        Slider TallSlider(Transform root, float x, float w, float t, float b, string glyph, out Label value,
            System.Func<float> get, System.Action<float> set, System.Action done)
        {
            var c = new Vector2(x, (t + b) / 2);
            var track = AddGlass(root, c, new Vector2(w, t - b), w / 2, Track, 1, bevel: 0.004f);
            var fill = AddGlass(root, c, Vector2.one * w, w / 2, Filled, 2, bevel: 0.002f);
            fill.Dim = 0f;
            AddIcon(root, glyph, new Vector2(x, b + w / 2), 0.018f, TileInk, 3);
            value = AddLabel(root, "", new Vector2(x, t - 0.016f), 0.0098f, Secondary, TextAlignmentOptions.Midline, w, layer: 3);
            var hit = AddHit(root, c, new Vector2(w + 0.006f, t - b), null, track);
            return AddSlider(hit, track, fill, root, true, b + w / 2, t - w / 2, x, get, set, done);
        }

        // The tile, opened: Auto, English, the ones used lately and the most common, and every language.
        void BuildLanguageSheet(Transform root)
        {
            _group = _langSheet = new Group();
            _sheetCard = AddGlass(root, _tileRect.center, _tileRect.size, 0.03f, SheetFill, 4, bevel: 0.005f);
            _sheetCard.Dim = 0f;
            var r = _sheetRect;
            _sheetTitle = AddLabel(root, "Spoken language", new Vector2(r.xMin + 0.016f, r.yMax - 0.024f), 0.0165f, Primary, TextAlignmentOptions.MidlineLeft, 0.2f, layer: 5);
            _sheetHint = AddLabel(root, "", new Vector2(r.xMin + 0.016f, r.yMax - 0.043f), 0.0105f,
                Secondary, TextAlignmentOptions.MidlineLeft, r.width - 0.07f, layer: 5);
            var close = new Vector2(r.xMax - 0.024f, r.yMax - 0.026f);
            var dot = AddGlass(root, close, Vector2.one * CloseBtn, CloseBtn / 2, Circle, 5, bevel: 0.003f, round: true);
            AddIcon(root, Glyphs.Close, close, 0.015f, Primary, 6);
            AddHit(root, close, Vector2.one * (CloseBtn + 0.008f), () => { _langWanted = false; UiFeedback.Play(UiSound.Back, _quick.Root.position); }, dot);
            const int cols = 4;
            const float chipH = MediumBtn, gap = 0.008f; // the kit's Medium bordered button: a 34 pt capsule
            var chipW = (r.width - 0.032f - (cols - 1) * gap) / cols;
            var y0 = r.yMax - 0.068f - chipH / 2;
            for (var i = 0; i < SheetChips; i++)
            {
                var at = new Vector2(r.xMin + 0.016f + chipW / 2 + i % cols * (chipW + gap), y0 - i / cols * (chipH + gap));
                var g = AddGlass(root, at, new Vector2(chipW, chipH), chipH / 2, FillTertiary, 5, bevel: 0.003f, round: true);
                var l = AddLabel(root, "", at, 0.0125f, Primary, TextAlignmentOptions.Midline, chipW - 0.008f, layer: 6);
                var k = i;
                var c = (BoxCollider)AddHit(root, at, new Vector2(chipW + gap, chipH + gap), () => PickChip(k), g);
                _chips[i] = (g, l, c);
            }
            _group = null;
            SetGroup(_langSheet, false);
        }

        void FillChips()
        {
            _chipCodes.Clear();
            var known = new HashSet<string>(Week1Bench.SpeechStack.MultilingualLanguages);
            void Add(string code) { if (code != null && (code == "auto" || known.Contains(code)) && !_chipCodes.Contains(code)) _chipCodes.Add(code); }
            _sheetTitle.T.text = _sheetMine ? "Translate into" : "Spoken language";
            _sheetHint.T.text = _sheetMine ? "Other languages show in this one. Translated captions come a little slower."
                : "Choose one: faster and more accurate than Auto";
            if (_sheetMine)
            {
                Add(director.MyLanguage); Add("en");
                foreach (var c in PlayerPrefs.GetString(RecentMineKey, "").Split(',')) if (c.Length > 0) Add(c);
            }
            else
            {
                Add("auto"); Add("en");
                if (director.speechLanguage != "auto") Add(director.speechLanguage);
                Add(director.LiveLanguage);
                foreach (var c in PlayerPrefs.GetString(RecentKey, "").Split(',')) if (c.Length > 0) Add(c);
            }
            foreach (var c in CommonSpoken) Add(c);
            while (_chipCodes.Count > SheetChips - 1) _chipCodes.RemoveAt(_chipCodes.Count - 1);
            _chipCodes.Add("*"); // every language: the full list on the start screen
            for (var i = 0; i < SheetChips; i++)
            {
                var (g, l, c) = _chips[i];
                var used = i < _chipCodes.Count;
                g.Hidden = l.Hidden = !used;
                c.enabled = used;
                if (!used) continue;
                var code = _chipCodes[i];
                var on = code == (_sheetMine ? director.MyLanguage : director.speechLanguage);
                l.T.text = code == "*" ? "More ›" : code == "auto" ? "Auto" : CaptionTranslator.Name(code);
                l.C = on ? TileInk : code == "*" ? Secondary : Primary;
                g.Tint = on ? Filled : CardTint;
            }
        }

        void PickChip(int i)
        {
            if (i >= _chipCodes.Count) return;
            var code = _chipCodes[i];
            if (code == "*") { _langWanted = false; _langOpen = 0f; PickLanguageFromQuick(_sheetMine ? "mine" : "spoken"); return; }
            if (_sheetMine)
            {
                director.SetMyLanguage(code);
                if (!director.translate) director.SetTranslate(true); // picking a language to translate into means: translate
            }
            else director.SetLanguage(code);
            if (code != "auto")
            {
                var key = _sheetMine ? RecentMineKey : RecentKey;
                var recent = new List<string>(PlayerPrefs.GetString(key, "").Split(','));
                recent.Remove(code);
                recent.RemoveAll(c => c.Length == 0);
                recent.Insert(0, code);
                if (recent.Count > 4) recent.RemoveRange(4, recent.Count - 4);
                PlayerPrefs.SetString(key, string.Join(",", recent));
                PlayerPrefs.Save();
            }
            UiFeedback.Play(UiSound.Confirm, _quick.Root.position);
            FillChips();
            _langWanted = false;
        }

        void TogglePause()
        {
            _paused = !_paused;
            director.live = !_paused;
            if (_paused) director.StartFresh(); // what was under way is dropped: captions start again from what's said next
            UiFeedback.Play(_paused ? UiSound.Back : UiSound.Confirm, _quick.Root.position);
        }

        // Opening: the menu as you left it, minus an open language sheet.
        void ResetQuick()
        {
            _langWanted = false;
            _langOpen = 0f;
            _sheetCard.Hidden = true;
            SetGroup(_langSheet, false);
            SetGroup(_quickMain, true);
        }

        // Per frame while the conversation menu shows.
        void UpdateQuick()
        {
            var running = _log != null && _log.Running;
            var saving = _log != null && _log.Saving;
            if (_log != null && _countFor != _log.CurrentId) { _countFor = _log.CurrentId; _lineWords.Clear(); }
            var minutes = running ? Mathf.FloorToInt((Time.realtimeSinceStartup - _conversationStart) / 60f) : 0;
            var mins = !running ? "" : minutes < 1 ? "just started" : $"{minutes} min";
            _quickStatus.T.text = saving ? $"<color=#FF5A4E>●</color> Saving · {mins}" : director.translate
                ? $"Translating into {CaptionTranslator.Name(director.MyLanguage)}" : mins;

            // Live card.
            var talking = Time.time - _talkerAt < 1.5f;
            var who = _talkerYou ? "You" : _talker != null ? _talker.Label : _talkerAt > 0 ? "Someone" : "Listening";
            _talkerName.T.text = _paused ? "Paused" : who;
            _talkerState.T.text = _paused ? "Captions are off" : _talkerAt < 0 ? "Nobody has spoken yet" : talking ? "talking now" : "spoke last";
            var dot = _paused ? Tertiary : _talkerYou ? Primary : _talker != null ? _talker.Color : Accent;
            _talkerDot.Tint = new Color(dot.r, dot.g, dot.b, talking && !_paused ? 0.75f + 0.25f * Mathf.Sin(Time.time * 6f) : 0.7f);
            var words = 0;
            foreach (var n in _lineWords.Values) words += n;
            _liveStats.T.text = words == 0 ? (running ? mins : "") : $"{words} words" + (running ? $" · {mins}" : "");
            var level = _paused || micSettings == null || micSettings.mic == null ? 0f : Mathf.InverseLerp(-62f, -18f, micSettings.LevelDb);
            for (var i = 0; i < _bars.Length; i++)
            {
                var wobble = 0.55f + 0.45f * Mathf.Sin(Time.time * (7f + i * 2.3f) + i * 1.7f);
                var goal = Mathf.Max(0.12f, level * wobble);
                _barLevel[i] = Mathf.MoveTowards(_barLevel[i], goal, Time.deltaTime * 3f);
                _bars[i].Size = new Vector2(0.0042f, Mathf.Lerp(0.004f, 0.022f, _barLevel[i]));
            }
            _pauseIcon.T.text = _paused ? Glyphs.Mic : Glyphs.Pause;
            _pauseButton.Tint = _paused ? Filled : Circle;
            _pauseIcon.C = _paused ? TileInk : Primary;
            var pulse = saving ? 0.8f + 0.2f * Mathf.Sin(Time.time * 4f) : 1f;
            _saveButton.Tint = saving ? WithAlpha(Recording, 0.85f * pulse) : Circle;
            _saveIcon.C = saving ? Primary : running ? Recording : Tertiary;
            _saveText.T.text = saving ? "Saving" : "Save";
            _saveText.C = running ? Primary : Tertiary;

            foreach (var t in _roundToggles)
            {
                var on = t.On();
                t.G.Tint = on ? WithAlpha(t.OnColor, 0.92f) : Circle;
            }
            _modeBadge.Tint = WithAlpha(ModeColor(director.mode), 0.95f);
            _modeIcon.T.text = director.mode == CaptionDirector.CaptionMode.Signing ? Glyphs.Signing
                : director.mode == CaptionDirector.CaptionMode.SignToSpeech ? Glyphs.SignToSpeech
                : director.Subtitles || director.TrackingFallback ? Glyphs.Captions : Glyphs.Bubbles;
            _modeName.T.text = director.Subtitles || director.TrackingFallback ? "Subtitles"
                : director.mode == CaptionDirector.CaptionMode.Signing ? "Signing"
                : director.mode == CaptionDirector.CaptionMode.SignToSpeech ? "Sign to speech" : "Bubbles";

            // Language tile and its sheet.
            _langTitle.T.text = director.speechLanguage == "auto"
                ? $"Auto · {CaptionTranslator.Name(director.LiveLanguage)}" : CaptionTranslator.Name(director.speechLanguage);
            _mineTitle.T.text = CaptionTranslator.Name(director.MyLanguage) + (director.translate ? "" : " · off");
            _mineTitle.C = director.translate ? Primary : Secondary;
            var was = _langOpen;
            _langOpen = Mathf.MoveTowards(_langOpen, _langWanted ? 1f : 0f, Time.deltaTime * 4.5f);
            if (_langWanted && was == 0f) { FillChips(); SetGroup(_quickMain, false); }
            if (_langOpen > 0.55f != was > 0.55f || (_langOpen >= 1f && was < 1f)) SetGroup(_langSheet, _langOpen > 0.55f);
            if (!_langWanted && _langOpen <= 0f && was > 0f) { SetGroup(_langSheet, false); SetGroup(_quickMain, true); }
            if (_langOpen > 0.55f) FillChips(); // (hides the unused chips SetGroup just showed)
            var e = Mathf.SmoothStep(0f, 1f, _langOpen);
            var rect = new Rect(Vector2.Lerp(_tileRect.min, _sheetRect.min, e), Vector2.Lerp(_tileRect.size, _sheetRect.size, e));
            _sheetCard.Size = rect.size;
            At(_sheetCard, rect.center);
            _sheetCard.Hidden = _langOpen <= 0f;

            // Sliders.
            LayoutSlider(_sizeSlider);
            LayoutSlider(_holdSlider);
            LayoutSlider(_linesSlider);
            _sizeValue.T.text = $"{CaptionBubble.TextScale:0.0}×";
            _holdValue.T.text = $"{director.captionHoldSeconds:0.#} s";
            _linesValue.T.text = $"{CaptionBubble.Lines} lines";
            _linesValue.C = _linesSlider.Get() > 0.88f ? TileInk : Secondary;
            _sizeValue.C = _sizeSlider.Get() > 0.88f ? TileInk : Secondary;
            _holdValue.C = _holdSlider.Get() > 0.88f ? TileInk : Secondary;
        }
    }
}
