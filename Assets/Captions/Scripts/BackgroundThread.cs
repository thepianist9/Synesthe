namespace MetaStarter.Captions
{
    /// <summary>
    /// Lowers the calling thread's Linux nice level, so background models (voice prints, sound tags, translation)
    /// give way to Unity's main and render threads when the cores are all busy. .NET's Thread.Priority does nothing
    /// on Android (SCHED_OTHER threads are scheduled by nice alone). Also names the thread after its managed name
    /// (Android otherwise calls every attached thread "Thread-N", Unity's render thread included), so traces show which
    /// model is busy. Does nothing in the Editor.
    /// </summary>
    public static class BackgroundThread
    {
        /// <summary>
        /// Speech (its thread and onnxruntime's worker) in the idle class instead of normal priority (-e speechidle 0
        /// for normal). Measured on Quest 3, Bubbles with a talking video, 2026-11-11: normal priority 6.1 % late frames
        /// with bursts of 6-8 in a row (the visible jumps), 44 hitches; idle 4.5 %, never more than 3 in a row, 23 hitches.
        /// Captions fell behind at most 0.7 s instead of 0.4 s: speech still gets about two of the app's three cores.
        /// </summary>
        public static bool SpeechIdle = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("c")] static extern int gettid();
        [System.Runtime.InteropServices.DllImport("c")] static extern int setpriority(int which, int who, int prio);
        [System.Runtime.InteropServices.DllImport("c")] static extern int sched_setscheduler(int pid, int policy, ref int param);
        [System.Runtime.InteropServices.DllImport("c")] static extern int prctl(int option, string arg2, long a3, long a4, long a5);
        [System.ThreadStatic] static int _nicePlus1; // 0: not set on this thread yet
        [System.ThreadStatic] static bool _named;

        static void NameIt()
        {
            if (_named) return;
            _named = true;
            var n = System.Threading.Thread.CurrentThread.Name;
            if (string.IsNullOrEmpty(n)) return; // pool threads keep theirs
            try { prctl(15 /* PR_SET_NAME */, n.Length > 15 ? n.Substring(0, 15) : n, 0, 0, 0); } catch (System.Exception) { }
        }
#endif

        /// <summary>Also sets 0 explicitly: a thread started from a lowered one inherits its level.</summary>
        /// <param name="nice">0 normal … 19 lowest. Linux: per thread; threads it starts inherit it.</param>
        public static void Nice(int nice)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            NameIt();
            if (_nicePlus1 == nice + 1) return;
            try { if (setpriority(0 /* PRIO_PROCESS with a thread id: that thread */, gettid(), nice) == 0) _nicePlus1 = nice + 1; }
            catch (System.Exception) { _nicePlus1 = nice + 1; } // no libc entry: don't try again on this thread
#endif
        }

        /// <summary>
        /// Runs a speech model load on a thread of its own, in speech's class (normal, or idle with SpeechIdle):
        /// onnxruntime starts its worker threads during the load, and they keep the class of the thread that made them.
        /// (From a pool thread they'd stay normal priority whatever the speech thread does: a trace showed one of them
        /// as busy as the speech thread itself.)
        /// </summary>
        public static System.Threading.Tasks.Task<T> RunSpeech<T>(System.Func<T> load, string name)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<T>();
            new System.Threading.Thread(() =>
            {
                if (SpeechIdle) Idle(); else Nice(0);
                try { done.SetResult(load()); }
                catch (System.Exception e) { done.SetException(e); }
            }) { IsBackground = true, Name = name }.Start();
            return done.Task;
        }

        /// <summary>
        /// The idle class (SCHED_IDLE): the thread runs only on CPU time nothing else wants, and gives the core back the
        /// moment a normal thread (the frame) wakes. Threads it starts afterwards (onnxruntime's pool) inherit it. Only for
        /// a thread of its own: an unprivileged thread can't come back from it. Falls back to nice 10.
        /// </summary>
        public static void Idle()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            NameIt();
            try { var zero = 0; if (sched_setscheduler(0, 5 /* SCHED_IDLE */, ref zero) == 0) { _nicePlus1 = 20; return; } }
            catch (System.Exception) { }
            Nice(10);
#endif
        }
    }
}
