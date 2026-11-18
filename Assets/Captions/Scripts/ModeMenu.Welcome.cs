using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// First-run welcome (also from Settings › Getting started), on the setup panel: what the app does, how you hear
    /// (which picks the mode to start with), which microphone, other languages (captions as spoken or translated into
    /// yours), your voice (when it can be learned), and the held left
    /// pinch that opens the menu in every mode, tried once. Skippable at every step; then the mode menu opens.
    /// </summary>
    public partial class ModeMenu
    {
        const string OnboardedKey = "captions.onboarded", HearingKey = "captions.hearing", AutoTagKey = "captions.autoTag";

        bool _onboarding, _pinchTried;

        static bool Onboarded => PlayerPrefs.GetInt(OnboardedKey, 0) == 1;
        static int Hearing => PlayerPrefs.GetInt(HearingKey, -1); // 0 deaf, 1 hard of hearing, 2 hearing, -1 not asked
        // How people get tagged in speech bubbles: by hand only, automatically only ("Person 1, 2…", faces can't be picked
        // in the setup, so looking around never tags anyone by gaze), or both (automatic, and pinch someone to rename).
        enum TagMode { ByHand, Automatic, Both }
        const string TagModeKey = "captions.tagMode";
        static TagMode TagModePref
        {
            // Before the three-way setting there was only "Tag automatically": on carries over as Both.
            get => (TagMode)PlayerPrefs.GetInt(TagModeKey, PlayerPrefs.GetInt(AutoTagKey, 0) == 1 ? (int)TagMode.Both : (int)TagMode.ByHand);
            set { PlayerPrefs.SetInt(TagModeKey, (int)value); PlayerPrefs.Save(); }
        }
        static bool AutoTagPref => TagModePref != TagMode.ByHand;

        int WelcomeSteps => director.CanLearnVoice ? 5 : 4;
        Stage AfterMic => Stage.Language;
        Stage AfterLanguage => director.CanLearnVoice ? Stage.YourVoice : Stage.Pinch;

        /// <summary>Open the welcome steps (first launch, or again from Settings).</summary>
        void OpenWelcome()
        {
            _resumeOnClose = false; _paused = false;
            if (director.live) { director.live = false; director.StartFresh(); _log?.EndConversation(); }
            _onboarding = true;
            _pinchTried = false;
            _menu.Open = false;
            if (_quick != null) _quick.Open = false;
            _closeAt = -1f;
            _dock.Open = false;
            Place(_setup, FrontOf(_setup));
            _setup.Returning = false;
            _setup.Root.gameObject.SetActive(true);
            _setup.Open = true;
            Enter(Stage.Welcome);
        }

        void FinishWelcome()
        {
            PlayerPrefs.SetInt(OnboardedKey, 1);
            PlayerPrefs.Save();
            _onboarding = false;
            director.StopLearningVoice();
            Debug.Log("[Captions] Welcome finished");
            Open(); // the mode menu, with the recommended mode chosen
        }

        void EnterWelcome(Stage stage)
        {
            var n = WelcomeSteps;
            switch (stage)
            {
                case Stage.Welcome:
                    Say(0, n, "Welcome",
                        "Captions appear beside each person as they talk, in 34 languages. An avatar can sign what they say, " +
                        "and your own signs can be spoken aloud.\nIt all runs on this headset: nothing you hear or say leaves it.", Glyphs.Wave);
                    SetButtons(("Skip", FinishWelcome, false), ("Get started", () => Enter(Stage.Hearing), true));
                    break;
                case Stage.Hearing:
                    Say(1, n, "How do you hear?",
                        "This picks the mode to start with. You can change it any time from the menu.\n" +
                        "<color=#FFFFFF80>Not sure? A 3-minute hearing check with the headset's speakers.</color>", Glyphs.Hearing);
                    SetButtons(("Deaf", () => PickHearing(0), Hearing == 0), ("Hard of hearing", () => PickHearing(1), Hearing == 1),
                        ("Hearing", () => PickHearing(2), Hearing == 2), ("Not sure? Check", () => StartHearCheck(true), false));
                    break;
                case Stage.Recommend:
                    Say(1, n, "Start with speech bubbles", RecommendText(), Glyphs.Bubbles, done: true);
                    SetButtons(("Next", () => Enter(Stage.Mic), true));
                    break;
                case Stage.Mic:
                {
                    var ext = ExternalMic();
                    var usingExt = micSettings.Current != null && micSettings.Current.External;
                    if (ext != null)
                    {
                        Say(2, n, "Which microphone?",
                            $"{ext.Title} is plugged in{(usingExt ? " and in use" : "")}. A mic clipped on the person you talk " +
                            "with gives much clearer captions than the headset's own.", Glyphs.ExternalMic, done: usingExt);
                        SetButtons(("Quest mic", () => { UseBuiltInMic(); Enter(AfterMic); }, false),
                            ($"Use {Short(ext.Title)}", () => { micSettings.Select(ext); Enter(AfterMic); }, true));
                    }
                    else
                    {
                        Say(2, n, "Which microphone?",
                            "The headset's own mic works for people near you. A mic clipped on the person you talk with " +
                            "gives much clearer captions, even in a noisy room.", Glyphs.Mic);
                        SetButtons(("Add a mic", () => Enter(Stage.MicHelp), false), ("Use Quest mic", () => Enter(AfterMic), true));
                    }
                    break;
                }
                case Stage.MicHelp:
                    Say(2, n, "Add a microphone",
                        "1. Plug a USB-C mic or a wireless clip-on receiver into the headset.\n" +
                        "2. Quest Settings › Experimental › turn on External microphone.\n" +
                        "3. It shows up here within a few seconds.", Glyphs.ExternalMic);
                    SetButtons(("Use Quest mic", () => Enter(AfterMic), false), ("Done", () => Enter(Stage.Mic), true));
                    break;
                case Stage.Language:
                {
                    var mine = CaptionTranslator.Name(director.MyLanguage);
                    Say(3, n, "Other languages",
                        "Captions switch by themselves to whichever of 34 languages people speak. They can also be shown " +
                        $"in {mine}, translated on this headset (a one-time download of about 1 GB covers every language).",
                        Glyphs.Captions, done: director.translate);
                    SetButtons(("As spoken", () => PickTranslate(false), !director.translate),
                        ("Translate", () => PickTranslate(true), director.translate));
                    break;
                }
                case Stage.Pinch:
                    if (_pinchTried)
                    {
                        Say(n, n, "That's it",
                            "Hold that pinch, or double-tap your thumb, whenever you want the menu: to switch mode, save the " +
                            "conversation or read past ones.\n" +
                            "Now pick a mode.", Glyphs.Check, done: true);
                        SetButtons(("Choose a mode", FinishWelcome, true));
                    }
                    else
                    {
                        Say(n, n, "The menu, from any mode",
                            "Lower either hand, then pinch thumb and index finger together and hold for 2 seconds. " +
                            "A ring fills, then the menu opens. Or tap your thumb twice on the side of your index finger.\n" +
                            "Try it now. Hands track best in good indoor light." +
                            (Time.time - _handsSeenAt > 5f ? "\nNo hands? The round button at the bottom right of your view does the same." : ""),
                            Glyphs.Hand);
                        SetButtons(("Skip", FinishWelcome, false));
                    }
                    break;
            }
        }

        void BackWelcome()
        {
            switch (_stage)
            {
                case Stage.Welcome: FinishWelcome(); break;
                case Stage.Hearing: Enter(Stage.Welcome); break;
                case Stage.Recommend: Enter(Stage.Hearing); break;
                case Stage.Mic: Enter(Hearing >= 0 ? Stage.Recommend : Stage.Hearing); break;
                case Stage.MicHelp: Enter(Stage.Mic); break;
                case Stage.Language: Enter(Stage.Mic); break;
                case Stage.Pinch: _pinchTried = false; Enter(director.CanLearnVoice ? Stage.YourVoice : Stage.Language); break;
            }
        }

        // Speech bubbles is where everyone starts (it works for anyone talking); the second suggestion depends on you.
        void PickHearing(int level)
        {
            PlayerPrefs.SetInt(HearingKey, level);
            director.mode = CaptionDirector.CaptionMode.Bubbles;
            PlayerPrefs.SetInt(PrefKey, (int)director.mode);
            PlayerPrefs.Save();
            Refresh();
            Debug.Log($"[Captions] Hearing: {(level == 0 ? "deaf" : level == 1 ? "hard of hearing" : "hearing")}");
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(Stage.Recommend);
        }

        void PickTranslate(bool on)
        {
            if (on != director.translate) director.SetTranslate(on);
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(AfterLanguage);
        }

        string RecommendText() => Hearing switch
        {
            0 => Bullet("Captions float beside each person as they speak") + "\n" +
                 Bullet("Want to answer in ASL? <b>Sign to speech</b> says your signs aloud") + "\n" +
                 Bullet("<b>Avatar signing</b> shows one person's words in ASL"),
            1 => Bullet("Captions float beside each person, even in a noisy room") + "\n" +
                 Bullet("A clip-on mic on the person you talk with helps most: next step") + "\n" +
                 Bullet("<b>Avatar signing</b> and <b>Sign to speech</b> are in the menu too"),
            _ => Bullet("Captions float beside each person as they speak") + "\n" +
                 Bullet("<b>Avatar signing</b> shows what's said in ASL: a way to learn signs") + "\n" +
                 Bullet("Talking with a Deaf friend? They can wear it instead"),
        };

        MicSettings.Option ExternalMic()
        {
            foreach (var o in micSettings.Options) if (o.External) return o;
            return null;
        }

        void UseBuiltInMic()
        {
            if (micSettings.Current == null || !micSettings.Current.External) return;
            foreach (var o in micSettings.Options) if (!o.External) { micSettings.Select(o); return; }
        }

        static string Short(string title) => title.Length <= 10 ? title : "this mic";

        // While the welcome is open, mic news shows on its microphone step instead of opening the mic tab.
        bool WelcomeTakesMic(MicSettings.Option offered)
        {
            if (!_onboarding || !_setup.Open) return false;
            if (_stage is Stage.Mic or Stage.MicHelp) Enter(Stage.Mic);
            return true;
        }

        void UpdateWelcome()
        {
            if (_stage == Stage.Pinch && !_pinchTried && Time.time - _handsSeenAt > 5f && !_bodyText.T.text.Contains("No hands"))
                EnterWelcome(Stage.Pinch); // add the line about the round button
        }

        /// <summary>The held pinch was done on the welcome's pinch step.</summary>
        bool TryPinchTutorial()
        {
            if (!_onboarding || !_setup.Open || _stage != Stage.Pinch || _pinchTried) return false;
            _pinchTried = true;
            Debug.Log("[Captions] Welcome: held pinch tried");
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(Stage.Pinch);
            return true;
        }
    }
}
