using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// The conversation setup, after a mode is chosen on the start screen. A glass panel in front, just below the eye line, walks
    /// through it; captions wait (<see cref="CaptionDirector.live"/>) until Start conversation.
    ///   Avatar signing (one person): tag them → meet the signer (it signs "hello") → place it where it is easy to
    ///   watch (a spatial anchor) → ready → start.
    ///   Speech bubbles (2–3 people): tag each person, optionally → read a few sentences so your own voice is known
    ///   (your speech then goes to your strip, never into someone's bubble) → translate or not → ready → start.
    /// Tagging: faces in view get a frame; point at one and pinch (or look at it until the ring fills), then pick a
    /// name. The name and its colour show on their captions. Moving the signer: point at it, pinch and hold, move,
    /// let go; by gaze, look at it to pick it up, then look where it should go and hold still.
    /// </summary>
    public partial class ModeMenu
    {
        enum Stage { TagOne, TagName, AvatarIntro, PlaceAvatar, Ready, TagMany, Conversation, SignIntro, YourVoice,
                     TagLive, // naming people during a conversation (from the conversation menu); captions carry on
                     AskTranslate, // speech bubbles: captions as spoken, or translated (a little slower)
                     Welcome, Hearing, Recommend, Mic, MicHelp, Language, Pinch, // from Welcome on: the first-run welcome (ModeMenu.Welcome.cs)
                     SoundList, SoundPlace, SoundName, SoundRecord, SoundSaved, // the menu's Sounds button (ModeMenu.Sounds.cs)
                     NameListen, NameConfirm, // …and your name: an alert when someone calls it
                     HearIntro, HearRun, HearResult } // the hearing check (ModeMenu.HearingCheck.cs) // …and your name: an alert when someone calls it

        // Read aloud while your voice is learned: everyday words, about 10 s at a relaxed pace.
        const string VoiceText = "Hi, I'm wearing live captions today. They show me what everyone says, so please " +
                                 "talk the way you normally would. If I miss something, I'll ask you to say it again.";

        const float SW = 0.46f, SH = 0.25f;         // setup panel size (m)
        const float DropSeconds = 1.4f;             // gaze: hold still this long to put the signer down
        const int MaxTagged = 6;                    // speech bubbles work for small groups

        static readonly (string name, Color color)[] Tags =
        {
            ("Doctor", Hex(0x0091FF)), ("Nurse", Hex(0xFF375F)),
            ("Friend", Hex(0x30D158)), ("Speaker", Hex(0xFF9230)),
        };
        // A second "Friend" gets the next free colour of these, so every tagged person's bubble looks different.
        static readonly Color[] SpareColors =
        {
            new(0.35f, 0.62f, 1f), new(1f, 0.45f, 0.72f), new(0.38f, 0.86f, 0.52f), new(1f, 0.74f, 0.3f),
            new(0.72f, 0.52f, 1f), new(0.3f, 0.85f, 0.9f), new(1f, 0.42f, 0.36f),
        };

        sealed class Slot { public Glass G; public Label L; public BoxCollider C; public System.Action Act; }
        sealed class Frame { public LineRenderer Line; public TextMeshPro Name; public float Hover; }

        Stage _stage = Stage.Conversation;
        Stage _beforeNaming;
        bool _gazeHold; // gaze: the buttons just changed, wait for the look to leave them before pressing
        TrackedPerson _naming, _faceHover;
        readonly List<TrackedPerson> _tagged = new();
        Label _stepText, _titleText, _bodyText, _hero;
        Glass _heroCircle;
        Vector2 _heroPos;
        readonly Glass[] _dots = new Glass[5];
        readonly float[] _dotW = new float[5];
        float _heroPop = 1f;                        // 0 → 1 as a new step's symbol pops in
        readonly Slot[] _slots = new Slot[4];
        readonly Dictionary<int, Frame> _frames = new();
        readonly HashSet<int> _framed = new();

        // The signer's spot (a spatial anchor once placed) and carrying it.
        Transform _anchor;
        bool _avatarHover, _carrying, _carryByGaze;
        float _carryDist, _still, _turnRate;
        Vector3 _carryOffset, _lastLook;

        // ---------------- building ----------------

        void BuildSetup()
        {
            var root = new GameObject("Setup panel").transform;
            root.SetParent(transform, false);
            _building = _setup = new Panel { Root = root, Dist = Distance, Drop = 0.24f }; // where you look, low: its top edge stays under the faces you tag

            AddGlass(root, Vector2.zero, new Vector2(SW, SH), 0.044f, PanelTint, 0, bevel: 0.008f).Dim = PanelDim;
            AddBacking(root, new Vector2(SW, SH));
            float left = -SW / 2 + Pad, right = SW / 2 - Pad, top = SH / 2;

            // Header: a round back button, the step's title, page dots on the right.
            var barY = top - 0.038f;
            var backPos = new Vector2(left + 0.018f, barY);
            var back = AddGlass(root, backPos, Vector2.one * LargeBtn, LargeBtn / 2, Circle, 1, bevel: 0.003f, round: true);
            AddIcon(root, Glyphs.Back, backPos, 0.02f, Primary, 2);
            AddHit(root, backPos, Vector2.one * 0.046f, Back, back);
            _titleText = AddLabel(root, "", new Vector2(left + 0.048f, barY), 0.024f, Primary, TextAlignmentOptions.MidlineLeft, SW - 0.16f);
            for (var i = 0; i < _dots.Length; i++)
            {
                _dots[i] = AddGlass(root, new Vector2(right - 0.006f - i * 0.016f, barY), Vector2.one * 0.008f, 0.004f, Tertiary, 1, bevel: 0.001f);
                _dots[i].Dim = 0f;
            }

            // The step: its symbol in a big circle, which mode and step, what to do.
            _heroPos = new Vector2(left + 0.032f, top - 0.112f);
            _heroCircle = AddGlass(root, _heroPos, Vector2.one * 0.064f, 0.032f, Circle, 1, bevel: 0.004f);
            _hero = AddIcon(root, Glyphs.PersonAdd, _heroPos, 0.034f, Primary, 2);
            var tx = left + 0.08f;
            _stepText = AddLabel(root, "", new Vector2(tx, top - 0.086f), 0.0125f, Secondary, TextAlignmentOptions.MidlineLeft, right - tx);
            _bodyText = AddLabel(root, "", new Vector2(tx, top - 0.098f), 0.0145f, Primary, TextAlignmentOptions.TopLeft, right - tx);
            _bodyText.T.lineSpacing = -4f;
            // The body fills the space down to the buttons and no further: longer text (the hearing check's result)
            // gets smaller rather than running into them.
            var bodyTop = top - 0.098f;
            var buttonsTop = -SH / 2 + Pad + LargeBtn - 0.008f;
            _bodyText.T.rectTransform.sizeDelta = new Vector2(right - tx, bodyTop - buttonsTop - 0.01f);
            _bodyText.T.enableAutoSizing = true;
            _bodyText.T.fontSizeMax = _bodyText.T.fontSize;
            _bodyText.T.fontSizeMin = _bodyText.T.fontSize * 0.62f;
            _setupPill = AddReadyPill(root, right, top - 0.086f, shortText: true); // on the step line, at the right

            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i] = new Slot();
                slot.G = AddGlass(root, Vector2.zero, new Vector2(0.1f, LargeBtn), LargeBtn / 2, Button, 2, round: true);
                slot.L = AddLabel(root, "", Vector2.zero, 0.0155f, Color.white, TextAlignmentOptions.Midline, layer: 3);
                slot.C = (BoxCollider)AddHit(root, Vector2.zero, Vector2.one * 0.01f, () => slot.Act?.Invoke(), slot.G);
            }
            AddGrabBar(root, SW, SH);
            root.gameObject.SetActive(false);
        }

        // One row of buttons along the bottom, right-aligned; the primary one in the accent colour.
        void SetButtons(params (string text, System.Action act, bool primary)[] buttons)
        {
            const float gap = 0.012f, h = LargeBtn; // the kit's Large button: a 50 pt capsule
            var n = buttons.Length;
            var w = n == 0 ? 0f : Mathf.Min(0.16f, (SW - 2 * Pad - gap * (n - 1)) / n);
            var x0 = SW / 2 - Pad - (n * w + (n - 1) * gap);
            var y = -SH / 2 + Pad + h / 2 - 0.008f;
            _gazeHold = true;
            for (var i = 0; i < _slots.Length; i++)
            {
                var s = _slots[i];
                var on = i < n;
                s.G.Hidden = s.L.Hidden = !on;
                s.C.enabled = on;
                s.Act = on ? buttons[i].act : null;
                if (!on) continue;
                var pos = new Vector2(x0 + w / 2 + i * (w + gap), y);
                s.G.Size = new Vector2(w, h);
                s.G.Tint = buttons[i].primary ? Accent : Button;
                s.G.R.transform.localPosition = new Vector3(pos.x, pos.y, s.G.R.transform.localPosition.z);
                s.L.T.text = buttons[i].text;
                s.L.C = Primary;
                s.L.T.rectTransform.sizeDelta = new Vector2(w, s.L.T.rectTransform.sizeDelta.y);
                s.L.T.transform.localPosition = new Vector3(pos.x, pos.y, s.L.T.transform.localPosition.z);
                s.C.transform.localPosition = new Vector3(pos.x, pos.y, -0.01f);
                s.C.size = new Vector3(w + 0.006f, h + 0.008f, 0.01f);
            }
        }

        void Say(int step, int of, string title, string body, string glyph, bool done = false)
        {
            var what = HearStage(_stage) ? "Hearing check" : _onboarding ? "Getting started" : _soundsOpen ? "Your sounds" : SigningMode ? "Avatar signing" : SpeechMode ? "Sign to speech" : "Speech bubbles";
            _stepText.T.text = step > 0 ? $"{what} · Step {step} of {of}" : what;
            _titleText.T.text = title;
            _bodyText.T.text = body;
            _hero.T.text = glyph;
            // A mode's setup wears its colour; the welcome stays neutral.
            _heroCircle.Tint = done ? Accent : _onboarding ? Circle : WithAlpha(ModeColor(director.mode), 0.8f);
            // Page dots, right to left: the current step a wide white pill, the others small and dim.
            for (var i = 0; i < _dots.Length; i++)
            {
                var d = _dots[i];
                var index = of - 1 - i;             // dot i from the right shows step index+1
                d.Hidden = i >= of;
                var on = index == step - 1;
                _dotW[i] = on ? 0.02f : 0.008f;
                if (_setup.Fade <= 0f) d.Size = new Vector2(_dotW[i], d.Size.y); // not on screen yet: no need to animate
                d.Tint = on ? Primary : Tertiary;
            }
            LayDots(1f);
        }

        // The dots from the right, each easing toward its width (the current step's pill stretches, the last one shrinks).
        void LayDots(float follow)
        {
            var x = SW / 2 - Pad;
            for (var i = 0; i < _dots.Length; i++)
            {
                var d = _dots[i];
                d.Size = new Vector2(Mathf.Lerp(d.Size.x, _dotW[i], follow), d.Size.y);
                At(d, new Vector2(x - d.Size.x / 2, d.R.transform.localPosition.y));
                x -= d.Size.x + 0.006f;
            }
        }

        // ---------------- the steps ----------------

        bool SigningMode => director.mode == CaptionDirector.CaptionMode.Signing;
        bool SpeechMode => director.mode == CaptionDirector.CaptionMode.SignToSpeech;
        TrackedPerson Signee => signAvatar != null ? signAvatar.only : null;

        /// <summary>After a mode is chosen: pause captions and open the setup where you're looking.</summary>
        void BeginSetup()
        {
            _resumeOnClose = false; _paused = false;
            director.live = false;
            director.StartFresh();   // back in the menu: nothing left recognising or translating in the background
            _log?.EndConversation(); // a saved conversation ends here (and gets its summary)
            _onboarding = false;
            // Every mode starts with nobody tagged: tags from the last conversation would silence new people.
            tracker?.ClearTags();
            _tagged.Clear();
            _naming = null;
            if (signAvatar != null) { signAvatar.preview = false; signAvatar.only = null; }
            tracker?.SetAutoTag(!SigningMode && !SpeechMode && !director.Subtitles && AutoTagPref);
            if (director.Subtitles) { StartConversation(); return; } // nobody to tag or place: straight in
            _dock.Open = false;
            var first = SigningMode ? Stage.TagOne : SpeechMode ? Stage.SignIntro : Stage.TagMany;
            SetupSpot(first, at: true);
            _setup.Returning = false;
            _setup.Root.gameObject.SetActive(true);
            _setup.Open = true;
            Enter(first);
        }

        void Enter(Stage stage)
        {
            if (stage != _stage) { _setup.Swap = 0f; _heroPop = 0f; } // a new step: its text fades in, its symbol pops
            SetupSpot(stage, at: false);
            _stage = stage;
            var who = Signee;
            SyncTagged();
            switch (stage)
            {
                case Stage.Welcome: case Stage.Hearing: case Stage.Recommend: case Stage.Mic: case Stage.MicHelp: case Stage.Language: case Stage.Pinch:
                    EnterWelcome(stage);
                    break;
                case Stage.SoundList: case Stage.SoundPlace: case Stage.SoundName: case Stage.SoundRecord: case Stage.SoundSaved:
                case Stage.NameListen: case Stage.NameConfirm:
                    EnterSounds(stage);
                    break;
                case Stage.HearIntro: case Stage.HearRun: case Stage.HearResult:
                    EnterHearCheck(stage);
                    break;
                case Stage.TagOne:
                    Say(1, 4, "Who will you talk with?",
                        "Point at their face and pinch. No hands? Look at their face until the ring fills.\n" +
                        "Signing follows this one person.", Glyphs.PersonAdd);
                    if (signAvatar != null) signAvatar.preview = false;
                    if (who != null) SetButtons(($"Keep {who.Label}", () => Enter(Stage.AvatarIntro), true));
                    else SetButtons();
                    break;
                case Stage.TagName:
                    if (_naming != null && _naming.Tag != null)
                    {
                        // Already named: three other names, or take the name off.
                        Say(NameStep, NameOf, $"This is {_naming.Tag}",
                            "Pick another name, or remove the tag: they show as a numbered person again" +
                            (tracker != null && tracker.rememberPeople ? " and are no longer remembered." : "."), Glyphs.Person);
                        var others = new List<(string, System.Action, bool)>();
                        for (var k = 0; k < Tags.Length && others.Count < 3; k++)
                        {
                            if (_naming.Tag.StartsWith(Tags[k].name)) continue;
                            var n = k;
                            others.Add((NameButton(k), () => Name(n), false));
                        }
                        others.Add(("Remove tag", RemoveTag, false));
                        SetButtons(others.ToArray());
                        break;
                    }
                    Say(NameStep, NameOf, "Who is this?",
                        "Pick a name for them. It shows, in its colour, on what they say.", Glyphs.Person);
                    SetButtons((NameButton(0), () => Name(0), false), (NameButton(1), () => Name(1), false),
                        (NameButton(2), () => Name(2), false), (NameButton(3), () => Name(3), false));
                    break;
                case Stage.AvatarIntro:
                    Say(2, 4, "Meet your signer",
                        $"It signs what {who?.Label ?? "they"} say{(who != null ? "s" : "")}, sentence by sentence, with the words underneath.\n" +
                        "Next, put it where it's easy to watch.", Glyphs.Wave);
                    SetButtons(("Next", () => Enter(Stage.PlaceAvatar), true));
                    if (signAvatar != null) { signAvatar.preview = true; signAvatar.Greet(); }
                    break;
                case Stage.PlaceAvatar:
                    Say(3, 4, "Place your signer",
                        "Point at the signer, pinch and hold, move it, let go.\n" +
                        "By gaze: look at it to pick it up, then look where it should go and hold still.", Glyphs.Grab);
                    SetButtons(("Place here", PlaceHere, true));
                    EnsureAnchor();
                    break;
                case Stage.SignIntro:
                    Say(1, 2, "Sign, and it's said aloud",
                        "Sign in front of your chest. Hold a handshape still to spell a letter.\n" +
                        "Lower your hands to speak the sentence. Shake your head first to cancel it.", Glyphs.SignToSpeech);
                    SetButtons(("Next", () => Enter(Stage.Ready), true));
                    break;
                case Stage.YourVoice:
                    if (director.LearningVoice)
                    {
                        Say(VoiceStep, VoiceOf, "Listening to you…", VoiceBody(), Glyphs.Mic);
                        SetButtons(("Skip", () => { director.StopLearningVoice(); Enter(AfterVoice); }, false));
                    }
                    else if (director.HasWearerVoice)
                    {
                        Say(VoiceStep, VoiceOf, "Your voice is learned",
                            "What you say goes to your strip at the bottom centre, never into someone's bubble, " +
                            "even when they're looking at you.", Glyphs.Check, done: true);
                        SetButtons(("Learn again", LearnVoice, false), ("Next", () => Enter(AfterVoice), true));
                    }
                    else
                    {
                        Say(VoiceStep, VoiceOf, "Teach it your voice",
                            "Read two sentences aloud, so your own speech never lands in someone else's bubble. " +
                            "About 10 seconds; best when others are quiet.", Glyphs.Mic);
                        SetButtons(("Skip", () => Enter(AfterVoice), false), ("Start", LearnVoice, true));
                    }
                    break;
                case Stage.AskTranslate:
                {
                    var mine = CaptionTranslator.Name(director.MyLanguage);
                    Say(3, 4, "Do you need translation?",
                        $"Speech in another language can show in {mine}, translated on this headset. " +
                        "Translated captions come a little slower than captions as spoken.\n" +
                        "Change the language any time in the conversation menu (Translate into).", Glyphs.Translate,
                        done: director.translate);
                    SetButtons(("As spoken", () => { if (director.translate) director.SetTranslate(false); Enter(Stage.Ready); }, !director.translate),
                        ($"Into {mine}", () => { if (!director.translate) director.SetTranslate(true); Enter(Stage.Ready); }, director.translate));
                    break;
                }
                case Stage.Ready:
                    Say(SigningMode ? 4 : SpeechMode ? 2 : 4, SigningMode ? 4 : SpeechMode ? 2 : 4, "Ready to talk", ReadyText(), Glyphs.Check, done: true);
                    SetButtons(("Start conversation", StartConversation, true));
                    break;
                case Stage.TagMany:
                    Say(1, 4, "Who's in the conversation?", TagManyBody(), Glyphs.PersonAdd);
                    SetButtons((TagModeLabel, CycleTagMode, false),
                        ("Next", () => Enter(director.CanLearnVoice ? Stage.YourVoice : Stage.AskTranslate), true));
                    break;
                case Stage.TagLive:
                    Say(0, 0, "Name people", TagLiveBody(), Glyphs.PersonAdd);
                    if (BubbleMode) SetButtons((TagModeLabel, CycleTagMode, false), ("Done", BackToConversation, true));
                    else SetButtons(("Done", BackToConversation, true));
                    break;
            }
        }

        static bool IsTagging(Stage s) => s is Stage.TagOne or Stage.TagMany or Stage.TagName or Stage.TagLive;

        // Where the setup panel sits. While tagging: low, to one side and nearer, turned toward you, on the side away
        // from the faces, so nothing is between you and the faces you point at and no face frame stands in front of
        // the panel (2026-11-09: frames and panel were in one line, hard to pick either). Other steps: in front.
        // at: put it there now (opening); otherwise it glides there when the spot changes.
        void SetupSpot(Stage stage, bool at)
        {
            if (head == null) return; // Editor preview: placed by hand
            float yaw = 0f, dist = Distance, drop = 0.24f;
            if (IsTagging(stage) || stage == Stage.SoundPlace) // placing a sound pin: out of the way of the room too
            {
                // Keep the side through a tagging session; choose it when tagging starts.
                var side = Mathf.Abs(_setup.Yaw) > 1f && IsTagging(_stage) && !at ? Mathf.Sign(_setup.Yaw) : SideAwayFromFaces();
                yaw = side * 36f; dist = 0.52f; drop = 0.2f;
            }
            var moved = !Mathf.Approximately(yaw, _setup.Yaw) || !Mathf.Approximately(dist, _setup.Dist) || !Mathf.Approximately(drop, _setup.Drop);
            _setup.Yaw = yaw; _setup.Dist = dist; _setup.Drop = drop;
            if (at || !_setup.Open || _setup.Fade <= 0.01f) Place(_setup, FrontOf(_setup));
            else if (moved) _setup.Returning = true;
        }

        // +1 (right) unless the faces in view are mostly on the right.
        float SideAwayFromFaces()
        {
            if (tracker == null || head == null) return 1f;
            float sum = 0f; var n = 0;
            foreach (var p in tracker.People)
            {
                if (!p.Visible) continue;
                sum += Vector3.SignedAngle(Flat(head.forward), Flat(p.HeadPosition - head.position), Vector3.up);
                n++;
            }
            return n > 0 && sum / n > 5f ? -1f : 1f;
        }

        bool BubbleMode => !SigningMode && !SpeechMode && !director.Subtitles;
        bool NamingLive => _beforeNaming == Stage.TagLive;
        int NameStep => NamingLive ? 0 : 1;
        int NameOf => NamingLive ? 0 : 4;

        string TagModeLabel => TagModePref switch
        {
            TagMode.ByHand => "Tagging: by hand",
            TagMode.Automatic => "Tagging: automatic",
            _ => "Tagging: both",
        };

        string TagManyBody() =>
            (TagModePref switch
            {
                TagMode.Both => "Everyone who comes into view is tagged, each in a colour of their own (up to 6). Point at someone and pinch to rename them.",
                TagMode.Automatic => "Everyone who comes into view is tagged, each in a colour of their own (up to 6). " +
                                     "To rename someone, choose both, or People in the conversation menu.",
                _ => "Point at each person and pinch to name them (up to 6). Only tagged people get captions; tag nobody and everyone does.",
            }) +
            (_tagged.Count > 0 ? "\n" + TaggedList() : "");

        string TagLiveBody() =>
            "Point at someone and pinch to name or rename them; no hands, look at their face until the ring fills. " +
            "Captions carry on meanwhile." +
            (tracker != null && tracker.rememberPeople ? " Named people are remembered next time." : "") +
            (_tagged.Count > 0 ? "\n" + TaggedList()
                : BubbleMode && TagModePref == TagMode.ByHand ? "\nOnce someone is named, only named people get captions." : "");

        // By hand → both → automatic → by hand. By hand clears the automatic tags (the ones you named stay).
        void CycleTagMode()
        {
            if (tracker == null) return;
            var mode = TagModePref switch { TagMode.ByHand => TagMode.Both, TagMode.Both => TagMode.Automatic, _ => TagMode.ByHand };
            TagModePref = mode;
            var on = mode != TagMode.ByHand;
            if (!on)
                foreach (var p in tracker.People)
                    if (p.Tag != null && p.Tag.StartsWith("Person ")) p.Tag = null;
            tracker.SetAutoTag(on);
            _tagged.RemoveAll(t => t.Tag == null);
            Debug.Log($"[Captions] Tagging: {mode}");
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(_stage);
        }

        /// <summary>From the conversation menu: name or rename people without stopping the conversation.</summary>
        void NamePeople()
        {
            _quick.Open = false;
            _dock.Open = false;
            _naming = null;
            SetupSpot(Stage.TagLive, at: true);
            _setup.Returning = false;
            _setup.Root.gameObject.SetActive(true);
            _setup.Open = true;
            Enter(Stage.TagLive);
        }

        void BackToConversation()
        {
            _naming = null;
            _setup.Open = false;
            _stage = Stage.Conversation;
            ShowDock();
            UiFeedback.Play(UiSound.Back, _setup.Root.position);
        }

        // Auto tagging tags people in the tracker: list them here too (in the order they were tagged).
        void SyncTagged()
        {
            _tagged.RemoveAll(t => t.Tag == null);
            if (tracker == null) return;
            foreach (var p in tracker.People) if (p.Tag != null && !_tagged.Contains(p)) _tagged.Add(p);
        }

        int VoiceStep => _onboarding ? 4 : 2;
        int VoiceOf => _onboarding ? WelcomeSteps : 4;
        Stage AfterVoice => _onboarding ? Stage.Pinch : Stage.AskTranslate;

        void LearnVoice()
        {
            director.StartLearningVoice();
            Enter(Stage.YourVoice);
        }

        string VoiceBody()
        {
            var p = director.VoiceLearnProgress;
            const int n = 20;
            var on = Mathf.RoundToInt(p * n);
            return $"Read this aloud:\n<i>“{VoiceText}”</i>\n" +
                   $"<color=#FFFFFF>{new string('•', on)}</color><color=#FFFFFF40>{new string('•', n - on)}</color>  {p:P0}";
        }

        /// <summary>Editor preview: the setup panel straight in front, at one step (0 TagOne … 5 TagMany).</summary>
        public void OpenSetupForPreview(Transform viewer, int stage)
        {
            var pos = viewer.position + viewer.forward * Distance;
            _setup.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - viewer.position, Vector3.up));
            _setup.Root.gameObject.SetActive(true);
            Enter((Stage)stage);
            _setup.Fade = _setup.Swap = _heroPop = 1f; // a still picture: skip the animations
            UpdateSetup();
            UpdateReadyPill(_setupPill);
            ApplyPanel(_setup);
        }

        void Back()
        {
            if (HearStage(_stage)) { BackHearCheck(); return; }
            if (_onboarding && _stage != Stage.YourVoice) { BackWelcome(); return; }
            if (_soundsOpen && SoundStage(_stage)) { BackSounds(); return; }
            switch (_stage)
            {
                case Stage.TagOne:
                case Stage.TagMany:
                case Stage.SignIntro:
                    _setup.Open = false;
                    Open(); // back to the modes
                    break;
                case Stage.TagName: _naming = null; Enter(_beforeNaming); break;
                case Stage.TagLive: BackToConversation(); break;
                case Stage.AvatarIntro: Enter(Stage.TagOne); break;
                case Stage.PlaceAvatar: if (_carrying) Drop(); Enter(Stage.AvatarIntro); break;
                case Stage.YourVoice: director.StopLearningVoice(); Enter(_onboarding ? Stage.Language : Stage.TagMany); break;
                case Stage.AskTranslate: Enter(director.CanLearnVoice ? Stage.YourVoice : Stage.TagMany); break;
                case Stage.Ready: Enter(SigningMode ? Stage.PlaceAvatar : SpeechMode ? Stage.SignIntro : Stage.AskTranslate); break;
            }
        }

        void PickFace(TrackedPerson p)
        {
            if (_stage is Stage.TagMany or Stage.TagLive && p.Tag == null && _tagged.FindAll(t => t.Tag != null).Count >= MaxTagged)
            {
                _bodyText.T.text = $"That's {MaxTagged} people already: speech bubbles work best for small groups. Point at a tagged person to rename them.";
                return;
            }
            if (_stage != Stage.TagName) _beforeNaming = _stage;
            _naming = p;
            Enter(Stage.TagName); // the names show on the panel at the side: it doesn't move in front of the faces

        }

        void Name(int tag)
        {
            var p = _naming;
            if (p == null) return;
            _naming = null;
            var (name, color) = Tags[tag];
            // Two friends: "Friend", "Friend 2".
            var same = 0;
            foreach (var t in _tagged) if (t != p && t.Tag != null && t.Tag.StartsWith(name)) same++;
            p.Tag = same > 0 ? $"{name} {same + 1}" : name;
            bool Taken(Color c) => _tagged.Exists(t => t != p && t.Tag != null && t.Color == c);
            if (Taken(color)) color = System.Array.Find(SpareColors, c => !Taken(c));
            p.Color = color;
            if (!_tagged.Contains(p)) _tagged.Add(p);
            Debug.Log($"[Captions] Tagged Person {p.Id} as {p.Tag}");
            UiFeedback.Play(UiSound.Confirm, p.HeadPosition);
            if (_beforeNaming == Stage.TagOne && signAvatar != null)
            {
                signAvatar.only = p;
                Enter(Stage.AvatarIntro);
            }
            else Enter(_beforeNaming);
        }

        void RemoveTag()
        {
            var p = _naming;
            if (p == null) return;
            _naming = null;
            Debug.Log($"[Captions] Removed tag {p.Tag} from Person {p.Id}");
            tracker?.Untag(p);
            _tagged.Remove(p);
            if (signAvatar != null && signAvatar.only == p) signAvatar.only = null;
            UiFeedback.Play(UiSound.Back, p.HeadPosition);
            Enter(_beforeNaming);
        }

        // A name to give someone, with a dot in its colour.
        static string NameButton(int tag) => $"<color=#{ColorUtility.ToHtmlStringRGB(Tags[tag].color)}>●</color> {Tags[tag].name}";

        string TaggedList()
        {
            var parts = new List<string>();
            foreach (var t in _tagged)
                if (t.Tag != null) parts.Add($"<color=#{ColorUtility.ToHtmlStringRGB(t.Color)}>●</color> {t.Tag}");
            return string.Join("   ", parts);
        }

        string ReadyText()
        {
            if (SpeechMode)
                return Bullet("Your signs show below your view as you sign") + "\n" +
                       Bullet("Fingerspelling and the signs the avatar knows") + "\n" +
                       Bullet("Other people's replies appear as speech bubbles") + "\n" +
                       Bullet("For the menu, pinch and hold either hand for 2 s");
            if (!SigningMode)
                return (_tagged.Count > 0 ? TaggedList() + "\nOnly they get captions; other voices are ignored.\n" : "No one tagged: people show as Person 1, 2…\n") +
                       "Captions appear beside each person as they speak." +
                       (director.HasWearerVoice ? "\nYour own words go to your strip, bottom centre." : "") +
                       "\nFor the menu again, pinch and hold either hand for 2 seconds.";
            var anchored = _anchor != null && _anchor.GetComponent<OVRSpatialAnchor>() != null;
            return Bullet($"Signing follows {Signee?.Label ?? "nobody yet"}") + "\n" +
                   Bullet($"Signer placed{(anchored ? " and anchored to the room" : "")}") + "\n" +
                   Bullet("Other voices are not signed: use speech bubbles for groups") + "\n" +
                   Bullet("For the menu, pinch and hold either hand for 2 s");
        }

        // A list line: a dim bullet, wrapped lines indented under the text.
        static string Bullet(string text) => $"<color=#FFFFFF80>•</color> <indent=0.9em>{text}</indent>";

        void StartConversation()
        {
            if (_carrying) Drop();
            _resumeOnClose = false; _paused = false;
            director.StartFresh();   // a new conversation: only what's said from now on
            director.live = true;
            _log?.BeginConversation();
            _summarisedSaid = 0;
            _conversationStart = Time.realtimeSinceStartup;
            if (signAvatar != null) signAvatar.preview = false;
            _setup.Open = false;
            _menu.Open = false;
            _stage = Stage.Conversation;
            _naming = null;
            ShowDock();
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Debug.Log($"[Captions] Conversation started: {director.mode}" + (SigningMode && Signee != null ? $", signing {Signee.Label}" : ""));
        }

        void UpdateSetup()
        {
            // The symbol nods a little while the step waits for you to point at someone.
            var bob = _stage is Stage.TagOne or Stage.TagMany ? Mathf.Sin(Time.time * 3.2f) * 0.002f : 0f;
            At(_hero, _heroPos + new Vector2(0, bob));
            // A new step's symbol pops in; when ready, the check breathes slowly.
            _heroPop = Mathf.MoveTowards(_heroPop, 1f, Time.deltaTime * 3f);
            var s = Mathf.LerpUnclamped(0.8f, 1f, EaseOutBack(_heroPop));
            if (_stage == Stage.Ready) s *= 1f + 0.02f * Mathf.Sin(Time.time * 2.2f);
            _heroCircle.Size = Vector2.one * 0.064f * s;
            _hero.T.transform.localScale = Vector3.one * s;
            LayDots(1f - Mathf.Exp(-14f * Time.deltaTime));
            // Learning your voice: the progress bar fills; when done, the step shows its result.
            if (_stage == Stage.YourVoice)
            {
                if (director.LearningVoice) _bodyText.T.text = VoiceBody();
                else if (_wasLearning) Enter(Stage.YourVoice);
            }
            _wasLearning = director.LearningVoice;
            // Auto tagging: people tagged as they come into view show in the list straight away.
            if (_stage is Stage.TagMany or Stage.TagLive)
            {
                var n = _tagged.Count;
                SyncTagged();
                if (_tagged.Count != n) _bodyText.T.text = _stage == Stage.TagLive ? TagLiveBody() : TagManyBody();
            }
            if (_onboarding) UpdateWelcome();
        }
        bool _wasLearning;

        // ---------------- placing the signer ----------------

        void EnsureAnchor()
        {
            if (signAvatar == null) return;
            if (_anchor == null)
            {
                _anchor = new GameObject("Signer anchor").transform;
                _anchor.SetParent(transform, true);
                if (!signAvatar.AvatarEyes(out var eyes, out _))
                {
                    var fwd = Flat(head.forward);
                    eyes = head.position + fwd * 1.1f + Vector3.Cross(Vector3.up, fwd) * 0.35f;
                }
                _anchor.position = eyes;
            }
            signAvatar.placedAt = _anchor;
        }

        // Fixed to the room: a fresh anchor where the signer stands now.
        void PlaceHere()
        {
            if (_carrying) Drop();
            if (_anchor != null && signAvatar != null)
            {
                var pos = _anchor.position;
                Destroy(_anchor.gameObject);
                _anchor = new GameObject("Signer anchor").transform;
                _anchor.SetPositionAndRotation(pos, Quaternion.LookRotation(Flat(pos - head.position), Vector3.up));
                _anchor.gameObject.AddComponent<OVRSpatialAnchor>();
                signAvatar.placedAt = _anchor;
                UiFeedback.Play(UiSound.Drop, pos);
            }
            Enter(Stage.Ready);
        }

        // The signer under the ray: head to waist, in the plane facing you.
        bool AvatarUnder(Ray ray, ref Vector3 end)
        {
            if (_stage != Stage.PlaceAvatar || !_setup.Open || _anchor == null || signAvatar == null) return false;
            signAvatar.AvatarEyes(out _, out var scale);
            var eyes = _anchor.position;
            var facing = new Plane(Flat(head.position - eyes), eyes);
            if (!facing.Raycast(ray, out var d)) return false;
            var p = ray.GetPoint(d);
            var off = p - eyes;
            var sideways = Vector3.ProjectOnPlane(off, Vector3.up).magnitude;
            if (sideways > 0.3f * scale + 0.04f || off.y > 0.2f * scale + 0.03f || off.y < -0.9f * scale) return false;
            end = p;
            return true;
        }

        void Pick(Ray ray, Vector3 at)
        {
            var anchor = _anchor.GetComponent<OVRSpatialAnchor>();
            if (anchor != null) Destroy(anchor); // it pins the transform to the room; moving needs it gone
            _carrying = true;
            _carryByGaze = _gaze;
            _carryDist = Mathf.Clamp(Vector3.Distance(ray.origin, at), 0.4f, 3f);
            _carryOffset = _anchor.position - at;
            _lastLook = head.forward;
            _still = -0.6f; // a moment before holding still counts: you just held still to pick it up
            _turnRate = 0f;
            UiFeedback.Play(UiSound.Pick, at);
        }

        Vector3 Carry(Ray ray, bool pinch, ref float progress)
        {
            var to = ray.GetPoint(_carryDist);
            _anchor.position = to + _carryOffset;
            if (_carryByGaze)
            {
                var dt = Mathf.Max(Time.deltaTime, 1e-4f);
                _turnRate = Mathf.Lerp(_turnRate, Vector3.Angle(head.forward, _lastLook) / dt, dt * 8f);
                _lastLook = head.forward;
                _still = _turnRate < 8f ? _still + dt : Mathf.Min(_still, 0f);
                progress = Mathf.Clamp01(_still / DropSeconds);
                if (_still >= DropSeconds) Drop();
            }
            else if (!pinch) Drop();
            return to;
        }

        void Drop()
        {
            _carrying = false;
            UiFeedback.Play(UiSound.Drop, _anchor.position);
            _gazeOn = _anchor; // still looking at it: don't pick it straight up again until the look leaves it
            _dwell = -1f;
        }

        // ---------------- faces ----------------

        bool Tagging => _setup.Open && _stage is Stage.TagOne or Stage.TagMany or Stage.TagName or Stage.TagLive;
        // Faces can be picked while tagging, except in the setup with automatic-only tagging (so a look never tags).
        bool PickingFaces => Tagging && !(_stage == Stage.TagMany && TagModePref == TagMode.Automatic);

        // The face nearest the ray, within about half a face of it.
        TrackedPerson FaceUnder(Ray ray, ref Vector3 end)
        {
            if (!PickingFaces || tracker == null) return null;
            TrackedPerson best = null;
            var bestOff = 0.14f;
            foreach (var p in tracker.People)
            {
                if (!p.Visible) continue;
                var to = p.HeadPosition - ray.origin;
                var along = Vector3.Dot(to, ray.direction);
                if (along < 0.3f) continue;
                var off = (to - ray.direction * along).magnitude;
                if (off >= bestOff) continue;
                best = p;
                bestOff = off;
                end = ray.GetPoint(along);
            }
            return best;
        }

        // A rounded frame around each face while tagging (tagged ones in their colour, with their name above);
        // during the other steps just the tagged faces.
        void UpdateFaces()
        {
            _framed.Clear();
            if (tracker != null && _setup.Open)
                foreach (var p in tracker.People)
                {
                    if (!p.Visible || (!PickingFaces && p.Tag == null)) continue;
                    _framed.Add(p.Id);
                    if (!_frames.TryGetValue(p.Id, out var f)) _frames[p.Id] = f = NewFrame();
                    DrawFrame(f, p);
                }
            foreach (var (id, f) in _frames)
                if (!_framed.Contains(id) && f.Line.gameObject.activeSelf) { f.Line.gameObject.SetActive(false); f.Hover = 0f; }
        }

        void DrawFrame(Frame f, TrackedPerson p)
        {
            f.Line.gameObject.SetActive(true);
            var hot = p == _faceHover || p == _naming;
            f.Hover = Mathf.MoveTowards(f.Hover, hot ? 1f : 0f, Time.deltaTime * 6f);
            var color = p.Tag != null ? p.Color : Color.white;
            color.a = Tagging ? 0.55f + 0.45f * f.Hover : 0.45f;

            var c = p.HeadPosition;
            var toViewer = Flat(head.position - c);
            var right = Vector3.Cross(Vector3.up, toViewer);
            // Frames never overlap: narrower when faces are close together (people side by side, faces on a screen).
            var w = 0.1f;
            foreach (var q in tracker.People)
                if (q != p && q.Visible) w = Mathf.Min(w, 0.42f * Vector3.Distance(q.HeadPosition, c));
            w = Mathf.Max(w, 0.035f);
            var half = new Vector2(w, 1.3f * w) * (1f + 0.08f * f.Hover);
            var r = 0.4f * w;
            const int arc = 6;
            var n = f.Line.positionCount;
            for (var i = 0; i < n; i++)
            {
                var corner = i / arc; // 0 top right, 1 bottom right, 2 bottom left, 3 top left
                var a = (Mathf.PI / 2) * (1 - corner) - (Mathf.PI / 2) * (i % arc) / (arc - 1);
                var cx = (corner < 2 ? 1 : -1) * (half.x - r);
                var cy = (corner == 0 || corner == 3 ? 1 : -1) * (half.y - r);
                f.Line.SetPosition(i, c + right * (cx + Mathf.Cos(a) * r) + Vector3.up * (cy + Mathf.Sin(a) * r));
            }
            f.Line.widthMultiplier = 0.003f + 0.002f * f.Hover;
            f.Line.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, color);
            _mpb.SetTexture(BaseMapId, Texture2D.whiteTexture);
            f.Line.SetPropertyBlock(_mpb);

            var dist = Vector3.Distance(head.position, c);
            var label = p.Tag != null ? $"<b>{p.Tag}</b>" : f.Hover > 0.5f ? (_gaze ? "Keep looking to tag" : "Pinch to tag") : "";
            f.Name.text = label;
            f.Name.fontSize = 0.02f * Mathf.Max(1f, dist / 1.2f) * _fontScale;
            f.Name.color = new Color(Mathf.Lerp(1, color.r, 0.5f), Mathf.Lerp(1, color.g, 0.5f), Mathf.Lerp(1, color.b, 0.5f), Mathf.Max(color.a, 0.8f));
            f.Name.transform.SetPositionAndRotation(c + Vector3.up * (half.y + 0.025f), Quaternion.LookRotation(-toViewer, Vector3.up));
        }

        Frame NewFrame()
        {
            var go = new GameObject("Face frame");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.positionCount = 4 * 6;
            line.numCornerVertices = 2;
            line.sharedMaterial = panelMaterial;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var textGo = new GameObject("Face name");
            textGo.transform.SetParent(go.transform, false);
            var t = textGo.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            ComplexText.Attach(t);
            t.alignment = TextAlignmentOptions.Bottom;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.pivot = new Vector2(0.5f, 0f);
            t.rectTransform.sizeDelta = new Vector2(0.5f, 0.05f);
            t.GetComponent<MeshRenderer>().sortingOrder = 20;
            return new Frame { Line = line, Name = t };
        }
    }
}
