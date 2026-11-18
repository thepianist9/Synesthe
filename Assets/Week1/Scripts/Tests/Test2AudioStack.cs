using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 2 · One shared mic stream feeding VAD + streaming speech-to-text.
    /// All three run on every chunk (no volume gate) to measure the worst case.
    /// PASS: both produce output, no mic overruns, the worker keeps up (real-time factor &lt; 1).
    /// </summary>
    public class Test2AudioStack : BenchTest
    {
        public override string Id => "2";
        public override string Title => "Test 2 · Shared mic: STT + VAD";
        public override string Instructions => "30 s in three parts: 10 s quiet, 10 s YOU talk, 10 s someone else talks. The panel tells you when.";
        public override string PassRule => "Both run · 0 mic overruns · combined real-time factor < 1";

        // Three phases so we can compare the wearer's voice with someone else's at the same mic.
        static string Phase(float t, float d) => t < d / 3 ? "quiet" : t < 2 * d / 3 ? "wearer" : "other";
        static string PhasePrompt(float t, float d) => Phase(t, d) switch
        {
            "quiet" => "Stay quiet",
            "wearer" => "YOU talk now",
            _ => "SOMEONE ELSE talks now (you stay quiet)",
        };
        static float Db(float rms) => rms > 0 ? 20f * Mathf.Log10(rms) : -120f;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test2_audio_stack" };
            var duration = ctx.DurationOverride > 0 ? ctx.DurationOverride : 30f;

            using var stack = new SpeechStack(new SpeechStack.Options());
            if (!stack.Init())
            {
                result.Headline = stack.Error;
                return result;
            }
            ctx.Mic.StartCapture();
            if (!ctx.Mic.Running)
            {
                result.Headline = ctx.Mic.Error;
                return result;
            }
            void Feed(float[] s, double t) => stack.Push(s, t);
            ctx.Mic.OnChunk += Feed;

            using var log = new BenchLog(result.Name, "t", "audio_s", "vad_rtf", "asr_rtf", "total_rtf",
                "max_backlog", "vad_speech", "partial_chars", "mic_level", "mic_dbfs", "phase", "app_fps", "cpu_pct", "thermal");
            ctx.Perf.Begin();
            var start = Time.realtimeSinceStartup;
            var nextRow = 1f;
            var sawText = false;
            var sawSpeech = false;
            var secPeak = 0f;
            SpeechStack.Stats s = null;

            while (Elapsed(start) < duration && !ctx.AbortRequested)
            {
                await Awaitable.NextFrameAsync();
                s = stack.Snapshot();
                if (s.Partial.Length > 0) sawText = true;
                if (s.VadSpeech) sawSpeech = true;
                while (stack.TryGetUtterance(out var u)) { sawText = true; ctx.Hud.Caption(u.Text); }
                if (s.Partial.Length > 0) ctx.Hud.Caption(s.Partial);

                secPeak = Mathf.Max(secPeak, ctx.Mic.Level);
                if (Elapsed(start) >= nextRow)
                {
                    nextRow += 1f;
                    log.Row(Elapsed(start), s.AudioSec, s.VadRtf, s.AsrRtf, s.TotalRtf, s.MaxBacklog,
                        s.VadSpeech, s.Partial.Length, ctx.Mic.Level, Db(secPeak), Phase(Elapsed(start), duration), ctx.FpsLastSecond,
                        F(ctx.Perf.CpuUtilAvg, "F0"), PerfMonitor.ThermalName(ctx.Perf.ThermalStatus));
                    ctx.Hud.Set(Title, $">>> {PhasePrompt(Elapsed(start), duration)} <<<\n" +
                                       $"{Elapsed(start):F0}/{duration:F0} s · mic {ctx.Mic.InputRate} Hz · loudness {Db(secPeak):F0} dB · overruns {ctx.Mic.Overruns}\n" +
                                       (ctx.Mic.Error != null ? $"⚠ {ctx.Mic.Error}\n" : "") +
                                       $"Real-time factor  VAD {s.VadRtf:F3} · STT {s.AsrRtf:F3}\n" +
                                       $"Speech now: {(s.VadSpeech ? "YES" : "no")} · backlog {s.MaxBacklog}\n" +
                                       $"\n{ctx.Perf.Summary()}");
                    secPeak = 0f;
                }
            }
            ctx.Mic.OnChunk -= Feed;
            ctx.Mic.StopCapture();

            s ??= stack.Snapshot();
            AddPerf(result, ctx.Perf);
            result.Fields["mic_input_rate"] = ctx.Mic.InputRate;
            result.Fields["mic_overruns"] = ctx.Mic.Overruns;
            result.Fields["mic_error"] = ctx.Mic.Error;
            result.Fields["audio_seconds"] = s.AudioSec;
            result.Fields["vad_rtf"] = s.VadRtf;
            result.Fields["asr_rtf"] = s.AsrRtf;
            result.Fields["total_rtf"] = s.TotalRtf;
            result.Fields["max_backlog_chunks"] = s.MaxBacklog;
            result.Fields["vad_detected_speech"] = sawSpeech;
            result.Fields["asr_produced_text"] = sawText;

            result.Pass = sawSpeech && sawText && ctx.Mic.Overruns == 0 && s.TotalRtf < 1.0;
            result.Headline = $"RTF total {s.TotalRtf:F2} (VAD {s.VadRtf:F3}, STT {s.AsrRtf:F2}) · overruns {ctx.Mic.Overruns}" +
                              (sawText ? "" : " · no speech recognised");
            log.Summary(result.Fields);
            return result;
        }
    }
}
