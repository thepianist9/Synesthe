using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 6 · Which microphone source hears OTHER people?
    /// Quest's default (Unity → VOICE_RECOGNITION) keeps the wearer's voice and suppresses others.
    /// This test cycles Android audio sources through the RawMic plugin while someone else (or a
    /// laptop) talks and the wearer stays quiet, and measures loudness, speech detection and text.
    /// PASS: at least one source detects ≥ 4 s of speech and recognises ≥ 20 characters.
    /// </summary>
    public class Test6MicSources : BenchTest
    {
        public override string Id => "6";
        public override string Title => "Test 6 · Mic sources (other speaker)";
        public override string Instructions => "Stay quiet. Someone else (or a laptop ~1 m away) talks the whole time.\n4 mic sources × 14 s.";
        public override string PassRule => "A source hears ≥ 4 s of speech and ≥ 20 characters from the other speaker";

        // gainDb < 0 means automatic gain. Unity's source already has the system's own gain.
        static readonly (int source, float gainDb)[] AllSources =
        {
            (MicStream.SourceUnity, 0f),
            (MicStream.SourceUnprocessed, -1f),
            (MicStream.SourceMic, -1f),
            (MicStream.SourceCamcorder, -1f),
        };
        // Launch with -e sources raw to skip Unity's default and CAMCORDER.
        public static bool RawOnly;
        static (int source, float gainDb)[] Sources => RawOnly ? new[] { AllSources[1], AllSources[2] } : AllSources;
        const float SettleSec = 2f, MeasureSec = 12f;

        static float Db(float rms) => rms > 0 ? 20f * Mathf.Log10(rms) : -120f;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test6_mic_sources" };
            var mic = ctx.Mic;
            var originalSource = mic.androidSource;
            var originalGain = mic.gainDb;
            var originalAuto = mic.autoGain;
            using var log = new BenchLog(result.Name, "t", "source", "gain_db", "peak_dbfs_raw", "left_dbfs", "right_dbfs",
                "lr_correlation", "vad_speech", "partial_chars");
            ctx.Perf.Begin();
            var best = (name: "", chars: 0, speech: 0);
            var overall = Time.realtimeSinceStartup;

            foreach (var (source, gain) in Sources)
            {
                if (ctx.AbortRequested) break;
                var name = MicStream.SourceName(source);
                mic.androidSource = source;
                mic.autoGain = gain < 0;
                mic.gainDb = gain < 0 ? 0 : gain;
                mic.channels = 1;

                using var stack = new SpeechStack(new SpeechStack.Options());
                if (!stack.Init()) { result.Headline = stack.Error; break; }
                mic.StartCapture();
                if (!mic.Running)
                {
                    result.Fields[$"{name}_error"] = mic.Error;
                    Debug.LogWarning($"[Bench] {name}: {mic.Error}");
                    continue;
                }
                void Feed(float[] s, double t) => stack.Push(s, t);
                mic.OnChunk += Feed;

                var start = Time.realtimeSinceStartup;
                var nextRow = SettleSec + 1f;
                float secPeak = 0, peakSum = 0, lSum = 0, rSum = 0, corrSum = 0;
                int rows = 0, speechSec = 0, maxPartial = 0;
                var text = new StringBuilder();

                while (Elapsed(start) < SettleSec + MeasureSec && !ctx.AbortRequested)
                {
                    await Awaitable.NextFrameAsync();
                    var t = Elapsed(start);
                    var snap = stack.Snapshot();
                    while (stack.TryGetUtterance(out var u)) text.Append(u.Text).Append(" | ");
                    if (t < SettleSec) continue;

                    secPeak = Mathf.Max(secPeak, mic.Level);
                    maxPartial = Mathf.Max(maxPartial, snap.Partial.Length);
                    ctx.Hud.Caption(snap.Partial);
                    if (t >= nextRow)
                    {
                        nextRow += 1f;
                        var rawDb = Db(secPeak) - mic.CurrentGainDb;
                        log.Row(Elapsed(overall), name, mic.CurrentGainDb, rawDb, Db(mic.LeftLevel), Db(mic.RightLevel),
                            mic.ChannelCorrelation, snap.VadSpeech, snap.Partial.Length);
                        rows++;
                        peakSum += rawDb;
                        lSum += Db(mic.LeftLevel);
                        rSum += Db(mic.RightLevel);
                        corrSum += mic.ChannelCorrelation;
                        if (snap.VadSpeech) speechSec++;
                        ctx.Hud.Set(Title, $"Source: {name}  ({t - SettleSec:F0}/{MeasureSec:F0} s)\n" +
                                           "STAY QUIET — the other person keeps talking\n\n" +
                                           $"Loudness {rawDb:F0} dB raw → +{mic.CurrentGainDb:F0} dB gain → {Db(secPeak):F0} dB\n" +
                                           $"Speech seconds {speechSec} · text {maxPartial} chars\n{mic.Status}");
                        secPeak = 0;
                    }
                }
                mic.OnChunk -= Feed;
                mic.StopCapture();

                var heard = text.ToString().Trim();
                var chars = Mathf.Max(maxPartial, heard.Replace(" | ", "").Length);
                result.Fields[$"{name}_avg_peak_dbfs"] = rows > 0 ? peakSum / rows : -120f;
                result.Fields[$"{name}_left_dbfs"] = rows > 0 ? lSum / rows : -120f;
                result.Fields[$"{name}_right_dbfs"] = rows > 0 ? rSum / rows : -120f;
                result.Fields[$"{name}_lr_correlation"] = rows > 0 ? corrSum / rows : 0f;
                result.Fields[$"{name}_speech_seconds"] = speechSec;
                result.Fields[$"{name}_chars"] = chars;
                result.Fields[$"{name}_text"] = heard;
                result.Fields[$"{name}_status"] = mic.Status;
                if (speechSec > best.speech || (speechSec == best.speech && chars > best.chars))
                    best = (name, chars, speechSec);
            }

            mic.androidSource = originalSource;
            mic.gainDb = originalGain;
            mic.autoGain = originalAuto;
            AddPerf(result, ctx.Perf);
            result.Fields["best_source"] = best.name;
            result.Pass = best.speech >= 4 && best.chars >= 20;
            result.Headline = string.IsNullOrEmpty(best.name)
                ? "No source produced audio"
                : $"Best: {best.name} · {best.speech} s speech · {best.chars} chars";
            log.Summary(result.Fields);
            return result;
        }
    }
}
