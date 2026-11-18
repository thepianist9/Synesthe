using System.Collections.Generic;
using UnityEngine;
using Week1Bench;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Where captions listen from: the Quest's own mic in three modes, plus every external input Android reports
    /// (USB-C mics, wireless clip-on receivers, Bluetooth headsets). Plugging in a new mic offers to switch to it
    /// (<see cref="Offered"/>); if the mic in use is unplugged it falls back to the Quest mic. The choice is remembered.
    /// The Quest's mics are tuned for the wearer's voice (other people arrive ~40 dB quieter, Week 1 Test 6), so a
    /// mic clipped on the person you talk with is the biggest single improvement to captions.
    /// No UI of its own: the Settings panel (ModeMenu.Settings.cs) shows and changes it.
    /// </summary>
    public class MicSettings : MonoBehaviour
    {
        public MicStream mic;
        public CaptionDirector director;

        [Tooltip("Boost for the Quest's own mic (dB): other people's speech arrives very quietly.")]
        public float builtInGainDb = 50f;
        [Tooltip("Boost for an external mic near the speaker (dB): its speech is already loud.")]
        public float externalGainDb = 15f;

        const string PrefKey = "captions.mic";

        public sealed class Option
        {
            public string Key, Title, Detail, Glyph;
            public int Source, DeviceId;
            public float GainDb;
            public bool External;
        }

        /// <summary>The list of inputs changed (a mic was plugged in or out).</summary>
        public event System.Action Changed;
        /// <summary>A new mic was plugged in: offer to switch to it.</summary>
        public event System.Action<Option> Offered;
        /// <summary>Something to tell the user (a mic couldn't start, or was unplugged).</summary>
        public event System.Action<string> Message;

        readonly List<Option> _options = new();
        HashSet<string> _knownDevices = new();
        string _savedKey;
        float _nextScan;

        public IReadOnlyList<Option> Options => _options;
        public Option Current { get; private set; }

        /// <summary>Input level in dBFS (after the boost).</summary>
        public float LevelDb => mic.Level > 0 ? 20f * Mathf.Log10(mic.Level) : -120f;

        /// <summary>One line on what the mic is doing: boost, routed device, error.</summary>
        public string StatusLine
        {
            get
            {
                if (mic.Error != null) return $"<color=#FF8A80>{mic.Error}</color>";
                if (!mic.Running) return "Microphone off";
                var routed = mic.RoutedDevice;
                return $"Boost +{mic.CurrentGainDb:F0} dB" + (string.IsNullOrEmpty(routed) ? "" : $" · recording from {routed}");
            }
        }

        // Runs before CaptionDirector.Start, so the remembered mic is used from the first word.
        void Awake() => Init();

        /// <summary>Load the remembered choice and list the inputs (Awake; the Editor preview calls it directly).</summary>
        public void Init()
        {
            _savedKey = PlayerPrefs.GetString(PrefKey, "builtin:mic");
            RebuildOptions();
            Configure(_options.Find(o => o.Key == _savedKey) ?? _options[0]);
            foreach (var d in MicStream.ListInputs()) _knownDevices.Add(d.Key);
        }

        void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 2f;
            ScanDevices();
        }

        void RebuildOptions()
        {
            _options.Clear();
            _options.Add(new Option
            {
                Key = "builtin:mic", Title = "People around me", Detail = "Quest mic, boosted. Best built-in choice.",
                Glyph = Glyphs.Hearing, Source = MicStream.SourceMic, GainDb = builtInGainDb
            });
            _options.Add(new Option
            {
                Key = "builtin:raw", Title = "Unprocessed", Detail = "Quest mic, no system processing, boosted.",
                Glyph = Glyphs.Mic, Source = MicStream.SourceUnprocessed, GainDb = builtInGainDb
            });
            // (No voice-chat option: it mostly mutes other people, so captions and signing go quiet. A choice of it
            // saved by an earlier version falls back to the first option.)
            foreach (var d in MicStream.ListInputs())
            {
                if (d.Kind == MicStream.InputKind.BuiltIn) continue;
                var bt = d.Kind == MicStream.InputKind.Bluetooth;
                _options.Add(new Option
                {
                    Key = "device:" + d.Key,
                    Title = string.IsNullOrEmpty(d.Name) ? KindLabel(d.Kind) : d.Name,
                    Detail = bt ? "Bluetooth mics may not work on Quest." : $"{KindLabel(d.Kind)} · clip it on the person you talk with.",
                    Glyph = d.Kind switch
                    {
                        MicStream.InputKind.Usb => Glyphs.Usb, MicStream.InputKind.Bluetooth => Glyphs.Bluetooth,
                        MicStream.InputKind.Wired => Glyphs.HeadsetMic, _ => Glyphs.ExternalMic,
                    },
                    // Bluetooth headset mics need the communication route; everything else records as MIC.
                    Source = bt ? 7 : MicStream.SourceMic, DeviceId = d.Id, GainDb = externalGainDb, External = true
                });
            }
        }

        static string KindLabel(MicStream.InputKind k) => k switch
        {
            MicStream.InputKind.Usb => "USB mic",
            MicStream.InputKind.Bluetooth => "Bluetooth",
            MicStream.InputKind.Wired => "Wired mic",
            _ => "Microphone"
        };

        void Configure(Option o)
        {
            mic.androidSource = o.Source;
            mic.preferredDeviceId = o.DeviceId;
            mic.autoGain = o.Source != MicStream.SourceUnity;
            mic.maxAutoGainDb = o.GainDb;
            Current = o;
        }

        /// <summary>Listen with <paramref name="o"/> from now on. False (and back to the previous one) if it won't start.</summary>
        public bool Select(Option o)
        {
            var previous = Current;
            var wasRunning = mic.Running;
            mic.StopCapture();
            Configure(o);
            if (wasRunning)
            {
                // (captions keep their SpeechStack; only the audio source changes)
                mic.StartCapture();
                if (!mic.Running)
                {
                    var error = mic.Error;
                    Configure(previous);
                    mic.StartCapture();
                    Message?.Invoke($"Couldn't use {o.Title}: {error}");
                    return false;
                }
            }
            PlayerPrefs.SetString(PrefKey, o.Key);
            PlayerPrefs.Save();
            _savedKey = o.Key;
            Debug.Log($"[Captions] Mic → {o.Title} · {mic.Status}");
            return true;
        }

        // New or removed inputs: refresh the list, offer a newly plugged mic, fall back if ours disappeared.
        void ScanDevices()
        {
            var now = new HashSet<string>();
            MicStream.InputDevice? added = null;
            foreach (var d in MicStream.ListInputs())
            {
                now.Add(d.Key);
                if (d.Kind != MicStream.InputKind.BuiltIn && !_knownDevices.Contains(d.Key)) added = d;
            }
            if (now.SetEquals(_knownDevices)) return;
            _knownDevices = now;
            RebuildOptions();

            if (Current.External && !now.Contains(Current.Key.Substring("device:".Length)))
            {
                Select(_options[0]);
                Message?.Invoke("The external mic was unplugged — back to the Quest mic.");
            }
            else if (added.HasValue && _options.Find(o => o.Key == "device:" + added.Value.Key) is { } opt)
            {
                if (opt.Key == _savedKey) Select(opt); // the mic we used last time is back
                else Offered?.Invoke(opt);
            }
            // (Current still points at the old Option object: re-find it in the new list)
            Current = _options.Find(o => o.Key == Current.Key) ?? Current;
            Changed?.Invoke();
        }
    }
}
