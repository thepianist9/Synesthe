using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 4 · Hand-joint recorder. Guides the wearer through 5 ASL signs × 10 repetitions,
    /// recording both hands at 30 Hz as MediaPipe-21 points in head space. The CSV feeds
    /// tools/kaggle_benchmark.py and, later, training of our own phrase classifier.
    /// PASS: all 50 clips recorded and ≥ 95% of frames have at least one hand tracked.
    /// </summary>
    public class Test4HandRecorder : BenchTest
    {
        public override string Id => "4";
        public override string Title => "Test 4 · ASL hand-joint recorder";
        public override string Instructions => "Sign each word when the panel says GO. 5 signs × 3 reps, ~1 min.";
        public override string PassRule => "15 clips recorded · ≥ 95% frames with a hand tracked";

        // Signs that exist in the Kaggle 250-sign set, so the recordings can be benchmarked against it.
        public static readonly string[] Signs = { "yes", "no", "please", "thankyou", "where" };
        const int Reps = 3;
        const float Countdown = 1.5f, ClipSeconds = 2.0f, SampleHz = 30f;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test4_hand_recorder" };
            Directory.CreateDirectory(BenchLog.Folder);
            var runId = $"{result.Name}_{System.DateTime.Now:yyyyMMdd_HHmmss}";
            var path = Path.Combine(BenchLog.Folder, runId + "_joints.csv");
            using var csv = new StreamWriter(path, false, new UTF8Encoding(false));

            var header = new StringBuilder("clip,sign,rep,frame,t,hand,tracked,high_conf");
            for (var i = 0; i < 21; i++) header.Append($",x{i},y{i},z{i}");
            csv.WriteLine(header.ToString());

            ctx.Perf.Begin();
            int clips = 0, frames = 0, framesAnyHand = 0, framesHighConf = 0;
            var inv = CultureInfo.InvariantCulture;

            for (var s = 0; s < Signs.Length && !ctx.AbortRequested; s++)
            for (var rep = 1; rep <= Reps && !ctx.AbortRequested; rep++)
            {
                var sign = Signs[s].ToUpperInvariant();
                var t0 = Time.realtimeSinceStartup;
                while (Elapsed(t0) < Countdown && !ctx.AbortRequested)
                {
                    ctx.Hud.Set(Title, $"Next: {sign}   (rep {rep}/{Reps}, sign {s + 1}/{Signs.Length})\n\nGet ready… {Countdown - Elapsed(t0):F1}",
                        "Hands in front of your chest. Rest them in your lap between clips.");
                    ctx.Hud.Caption("");
                    await Awaitable.NextFrameAsync();
                }

                var start = Time.realtimeSinceStartup;
                var nextSample = 0f;
                var frame = 0;
                while (Elapsed(start) < ClipSeconds && !ctx.AbortRequested)
                {
                    var t = Elapsed(start);
                    if (t >= nextSample)
                    {
                        nextSample += 1f / SampleHz;
                        var any = false;
                        var high = false;
                        foreach (var left in new[] { true, false })
                        {
                            var h = ctx.Hands.Read(left);
                            any |= h.Tracked;
                            high |= h.HighConfidence;
                            var sb = new StringBuilder();
                            sb.Append(clips).Append(',').Append(Signs[s]).Append(',').Append(rep).Append(',')
                              .Append(frame).Append(',').Append(t.ToString("0.0000", inv)).Append(',')
                              .Append(left ? "left" : "right").Append(',').Append(h.Tracked ? 1 : 0).Append(',')
                              .Append(h.HighConfidence ? 1 : 0);
                            foreach (var p in h.Points)
                                sb.Append(',').Append(p.x.ToString("0.#####", inv))
                                  .Append(',').Append(p.y.ToString("0.#####", inv))
                                  .Append(',').Append(p.z.ToString("0.#####", inv));
                            csv.WriteLine(sb.ToString());
                        }
                        frames++;
                        if (any) framesAnyHand++;
                        if (high) framesHighConf++;
                        frame++;
                    }
                    ctx.Hud.Set(Title, $"GO — sign  {sign}\n\nrecording {t:F1}/{ClipSeconds:F1} s",
                        $"Tracked frames so far: {(frames > 0 ? 100f * framesAnyHand / frames : 0):F0}%");
                    await Awaitable.NextFrameAsync();
                }
                clips++;
            }
            csv.Flush();

            var trackedPct = frames > 0 ? 100f * framesAnyHand / frames : 0;
            AddPerf(result, ctx.Perf);
            result.Fields["joints_csv"] = path;
            result.Fields["clips"] = clips;
            result.Fields["frames"] = frames;
            result.Fields["pct_frames_hand_tracked"] = trackedPct;
            result.Fields["pct_frames_high_confidence"] = frames > 0 ? 100f * framesHighConf / frames : 0;
            result.Pass = clips == Signs.Length * Reps && trackedPct >= 95f;
            result.Headline = $"{clips} clips · {trackedPct:F1}% frames tracked · saved {Path.GetFileName(path)}";

            using (var log = new BenchLog(result.Name, "clips", "frames", "pct_tracked"))
            {
                log.Row(clips, frames, trackedPct);
                log.Summary(result.Fields);
            }
            return result;
        }
    }
}
