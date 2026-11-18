using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The start screen's tabs, after the visionOS dashboard look: a glass tab bar beside the window (Modes,
    /// Microphone, Settings, Conversations: ModeMenu.History.cs) switches what the window shows.
    ///   Microphone: the inputs (<see cref="MicSettings"/>) as a column of rows (blue toggle on the one in use), and
    ///   beside them a live input-level tile and an "add a microphone" tile. Notes (how to add a mic, a newly plugged
    ///   mic, a mic that wouldn't start) take the tab's place until answered.
    ///   Settings: the conversation topic as one segmented pill, and the debug view switch.
    /// Editor: M opens the microphone tab, 1–9 choose an input.
    /// </summary>
    public partial class ModeMenu
    {
        enum Tab { Modes, Mic, Topic, History }

        const int MaxMicRows = 5;

        // What one tab draws, so it can be shown and hidden as one.
        sealed class Group
        {
            public readonly List<Glass> Glass = new();
            public readonly List<Label> Labels = new();
            public readonly List<Collider> Hits = new();
        }

        sealed class Toggle { public Glass Track, Knob; public Vector2 At; }

        sealed class MicRow
        {
            public Glass Bg, Symbol;
            public Label Icon, Title, Detail;
            public Toggle Switch;
            public BoxCollider C;
            public MicSettings.Option Option;
        }

        sealed class Note { public string Text; public (string text, System.Action act, bool primary)[] Buttons; }

        Tab _tab;
        Group _group;                     // the group Add* calls add to (null: always shown)
        Group _modesTab, _micTab, _noteTab, _topicTab;
        Toggle _debugToggle, _rememberToggle, _soundsToggle;
        readonly Glass[] _tabButtons = new Glass[4];
        Glass _summaryPulse;
        Vector2 _summaryPulseAt;
        float _pulseStart, _summaryBarY, _shownProgress;
        Glass _summaryTrack, _summaryFill;
        const float SummaryBarW = 0.03f;
        static readonly Color PulseColor = Hex(0xFF9230); // the kit's orange (dark)

        // Per frame with the menu open: the Conversations button pulses while a conversation is being summarised.
        void UpdateSummaryPulse()
        {
            if (_summaryPulse == null) return;
            var working = _summarizer != null && _summarizer.Working;
            var progress = working ? _summarizer.Progress : -1f;
            // A number: the bar fills to it (easing, so steps between requests don't jump). No number yet (loading
            // the model): the button breathes instead.
            _summaryTrack.Hidden = _summaryFill.Hidden = progress < 0f;
            if (progress >= 0f)
            {
                _shownProgress = Mathf.MoveTowards(_shownProgress, progress, Time.deltaTime * 0.5f);
                var fw = Mathf.Lerp(0.005f, SummaryBarW, _shownProgress);
                _summaryFill.Size = new Vector2(fw, 0.005f);
                At(_summaryFill, new Vector2(_summaryPulseAt.x - SummaryBarW / 2 + fw / 2, _summaryBarY));
            }
            else if (!working) _shownProgress = 0f;
            working &= progress < 0f;
            if (working && _summaryPulse.Hidden) _pulseStart = Time.time;
            _summaryPulse.Hidden = !working;
            if (!working) return;
            var phase = (Time.time - _pulseStart) / 1.6f % 1f;            // one breath every 1.6 s
            var grow = Mathf.SmoothStep(0f, 1f, phase);
            var s = 0.048f * (1f + 0.42f * grow);
            _summaryPulse.Size = new Vector2(s, s);
            _summaryPulse.Radius = 0.012f * (1f + 0.42f * grow);
            // Bright as it leaves the button, gone by the time it's at its widest.
            _summaryPulse.Tint = WithAlpha(PulseColor, 0.85f * (1f - grow) * Mathf.Clamp01(phase * 8f));
        }
        Label _title;
        readonly MicRow[] _micRows = new MicRow[MaxMicRows];
        Glass _meterFill;
        Label _levelText, _status, _noteBody;
        readonly Slot[] _noteSlots = new Slot[2];
        Label _languageText, _spokenValue, _mineValue;
        Group _pickerTab;
        string _picking;  // the language list open instead of Settings: "spoken", "mine" or null
        const int PickerSlots = 36;
        readonly (Glass g, Label l, Collider c)[] _pickSlots = new (Glass, Label, Collider)[PickerSlots];
        Toggle _translateToggle;
        Label _translateDetail, _speedChip, _signerChip;
        Note _note;
        float _meterX0, _meterW, _meterY;

        // ---------------- building ----------------

        // The tab bar: a glass pill to the left of the window with one round button per tab.
        void BuildTabBar()
        {
            _group = null;
            var x = -W / 2 - 0.046f;
            // Five buttons: the four tabs, then Sounds (teach the app your own sounds: its own panel, not a tab).
            AddGlass(_root, new Vector2(x, 0), new Vector2(0.064f, 0.332f), 0.032f, PanelTint, 0, bevel: 0.006f).Dim = PanelDim;
            var glyphs = new[] { Glyphs.Captions, Glyphs.Mic, Glyphs.Tune, Glyphs.Chat };
            var sounds = new Vector2(x, 0.128f - glyphs.Length * 0.064f);
            var soundsButton = AddGlass(_root, sounds, Vector2.one * 0.048f, 0.024f, Borderless, 1, bevel: 0.003f);
            AddIcon(_root, Glyphs.Hearing, sounds, 0.024f, Primary, 2);
            AddHit(_root, sounds, Vector2.one * 0.056f, () => { _picking = null; OpenSounds(); }, soundsButton);
            for (var i = 0; i < glyphs.Length; i++)
            {
                var tab = (Tab)i;
                var at = new Vector2(x, 0.128f - i * 0.064f);
                if (tab == Tab.History)
                {
                    // Behind the Conversations button: a ring of light that swells and fades while a summary is written.
                    _summaryPulse = AddGlass(_root, at, Vector2.one * 0.048f, 0.024f, WithAlpha(PulseColor, 0f), 1, bevel: 0.004f);
                    _summaryPulse.Dim = 0f;
                    _summaryPulse.Hidden = true;
                    // Between the bar and the button: in front of the one, drawn before the other.
                    _summaryPulse.R.transform.localPosition = new Vector3(at.x, at.y, -0.0015f);
                    _summaryPulse.R.sortingOrder -= 1;
                    _summaryPulseAt = at;
                }
                _tabButtons[i] = AddGlass(_root, at, Vector2.one * 0.048f, 0.024f, Borderless, 1, bevel: 0.003f);
                AddIcon(_root, glyphs[i], at, 0.024f, Primary, 2);
                if (tab == Tab.History)
                {
                    // Under the icon: a slim bar that fills as the summary is written.
                    _summaryBarY = at.y - 0.0175f;
                    _summaryTrack = AddGlass(_root, new Vector2(at.x, _summaryBarY), new Vector2(SummaryBarW, 0.005f), 0.0025f, Track, 2, bevel: 0.001f);
                    _summaryFill = AddGlass(_root, new Vector2(at.x, _summaryBarY), new Vector2(0.005f, 0.005f), 0.0025f, PulseColor, 3, bevel: 0.001f);
                    _summaryTrack.Dim = _summaryFill.Dim = 0f;
                    _summaryTrack.Hidden = _summaryFill.Hidden = true;
                }
                AddHit(_root, at, Vector2.one * 0.056f, () => { _picking = null; ShowTab(tab); }, _tabButtons[i]);
            }
        }

        void BuildMicTab(float left, float right, float top)
        {
            _group = _micTab = new Group();
            var colW = (right - left) * 0.6f;
            for (var i = 0; i < MaxMicRows; i++)
            {
                var row = _micRows[i] = new MicRow();
                row.Bg = AddGlass(_root, Vector2.zero, new Vector2(colW, 0.046f), 0.023f, CardTint, 1, bevel: 0.004f);
                row.Symbol = AddGlass(_root, Vector2.zero, Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
                row.Icon = AddIcon(_root, Glyphs.Mic, Vector2.zero, 0.016f, Primary, 3);
                row.Title = AddLabel(_root, "", Vector2.zero, 0.0145f, Primary, TextAlignmentOptions.MidlineLeft, colW - 0.11f, layer: 3);
                row.Detail = AddLabel(_root, "", Vector2.zero, 0.011f, Secondary, TextAlignmentOptions.MidlineLeft, colW - 0.11f, layer: 3);
                row.Switch = AddToggle(Vector2.zero);
                row.C = (BoxCollider)AddHit(_root, Vector2.zero, Vector2.one * 0.01f, () => { if (row.Option != null) PickMic(row.Option); }, row.Bg);
            }

            // Right column: the input level as a stat tile (big number, a meter under it), and adding a mic.
            var x0 = left + colW + 0.014f;
            var w = right - x0;
            var y = top - 0.07f;
            const float statH = 0.118f, addH = 0.112f;
            AddGlass(_root, new Vector2(x0 + w / 2, y - statH / 2), new Vector2(w, statH), 0.03f, CardTint, 1, bevel: 0.005f);
            AddLabel(_root, "Input level", new Vector2(x0 + 0.018f, y - 0.026f), 0.0125f, Secondary, TextAlignmentOptions.MidlineLeft, w - 0.03f);
            _levelText = AddLabel(_root, "", new Vector2(x0 + 0.018f, y - 0.06f), 0.03f, Primary, TextAlignmentOptions.MidlineLeft, w - 0.03f);
            _meterX0 = x0 + 0.018f;
            _meterW = w - 0.036f;
            _meterY = y - statH + 0.022f;
            AddGlass(_root, new Vector2(_meterX0 + _meterW / 2, _meterY), new Vector2(_meterW, 0.008f), 0.004f, Track, 2, bevel: 0.0015f);
            _meterFill = AddGlass(_root, Vector2.zero, new Vector2(0.01f, 0.008f), 0.004f, SysGreen, 3, bevel: 0.001f);
            _meterFill.Dim = 0f;

            y -= statH + 0.012f;
            var c = new Vector2(x0 + w / 2, y - addH / 2);
            var add = AddGlass(_root, c, new Vector2(w, addH), 0.03f, CardTint, 1, bevel: 0.005f);
            var ip = new Vector2(x0 + 0.016f + 0.02f, y - 0.016f - 0.02f);
            AddGlass(_root, ip, Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
            AddIcon(_root, Glyphs.ExternalMic, ip, 0.018f, Primary, 3);
            AddLabel(_root, "›", new Vector2(x0 + w - 0.022f, ip.y), 0.026f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
            AddLabel(_root, "USB-C or clip-on", new Vector2(x0 + 0.018f, y - addH + 0.044f), 0.0115f, Secondary, TextAlignmentOptions.MidlineLeft, w - 0.03f);
            AddLabel(_root, "Add a mic", new Vector2(x0 + 0.018f, y - addH + 0.022f), 0.018f, Primary, TextAlignmentOptions.MidlineLeft, w - 0.03f);
            AddHit(_root, c, new Vector2(w, addH), AddMicNote, add);

            _status = AddLabel(_root, "", new Vector2(left + 0.004f, -H / 2 + 0.016f), 0.0115f, Tertiary, TextAlignmentOptions.MidlineLeft, right - left);

            // Notes: a paragraph and up to two buttons, bottom right.
            _group = _noteTab = new Group();
            _noteBody = AddLabel(_root, "", new Vector2(left, top - 0.07f), 0.015f, Primary, TextAlignmentOptions.TopLeft, right - left);
            _noteBody.T.lineSpacing = -4f;
            for (var i = 0; i < _noteSlots.Length; i++)
            {
                var slot = _noteSlots[i] = new Slot();
                slot.G = AddGlass(_root, Vector2.zero, new Vector2(0.12f, LargeBtn), LargeBtn / 2, Button, 2, round: true);
                slot.L = AddLabel(_root, "", Vector2.zero, 0.0155f, Primary, TextAlignmentOptions.Midline, layer: 3);
                slot.C = (BoxCollider)AddHit(_root, Vector2.zero, Vector2.one * 0.01f, () => slot.Act?.Invoke(), slot.G);
            }
            _group = null;
        }

        /// <summary>The picker's list: "auto" (spoken only), English, then every other language the multilingual model
        /// can caption, by name.</summary>
        static List<string> PickerCodes(bool spoken)
        {
            var codes = new List<string>(Week1Bench.SpeechStack.MultilingualLanguages);
            codes.Remove("en");
            codes.Sort((x, y) => string.CompareOrdinal(CaptionTranslator.Name(x), CaptionTranslator.Name(y)));
            codes.Insert(0, "en");
            if (spoken) codes.Insert(0, "auto");
            return codes;
        }

        // Top row, two halves that open the language lists: the language people speak, and yours (translations go into it).
        void BuildTopicTab(float left, float right, float top)
        {
            _group = _topicTab = new Group();
            _languageText = AddLabel(_root, "", new Vector2(left, top - 0.07f), 0.015f, Primary,
                TextAlignmentOptions.TopLeft, right - left);
            _languageText.T.lineSpacing = -4f;
            const float h = 0.054f;
            var y = 0.02f;
            var lhalf = (right - left - 0.01f) / 2;
            for (var k = 0; k < 2; k++)
            {
                var spoken = k == 0;
                var lx0 = spoken ? left : right - lhalf;
                var lx1 = lx0 + lhalf;
                var lcard = AddGlass(_root, new Vector2((lx0 + lx1) / 2, y), new Vector2(lhalf, h), h / 2, CardTint, 1, bevel: 0.004f);
                var ltx = lx0 + 0.022f;
                AddLabel(_root, spoken ? "Spoken language" : "My language", new Vector2(ltx, y + 0.009f), 0.0165f, Primary,
                    TextAlignmentOptions.MidlineLeft, lx1 - ltx - 0.034f);
                var lv = AddLabel(_root, "", new Vector2(ltx, y - 0.011f), 0.0125f, Secondary, TextAlignmentOptions.MidlineLeft, lx1 - ltx - 0.034f);
                if (spoken) _spokenValue = lv; else _mineValue = lv;
                AddLabel(_root, "›", new Vector2(lx1 - 0.018f, y), 0.022f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
                var which = spoken ? "spoken" : "mine";
                AddHit(_root, new Vector2((lx0 + lx1) / 2, y), new Vector2(lhalf, h), () => OpenPicker(which), lcard);
            }

            // Middle row, two halves: translate (captions as spoken, or in your language) and signing speed (deaf viewers
            // read at different paces: 0.75×, 1×, 1.25×, a chip that steps through them, like a podcast's speed button).
            const float rh = 0.052f;
            var ry = y - h / 2 - 0.014f - rh / 2;
            var half = (right - left - 0.01f) / 2;
            {
                var tl = left; var tr = left + half;
                var card = AddGlass(_root, new Vector2((tl + tr) / 2, ry), new Vector2(half, rh), rh / 2, CardTint, 1, bevel: 0.004f);
                var ix = tl + rh / 2;
                AddGlass(_root, new Vector2(ix, ry), Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
                AddIcon(_root, Glyphs.Captions, new Vector2(ix, ry), 0.017f, Primary, 3);
                var ttx = ix + 0.032f;
                AddLabel(_root, "Translate", new Vector2(ttx, ry + 0.009f), 0.0165f, Primary, TextAlignmentOptions.MidlineLeft, tr - ttx - 0.05f);
                _translateDetail = AddLabel(_root, "", new Vector2(ttx, ry - 0.011f), 0.0125f, Secondary,
                    TextAlignmentOptions.MidlineLeft, tr - ttx - 0.05f);
                _translateToggle = AddToggle(new Vector2(tr - 0.011f - ToggleW / 2, ry));
                AddHit(_root, new Vector2((tl + tr) / 2, ry), new Vector2(half, rh), () =>
                {
                    director.SetTranslate(!director.translate);
                    RefreshTopic();
                }, card);
            }
            {
                var sl = right - half;
                var card = AddGlass(_root, new Vector2((sl + right) / 2, ry), new Vector2(half, rh), rh / 2, CardTint, 1, bevel: 0.004f);
                var ix = sl + rh / 2;
                AddGlass(_root, new Vector2(ix, ry), Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
                AddIcon(_root, Glyphs.Signing, new Vector2(ix, ry), 0.017f, Primary, 3);
                var qx = ix + 0.032f;
                // Two chips, each tapped to cycle: who signs (one signer shows at a time) and their pace.
                const float chipW = 0.052f, signerW = 0.07f, chipH = MediumBtn;
                var cx = right - 0.012f - chipW / 2;
                var nx = cx - chipW / 2 - 0.008f - signerW / 2;
                var choose = signAvatar != null && signAvatar.signers is { Length: > 1 };
                var textRight = (choose ? nx - signerW / 2 : cx - chipW / 2) - 0.006f;
                AddLabel(_root, choose ? "Signing" : "Signing speed", new Vector2(qx, ry + 0.009f), 0.0165f, Primary, TextAlignmentOptions.MidlineLeft, textRight - qx);
                AddLabel(_root, choose ? "Signer and pace" : "The avatar's pace", new Vector2(qx, ry - 0.011f), 0.0125f, Secondary, TextAlignmentOptions.MidlineLeft, textRight - qx);
                AddGlass(_root, new Vector2(cx, ry), new Vector2(chipW, chipH), chipH / 2, FillSecondary, 2, bevel: 0.002f, round: true);
                _speedChip = AddLabel(_root, "", new Vector2(cx, ry), 0.014f, Primary, TextAlignmentOptions.Midline, chipW, layer: 3);
                var split = choose ? (nx + signerW / 2 + cx - chipW / 2) / 2 : sl;
                if (choose)
                {
                    AddGlass(_root, new Vector2(nx, ry), new Vector2(signerW, chipH), chipH / 2, FillSecondary, 2, bevel: 0.002f, round: true);
                    _signerChip = AddLabel(_root, "", new Vector2(nx, ry), 0.014f, Primary, TextAlignmentOptions.Midline, signerW, layer: 3);
                    AddHit(_root, new Vector2((sl + split) / 2, ry), new Vector2(split - sl, rh), () =>
                    {
                        signAvatar.NextSigner();
                        RefreshTopic();
                    }, card);
                }
                AddHit(_root, new Vector2((split + right) / 2, ry), new Vector2(right - split, rh), () =>
                {
                    var speeds = SignAvatar.Speeds;
                    var i = System.Array.FindIndex(speeds, v => Mathf.Approximately(v, SignAvatar.UserSpeed));
                    SignAvatar.UserSpeed = speeds[(i + 1) % speeds.Length];
                    RefreshTopic();
                }, card);
            }

            // Bottom row, three thirds: the debug view (face boxes, lip outlines and scores in the room; off unless you
            // want them), remember people (names kept between sessions by face, on the headset only) and the welcome
            // steps again.
            ry -= rh + 0.01f;
            var third = (right - left - 0.03f) / 4;   // four cards (named third from when there were three)
            void Card(float x0, string glyph, string title, string sub, bool toggle, System.Action act, out Toggle sw)
            {
                var x1 = x0 + third;
                var card = AddGlass(_root, new Vector2((x0 + x1) / 2, ry), new Vector2(third, rh), rh / 2, CardTint, 1, bevel: 0.004f);
                var ix = x0 + rh / 2;
                AddGlass(_root, new Vector2(ix, ry), Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
                AddIcon(_root, glyph, new Vector2(ix, ry), 0.017f, Primary, 3);
                var tx = ix + 0.032f;
                var tw = x1 - tx - (toggle ? ToggleW + 0.016f : 0.03f);
                AddLabel(_root, title, new Vector2(tx, ry + 0.009f), 0.0155f, Primary, TextAlignmentOptions.MidlineLeft, tw);
                AddLabel(_root, sub, new Vector2(tx, ry - 0.011f), 0.0118f, Secondary, TextAlignmentOptions.MidlineLeft, tw);
                sw = toggle ? AddToggle(new Vector2(x1 - 0.011f - ToggleW / 2, ry)) : null;
                if (!toggle) AddLabel(_root, "›", new Vector2(x1 - 0.018f, ry), 0.022f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
                AddHit(_root, new Vector2((x0 + x1) / 2, ry), new Vector2(third, rh), act, card);
            }
            Card(left, Glyphs.Debug, "Debug view", "Faces, lips", true, () =>
            {
                director.SetDebugView(!director.DebugView);
                RefreshTopic();
            }, out _debugToggle);
            Card(left + third + 0.01f, Glyphs.Person, "People", "Remember names", true, () =>
            {
                if (tracker == null) return;
                tracker.SetRememberPeople(!tracker.rememberPeople);
                RefreshTopic();
            }, out _rememberToggle);
            Card(left + 2 * (third + 0.01f), Glyphs.Hearing, "Sounds", "Laughs, cries…", true, () =>
            {
                director.SetSounds(!director.SoundsOn);
                RefreshTopic();
            }, out _soundsToggle);
            Card(right - third, Glyphs.Wave, "Welcome", "Start again", false, OpenWelcome, out _);
            _group = null;
        }

        // The language list, in place of the Settings tab: a 6 × 6 grid (Auto, English, then the rest by name).
        void BuildLanguagePicker(float left, float right, float top)
        {
            _group = _pickerTab = new Group();
            const int cols = 6, rows = PickerSlots / cols;
            const float gap = 0.006f;
            var cw = (right - left - gap * (cols - 1)) / cols;
            var y0 = top - 0.07f;
            var ch = Mathf.Min(0.04f, (y0 - (-H / 2 + 0.03f) - gap * (rows - 1)) / rows);
            for (var i = 0; i < PickerSlots; i++)
            {
                var at = new Vector2(left + (cw + gap) * (i % cols) + cw / 2, y0 - (ch + gap) * (i / cols) - ch / 2);
                var g = AddGlass(_root, at, new Vector2(cw, ch), ch / 2, CardTint, 1, bevel: 0.003f);
                var l = AddLabel(_root, "", at, 0.0125f, Primary, TextAlignmentOptions.Midline, cw - 0.006f, layer: 2);
                var idx = i;
                var c = AddHit(_root, at, new Vector2(cw + gap, ch + gap), () => PickLanguage(idx), g);
                _pickSlots[i] = (g, l, c);
            }
            _group = null;
        }

        void OpenPicker(string which)
        {
            _picking = which;
            _menu.Swap = 0f;
            ShowTab(Tab.Topic);
        }

        void RefreshPicker()
        {
            var spoken = _picking == "spoken";
            var codes = PickerCodes(spoken);
            var chosen = spoken ? director.speechLanguage : director.MyLanguage;
            for (var i = 0; i < PickerSlots; i++)
            {
                var (g, l, c) = _pickSlots[i];
                var used = i < codes.Count;
                g.Hidden = l.Hidden = !used;
                c.enabled = used;
                if (!used) continue;
                var on = codes[i] == chosen;
                l.T.text = codes[i] == "auto" ? "Auto" : CaptionTranslator.Name(codes[i]);
                l.C = on ? Primary : Secondary;
                g.Tint = on ? SegmentOn : CardTint;
            }
        }

        void PickLanguage(int idx)
        {
            var spoken = _picking == "spoken";
            var codes = PickerCodes(spoken);
            if (_picking == null || idx >= codes.Count) return;
            if (spoken) director.SetLanguage(codes[idx]);
            else
            {
                director.SetMyLanguage(codes[idx]);
                if (_pickForQuick && !director.translate) director.SetTranslate(true); // from "Translate into": translate
            }
            UiFeedback.Play(UiSound.Confirm, _root.position);
            _picking = null;
            _menu.Swap = 0f;
            if (_pickForQuick) { _pickForQuick = false; Close(); return; } // from the conversation menu: back to it
            ShowTab(Tab.Topic);
        }

        void UpdateLanguageLabels()
        {
            _languageText.T.text = LanguageText();
            _spokenValue.T.text = director.speechLanguage == "auto"
                ? $"Auto · {CaptionTranslator.Name(director.LiveLanguage)} now"
                : CaptionTranslator.Name(director.speechLanguage);
            _mineValue.T.text = CaptionTranslator.Name(director.MyLanguage);
            _translateDetail.T.text = TranslateDetail();
        }

        // Under "Translate": what it does now, in the few words the half row has room for.
        string TranslateDetail()
        {
            if (!director.translate) return "Off: as spoken";
            var st = director.TranslateStatus;
            if (st == null) return $"Into {CaptionTranslator.Name(director.MyLanguage)}";
            var pct = st.LastIndexOf('…');
            return st.StartsWith("Getting") && pct >= 0 ? "Getting model" + st[pct..] : "Couldn't translate";
        }

        string LanguageText()
        {
            var live = CaptionTranslator.Name(director.LiveLanguage);
            var spoken = director.speechLanguage switch
            {
                "auto" => $"Auto starts in English and follows whichever of 34 languages it hears. Captions are in {live} now.",
                "en" => "English only: the most accurate for English, never switches.",
                var c => $"Captions in {CaptionTranslator.Name(c)}. Auto follows whatever is spoken.",
            };
            return spoken + (director.translate
                ? $" Translate shows other languages in {CaptionTranslator.Name(director.MyLanguage)}."
                : " Translate shows them in your language.");
        }

        Toggle AddToggle(Vector2 at) => AddToggle(_root, at);

        Toggle AddToggle(Transform parent, Vector2 at)
        {
            var t = new Toggle { At = at };
            // The kit's switch: a 64 × 28 pt capsule, a 38 × 24 pt white capsule knob; off is a fill, on is green.
            t.Track = AddGlass(parent, at, new Vector2(ToggleW, ToggleH), ToggleH / 2, FillPrimary, 2, bevel: 0.002f, round: true);
            t.Knob = AddGlass(parent, at, new Vector2(KnobW, KnobH), KnobH / 2, Hex(0xFFFFFF), 3, bevel: 0.002f, round: true);
            t.Knob.Dim = 0f;
            return t;
        }

        static void SetToggle(Toggle t, bool on, Vector2? at = null, Color? onColor = null)
        {
            if (at.HasValue) { t.At = at.Value; At(t.Track, t.At); }
            t.Track.Tint = on ? onColor ?? SysGreen : FillPrimary;
            t.Knob.Glide = true;
            const float travel = (ToggleW - KnobW) / 2 - 2 * Pt;
            t.Knob.To = t.At + new Vector2(on ? travel : -travel, 0);
            if (at.HasValue) At(t.Knob, t.Knob.To); // laid out somewhere new: no slide from the old place
        }

        static void At(Transform t, Vector2 p) => t.localPosition = new Vector3(p.x, p.y, t.localPosition.z);
        static void At(Glass g, Vector2 p) => At(g.R.transform, p);
        static void At(Label l, Vector2 p) => At(l.T.transform, p);

        static void SetGroup(Group g, bool on)
        {
            foreach (var x in g.Glass) x.Hidden = !on;
            foreach (var x in g.Labels) x.Hidden = !on;
            foreach (var x in g.Hits) x.enabled = on;
        }

        // ---------------- tabs ----------------

        void ShowTab(Tab tab)
        {
            if (tab != Tab.Mic) _note = null;
            if (tab != Tab.Topic) { _picking = null; _pickForQuick = false; }
            if (tab != _tab) _menu.Swap = 0f; // the new tab's text fades in
            _tab = tab;
            _gazeHold = true;
            _title.T.text = tab switch { Tab.Mic => "Microphone", Tab.Topic => _picking == "spoken" ? "Spoken language" : _picking == "mine" ? "My language" : "Settings", Tab.History => "Conversations", _ => AppName };
            At(_title, _titleAt);
            SetGroup(_modesTab, tab == Tab.Modes);
            SetGroup(_micTab, tab == Tab.Mic && _note == null);
            SetGroup(_noteTab, tab == Tab.Mic && _note != null);
            SetGroup(_topicTab, tab == Tab.Topic && _picking == null);
            SetGroup(_pickerTab, tab == Tab.Topic && _picking != null);
            SetGroup(_historyList, false);
            SetGroup(_historyDetail, false);
            for (var i = 0; i < _tabButtons.Length; i++) _tabButtons[i].Tint = i == (int)tab ? Circle : Borderless;
            if (tab == Tab.Mic) { if (_note == null) { RefreshMic(); UpdateMicTab(); } else ShowNote(); }
            if (tab == Tab.Topic) { if (_picking != null) RefreshPicker(); else RefreshTopic(); }
            if (tab == Tab.History) RefreshHistory(reload: true);
        }

        /// <summary>Open the start screen on the microphone tab (M, or a newly plugged-in mic).</summary>
        void OpenMic(Note note = null)
        {
            if (!_menu.Open) Open();
            _note = note;
            ShowTab(Tab.Mic);
        }

        void RefreshMic()
        {
            var options = micSettings.Options;
            float left = -W / 2 + Pad, top = H / 2;
            var colW = (W - 2 * Pad) * 0.6f;
            var n = Mathf.Min(options.Count, MaxMicRows);
            const float gap = 0.008f;
            var h = Mathf.Min(0.05f, (0.244f - gap * (n - 1)) / Mathf.Max(n, 1));
            var y = top - 0.07f;
            for (var i = 0; i < MaxMicRows; i++)
            {
                var r = _micRows[i];
                var on = i < n;
                r.Bg.Hidden = r.Symbol.Hidden = r.Icon.Hidden = r.Title.Hidden = r.Detail.Hidden = r.Switch.Track.Hidden = r.Switch.Knob.Hidden = !on;
                r.C.enabled = on;
                r.Option = on ? options[i] : null;
                if (!on) continue;
                var o = options[i];
                var c = new Vector2(left + colW / 2, y - h / 2);
                var active = o == micSettings.Current;
                r.Bg.Size = new Vector2(colW, h);
                r.Bg.Tint = active ? CardActive : CardTint;
                At(r.Bg, c);
                r.C.size = new Vector3(colW, h, 0.01f);
                At(r.C.transform, c);
                var sx = left + 0.008f + 0.017f;
                At(r.Symbol, new Vector2(sx, c.y));
                r.Icon.T.text = o.Glyph;
                At(r.Icon, new Vector2(sx, c.y));
                At(r.Title, new Vector2(sx + 0.028f, c.y + 0.0075f));
                At(r.Detail, new Vector2(sx + 0.028f, c.y - 0.009f));
                r.Title.T.text = o.Title;
                r.Detail.T.text = o.Detail;
                SetToggle(r.Switch, active, new Vector2(left + colW - 0.012f - ToggleW / 2, c.y));
                y -= h + gap;
            }
        }

        void RefreshTopic()
        {
            UpdateLanguageLabels();
            SetToggle(_debugToggle, director.DebugView);
            if (_rememberToggle != null) SetToggle(_rememberToggle, tracker != null && tracker.rememberPeople);
            if (_soundsToggle != null) SetToggle(_soundsToggle, PlayerPrefs.GetInt("captions.sounds", 1) == 1);
            SetToggle(_translateToggle, director.translate);
            _speedChip.T.text = SignAvatar.UserSpeed.ToString("0.##") + "×";
            if (_signerChip != null) _signerChip.T.text = signAvatar.SignerName;
        }

        void ShowNote()
        {
            _noteBody.T.text = _note.Text;
            var buttons = _note.Buttons;
            const float gap = 0.012f, h = LargeBtn, w = 0.13f; // the kit's Large button: a 50 pt capsule
            var right = W / 2 - Pad;
            var y = -H / 2 + Pad + h / 2;
            for (var i = 0; i < _noteSlots.Length; i++)
            {
                var s = _noteSlots[i];
                var on = i < buttons.Length;
                s.G.Hidden = s.L.Hidden = !on;
                s.C.enabled = on;
                s.Act = on ? buttons[i].act : null;
                if (!on) continue;
                var pos = new Vector2(right - w / 2 - (buttons.Length - 1 - i) * (w + gap), y);
                s.G.Size = new Vector2(w, h);
                s.G.Tint = buttons[i].primary ? Accent : Button;
                At(s.G, pos);
                At(s.L, pos);
                s.L.T.text = buttons[i].text;
                s.L.T.rectTransform.sizeDelta = new Vector2(w, s.L.T.rectTransform.sizeDelta.y);
                At(s.C.transform, pos);
                s.C.size = new Vector3(w + 0.006f, h + 0.008f, 0.01f);
            }
        }

        void AddMicNote() => OpenMic(new Note
        {
            Text = "<b>Add a microphone</b>\n" +
                   "1. Plug a USB-C mic or a wireless clip-on receiver into the headset.\n" +
                   "2. Quest Settings › Experimental › turn on <b>External microphone</b>.\n" +
                   "3. It shows up in the list within a few seconds.\n" +
                   "Clip the transmitter on the person you talk with: their words come through much clearer.",
            Buttons = new (string, System.Action, bool)[] { ("Done", () => OpenMic(), true) },
        });

        void OfferMic(MicSettings.Option o)
        {
            if (WelcomeTakesMic(o)) return;
            OpenMic(OfferNote(o));
        }

        Note OfferNote(MicSettings.Option o) => new Note
        {
            Text = $"<b>New microphone</b>\n{o.Title} was plugged in. Use it for captions?",
            Buttons = new (string, System.Action, bool)[] { ("Not now", () => OpenMic(), false), ("Use it", () => PickMic(o), true) },
        };

        void TellMic(string text)
        {
            if (_onboarding && _setup.Open) { Debug.Log($"[Captions] Mic: {text}"); WelcomeTakesMic(null); return; }
            OpenMic(TellNote(text));
        }

        Note TellNote(string text) => new Note
        {
            Text = $"<b>Microphone</b>\n{text}",
            Buttons = new (string, System.Action, bool)[] { ("OK", () => OpenMic(), true) },
        };

        void PickMic(MicSettings.Option o)
        {
            if (!micSettings.Select(o)) return; // (it says why, through TellMic)
            _note = null;
            ShowTab(Tab.Mic);
        }

        void MicsChanged()
        {
            if (_menu.Open && _tab == Tab.Mic && _note == null) RefreshMic();
        }

        // Per frame on the microphone tab: the level meter and status line.
        void UpdateMicTab()
        {
            var db = micSettings.LevelDb;
            var fill = Mathf.InverseLerp(-70f, -10f, db);
            var w = Mathf.Lerp(0.008f, _meterW, fill);
            _meterFill.Size = new Vector2(w, 0.008f);
            At(_meterFill, new Vector2(_meterX0 + w / 2, _meterY));
            _levelText.T.text = db > -100f ? $"{db:F0} <size=55%>dB</size>" : "– <size=55%>dB</size>";
            _status.T.text = micSettings.StatusLine;
        }
    }
}
