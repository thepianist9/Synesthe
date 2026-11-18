using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// A quick hearing check (about 3 minutes), from the welcome ("How do you hear?" › Check my hearing) or from the
    /// Sounds panel. Short beeps play in one ear at a time through the headset's speakers, at five pitches from low to
    /// high, getting quieter while you press "I hear it" and louder again when you don't (a simplified
    /// Hughson-Westlake staircase). The media volume is held at a fixed level during the check and put back after.
    /// The speakers aren't calibrated, so it can't say how many decibels you hear: it compares pitches and ears with
    /// each other (and with your last check), and says which are harder. It's a check, not a medical hearing test.
    /// In the welcome its result picks "hard of hearing" or "hearing"; anywhere, it's kept for next time.
    /// </summary>
    public partial class ModeMenu
    {
        const string HearCheckKey = "captions.hearingCheck";
        static readonly int[] CheckHz = { 1000, 2000, 4000, 8000, 500 };   // the usual order: 1 kHz first
        static readonly int[] ShowOrder = { 4, 0, 1, 2, 3 };               // shown low to high: 500 Hz … 8 kHz
        const float CheckStart = -40f, CheckLoudest = -6f, CheckQuietest = -80f;
        const float ToneSeconds = 1.1f, AnswerSeconds = 1.6f;
        const int MaxTonesPerPitch = 9;
        const float CheckVolume = 0.6f;                                    // of the media volume's range

        [Serializable] sealed class HearResult
        {
            public string date;
            public float[] right = new float[5], left = new float[5]; // dBFS heard, by CheckHz; > 0 = not heard at the loudest
            public int volume, volumeMax;
        }

        bool _checking, _checkFromWelcome;
        AudioSource _checkSrc;
        readonly Dictionary<int, AudioClip> _checkClips = new();
        HearResult _check;
        int _checkEar, _checkPitch, _checkTones, _savedVolume = -1;
        float _checkLevel, _toneAt, _nextToneAt, _heardAt;
        bool _toneOn, _heardThis, _missedOnce;
        int _falsePresses;

        static bool HearStage(Stage s) => s is Stage.HearIntro or Stage.HearRun or Stage.HearResult;

        void StartHearCheck(bool fromWelcome)
        {
            _checkFromWelcome = fromWelcome;
            Enter(Stage.HearIntro);
        }

        void EnterHearCheck(Stage stage)
        {
            switch (stage)
            {
                case Stage.HearIntro:
                    Say(1, 2, "Check your hearing",
                        "Find a quiet spot. Beeps play in one ear, then the other, getting quieter. " +
                        "Press <b>I hear it</b> each time, however faint. About 3 minutes.\n" +
                        "<color=#FFFFFF80>A quick check, not a medical test.</color>", Glyphs.Hearing);
                    SetButtons(("Not now", BackHearCheck, false), ("Start", BeginHearCheck, true));
                    break;
                case Stage.HearRun:
                    Say(2, 2, "Listen", CheckBody(), Glyphs.Hearing);
                    SetButtons(("Stop", BackHearCheck, false), ("I hear it", HeardTone, true));
                    break;
                case Stage.HearResult:
                    Say(0, 0, "Your hearing check", ResultBody(_check, LastCheck(exceptToday: true)), Glyphs.Check, done: true);
                    if (_checkFromWelcome) SetButtons(("Again", BeginHearCheck, false), ("Next", () => PickHearing(ResultLevel(_check)), true));
                    else SetButtons(("Again", BeginHearCheck, false), ("Done", BackHearCheck, true));
                    break;
            }
        }

        void BackHearCheck()
        {
            StopHearCheck();
            if (_checkFromWelcome) Enter(_stage == Stage.HearResult ? Stage.Recommend : Stage.Hearing);
            else Enter(Stage.SoundList);
        }

        void BeginHearCheck()
        {
            if (_checkSrc == null)
            {
                _checkSrc = new GameObject("Hearing check").AddComponent<AudioSource>();
                _checkSrc.transform.SetParent(transform, false);
                _checkSrc.playOnAwake = false;
                _checkSrc.spatialBlend = 0f; // straight to one ear
                _checkSrc.bypassEffects = _checkSrc.bypassListenerEffects = _checkSrc.bypassReverbZones = true;
            }
            _check = new HearResult { date = DateTime.Now.ToString("yyyy-MM-dd") };
            HoldVolume();
            _checking = true;
            _checkEar = 0;
            _falsePresses = 0;
            StartPitch(0);
            Enter(Stage.HearRun);
            Debug.Log($"[Captions] Hearing check started (volume {_check.volume}/{_check.volumeMax})");
        }

        void StopHearCheck()
        {
            if (!_checking) return;
            _checking = false;
            if (_checkSrc != null) _checkSrc.Stop();
            PutVolumeBack();
        }

        void StartPitch(int i)
        {
            _checkPitch = i;
            _checkLevel = CheckStart;
            _checkTones = 0;
            _missedOnce = false;
            _toneOn = false;
            _nextToneAt = Time.time + 1.2f;
        }

        // Beeps: three 220 ms pips with soft edges, so they're easy to tell from the room.
        AudioClip CheckClip(int hz)
        {
            if (_checkClips.TryGetValue(hz, out var c)) return c;
            const int rate = 48000;
            var n = (int)(rate * 0.95f);
            var data = new float[n];
            for (var s = 0; s < n; s++)
            {
                var t = (float)s / rate;
                var k = Mathf.Repeat(t, 0.36f);
                var env = k > 0.22f ? 0f : Mathf.Min(1f, Mathf.Min(k, 0.22f - k) / 0.02f);
                data[s] = Mathf.Sin(2f * Mathf.PI * hz * t) * env;
            }
            c = AudioClip.Create($"Check {hz} Hz", n, 1, rate, false);
            c.SetData(data, 0);
            return _checkClips[hz] = c;
        }

        void HeardTone()
        {
            if (!_checking) return;
            if (_toneOn && Time.time - _toneAt < ToneSeconds + AnswerSeconds) { _heardThis = true; _heardAt = Time.time; }
            else _falsePresses++;
            UiFeedback.Play(UiSound.Tick, _setup.Root.position);
        }

        void UpdateHearCheck()
        {
            if (!_checking || _stage != Stage.HearRun || !_setup.Open) return;
            if (!_toneOn)
            {
                if (Time.time < _nextToneAt) return;
                _toneOn = true;
                _heardThis = false;
                _toneAt = Time.time;
                _checkTones++;
                _checkSrc.panStereo = _checkEar == 0 ? 1f : -1f;
                _checkSrc.volume = 1f;
                _checkSrc.PlayOneShot(CheckClip(CheckHz[_checkPitch]), Mathf.Pow(10f, _checkLevel / 20f));
                _bodyText.T.text = CheckBody();
                return;
            }
            // Answered (or the time to answer ran out): next level.
            if (!_heardThis && Time.time - _toneAt < ToneSeconds + AnswerSeconds) return;
            _toneOn = false;
            _nextToneAt = Time.time + UnityEngine.Random.Range(1.0f, 2.4f); // never on a beat you can predict
            float? done = null;
            if (_heardThis)
            {
                if (_missedOnce || _checkLevel <= CheckQuietest) done = _checkLevel; // heard on the way back up: that's it
                else _checkLevel = Mathf.Max(CheckQuietest, _checkLevel - 10f);
            }
            else
            {
                _missedOnce = true;
                if (_checkLevel >= CheckLoudest) done = 1f; // not heard even at the loudest
                else _checkLevel = Mathf.Min(CheckLoudest, _checkLevel + 5f);
            }
            if (done == null && _checkTones >= MaxTonesPerPitch) done = _checkLevel + (_heardThis ? 0f : 5f);
            if (done == null) return;

            (_checkEar == 0 ? _check.right : _check.left)[_checkPitch] = done.Value;
            Debug.Log($"[Captions] Hearing check: {(_checkEar == 0 ? "right" : "left")} {CheckHz[_checkPitch]} Hz → " +
                      (done.Value > 0f ? "not heard" : $"{done.Value:F0} dBFS") + $" ({_checkTones} beeps)");
            if (_checkPitch + 1 < CheckHz.Length) { StartPitch(_checkPitch + 1); return; }
            if (_checkEar == 0) { _checkEar = 1; StartPitch(0); return; }
            FinishHearCheck();
        }

        void FinishHearCheck()
        {
            StopHearCheck();
            try { PlayerPrefs.SetString(HearCheckKey, JsonUtility.ToJson(_check)); PlayerPrefs.Save(); }
            catch (Exception e) { Debug.LogWarning($"[Captions] Hearing check not saved: {e.Message}"); }
            Debug.Log($"[Captions] Hearing check done: right {string.Join(" ", _check.right)}, left {string.Join(" ", _check.left)}, " +
                      $"{_falsePresses} presses without a beep");
            UiFeedback.Play(UiSound.Confirm, _setup.Root.position);
            Enter(Stage.HearResult);
        }

        string CheckBody()
        {
            var done = _checkEar * CheckHz.Length + _checkPitch;
            var total = 2 * CheckHz.Length;
            var ear = _checkEar == 0 ? "Right ear" : "Left ear";
            return $"{ear} · press <b>I hear it</b> at each beep.\n" +
                   $"<color=#FFFFFF>{new string('•', done)}</color><color=#FFFFFF40>{new string('•', total - done)}</color>";
        }

        // ---------------- the result ----------------

        HearResult LastCheck(bool exceptToday)
        {
            try
            {
                var s = PlayerPrefs.GetString(HearCheckKey, "");
                if (s.Length == 0) return null;
                var r = JsonUtility.FromJson<HearResult>(s);
                return exceptToday && r.date == DateTime.Now.ToString("yyyy-MM-dd") ? null : r;
            }
            catch { return null; }
        }

        // How much harder each pitch was than the easiest one you heard (in either ear); 99 = not heard.
        static float[,] Harder(HearResult r)
        {
            var best = float.MaxValue;
            foreach (var v in r.right) if (v <= 0f) best = Mathf.Min(best, v);
            foreach (var v in r.left) if (v <= 0f) best = Mathf.Min(best, v);
            var h = new float[2, 5];
            for (var i = 0; i < 5; i++)
            {
                h[0, i] = r.right[i] > 0f || best == float.MaxValue ? 99f : r.right[i] - best;
                h[1, i] = r.left[i] > 0f || best == float.MaxValue ? 99f : r.left[i] - best;
            }
            return h;
        }

        static string Dot(float harder) =>
            "<size=180%>" + (harder >= 99f ? "<color=#FF453A50>•</color>" : harder >= 30f ? "<color=#FF453A>•</color>"
            : harder >= 15f ? "<color=#FF9F0A>•</color>" : "<color=#30D158>•</color>") + "</size>";

        static string ResultBody(HearResult r, HearResult before)
        {
            var h = Harder(r);
            var sb = new System.Text.StringBuilder();
            sb.Append("<color=#FFFFFF80><mspace=0.62em>       low  ·  ·  · high</mspace></color>\n");
            for (var e = 0; e < 2; e++)
            {
                sb.Append(e == 0 ? "<mspace=0.62em>Right  </mspace>" : "<mspace=0.62em>Left   </mspace>");
                foreach (var i in ShowOrder) sb.Append(Dot(h[e, i])).Append("  ");
                sb.Append('\n');
            }
            sb.Append(Summary(h));
            if (before != null)
            {
                var hb = Harder(before);
                var worse = 0;
                for (var e = 0; e < 2; e++) for (var i = 0; i < 5; i++) if (h[e, i] < 99f && hb[e, i] < 99f && h[e, i] - hb[e, i] >= 15f) worse++;
                sb.Append(worse > 0 ? $" Some pitches are harder than on {before.date}." : $" About the same as on {before.date}.");
            }
            sb.Append("\n<color=#FFFFFF80>Not a medical test: if anything worries you, see a hearing professional.</color>");
            return sb.ToString();
        }

        static string Summary(float[,] h)
        {
            bool Any(int e, int from, int to, float min) { for (var i = from; i <= to; i++) if (h[e, i] >= min) return true; return false; }
            var high = new List<string>();
            var low = new List<string>();
            for (var e = 0; e < 2; e++)
            {
                if (Any(e, 2, 3, 15f)) high.Add(e == 0 ? "right" : "left");  // 4 and 8 kHz
                if (Any(e, 0, 1, 15f) || h[e, 4] >= 15f) low.Add(e == 0 ? "right" : "left");
            }
            if (high.Count == 0 && low.Count == 0) return "Every pitch came through about the same in both ears.";
            var parts = new List<string>();
            if (high.Count > 0) parts.Add($"high pitches were harder in your {string.Join(" and ", high)} ear{(high.Count > 1 ? "s" : "")}");
            if (low.Count > 0) parts.Add($"low and middle pitches were harder in your {string.Join(" and ", low)} ear{(low.Count > 1 ? "s" : "")}");
            var s = char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1) + (parts.Count > 1 ? "; " + parts[1] : "") + ".";
            if (high.Count > 0) s += " Beeps, timers and alarms are high: sound alerts will show them.";
            return s;
        }

        // For the welcome: anything clearly harder → hard of hearing; nothing heard at all → deaf.
        static int ResultLevel(HearResult r)
        {
            var h = Harder(r);
            var missed = 0;
            var harder = false;
            for (var e = 0; e < 2; e++) for (var i = 0; i < 5; i++) { if (h[e, i] >= 99f) missed++; else if (h[e, i] >= 15f) harder = true; }
            return missed == 10 ? 0 : harder || missed > 0 ? 1 : 2;
        }

        // ---------------- the media volume, held while checking ----------------

        void HoldVolume()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var audio = activity.Call<AndroidJavaObject>("getSystemService", "audio");
                const int music = 3;
                var max = audio.Call<int>("getStreamMaxVolume", music);
                _savedVolume = audio.Call<int>("getStreamVolume", music);
                var v = Mathf.RoundToInt(max * CheckVolume);
                audio.Call("setStreamVolume", music, v, 0);
                _check.volume = v;
                _check.volumeMax = max;
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Hearing check volume: {e.Message}"); _savedVolume = -1; }
#endif
        }

        void PutVolumeBack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_savedVolume < 0) return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var audio = activity.Call<AndroidJavaObject>("getSystemService", "audio");
                audio.Call("setStreamVolume", 3, _savedVolume, 0);
            }
            catch (Exception e) { Debug.LogWarning($"[Captions] Hearing check volume: {e.Message}"); }
#endif
            _savedVolume = -1;
        }
    }
}
