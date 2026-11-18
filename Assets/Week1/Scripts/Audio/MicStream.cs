using System;
using System.Collections.Generic;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// One shared microphone stream for every audio consumer (VAD, speech-to-text, sound tagging).
    /// Two capture paths:
    ///   • Unity's Microphone (Android source VOICE_RECOGNITION — Quest tunes this for the wearer's voice)
    ///   • RawMic Android plugin with a chosen audio source (UNPROCESSED, MIC, CAMCORDER…) and 1–2 channels
    /// Output is always 16 kHz mono chunks with the wall-clock time they were read, so latency can be measured.
    /// </summary>
    public class MicStream : MonoBehaviour
    {
        public const int OutputRate = 16000;
        const int ClipSeconds = 2;

        // Android MediaRecorder.AudioSource values.
        public const int SourceUnity = -1, SourceMic = 1, SourceCamcorder = 5, SourceVoiceRecognition = 6, SourceUnprocessed = 9;
        public static string SourceName(int s) => s switch
        {
            SourceUnity => "Unity (VOICE_RECOGNITION)", SourceMic => "MIC", SourceCamcorder => "CAMCORDER",
            SourceVoiceRecognition => "VOICE_RECOGNITION", SourceUnprocessed => "UNPROCESSED", _ => $"source {s}"
        };

        [Tooltip("-1 = Unity Microphone; otherwise an Android AudioSource for the RawMic plugin (Quest only).")]
        public int androidSource = SourceMic; // Test 6 on Quest 3: MIC + auto gain hears other people; Unity's default does not

        [Tooltip("RawMic input device: 0 = system default (the Quest's own mics); otherwise an id from ListInputs() " +
                 "— a USB-C mic, wireless clip-on receiver or Bluetooth headset.")]
        public int preferredDeviceId = 0;

        [Tooltip("RawMic channel count: 1 = mono, 2 = stereo (L/R mics).")]
        public int channels = 2;
        [Tooltip("Fixed software gain applied after capture (ignored when autoGain is on).")]
        public float gainDb = 0f;
        [Tooltip("Gain for raw sources: a constant boost (maxAutoGainDb) for everything quiet, and a compressor that only " +
                 "pulls loud sound (the wearer's own voice) down to compressorThresholdDb. Measured on a Quest recording of a " +
                 "video at ~-80 dBFS: gain that moves between words (any AGC) lost most words; constant gain + compressor got " +
                 "nearly all of them, and the wearer's voice stayed word-perfect.")]
        public bool autoGain = true;
        [Tooltip("Constant boost for quiet sound (dB). Quest 3 raw MIC: speech at 1–2 m is ~-90…-70 dBFS.")]
        public float maxAutoGainDb = 50f;
        [Tooltip("Above this output level (dBFS) the compressor pulls the level down; below it the gain never changes.")]
        public float compressorThresholdDb = -22f;
        [Tooltip("Compressor release (s): how fast the gain returns after a loud sound.")]
        public float agcReleaseSeconds = 0.4f;
        const float AgcStartEnvelope = 0.0063f; // start at about +20 dB instead of the maximum
        float _agcEnvelope = AgcStartEnvelope;
        public float CurrentGainDb { get; private set; }
        /// <summary>RMS before gain (after the DC filter), raw sources only.</summary>
        public float RawLevel { get; private set; }
        float _hpX, _hpY;

        // ---- Debug recording (what the speech model hears) ----
        float _recordSeconds;
        readonly List<float> _recPre = new(), _recPost = new();
        float[] _preForRecord;
        /// <summary>Record the next N seconds to persistentDataPath/mic_pre.wav (after DC filter, before gain) and mic_post.wav (what ASR gets).</summary>
        public void RecordDebug(float seconds) { _recordSeconds = seconds; _recPre.Clear(); _recPost.Clear(); _recMulti.Clear(); }
        readonly List<float> _recMulti = new(); // raw interleaved channels while recording (multi-channel raw only)
        int _recMultiChannels;

        public event Action<float[], double> OnChunk; // samples @16 kHz, realtime of the last sample

        AudioClip _clip;
        string _device;
        AndroidJavaObject _raw;
        int _inputRate;
        int _readPos;
        float[] _readBuf = new float[0];
        double _resamplePhase;
        float _lastInput;
        readonly List<float> _decimCarry = new List<float>(4);

        // Testing without sound in the room: -e audiofile /sdcard/x.wav (16 kHz mono 16-bit) -e audioat <unix ms when
        // the file's 0 s plays>: the file is fed in real time instead of the mic, in step with a muted video.
        float[] _file;
        long _fileAt;
        int _filePos;

        public bool Running => _clip != null || _raw != null || _file != null;
        public int InputRate => _inputRate;
        public int Overruns { get; private set; }
        public long SamplesOut { get; private set; }
        public float Level { get; private set; }      // RMS of the latest chunk (after gain, mono)
        public float LeftLevel { get; private set; }  // RMS per channel before downmix (stereo raw only)
        public float RightLevel { get; private set; }
        public float ChannelCorrelation { get; private set; } // 1 = identical channels, ~0 = independent
        public string Error { get; private set; }
        public string Status { get; private set; } = "";

        /// <summary>The device Android is actually recording from (it can ignore preferredDeviceId), e.g. "11 Wireless GO".</summary>
        public string RoutedDevice => _raw != null ? _raw.Call<string>("getRoutedDevice") : "";

        // ---- input devices ----

        public enum InputKind { BuiltIn, Usb, Bluetooth, Wired, Other }

        public struct InputDevice
        {
            public int Id, Type;
            public string Name, Address;
            public InputKind Kind;
            /// <summary>Stable across plug/unplug (ids are not), for remembering the user's choice.</summary>
            public string Key => $"{Type}:{Name}";
        }

        /// <summary>Audio inputs Android currently offers. Empty in the Editor and on failure.</summary>
        public static List<InputDevice> ListInputs()
        {
            var list = new List<InputDevice>();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var cls = new AndroidJavaClass("com.metastarter.week1bench.RawMic");
                cls.CallStatic("init", activity);
                var text = cls.CallStatic<string>("listInputs", activity) ?? "";
                foreach (var line in text.Split('\n'))
                {
                    var f = line.Split('\t');
                    if (f.Length < 4 || !int.TryParse(f[0], out var id) || !int.TryParse(f[1], out var type)) continue;
                    var kind = KindOf(type);
                    if (kind == InputKind.Other) continue; // telephony, FM tuner, echo reference, remote submix…
                    list.Add(new InputDevice { Id = id, Type = type, Name = f[2], Address = f[3], Kind = kind });
                }
            }
            catch (Exception e) { Debug.LogWarning($"[Bench] Could not list audio inputs: {e.Message}"); }
#endif
            return list;
        }

        // AudioDeviceInfo.TYPE_* values.
        static InputKind KindOf(int type) => type switch
        {
            15 => InputKind.BuiltIn,                 // TYPE_BUILTIN_MIC
            11 or 12 or 22 => InputKind.Usb,         // USB_DEVICE, USB_ACCESSORY, USB_HEADSET
            7 or 26 => InputKind.Bluetooth,          // BLUETOOTH_SCO, BLE_HEADSET
            3 => InputKind.Wired,                    // WIRED_HEADSET
            _ => InputKind.Other
        };
        float _zeroSeconds;
        bool _warnedSilent;

        public void StartCapture()
        {
            if (Running) return;
            Error = null;
            _readPos = 0;
            _resamplePhase = 0;
            _decimCarry.Clear();
            _zeroSeconds = 0;
            _warnedSilent = false;
            Overruns = 0;
            SamplesOut = 0;
            LeftLevel = RightLevel = ChannelCorrelation = 0;
            _agcEnvelope = AgcStartEnvelope;
            _hpX = _hpY = 0;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (StartFile()) return;
            if (androidSource != SourceUnity)
            {
                try
                {
                    using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var cls = new AndroidJavaClass("com.metastarter.week1bench.RawMic"))
                        cls.CallStatic("init", activity);
                    _raw = new AndroidJavaObject("com.metastarter.week1bench.RawMic");
                    if (!_raw.Call<bool>("start", androidSource, channels, preferredDeviceId))
                    {
                        Error = "RawMic: " + _raw.Call<string>("getStatus");
                        _raw.Dispose();
                        _raw = null;
                        return;
                    }
                    _inputRate = _raw.Call<int>("getSampleRate");
                    Status = $"{SourceName(androidSource)} · " + _raw.Call<string>("getStatus");
                    Debug.Log($"[Bench] RawMic {Status}");
                }
                catch (Exception e)
                {
                    Error = $"RawMic failed: {e.Message}";
                    _raw = null;
                }
                return;
            }
#endif
            if (Microphone.devices.Length == 0)
            {
                Error = "No microphone found (RECORD_AUDIO permission?)";
                return;
            }
            _device = Microphone.devices[0];
            Microphone.GetDeviceCaps(_device, out var min, out var max);
            // 0/0 means "any rate". On Quest the mic is 48 kHz; asking Unity for 16 kHz there delivers
            // pure silence, so capture at 48 kHz and resample ourselves. Elsewhere prefer 16 kHz if offered.
#if UNITY_ANDROID && !UNITY_EDITOR
            _inputRate = (min == 0 && max == 0) || (48000 >= min && 48000 <= max) ? 48000 : max;
#else
            _inputRate = (min == 0 && max == 0) || (OutputRate >= min && OutputRate <= max) ? OutputRate : max;
#endif
            Status = $"{SourceName(SourceUnity)} · '{_device}' {_inputRate} Hz";
            Debug.Log($"[Bench] Mic '{_device}' caps {min}-{max} Hz → capturing at {_inputRate} Hz (devices: {string.Join(", ", Microphone.devices)})");
            _clip = Microphone.Start(_device, true, ClipSeconds, _inputRate);
        }

        bool StartFile()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                var path = intent.Call<string>("getStringExtra", "audiofile");
                if (string.IsNullOrEmpty(path)) return false;
                long.TryParse(intent.Call<string>("getStringExtra", "audioat") ?? "", out _fileAt);
                var bytes = System.IO.File.ReadAllBytes(path);
                var data = 44; // skip the RIFF header: find the "data" chunk
                for (var i = 12; i + 8 <= bytes.Length; )
                {
                    var size = BitConverter.ToInt32(bytes, i + 4);
                    if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a') { data = i + 8; break; }
                    i += 8 + size;
                }
                _file = new float[(bytes.Length - data) / 2];
                for (var i = 0; i < _file.Length; i++) _file[i] = BitConverter.ToInt16(bytes, data + 2 * i) / 32768f;
                _filePos = -1;
                _inputRate = OutputRate;
                Status = $"file · {System.IO.Path.GetFileName(path)}";
                Debug.Log($"[Bench] Audio from file {path} ({_file.Length / (float)OutputRate:F0} s), 0 s at {_fileAt}");
                return true;
            }
            catch (Exception e) { Debug.LogWarning($"[Bench] audiofile: {e.Message}"); _file = null; return false; }
#else
            return false;
#endif
        }

        float[] ReadFile()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var to = (int)Math.Min(_file.Length, (now - _fileAt) * OutputRate / 1000);
            if (to <= 0) return null;
            if (_filePos < 0) _filePos = Math.Max(0, to - OutputRate / 10); // joined late: start here
            if (to <= _filePos) return null;
            var n = to - _filePos;
            var chunk = new float[n];
            Array.Copy(_file, _filePos, chunk, 0, n);
            _filePos = to;
            return chunk;
        }

        public void StopCapture()
        {
            _file = null;
            if (_raw != null)
            {
                _raw.Call("stop");
                _raw.Dispose();
                _raw = null;
            }
            if (_clip != null)
            {
                Microphone.End(_device);
                Destroy(_clip);
                _clip = null;
            }
        }

        void OnDestroy() => StopCapture();

        void Update()
        {
            if (!Running) return;
            float[] mono = _file != null ? ReadFile() : _raw != null ? ReadRaw() : ReadUnity();
            if (mono == null || mono.Length == 0) return;

            if (_raw != null)
            {
                // Raw sources skip Android's processing, including its DC removal. A constant offset makes the
                // AGC think the signal is already loud. One-pole high-pass at ~80 Hz removes DC and rumble.
                var a = Mathf.Exp(-2f * Mathf.PI * 80f / _inputRate);
                double raw2 = 0;
                for (var i = 0; i < mono.Length; i++)
                {
                    var x = mono[i];
                    var y = a * (_hpY + x - _hpX);
                    _hpX = x;
                    _hpY = y;
                    mono[i] = y;
                    raw2 += y * y;
                }
                RawLevel = (float)Math.Sqrt(raw2 / mono.Length);
            }

            if (_recordSeconds > 0) _preForRecord = (float[])mono.Clone();

            if (autoGain && _raw != null) // raw Android sources only; Unity's path already has system gain
            {
                // Constant makeup gain + compressor (5 ms attack, release agcReleaseSeconds) + soft limiter.
                var makeup = Mathf.Pow(10f, maxAutoGainDb / 20f);
                var threshold = Mathf.Pow(10f, compressorThresholdDb / 20f);
                var attack = Mathf.Exp(-1f / (0.005f * _inputRate));
                var release = Mathf.Exp(-1f / (agcReleaseSeconds * _inputRate));
                var g = makeup;
                for (var i = 0; i < mono.Length; i++)
                {
                    var a = Mathf.Abs(mono[i]) * makeup;
                    var k = a > _agcEnvelope ? attack : release;
                    _agcEnvelope = k * _agcEnvelope + (1f - k) * a;
                    g = _agcEnvelope > threshold ? makeup * threshold / _agcEnvelope : makeup;
                    var y = mono[i] * g;
                    var ay = Mathf.Abs(y);
                    mono[i] = ay < 0.5f ? y : Mathf.Sign(y) * (0.5f + 0.5f * (float)Math.Tanh((ay - 0.5f) / 0.5f)); // soft limiter
                }
                CurrentGainDb = 20f * Mathf.Log10(g);
            }
            else if (Mathf.Abs(gainDb) > 0.01f)
            {
                var g = Mathf.Pow(10f, gainDb / 20f);
                for (var i = 0; i < mono.Length; i++) mono[i] = Mathf.Clamp(mono[i] * g, -1f, 1f);
                CurrentGainDb = gainDb;
            }
            else CurrentGainDb = 0;

            var output = _inputRate == OutputRate ? mono : Resample(mono);
            if (output.Length == 0) return;

            double sum = 0;
            for (var i = 0; i < output.Length; i++) sum += output[i] * output[i];
            Level = (float)Math.Sqrt(sum / output.Length);
            // Real microphones are never exactly silent; all-zero audio means capture is broken.
            _zeroSeconds = Level == 0f ? _zeroSeconds + output.Length / (float)OutputRate : 0f;
            if (_zeroSeconds >= 2f && !_warnedSilent)
            {
                _warnedSilent = true;
                Error = $"Mic delivers pure silence at {_inputRate} Hz — capture is not working";
                Debug.LogWarning($"[Bench] {Error}");
            }
            SamplesOut += output.Length;
            if (_recordSeconds > 0) RecordChunk(output);
            OnChunk?.Invoke(output, Time.realtimeSinceStartupAsDouble);
        }

        void RecordChunk(float[] post)
        {
            // Pre-gain audio at the capture rate, full float resolution (to see what the hardware really delivers).
            if (_preForRecord != null) _recPre.AddRange(_preForRecord);
            _recPost.AddRange(post);
            if (_recPost.Count >= _recordSeconds * OutputRate)
            {
                _recordSeconds = 0;
                var dir = Application.persistentDataPath;
                WriteWav(System.IO.Path.Combine(dir, "mic_pre.wav"), _recPre, _inputRate);
                WriteWav(System.IO.Path.Combine(dir, "mic_post.wav"), _recPost, OutputRate);
                if (_recMulti.Count > 0) WriteWav(System.IO.Path.Combine(dir, "mic_multi.wav"), _recMulti, _inputRate, _recMultiChannels);
                Debug.Log($"[Bench] Debug recording saved to {dir}/mic_pre.wav and mic_post.wav");
            }
        }

        // 32-bit float WAV: 16-bit would round away the very quiet raw signal we want to inspect.
        static void WriteWav(string path, List<float> samples, int rate, int channels = 1)
        {
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            var n = samples.Count;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 4);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)3); w.Write((short)channels);
            w.Write(rate); w.Write(rate * 4 * channels); w.Write((short)(4 * channels)); w.Write((short)32);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 4);
            foreach (var x in samples) w.Write(x);
        }

        float[] ReadRaw()
        {
            var interleaved = _raw.Call<float[]>("poll");
            if (interleaved == null || interleaved.Length == 0) return null;
            var ch = _raw.Call<int>("getChannels");
            if (ch == 1) return interleaved;
            if (_recordSeconds > 0) { _recMulti.AddRange(interleaved); _recMultiChannels = ch; }

            // 2+ channels: measure the first two (for direction experiments), then average to mono.
            var n = interleaved.Length / ch;
            var mono = new float[n];
            double l2 = 0, r2 = 0, lr = 0;
            for (var i = 0; i < n; i++)
            {
                float l = interleaved[ch * i], r = interleaved[ch * i + 1], sum = 0;
                for (var c = 0; c < ch; c++) sum += interleaved[ch * i + c];
                mono[i] = sum / ch;
                l2 += l * l; r2 += r * r; lr += l * r;
            }
            LeftLevel = (float)Math.Sqrt(l2 / n);
            RightLevel = (float)Math.Sqrt(r2 / n);
            ChannelCorrelation = l2 > 0 && r2 > 0 ? (float)(lr / Math.Sqrt(l2 * r2)) : 0;
            return mono;
        }

        float[] ReadUnity()
        {
            var pos = Microphone.GetPosition(_device);
            if (pos < 0) return null;
            var total = _clip.samples;
            var available = (pos - _readPos + total) % total;
            if (available == 0) return null;

            // If a frame took longer than the ring buffer, audio was lost.
            if (Time.unscaledDeltaTime > ClipSeconds * 0.9f) Overruns++;

            if (_readBuf.Length != available) _readBuf = new float[available];
            if (_readPos + available <= total)
            {
                _clip.GetData(_readBuf, _readPos);
            }
            else
            {
                var first = total - _readPos;
                var a = new float[first];
                var b = new float[available - first];
                _clip.GetData(a, _readPos);
                _clip.GetData(b, 0);
                Array.Copy(a, 0, _readBuf, 0, first);
                Array.Copy(b, 0, _readBuf, first, b.Length);
            }
            _readPos = pos;
            return (float[])_readBuf.Clone();
        }

        float[] Resample(float[] input)
        {
            // 48 kHz → 16 kHz: average each group of 3 samples (simple anti-alias low-pass + decimate).
            if (_inputRate == 3 * OutputRate)
            {
                _decimCarry.AddRange(input);
                var count = _decimCarry.Count / 3;
                var output = new float[count];
                for (var i = 0; i < count; i++)
                    output[i] = (_decimCarry[3 * i] + _decimCarry[3 * i + 1] + _decimCarry[3 * i + 2]) / 3f;
                _decimCarry.RemoveRange(0, count * 3);
                return output;
            }
            return ResampleLinear(input);
        }

        // Linear-interpolation resampler that keeps its phase across chunks.
        // _resamplePhase is the next output position in input-sample units; index -1 is the
        // last sample of the previous chunk, so the phase always stays in [-1, step - 1).
        float[] ResampleLinear(float[] input)
        {
            var step = (double)_inputRate / OutputRate;
            var last = input.Length - 1;
            var t = _resamplePhase;
            var count = t <= last ? (int)Math.Floor((last - t) / step) + 1 : 0;
            var output = new float[count];
            for (var i = 0; i < count; i++, t += step)
            {
                var i0 = (int)Math.Floor(t);
                var frac = (float)(t - i0);
                var s0 = i0 < 0 ? _lastInput : input[i0];
                var s1 = i0 + 1 <= last ? input[i0 + 1] : input[last];
                output[i] = s0 + (s1 - s0) * frac;
            }
            _resamplePhase = t - input.Length;
            _lastInput = input[last];
            return output;
        }
    }
}
