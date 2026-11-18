using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MetaStarter.Captions
{
    /// <summary>
    /// A small text model run on the device itself (llama.cpp through tools/llm/sophie_llm.cpp, built by
    /// tools/llm/build_llm.sh into Assets/Plugins/SophieLlm). One model per instance; <see cref="Chat"/> blocks, so
    /// call it from a worker thread. <see cref="Progress"/> and <see cref="Cancel"/> are safe from any thread.
    /// </summary>
    public sealed class LocalLlm : IDisposable
    {
        const string Lib = "sophie_llm";

        [DllImport(Lib)] static extern IntPtr sl_load(byte[] path, int nCtx, int nThreads, int gpuLayers);
        [DllImport(Lib)] static extern void sl_free(IntPtr h);
        [DllImport(Lib)] static extern int sl_context_size(IntPtr h);
        [DllImport(Lib)] static extern int sl_count_tokens(IntPtr h, byte[] text);
        [DllImport(Lib)] static extern int sl_status(IntPtr h, int which);
        [DllImport(Lib)] static extern void sl_cancel(IntPtr h);
        [DllImport(Lib)] static extern int sl_chat(IntPtr h, byte[] system, byte[] user, int maxTokens, float temperature, byte[] output, int outCap);
        [DllImport(Lib)] static extern IntPtr sl_last_error();

        IntPtr _h;

        public int ContextSize { get; }

        LocalLlm(IntPtr h)
        {
            _h = h;
            ContextSize = sl_context_size(h);
        }

        /// <summary>Loads a GGUF model; throws with the reason if it can't (no runtime in this build, bad file…).</summary>
        public static LocalLlm Load(string path, int contextTokens, int threads)
        {
            IntPtr h;
            try { h = sl_load(Z(path), contextTokens, threads, 0); }
            catch (DllNotFoundException) { throw new Exception("the summary runtime isn't in this build"); }
            catch (EntryPointNotFoundException) { throw new Exception("the summary runtime is out of date"); }
            if (h == IntPtr.Zero) throw new Exception(LastError() is { Length: > 0 } e ? e.Trim() : "the model didn't load");
            return new LocalLlm(h);
        }

        public int CountTokens(string text) => _h == IntPtr.Zero ? 0 : sl_count_tokens(_h, Z(text));

        /// <summary>Prompt tokens read so far, of how many, and answer tokens written so far (of the current request).</summary>
        public (int read, int of, int written) Progress =>
            _h == IntPtr.Zero ? (0, 0, 0) : (sl_status(_h, 0), sl_status(_h, 1), sl_status(_h, 2));

        public void Cancel() { if (_h != IntPtr.Zero) sl_cancel(_h); }

        /// <summary>One system + user turn and the model's answer. Throws if it fails or is cancelled.</summary>
        public string Chat(string system, string user, int maxTokens, float temperature = 0.3f)
        {
            var output = new byte[maxTokens * 16 + 64];
            var n = sl_chat(_h, Z(system), Z(user), maxTokens, temperature, output, output.Length);
            if (n < 0)
                throw new Exception(n switch
                {
                    -2 => "too long for the model",
                    -4 => "cancelled",
                    _ => $"the model failed ({n}{(LastError() is { Length: > 0 } e ? ": " + e.Trim() : "")})",
                });
            return Encoding.UTF8.GetString(output, 0, n);
        }

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            sl_free(_h);
            _h = IntPtr.Zero;
        }

        static byte[] Z(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        static string LastError()
        {
            try
            {
                var p = sl_last_error();
                if (p == IntPtr.Zero) return "";
                var len = 0;
                while (Marshal.ReadByte(p, len) != 0 && len < 4096) len++;
                var b = new byte[len];
                Marshal.Copy(p, b, 0, len);
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }
}
