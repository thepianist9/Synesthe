using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 3 · Caption delay. Someone reads 10 short sentences aloud; for each one we measure:
    ///   stable  = end of speech → caption text stops changing (what the reader sees)
    ///   final   = end of speech → recognizer endpoint (includes 0.8 s trailing-silence rule)
    ///   partial = start of speech → first words appear
    /// PASS: median stable latency &lt; 1.5 s (goal &lt; 1 s).
    /// Mic capture latency before Unity receives audio is not included.
    /// </summary>
    public class Test3CaptionLatency : BenchTest
    {
        public override string Id => "3";
        public override string Title => "Test 3 · Caption delay";
        public override string Instructions => "Have someone stand ~1 m away and read 5 sentences, pausing 2 s after each.";
        public override string PassRule => "Median end-of-speech → caption < 1.5 s (goal < 1 s)";

        static readonly string[] Sentences =
        {
            "Good morning, what brings you in today?",
            "Have you been taking the tablets twice a day?",
            "Any pain in your chest when you climb the stairs?",
            "Please sit down here.",
            "I'll take your blood pressure now.",
            "How long have you had this headache?",
            "Your next appointment is on Friday.",
            "Do you have any allergies?",
            "Take one of these after breakfast.",
            "Is there anything else you would like to ask?",
        };

        const int SentenceCount = 5;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test3_caption_latency" };
            using var stack = new SpeechStack(new SpeechStack.Options { Vad = false });
            if (!stack.Init()) { result.Headline = stack.Error; return result; }
            ctx.Mic.StartCapture();
            if (!ctx.Mic.Running) { result.Headline = ctx.Mic.Error; return result; }
            void Feed(float[] s, double t) => stack.Push(s, t);
            ctx.Mic.OnChunk += Feed;

            using var log = new BenchLog(result.Name, "n", "expected", "recognised", "speech_s", "partial_latency_s",
                "stable_latency_s", "final_latency_s");
            ctx.Perf.Begin();
            var utterances = new List<SpeechStack.Utterance>();
            var start = Time.realtimeSinceStartup;
            var timeout = ctx.DurationOverride > 0 ? ctx.DurationOverride : 90f;

            while (utterances.Count < SentenceCount && Elapsed(start) < timeout && !ctx.AbortRequested)
            {
                await Awaitable.NextFrameAsync();
                var next = Sentences[utterances.Count];
                var snap = stack.Snapshot();
                ctx.Hud.Caption(snap.Partial);
                while (stack.TryGetUtterance(out var u))
                {
                    utterances.Add(u);
                    log.Row(utterances.Count, Sentences[utterances.Count - 1], u.Text, u.SpeechSeconds,
                        u.PartialLatency, u.StableLatency, u.FinalLatency);
                }
                var last = utterances.Count > 0 ? utterances[^1] : default;
                ctx.Hud.Set(Title,
                    $"Sentence {utterances.Count + 1}/{SentenceCount} — read aloud:\n“{next}”\n\n" +
                    (utterances.Count > 0
                        ? $"Last: stable {last.StableLatency:F2} s · final {last.FinalLatency:F2} s · first words {last.PartialLatency:F2} s\n" +
                          $"Median stable so far: {Median(utterances.Select(x => x.StableLatency)):F2} s"
                        : "Waiting for the first sentence…"));
            }
            ctx.Mic.OnChunk -= Feed;
            ctx.Mic.StopCapture();

            AddPerf(result, ctx.Perf);
            var stable = utterances.Select(u => u.StableLatency).ToList();
            var final = utterances.Select(u => u.FinalLatency).ToList();
            var partial = utterances.Where(u => u.PartialLatency >= 0).Select(u => u.PartialLatency).ToList();
            result.Fields["utterances"] = utterances.Count;
            result.Fields["stable_latency_median_s"] = Median(stable);
            result.Fields["stable_latency_p90_s"] = Percentile(stable, 90);
            result.Fields["final_latency_median_s"] = Median(final);
            result.Fields["first_words_latency_median_s"] = Median(partial);
            result.Fields["mic_input_rate"] = ctx.Mic.InputRate;

            var med = Median(stable);
            result.Pass = utterances.Count >= 3 && med < 1.5;
            result.Headline = utterances.Count == 0
                ? "No sentences recognised — check mic permission and speak louder/closer"
                : $"Median caption delay {med:F2} s (p90 {Percentile(stable, 90):F2} s) over {utterances.Count} sentences" +
                  (med < 1.0 ? " · meets 1 s goal" : "");
            log.Summary(result.Fields);
            return result;
        }

        static double Median(IEnumerable<double> v) => Percentile(v.ToList(), 50);

        static double Percentile(List<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var s = v.OrderBy(x => x).ToList();
            var i = Mathf.Clamp(Mathf.CeilToInt((float)(p / 100 * s.Count)) - 1, 0, s.Count - 1);
            return s[i];
        }
    }
}
