using UnityEngine;

namespace MetaStarter.Captions
{
    public enum UiSound { Hover, Press, Confirm, Back, Pick, Drop, Tick }

    /// <summary>
    /// Sound and touch for the menus: every hover, press and change answers with a short, quiet sound from where it
    /// happened (made here, no audio files) and, with controllers, a tap of vibration. Hover and Tick are barely there;
    /// Press is a soft click; Confirm (a choice that moves you on) rises, Back falls; Pick and Drop for carrying things.
    /// </summary>
    public class UiFeedback : MonoBehaviour
    {
        static UiFeedback _instance;
        AudioClip[] _clips;
        AudioSource[] _sources;
        int _next;
        float _buzzUntil = -1f;
        float _lastHover;

        static UiFeedback Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("UI feedback");
                DontDestroyOnLoad(go);
                return _instance = go.AddComponent<UiFeedback>();
            }
        }

        public static void Play(UiSound sound, Vector3 at)
        {
            if (!Application.isPlaying) return;
            Instance.PlayAt(sound, at);
        }

        void Awake()
        {
            const int voices = 4;
            _sources = new AudioSource[voices];
            for (var i = 0; i < voices; i++)
            {
                var src = new GameObject("Voice").AddComponent<AudioSource>();
                src.transform.SetParent(transform, false);
                src.playOnAwake = false;
                src.spatialBlend = 0.6f;    // from where it happened, but never faint
                src.minDistance = 0.6f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.maxDistance = 6f;
                _sources[i] = src;
            }
            _clips = new[]
            {
                Tone("Hover", 0.012f, f0: 3200, f1: 3200, decay: 400, gain: 0.5f),
                Click(),
                Chime("Confirm", 880, 1320),
                Chime("Back", 990, 660),
                Tone("Pick", 0.09f, f0: 420, f1: 720, decay: 30, gain: 0.8f),
                Tone("Drop", 0.11f, f0: 720, f1: 380, decay: 30, gain: 0.8f),
                Tone("Tick", 0.006f, f0: 4200, f1: 4200, decay: 700, gain: 0.5f),
            };
        }

        void PlayAt(UiSound sound, Vector3 at)
        {
            if (sound == UiSound.Hover)
            {
                if (Time.unscaledTime - _lastHover < 0.08f) return; // sweeping across buttons: not a rattle
                _lastHover = Time.unscaledTime;
            }
            var volume = sound switch
            {
                UiSound.Hover => 0.08f, UiSound.Tick => 0.07f, UiSound.Press => 0.35f,
                UiSound.Confirm => 0.3f, UiSound.Back => 0.25f, _ => 0.3f,
            };
            var src = _sources[_next];
            _next = (_next + 1) % _sources.Length;
            src.transform.position = at;
            src.PlayOneShot(_clips[(int)sound], volume);
            Buzz(sound);
        }

        // Controllers only (hands have nothing to vibrate): a short tap, stronger for presses than for hovers.
        void Buzz(UiSound sound)
        {
            var active = OVRInput.GetActiveController();
            if ((active & OVRInput.Controller.Touch) == 0) return;
            var (amp, secs) = sound switch
            {
                UiSound.Hover or UiSound.Tick => (0.12f, 0.02f),
                UiSound.Confirm or UiSound.Drop => (0.5f, 0.08f),
                _ => (0.35f, 0.04f),
            };
            OVRInput.SetControllerVibration(1f, amp, active & OVRInput.Controller.Touch);
            _buzzUntil = Time.unscaledTime + secs;
        }

        void Update()
        {
            if (_buzzUntil < 0f || Time.unscaledTime < _buzzUntil) return;
            _buzzUntil = -1f;
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.Touch);
        }

        const int Rate = 44100;

        // A sine sweeping from f0 to f1 that dies away (decay: per second).
        static AudioClip Tone(string name, float seconds, float f0, float f1, float decay, float gain)
        {
            var n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            var phase = 0f;
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / Rate;
                phase += 2 * Mathf.PI * Mathf.Lerp(f0, f1, t / seconds) / Rate;
                var attack = Mathf.Clamp01(t / 0.002f);
                data[i] = Mathf.Sin(phase) * Mathf.Exp(-t * decay) * attack * gain;
            }
            return Clip(name, data);
        }

        // A soft click: a short knock of low sine with a bright edge on top.
        static AudioClip Click()
        {
            var n = Mathf.CeilToInt(0.035f * Rate);
            var data = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / Rate;
                var attack = Mathf.Clamp01(t / 0.001f);
                data[i] = attack * (0.7f * Mathf.Sin(2 * Mathf.PI * 1400 * t) * Mathf.Exp(-t * 140)
                                    + 0.3f * Mathf.Sin(2 * Mathf.PI * 3600 * t) * Mathf.Exp(-t * 500));
            }
            return Clip("Press", data);
        }

        // Two notes, the second 70 ms after the first.
        static AudioClip Chime(string name, float a, float b)
        {
            var n = Mathf.CeilToInt(0.22f * Rate);
            var data = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / Rate;
                var v = Note(a, t);
                if (t > 0.07f) v += Note(b, t - 0.07f);
                data[i] = v * 0.45f;
            }
            return Clip(name, data);

            static float Note(float f, float t) =>
                Mathf.Clamp01(t / 0.003f) * Mathf.Exp(-t * 22) * (Mathf.Sin(2 * Mathf.PI * f * t) + 0.25f * Mathf.Sin(4 * Mathf.PI * f * t));
        }

        static AudioClip Clip(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
