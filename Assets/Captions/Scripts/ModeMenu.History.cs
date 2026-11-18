using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Saved conversations in the menus.
    ///   The conversation menu (ModeMenu.Conversation.cs) has Save (nothing is kept until it's on, see
    ///   <see cref="ConversationLog"/>) and Past conversations.
    ///   The Conversations tab of the start screen: the saved conversations, newest first, four to a page. One opens to
    ///   its summary (made on the headset, <see cref="ConversationSummarizer"/>) or its transcript, a page at a time,
    ///   with Summarise and Delete (pressed twice). A day with two or more conversations also gets a row for the whole
    ///   day: one summary made from that day's conversation summaries (with their to-dos merged).
    ///   Developer builds only (launched with -e onlinesum 1, see <see cref="OnlineSummary"/>): at the top right,
    ///   On headset | Online. Online sends a conversation's text off the headset to be summarised; turning it on takes
    ///   two taps, and the line beside it says that the text leaves the headset. Otherwise it isn't shown at all.
    /// </summary>
    public partial class ModeMenu
    {
        const int HistoryRows = 4;
        const float BodyH = 0.172f; // a page of summary or transcript (m): clear of the buttons under it
        static readonly Color Recording = Hex(0xFF4245); // the kit's red (dark)

        ConversationLog _log;
        ConversationSummarizer _summarizer;

        // The conversation menu (built in ModeMenu.Conversation.cs).
        Panel _quick;
        bool _pickForQuick; // the language list was opened from here: picking goes straight back to the conversation

        // The Conversations tab.
        sealed class HistoryRow { public Glass Bg; public Label Title, Detail; public BoxCollider C; public string Id; }
        Group _historyList, _historyDetail;
        readonly HistoryRow[] _historyRows = new HistoryRow[HistoryRows];
        Label _historyEmpty, _historyBody, _detailStatus, _pageText;
        Slot _listUp, _listDown, _pageUp, _pageDown, _back, _summarise, _delete;
        (Glass g, Label l) _segSummary, _segTranscript, _segLocal, _segOnline;
        Label _onlineNote;
        float _onlineArmed = -1f;
        string _onlineMsg;
        float _onlineMsgUntil;
        List<string> _historyIds = new();
        int _historyFirst, _page = 1;
        string _openId;
        ConversationLog.Saved _open;
        bool _transcript;
        float _deleteArmed = -1f, _historyTick;
        string _shownStatus;
        Vector2 _titleAt;

        /// <summary>The log and the summariser (Play mode only: not in the Editor preview).</summary>
        void StartConversationLog()
        {
            _log = gameObject.AddComponent<ConversationLog>();
            _log.director = director;
            _summarizer = gameObject.AddComponent<ConversationSummarizer>();
            _summarizer.log = _log;
            _summarizer.InMainMenu = () => _menu.Open;
            _log.enabled = false; _log.enabled = true;               // (OnEnable ran before the references were set)
            _summarizer.enabled = false; _summarizer.enabled = true;
            _summarizer.Done += _ => { if (_menu.Open && _tab == Tab.History) RefreshHistory(reload: true); };
            if (director.live) { _log.BeginConversation(); _summarisedSaid = 0; _conversationStart = Time.realtimeSinceStartup; } // no start screen: it starts now
        }

        // ---------------- building ----------------

        // A row button: a symbol (on a coloured rounded tile, as in iOS Settings, or a plain round one), a title with a
        // line under it, and a chevron — or, for an on/off row, a switch.
        void QuickRow(Transform root, float left, float right, float y, float h, string glyph, string title, string detail,
            System.Action act, out Glass bg, out Label titleLabel, out Label detailLabel, Color? tile = null, Color? ink = null,
            System.Action<Toggle> switchOut = null)
        {
            var w = right - left;
            bg = AddGlass(root, new Vector2((left + right) / 2, y), new Vector2(w, h), h / 2, CardTint, 1, bevel: 0.004f);
            var sx = left + 0.021f;
            if (tile.HasValue) AddGlass(root, new Vector2(sx, y), Vector2.one * 0.032f, 0.0085f, tile.Value, 2, bevel: 0.002f).Dim = 0f;
            else AddGlass(root, new Vector2(sx, y), Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
            if (glyph != null) AddIcon(root, glyph, new Vector2(sx, y), 0.018f, ink ?? Primary, 3);
            var textW = w - (switchOut != null ? 0.11f : 0.09f);
            titleLabel = AddLabel(root, title, new Vector2(sx + 0.028f, y + 0.0075f), 0.0145f, Primary, TextAlignmentOptions.MidlineLeft, textW, layer: 3);
            detailLabel = AddLabel(root, detail, new Vector2(sx + 0.028f, y - 0.009f), 0.011f, Secondary, TextAlignmentOptions.MidlineLeft, textW, layer: 3);
            if (switchOut != null) switchOut(AddToggle(root, new Vector2(right - 0.004f - ToggleW / 2, y)));
            else AddLabel(root, "›", new Vector2(right - 0.018f, y), 0.022f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
            AddHit(root, new Vector2((left + right) / 2, y), new Vector2(w, h), act, bg);
        }

        void BuildHistoryTab(float left, float right, float top)
        {
            // The list: four rows, with page arrows down the right when there are more.
            _group = _historyList = new Group();
            var rowW = right - left - 0.05f;
            const float h = 0.05f, gap = 0.008f;
            var y = top - 0.07f - h / 2;
            for (var i = 0; i < HistoryRows; i++)
            {
                var row = _historyRows[i] = new HistoryRow();
                var c = new Vector2(left + rowW / 2, y);
                row.Bg = AddGlass(_root, c, new Vector2(rowW, h), h / 2, CardTint, 1, bevel: 0.004f);
                var sx = left + 0.008f + 0.017f;
                AddGlass(_root, new Vector2(sx, y), Vector2.one * CloseBtn, CloseBtn / 2, Circle, 2, bevel: 0.003f, round: true);
                AddIcon(_root, Glyphs.Chat, new Vector2(sx, y), 0.017f, Primary, 3);
                row.Title = AddLabel(_root, "", new Vector2(sx + 0.028f, y + 0.0075f), 0.0145f, Primary, TextAlignmentOptions.MidlineLeft, rowW - 0.08f, layer: 3);
                row.Detail = AddLabel(_root, "", new Vector2(sx + 0.028f, y - 0.009f), 0.011f, Secondary, TextAlignmentOptions.MidlineLeft, rowW - 0.08f, layer: 3);
                AddLabel(_root, "›", new Vector2(left + rowW - 0.018f, y), 0.022f, Secondary, TextAlignmentOptions.Midline, 0.03f, layer: 3);
                row.C = (BoxCollider)AddHit(_root, c, new Vector2(rowW, h), () => { if (row.Id != null) OpenConversation(row.Id); }, row.Bg);
                y -= h + gap;
            }
            var ax = right - 0.018f;
            _listUp = AddRoundButton(new Vector2(ax, top - 0.07f - 0.02f), LargeBtn, Glyphs.ChevronLeft, -90f, () => PageList(-1));
            _listDown = AddRoundButton(new Vector2(ax, top - 0.07f - 4 * h - 3 * gap + 0.02f), LargeBtn, Glyphs.ChevronLeft, 90f, () => PageList(1));
            _historyEmpty = AddLabel(_root, "No saved conversations yet.\n\nDuring a conversation, pinch and hold either hand (or look at the " +
                                            "button at the bottom right) and choose <b>Save conversation</b>. Only the text is kept, on this headset. " +
                                            "When the conversation ends, the headset writes a short summary of it.",
                new Vector2(left, top - 0.07f), 0.015f, Primary, TextAlignmentOptions.TopLeft, right - left - 0.06f);
            _historyEmpty.T.lineSpacing = -4f;

            // Where summaries are made: on the headset (always works) or online (developer option only).
            if (OnlineSummary.DevUnlocked)
            {
                const float ow = 0.1f, oh = MediumBtn;
                var oy = top - 0.04f;
                var ox0 = right - 2 * ow;
                AddGlass(_root, new Vector2(ox0 + ow, oy), new Vector2(2 * ow + 0.006f, oh + 0.006f), (oh + 0.006f) / 2, Track, 1, bevel: 0.003f, round: true);
                _segLocal = Segment(new Vector2(ox0 + ow / 2, oy), ow, oh, "On headset", () => SetOnlineSummaries(false));
                _segOnline = Segment(new Vector2(ox0 + 1.5f * ow, oy), ow, oh, "Online", () => SetOnlineSummaries(true));
                const float nw = 0.26f;
                _onlineNote = AddLabel(_root, "", new Vector2(ox0 - 0.01f - nw / 2, oy), 0.0112f, Secondary, TextAlignmentOptions.MidlineRight, nw);
            }

            // One conversation: Summary | Transcript, a page of text with page arrows, then status, Delete and Summarise.
            _group = _historyDetail = new Group();
            _back = AddRoundButton(new Vector2(left + 0.017f, top - 0.04f), CloseBtn, Glyphs.ChevronLeft, 0f, CloseConversation);
            const float sh = MediumBtn, sw = 0.09f;
            var sy = top - 0.04f;
            var sx0 = right - 2 * sw;
            AddGlass(_root, new Vector2(sx0 + sw, sy), new Vector2(2 * sw + 0.006f, sh + 0.006f), (sh + 0.006f) / 2, Track, 1, bevel: 0.003f, round: true);
            _segSummary = Segment(new Vector2(sx0 + sw / 2, sy), sw, sh, "Summary", () => ShowTranscript(false));
            _segTranscript = Segment(new Vector2(sx0 + 1.5f * sw, sy), sw, sh, "Transcript", () => ShowTranscript(true));
            var bodyW = right - left - 0.05f;
            _historyBody = AddLabel(_root, "", new Vector2(left, top - 0.07f), 0.0145f, Primary, TextAlignmentOptions.TopLeft, bodyW);
            _historyBody.T.rectTransform.sizeDelta = new Vector2(bodyW, BodyH);
            _historyBody.T.overflowMode = TextOverflowModes.Page;
            _historyBody.T.lineSpacing = -2f;
            _pageUp = AddRoundButton(new Vector2(ax, top - 0.07f - 0.02f), LargeBtn, Glyphs.ChevronLeft, -90f, () => Page(-1));
            _pageDown = AddRoundButton(new Vector2(ax, top - 0.07f - BodyH + 0.02f), LargeBtn, Glyphs.ChevronLeft, 90f, () => Page(1));
            _pageText = AddLabel(_root, "", new Vector2(ax, top - 0.07f - BodyH / 2), 0.011f, Secondary, TextAlignmentOptions.Midline, 0.05f);
            const float bh = LargeBtn, bw = 0.12f;
            var by = -H / 2 + Pad + bh / 2 - 0.004f;
            _detailStatus = AddLabel(_root, "", new Vector2(left + 0.004f, by), 0.0115f, Secondary, TextAlignmentOptions.MidlineLeft, right - left - 2 * bw - 0.03f);
            _delete = AddTextButton(new Vector2(right - bw * 1.5f - 0.01f, by), new Vector2(bw, bh), "Delete", Button, DeletePressed);
            _summarise = AddTextButton(new Vector2(right - bw / 2, by), new Vector2(bw, bh), "Summarise", Accent, SummarisePressed);
            _group = null;
        }

        (Glass, Label) Segment(Vector2 at, float w, float h, string text, System.Action act)
        {
            var g = AddGlass(_root, at, new Vector2(w - 0.004f, h), h / 2, Borderless, 2, bevel: 0.002f, round: true);
            var l = AddLabel(_root, text, at, 0.0125f, Primary, TextAlignmentOptions.Midline, w, layer: 3);
            AddHit(_root, at, new Vector2(w, h + 0.006f), act, g);
            return (g, l);
        }

        Slot AddRoundButton(Vector2 at, float d, string glyph, float turn, System.Action act)
        {
            var s = new Slot { Act = act };
            s.G = AddGlass(_root, at, Vector2.one * d, d / 2, Circle, 2, bevel: 0.003f, round: true);
            s.L = AddIcon(_root, glyph, at, d * 0.55f, Primary, 3);
            s.L.T.transform.localRotation = Quaternion.Euler(0, 0, turn);
            s.C = (BoxCollider)AddHit(_root, at, Vector2.one * (d + 0.008f), () => s.Act?.Invoke(), s.G);
            return s;
        }

        Slot AddTextButton(Vector2 at, Vector2 size, string text, Color tint, System.Action act)
        {
            var s = new Slot { Act = act };
            s.G = AddGlass(_root, at, size, size.y / 2, tint, 2, round: true);
            s.L = AddLabel(_root, text, at, 0.0145f, Primary, TextAlignmentOptions.Midline, size.x, layer: 3);
            s.C = (BoxCollider)AddHit(_root, at, size + new Vector2(0.006f, 0.008f), () => s.Act?.Invoke(), s.G);
            return s;
        }

        static void Show(Slot s, bool on)
        {
            s.G.Hidden = s.L.Hidden = !on;
            s.C.enabled = on;
        }

        // ---------------- the conversation menu ----------------

        void OpenQuick()
        {
            _menu.Open = false;
            _setup.Open = false;
            _closeAt = -1f;
            Place(_quick, FrontOf(_quick));
            _quick.Returning = false;
            _quick.Root.gameObject.SetActive(true);
            _quick.Open = true;
            _dock.Open = false;
            _gazeHold = true;
            ResetQuick();
            UpdateQuick();
        }

        /// <summary>Editor preview: the conversation menu, open at once in front of <paramref name="viewer"/>.</summary>
        /// <param name="state">0: as it opens; 1: the language sheet open; 2: the dock (the round mode button) instead.</param>
        public void OpenQuickForPreview(Transform viewer, int state = 0)
        {
            if (state == 2)
            {
                director.live = true;
                ShowDock();
                var at = viewer.position + viewer.forward * 0.3f;
                _dock.Root.SetPositionAndRotation(at, Quaternion.LookRotation(at - viewer.position, Vector3.up));
                _dockWake = 1f;
                _dock.Fade = _dock.Swap = 1f;
                ApplyPanel(_dock);
                return;
            }
            var pos = viewer.position + viewer.forward * _quick.Dist;
            _quick.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - viewer.position, Vector3.up));
            _quick.Root.gameObject.SetActive(true);
            ResetQuick();
            if (state == 1) { _langWanted = true; UpdateQuick(); _langOpen = 1f; SetGroup(_langSheet, true); }
            UpdateQuick();
            _quick.Fade = _quick.Swap = 1f;
            ApplyPanel(_quick);
        }

        void CloseQuick()
        {
            _quick.Open = false;
            UiFeedback.Play(UiSound.Back, _quick.Root.position);
            ShowDock();
        }

        void ToggleSave()
        {
            if (_log == null || !_log.Running) return;
            _log.SetSaving(!_log.Saving);
            PlayerPrefs.SetInt(ConversationLog.SavePref, _log.Saving ? 1 : 0);
            UiFeedback.Play(_log.Saving ? UiSound.Confirm : UiSound.Back, _quick.Root.position);
            UpdateQuick();
        }

        void ToggleTranslate()
        {
            director.SetTranslate(!director.translate);
            UiFeedback.Play(director.translate ? UiSound.Confirm : UiSound.Back, _quick.Root.position);
            UpdateQuick();
        }

        // ---------------- "Hearing Hindi: use Hindi?" ----------------

        Panel _ask;
        Label _askTitle, _askUseTitle, _askUseDetail, _askKeepDetail;
        string _askLang;
        float _askUntil;
        const float AskW = 0.3f, AskH = 0.19f, AskSeconds = 15f;

        void BuildAsk()
        {
            var root = new GameObject("Language question").transform;
            root.SetParent(transform, false);
            _building = _ask = new Panel { Root = root, Dist = 0.62f, Drop = 0.16f, Yaw = 18f }; // low and to the right: not over faces
            _group = null;
            AddGlass(root, Vector2.zero, new Vector2(AskW, AskH), 0.04f, PanelTint, 0, bevel: 0.007f).Dim = PanelDim;
            AddBacking(root, new Vector2(AskW, AskH));
            float left = -AskW / 2 + 0.018f, right = AskW / 2 - 0.018f, top = AskH / 2;
            _askTitle = AddLabel(root, "", new Vector2(left + 0.004f, top - 0.03f), 0.017f, Primary, TextAlignmentOptions.MidlineLeft, right - left);
            const float h = 0.048f, gap = 0.008f;
            var y = top - 0.058f - h / 2;
            QuickRow(root, left, right, y, h, Glyphs.Check, "", "Faster and more accurate than Auto", UseAskedLanguage, out _, out _askUseTitle, out _askUseDetail);
            y -= h + gap;
            QuickRow(root, left, right, y, h, Glyphs.Close, "Keep Auto", "", KeepAutoLanguage, out _, out _, out _askKeepDetail);
            director.SuggestLanguage += AskLanguage;
            root.gameObject.SetActive(false);
        }

        void AskLanguage(string lang)
        {
            if (_menu.Open || _setup.Open || _quick.Open || !director.live) return; // busy: not now (asked again next run)
            _askLang = lang;
            var name = CaptionTranslator.Name(lang);
            _askTitle.T.text = $"Hearing {name}. Caption it as {name}?";
            _askUseTitle.T.text = $"Use {name}";
            _askKeepDetail.T.text = $"Don't ask again for {name}";
            Place(_ask, FrontOf(_ask));
            _ask.Returning = false;
            _ask.Root.gameObject.SetActive(true);
            _ask.Open = true;
            _ask.Swap = 0f;
            _askUntil = Time.time + AskSeconds;
            UiFeedback.Play(UiSound.Confirm, _ask.Root.position);
        }

        void UseAskedLanguage()
        {
            if (_askLang == null) return;
            director.SetLanguage(_askLang);
            _ask.Open = false;
            UiFeedback.Play(UiSound.Confirm, _ask.Root.position);
            ShowDock();
        }

        void KeepAutoLanguage()
        {
            if (_askLang != null) director.KeepAuto(_askLang);
            _ask.Open = false;
            UiFeedback.Play(UiSound.Back, _ask.Root.position);
            ShowDock();
        }

        void PickLanguageFromQuick(string which)
        {
            Open();
            ShowTab(Tab.Topic);
            OpenPicker(which);
            _pickForQuick = true;
        }

        void PastConversations()
        {
            _quick.Open = false;
            Open();
            ShowTab(Tab.History);
        }

        void BackToMenu()
        {
            _quick.Open = false;
            Open();
        }

        float _conversationStart;

        // ---------------- the Conversations tab ----------------

        void RefreshHistory(bool reload = false)
        {
            if (reload || _historyIds == null) _historyIds = ConversationLog.ListWithDays();
            if (_openId != null && !_historyIds.Contains(_openId)) _openId = null;
            var detail = _openId != null;
            SetGroup(_historyList, !detail);
            SetGroup(_historyDetail, detail);
            float left = -W / 2 + Pad, top = H / 2;
            At(_title, detail ? new Vector2(left + 0.04f, top - 0.04f) : _titleAt);
            if (detail) { RefreshDetail(reload); return; }

            _title.T.text = "Conversations";
            _historyFirst = Mathf.Clamp(_historyFirst, 0, Mathf.Max(0, (_historyIds.Count - 1) / HistoryRows * HistoryRows));
            for (var i = 0; i < HistoryRows; i++)
            {
                var r = _historyRows[i];
                var k = _historyFirst + i;
                var on = k < _historyIds.Count;
                r.Id = on ? _historyIds[k] : null;
                r.Bg.Hidden = r.Title.Hidden = r.Detail.Hidden = !on;
                r.C.enabled = on;
                // (the row's symbol and chevron stay in the group; hide them by fading the whole row's labels/glass)
                if (!on) continue;
                var s = ConversationLog.Load(r.Id);
                r.Title.T.text = s.IsDay ? $"{Day(s.Start)}: the whole day" : When(s);
                r.Detail.T.text = RowDetail(s);
                _historyList.Labels[i * 4].T.text = s.IsDay ? Glyphs.Schedule : Glyphs.Chat;
            }
            HideEmptyRowParts();
            RefreshOnline();
            _historyEmpty.Hidden = _historyIds.Count > 0;
            Show(_listUp, _historyFirst > 0);
            Show(_listDown, _historyFirst + HistoryRows < _historyIds.Count);
        }

        // Rows without a conversation: their symbol circle, icon and chevron too (built in the same order as the rows).
        void HideEmptyRowParts()
        {
            var glass = _historyList.Glass;
            var labels = _historyList.Labels;
            for (var i = 0; i < HistoryRows; i++)
            {
                var on = _historyRows[i].Id != null;
                glass[i * 2 + 1].Hidden = !on;          // symbol circle (after the row's background)
                labels[i * 4 + 0].Hidden = !on;         // icon
                labels[i * 4 + 3].Hidden = !on;         // chevron
            }
        }

        string RowDetail(ConversationLog.Saved s)
        {
            var parts = new List<string> { s.IsDay ? $"{s.Parts.Count} conversations" : Length(s.Seconds) };
            if (s.People.Count > 0) parts.Add(string.Join(", ", s.People.Count > 3 ? s.People.GetRange(0, 3) : s.People) + (s.People.Count > 3 ? "…" : ""));
            var status = _summarizer != null ? _summarizer.StatusOf(s.Id) : null;
            parts.Add(status != null && _summarizer.Busy(s.Id) ? status
                : s.Summary != null ? (s.Stale ? "Summary out of date" : "Summary ready")
                : !s.IsDay && s.Lines.Count == 0 ? "Nothing said" : "No summary yet");
            return string.Join("  ·  ", parts);
        }

        static string When(ConversationLog.Saved s) => s.IsDay ? Day(s.Start) : $"{Day(s.Start)}, {s.Start:HH:mm}";

        static string Day(System.DateTime d) =>
            d.Date == System.DateTime.Today ? "Today" : d.Date == System.DateTime.Today.AddDays(-1) ? "Yesterday"
                : d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

        // ---------------- on headset | online ----------------

        void SetOnlineSummaries(bool on)
        {
            _gazeHold = true;
            if (!on)
            {
                if (OnlineSummary.Enabled) UiFeedback.Play(UiSound.Back, _segLocal.g.R.transform.position);
                OnlineSummary.Enabled = false;
                _onlineArmed = -1f;
                RefreshOnline();
                return;
            }
            if (OnlineSummary.Enabled) return;
            if (!OnlineSummary.CanGoOnline)
            {
                Tell("Online needs the summary server or an API key (see the README)");
                return;
            }
            if (_onlineArmed < 0f || Time.time - _onlineArmed > 4f)
            {
                // First tap: say what it means. The second turns it on.
                _onlineArmed = Time.time;
                Tell($"Tap Online again: conversation text will be sent to {OnlineSummary.Provider}", 4f);
                return;
            }
            OnlineSummary.Enabled = true;
            _onlineArmed = -1f;
            _onlineMsg = null;
            UiFeedback.Play(UiSound.Confirm, _segOnline.g.R.transform.position);
            RefreshOnline();
        }

        void Tell(string text, float seconds = 5f)
        {
            _onlineMsg = text;
            _onlineMsgUntil = Time.time + seconds;
            RefreshOnline();
        }

        void RefreshOnline()
        {
            if (_onlineNote == null) return;
            var on = OnlineSummary.Enabled;
            _segLocal.g.Tint = on ? Borderless : SegmentOn;
            _segOnline.g.Tint = on ? SegmentOn : Borderless;
            _segLocal.l.C = on ? Secondary : Primary;
            _segOnline.l.C = on ? Primary : Secondary;
            if (_onlineMsg != null && Time.time > _onlineMsgUntil) { _onlineMsg = null; _onlineArmed = -1f; }
            _onlineNote.T.text = _onlineMsg ?? (!on ? ""
                : !OnlineSummary.CanGoOnline ? "Online: no server or API key, so on the headset"
                : Application.internetReachability == NetworkReachability.NotReachable ? "Offline: summaries made on the headset"
                : $"Text is sent to {OnlineSummary.Provider} to summarise");
        }

        static string Length(double seconds) => seconds < 60 ? "under a minute" : $"{System.Math.Round(seconds / 60)} min";

        void PageList(int dir)
        {
            _historyFirst += dir * HistoryRows;
            _gazeHold = true;
            RefreshHistory();
        }

        void OpenConversation(string id)
        {
            _openId = id;
            _open = null;
            _deleteArmed = -1f;
            _menu.Swap = 0f;
            _gazeHold = true;
            RefreshHistory();
        }

        void CloseConversation()
        {
            _openId = null;
            _open = null;
            _menu.Swap = 0f;
            _gazeHold = true;
            RefreshHistory(reload: true);
        }

        void ShowTranscript(bool on)
        {
            if (_transcript == on) return;
            _transcript = on;
            _page = 1;
            _menu.Swap = 0f;
            RefreshDetail(false);
        }

        void RefreshDetail(bool reload)
        {
            if (_open == null || reload || _open.Id != _openId)
            {
                var first = _open == null || _open.Id != _openId;
                _open = ConversationLog.Load(_openId);
                if (first) { _transcript = _open.Summary == null; _page = 1; }
            }
            _title.T.text = _open.IsDay ? $"{When(_open)}  <size=70%><color=#FFFFFF9E>{_open.Parts.Count} conversations</color></size>"
                : $"{When(_open)}  <size=70%><color=#FFFFFF9E>{Length(_open.Seconds)}</color></size>";
            _segTranscript.l.T.text = _open.IsDay ? "Each one" : "Transcript";
            Show(_delete, !_open.IsDay);
            _segSummary.g.Tint = _transcript ? Borderless : SegmentOn;
            _segTranscript.g.Tint = _transcript ? SegmentOn : Borderless;
            _segSummary.l.C = _transcript ? Secondary : Primary;
            _segTranscript.l.C = _transcript ? Primary : Secondary;
            _historyBody.T.text = _transcript ? (_open.IsDay ? DayParts(_open) : TranscriptText(_open)) : SummaryText(_open);
            _historyBody.T.ForceMeshUpdate();
            var pages = Mathf.Max(1, _historyBody.T.textInfo.pageCount);
            _page = Mathf.Clamp(_page, 1, pages);
            _historyBody.T.pageToDisplay = _page;
            Show(_pageUp, _page > 1);
            Show(_pageDown, _page < pages);
            _pageText.T.text = pages > 1 ? $"{_page}/{pages}" : "";
            _shownStatus = null;
            UpdateDetailStatus();
        }

        void Page(int dir)
        {
            _page += dir;
            _gazeHold = true;
            RefreshDetail(false);
        }

        string SummaryText(ConversationLog.Saved s)
        {
            if (s.Summary == null && s.IsDay)
                return "<color=#FFFFFF9E>No summary of the day yet. Choose <b>Summarise</b>: it's made from the summaries of this " +
                       "day's conversations (any not summarised yet are done first).</color>";
            if (s.Summary == null)
                return s.Lines.Count == 0 ? "Nothing was said while this conversation was saved."
                    : "<color=#FFFFFF9E>No summary yet. Choose <b>Summarise</b> and the headset writes one (this takes a minute or two; " +
                      "the first time it also downloads the summary model).</color>";
            var t = s.Summary.Replace("<", "&lt;");
            t = Regex.Replace(t, @"^(Summary|Key points|People|To do)\s*:\s*", m => $"<b>{m.Groups[1].Value}</b>\n", RegexOptions.Multiline);
            t = Regex.Replace(t, @"^\s*[-*•]\s+", "<color=#FFFFFF80>•</color> ", RegexOptions.Multiline);
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            var by = s.Online ? "Written online" : "Written by a small model on this headset";
            var stale = s.Stale ? "<color=#FFFFFF9E>A conversation was summarised after this: choose <b>Summarise again</b> to update it.</color>\n\n" : "";
            return stale + t + $"\n\n<size=80%><color=#FFFFFF6B>{by}: check the {(s.IsDay ? "conversations" : "transcript")} for anything important.</color></size>";
        }

        // A day's conversations, each with its summary (or a note that it has none yet).
        static string DayParts(ConversationLog.Saved day)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var id in day.Parts)
            {
                var c = ConversationLog.Load(id);
                sb.Append($"<b>{c.Start:HH:mm}</b>  <color=#FFFFFF9E>{Length(c.Seconds)}{(c.People.Count > 0 ? " · " + string.Join(", ", c.People) : "")}</color>\n");
                var text = c.Summary != null ? c.Summary.Replace("<", "&lt;").Trim() : "<color=#FFFFFF9E>Not summarised yet</color>";
                var cut = text.IndexOf("\nKey points", System.StringComparison.OrdinalIgnoreCase);
                if (cut > 0) text = text[..cut];
                sb.Append(Regex.Replace(text, @"^Summary\s*:\s*", "", RegexOptions.Multiline).Trim()).Append("\n\n");
            }
            return sb.ToString().TrimEnd();
        }

        static string TranscriptText(ConversationLog.Saved s)
        {
            if (s.Lines.Count == 0) return "Nothing was said while this conversation was saved.";
            var sb = new System.Text.StringBuilder();
            foreach (var l in s.Lines)
            {
                var color = l.you || string.IsNullOrEmpty(l.color) ? "#FFFFFFB3" : l.color;
                var time = $"{(int)(l.t / 60)}:{(int)(l.t % 60):00}";
                sb.Append($"<color=#FFFFFF59><size=80%>{time}</size></color>  <b><color={color}>{l.who}{(l.src == "sign" ? " (signed)" : "")}</color></b>  ")
                  .Append("<noparse>").Append(l.text).Append("</noparse>\n");
            }
            return sb.ToString();
        }

        void SummarisePressed()
        {
            if (_openId == null || _summarizer == null) return;
            _summarizer.Request(_openId);
            UiFeedback.Play(UiSound.Confirm, _summarise.G.R.transform.position);
            _shownStatus = null;
            UpdateDetailStatus();
        }

        void DeletePressed()
        {
            if (_openId == null) return;
            if (_deleteArmed < 0f || Time.time - _deleteArmed > 3f)
            {
                _deleteArmed = Time.time;
                _delete.L.T.text = "Tap again";
                _delete.G.Tint = Recording;
                return;
            }
            if (_summarizer != null && _summarizer.Busy(_openId)) { _detailStatus.T.text = "Wait for the summary to finish"; return; }
            ConversationLog.Delete(_openId);
            UiFeedback.Play(UiSound.Back, _delete.G.R.transform.position);
            CloseConversation();
        }

        // The status line, buttons and (once a summary lands) the text, kept current while the tab is open.
        void UpdateDetailStatus()
        {
            if (_open == null) return;
            if (_deleteArmed >= 0f && Time.time - _deleteArmed > 3f)
            {
                _deleteArmed = -1f;
                _delete.L.T.text = "Delete";
                _delete.G.Tint = Button;
            }
            var busy = _summarizer != null && _summarizer.Busy(_open.Id);
            var status = _summarizer?.StatusOf(_open.Id);
            var size = _open.IsDay ? $"{_open.Summarised} of {_open.Parts.Count} summarised" : $"{_open.Lines.Count} lines";
            var text = status ?? (_open.Summary != null ? size + (_open.People.Count > 0 ? " · " + string.Join(", ", _open.People) : "")
                : $"{size} · not summarised");
            if (text == _shownStatus) return;
            _shownStatus = text;
            _detailStatus.T.text = text;
            Show(_summarise, !busy && (_open.IsDay || _open.Lines.Count > 0));
            _summarise.L.T.text = _open.Summary == null ? "Summarise" : "Summarise again";
            _summarise.L.T.fontSize = (_open.Summary == null ? 0.0145f : 0.0125f) * _fontScale;
        }

        // Per frame on the Conversations tab (a couple of times a second is plenty).
        void UpdateHistory()
        {
            if (Time.time < _historyTick) return;
            _historyTick = Time.time + 0.4f;
            if (_openId != null) UpdateDetailStatus();
            else
            {
                RefreshOnline();
                foreach (var r in _historyRows)
                    if (r.Id != null && _summarizer != null && _summarizer.Busy(r.Id))
                        r.Detail.T.text = RowDetail(ConversationLog.Load(r.Id));
            }
        }
    }
}
