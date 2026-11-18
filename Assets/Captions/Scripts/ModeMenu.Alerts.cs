using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MetaStarter.Captions
{
    /// <summary>
    /// Alerts (<see cref="SoundEvents.Alert"/>), always on: in every mode, in the menus, while paused. How loud they are
    /// follows how urgent:
    ///   Danger (smoke or fire alarm, an alarm): the conversation stops (captions pause) and a red card comes up in
    ///     front of you, its symbol pulsing with ripples; the edges of the view glow red, slowly (1 Hz: not a strobe),
    ///     and controllers buzz with each pulse. It stays while the sound goes on, and until you press "I'm safe"
    ///     (pinch, trigger or a look); then the conversation carries on. Still sounding 30 s later: it comes back.
    ///   Warning (siren, glass breaking, a scream, a baby crying, a car alarm): an amber card, the edges glow amber
    ///     twice; captions carry on. It goes 12 s after the sound stops, or with OK.
    ///   Info (doorbell, knocking, a phone, beeping, a dog): a small pill above the middle of the view with a ripple
    ///     from its symbol, and the edges of the view glow softly twice in its colour; gone 6 s after the sound stops.
    /// The cards follow your head lazily: turn more than 18° away and they come back in front of you.
    /// </summary>
    public partial class ModeMenu
    {
        const float AlertW = 0.36f, AlertH = 0.19f, ToastW = 0.25f, ToastH = 0.056f;
        const float SnoozeSeconds = 30f, WarningLinger = 12f, InfoLinger = 6f, FollowAngle = 18f;
        static readonly Color SysBlue = Hex(0x0A84FF);

        Panel _alert, _toast;
        Glass _alertCircle, _alertRipple, _alertButton, _toastCircle, _toastRipple;
        Label _alertIcon, _alertTitle, _alertStatus, _alertAdvice, _alertButtonText, _toastIcon, _toastTitle, _toastStatus;
        SoundEvents.Alert _shown, _toastShown;
        bool _alertUp, _toastUp, _alertPaused;
        float _alertLast, _alertSince, _toastLast, _alertTextAt, _toastTextAt;
        readonly System.Collections.Generic.Dictionary<string, float> _snoozed = new(); // kind → dismissed at
        Transform _vignette;
        Material _vignetteMat;
        float _vignetteAlpha, _toastSince = -99f;
        bool _alertBuzz;
        int _testAlert;

        void BuildAlerts()
        {
            // The card (danger and warning).
            var root = new GameObject("Alert").transform;
            root.SetParent(transform, false);
            _building = _alert = new Panel { Root = root, Dist = 0.55f, Drop = 0.05f };
            _group = null;
            AddGlass(root, Vector2.zero, new Vector2(AlertW, AlertH), 0.04f, PanelTint, 0, bevel: 0.007f).Dim = PanelDim;
            AddBacking(root, new Vector2(AlertW, AlertH));
            float left = -AlertW / 2 + 0.022f, right = AlertW / 2 - 0.022f, top = AlertH / 2;
            var c = new Vector2(left + 0.04f, top - 0.062f);
            _alertRipple = AddGlass(root, c, Vector2.one * 0.08f, 0.04f, WithAlpha(SysRed, 0f), 1, bevel: 0.002f, round: true);
            _alertCircle = AddGlass(root, c, Vector2.one * 0.08f, 0.04f, SysRed, 2, bevel: 0.004f, round: true);
            _alertIcon = AddIcon(root, Glyphs.Hearing, c, 0.042f, Primary, 3);
            var tx = left + 0.096f;
            _alertTitle = AddLabel(root, "", new Vector2(tx, top - 0.042f), 0.028f, Primary, TextAlignmentOptions.MidlineLeft, right - tx);
            _alertTitle.T.enableAutoSizing = true; // "Someone screamed" shrinks to one line
            _alertTitle.T.fontSizeMax = _alertTitle.T.fontSize;
            _alertTitle.T.fontSizeMin = _alertTitle.T.fontSize * 0.65f;
            _alertStatus = AddLabel(root, "", new Vector2(tx, top - 0.074f), 0.0135f, Secondary, TextAlignmentOptions.MidlineLeft, right - tx);
            _alertAdvice = AddLabel(root, "", new Vector2(tx, top - 0.098f), 0.0145f, Primary, TextAlignmentOptions.TopLeft, right - tx);
            var bw = 0.11f;
            var bp = new Vector2(right - bw / 2, -AlertH / 2 + 0.018f + LargeBtn / 2);
            _alertButton = AddGlass(root, bp, new Vector2(bw, LargeBtn), LargeBtn / 2, Filled, 2, round: true);
            _alertButtonText = AddLabel(root, "I'm safe", bp, 0.016f, TileInk, TextAlignmentOptions.Midline, bw, 3);
            AddHit(root, bp, new Vector2(bw, LargeBtn) + Vector2.one * 0.012f, DismissAlert, _alertButton);
            root.gameObject.SetActive(false);

            // The pill (info).
            var troot = new GameObject("Alert pill").transform;
            troot.SetParent(transform, false);
            _building = _toast = new Panel { Root = troot, Dist = 0.75f, Drop = -0.16f };
            AddGlass(troot, Vector2.zero, new Vector2(ToastW, ToastH), ToastH / 2, PanelTint, 0, bevel: 0.005f, round: true).Dim = PanelDim;
            var tc = new Vector2(-ToastW / 2 + ToastH / 2, 0);
            _toastRipple = AddGlass(troot, tc, Vector2.one * 0.042f, 0.021f, WithAlpha(SysBlue, 0f), 1, bevel: 0.002f, round: true);
            _toastCircle = AddGlass(troot, tc, Vector2.one * 0.042f, 0.021f, SysBlue, 2, bevel: 0.003f, round: true);
            _toastIcon = AddIcon(troot, Glyphs.Hearing, tc, 0.024f, Primary, 3);
            var ttx = tc.x + 0.033f;
            _toastTitle = AddLabel(troot, "", new Vector2(ttx, 0.008f), 0.018f, Primary, TextAlignmentOptions.MidlineLeft, ToastW / 2 - ttx);
            _toastStatus = AddLabel(troot, "", new Vector2(ttx, -0.012f), 0.011f, Secondary, TextAlignmentOptions.MidlineLeft, ToastW / 2 - ttx);
            troot.gameObject.SetActive(false);

            if (Application.isPlaying) BuildVignette();
            director.AlertHeard += OnAlert;
        }

        // A soft frame round the whole view, clear in the middle, held just in front of the eyes.
        void BuildVignette()
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Alert glow" };
            var px = new Color32[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    var r = Mathf.Sqrt(u * u + v * v * 1.15f);
                    var a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1.05f, r));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _vignetteMat = new Material(panelMaterial) { name = "Alert glow", renderQueue = 3500 };
            _vignetteMat.SetTexture(BaseMapId, tex);
            var q = Quad("Alert glow", head, Vector2.zero, 0);
            q.localPosition = new Vector3(0, 0, 0.3f);
            q.localRotation = Quaternion.identity;
            q.localScale = new Vector3(1.1f, 0.95f, 1f);
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _vignetteMat;
            mr.sortingOrder = 100;
            _vignette = q;
            q.gameObject.SetActive(false);
        }

        void OnAlert(SoundEvents.Alert a)
        {
            if (_snoozed.TryGetValue(a.Kind, out var at) && Time.time - at < SnoozeSeconds) return;
            if (_checking && a.Level != SoundEvents.Priority.Danger) return; // the check's own beeps aren't news
            if (a.Kind.StartsWith(SoundEvents.TaughtPrefix, System.StringComparison.Ordinal)) FireSound(a);
            if (a.Level == SoundEvents.Priority.Info || _alertUp && _shown.Level > a.Level)
            {
                ShowToast(a);
                return;
            }
            var fresh = !_alertUp || _shown.Kind != a.Kind;
            _shown = a;
            _alertLast = Time.time;
            if (!fresh) return;
            _alertSince = Time.time;
            _alertTextAt = 0f;
            var danger = a.Level == SoundEvents.Priority.Danger;
            var color = danger ? SysRed : SysOrange;
            _alertCircle.Tint = color;
            _alertIcon.T.text = a.Glyph;
            _alertTitle.T.text = a.Name;
            _alertAdvice.T.text = a.Advice;
            _alertButtonText.T.text = danger ? "I'm safe" : "OK";
            if (!_alertUp)
            {
                Place(_alert, AlertFront(_alert));
                _alert.Returning = false;
                _alert.Root.gameObject.SetActive(true);
            }
            _alert.Open = true;
            _alert.Swap = 0f;
            _alertUp = true;
            if (danger)
            {
                // Stop the mode: captions pause, menus that were in the way close (the start menu and a setup stay).
                if (director.live && !_paused)
                {
                    _paused = true;
                    director.live = false;
                    director.StartFresh();
                    _alertPaused = true;
                }
                _quick.Open = false;
                _ask.Open = false;
                _dock.Open = false;
            }
            UiFeedback.Play(UiSound.Confirm, _alert.Root.position);
            Debug.Log($"[Captions] Alert shown: {a.Name} ({a.Level}){(_alertPaused ? ", conversation paused" : "")}");
        }

        [Tooltip("On: notifications (a taught sound, your name, a doorbell…) come up as a caption bubble that glows twice in " +
                 "their colour, instead of the pill and the glow round the edge of the view.")]
        public bool bubbleNotices = true;

        void ShowToast(SoundEvents.Alert a)
        {
            if (bubbleNotices)
            {
                var tint = a.Level == SoundEvents.Priority.Info ? SysBlue : a.Level == SoundEvents.Priority.Warning ? SysOrange : SysRed;
                var isNew = !_toastUp || _toastShown.Kind != a.Kind;
                director.Notify(a.Kind == "name" ? "Your name" : "Heard", a.Name, tint, InfoLinger);
                _toastShown = a;
                _toastUp = true;
                _toastLast = Time.time;
                if (isNew) UiFeedback.Play(UiSound.Pick, head.position + head.forward * 0.9f);
                return;
            }
            var fresh = !_toastUp || _toastShown.Kind != a.Kind;
            _toastShown = a;
            _toastLast = Time.time;
            if (!fresh) return;
            _toastTextAt = 0f;
            _toastSince = Time.time; // the edge of the view glows twice
            var color = a.Level == SoundEvents.Priority.Info ? SysBlue : a.Level == SoundEvents.Priority.Warning ? SysOrange : SysRed;
            _toastCircle.Tint = color;
            _toastIcon.T.text = a.Glyph;
            _toastTitle.T.text = a.Name;
            if (!_toastUp)
            {
                Place(_toast, AlertFront(_toast));
                _toast.Returning = false;
                _toast.Root.gameObject.SetActive(true);
            }
            _toast.Open = true;
            _toast.Swap = 0f;
            _toastUp = true;
            UiFeedback.Play(UiSound.Pick, _toast.Root.position);
        }

        void DismissAlert()
        {
            if (!_alertUp) return;
            _snoozed[_shown.Kind] = Time.time;
            HideAlert();
            UiFeedback.Play(UiSound.Back, _alert.Root.position);
        }

        void HideAlert()
        {
            _alertUp = false;
            _alert.Open = false;
            if (_alertPaused && _paused)
            {
                _paused = false;
                director.live = true; // the conversation carries on
            }
            _alertPaused = false;
            foreach (var p in new[] { _menu, _setup, _dock, _quick, _ask }) // what the card covered comes back
                if (p.Open) p.Root.gameObject.SetActive(true);
            Debug.Log($"[Captions] Alert closed: {_shown.Name}");
        }

        void UpdateAlerts()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.aKey.wasPressedThisFrame)
                director.TestAlert(new[] { "smoke", "siren", "doorbell" }[_testAlert++ % 3]); // editor: A = the next test alert
#endif
            var danger = _alertUp && _shown.Level == SoundEvents.Priority.Danger;
            if (_alertUp && !danger && Time.time - _alertLast > WarningLinger) HideAlert();
            if (!_alertUp) SetAlertBuzz(false);
            if (_toastUp && Time.time - _toastLast > InfoLinger) { _toastUp = false; _toast.Open = false; }

            foreach (var p in new[] { _alert, _toast })
            {
                p.Fade = Mathf.MoveTowards(p.Fade, p.Open ? 1f : 0f, Time.deltaTime * 5f);
                if (p.Fade <= 0f) { if (p.Root.gameObject.activeSelf) p.Root.gameObject.SetActive(false); continue; }
                FollowView(p);
                ApplyPanel(p);
            }

            if (_alert.Root.gameObject.activeSelf)
            {
                // The symbol beats once a second (danger) or every 1.6 s, a ring spreading out from it each beat.
                var period = danger ? 1f : 1.6f;
                var k = Mathf.Repeat((Time.time - _alertSince) / period, 1f);
                var beat = Mathf.Exp(-k * 7f);
                _alertCircle.Size = Vector2.one * 0.08f * (1f + 0.1f * beat);
                _alertCircle.Radius = _alertCircle.Size.x / 2;
                _alertRipple.Size = Vector2.one * 0.08f * (1f + 0.9f * k);
                _alertRipple.Radius = _alertRipple.Size.x / 2; // stays a circle as it spreads
                _alertRipple.Tint = WithAlpha(_alertCircle.Tint, 0.5f * (1f - k));
                if (Time.time >= _alertTextAt)
                {
                    _alertTextAt = Time.time + 1f;
                    var ago = Time.time - _alertLast;
                    var heard = ago < 2.5f ? (Time.time - _alertSince < 2.5f ? "Heard just now" : "Still sounding") : $"Heard {ago:F0} s ago";
                    _alertStatus.T.text = heard + (_alertPaused ? " · captions paused" : "");
                }
                SetAlertBuzz(danger && k < 0.2f); // controllers buzz with each beat
            }
            if (_toast.Root.gameObject.activeSelf)
            {
                var k = Mathf.Repeat(Time.time - _toastLast, 1.4f) / 1.4f;
                _toastRipple.Size = Vector2.one * 0.042f * (1f + 0.8f * k);
                _toastRipple.Radius = _toastRipple.Size.x / 2;
                _toastRipple.Tint = WithAlpha(_toastCircle.Tint, 0.45f * (1f - k));
                if (Time.time >= _toastTextAt)
                {
                    _toastTextAt = Time.time + 1f;
                    var ago = Time.time - _toastLast;
                    _toastStatus.T.text = (ago < 2.5f ? "Just now" : $"{ago:F0} s ago") + ToastWhere();
                }
            }

            // The glow round the view: danger pulses red as long as the card is up; a warning glows amber twice.
            var want = 0f;
            var glow = SysRed;
            if (_alertUp)
            {
                var t = Time.time - _alertSince;
                if (danger) want = 0.35f + 0.35f * (0.5f + 0.5f * Mathf.Cos(t * Mathf.PI * 2f));
                else { glow = SysOrange; want = t < 3.2f ? 0.45f * (0.5f - 0.5f * Mathf.Cos(t * Mathf.PI * 2f / 1.6f)) : 0f; }
            }
            else if (Time.time - _toastSince < 3.2f)
            {
                // A notification (a taught sound, your name): the whole edge glows softly twice in its colour, no direction.
                var t = Time.time - _toastSince;
                glow = _toastCircle.Tint;
                want = 0.35f * (0.5f - 0.5f * Mathf.Cos(t * Mathf.PI * 2f / 1.6f));
            }
            _vignetteAlpha = Mathf.MoveTowards(_vignetteAlpha, want, Time.deltaTime * 3f);
            if (_vignette != null)
            {
                var on = _vignetteAlpha > 0.005f;
                if (_vignette.gameObject.activeSelf != on) _vignette.gameObject.SetActive(on);
                if (on) _vignetteMat.SetColor(BaseColorId, WithAlpha(glow, _vignetteAlpha));
            }
        }

        // In front of where you look, up or down too (an alarm while you look at the floor still shows), within ±35°.
        Vector3 AlertFront(Panel p)
        {
            var pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg, -35f, 35f);
            var dir = Quaternion.LookRotation(Flat(head.forward)) * Quaternion.Euler(pitch, 0, 0) * Vector3.forward;
            return head.position + dir * p.Dist + Vector3.down * p.Drop;
        }

        void SetAlertBuzz(bool on)
        {
            if (on == _alertBuzz) return;
            _alertBuzz = on;
            OVRInput.SetControllerVibration(on ? 1f : 0f, on ? 0.8f : 0f, OVRInput.Controller.Touch);
        }

        // Stays where it is while you look at it (so you can point at its button), and comes back in front of you
        // once you turn more than FollowAngle away.
        void FollowView(Panel p)
        {
            var to = p.Root.position - head.position;
            if (Vector3.Angle(head.forward, to) > FollowAngle) p.Returning = true;
            if (!p.Returning) return;
            var goal = AlertFront(p);
            Place(p, Vector3.SmoothDamp(p.Root.position, goal, ref p.Vel, 0.18f));
            if ((p.Root.position - goal).sqrMagnitude < 1e-4f) p.Returning = false;
        }
    }
}
