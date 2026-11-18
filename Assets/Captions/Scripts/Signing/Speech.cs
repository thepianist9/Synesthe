using UnityEngine;

namespace MetaStarter.Captions
{
    /// <summary>
    /// Android's text-to-speech, for saying the wearer's signed sentences aloud (<see cref="SignReader"/>). Works when
    /// the headset has a speech engine installed; <see cref="Failed"/> says when it hasn't (or in the Editor, where
    /// sentences are only logged).
    /// </summary>
    public sealed class Speech : System.IDisposable
    {
        AndroidJavaObject _tts;
        volatile int _status = 1; // 1 starting, 0 ready, -1 no engine
        bool _languageSet;
        int _n;

        sealed class Init : AndroidJavaProxy
        {
            readonly Speech _s;
            public Init(Speech s) : base("android.speech.tts.TextToSpeech$OnInitListener") => _s = s;
            // Called on Android's main thread: only records the result.
            void onInit(int status) => _s._status = status == 0 ? 0 : -1;
        }

        public bool Ready => _status == 0;
        public bool Failed => _status < 0;

        public Speech()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new Init(this));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Captions] Text to speech unavailable: {e.Message}");
                _status = -1;
            }
#else
            _status = -1;
#endif
        }

        /// <summary>Say <paramref name="text"/> now, cutting off anything still being said. False if it can't.</summary>
        public bool Say(string text)
        {
            if (!Ready || _tts == null) return false;
            try
            {
                if (!_languageSet)
                {
                    _languageSet = true;
                    using var locale = new AndroidJavaClass("java.util.Locale");
                    _tts.Call<int>("setLanguage", locale.GetStatic<AndroidJavaObject>("US"));
                    _tts.Call<int>("setSpeechRate", 0.95f);
                }
                using var bundle = new AndroidJavaObject("android.os.Bundle");
                return _tts.Call<int>("speak", text, 0 /* QUEUE_FLUSH */, bundle, $"sign{++_n}") == 0;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Captions] Text to speech failed: {e.Message}");
                return false;
            }
        }

        public bool Speaking
        {
            get
            {
                try { return _tts != null && Ready && _tts.Call<bool>("isSpeaking"); }
                catch { return false; }
            }
        }

        public void Dispose()
        {
            try { _tts?.Call("shutdown"); } catch { /* already gone */ }
            _tts?.Dispose();
            _tts = null;
        }
    }
}
