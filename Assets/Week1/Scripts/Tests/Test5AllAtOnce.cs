using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 5 · Everything at once, as the real app would run:
    ///   face detection (CPU — 4× faster than GPU on Quest 3, capped at 15 Hz) + VAD + streaming STT
    ///   + hand capture every frame + sign-classifier-sized model at 10 Hz.
    /// PASS: holds the refresh rate, faces ≥ 8/s, audio keeps up, thermal below "severe".
    /// For Test 6 (20-min endurance) launch with the intent extra duration=1200.
    /// </summary>
    public class Test5AllAtOnce : BenchTest
    {
        public override string Id => "5";
        public override string Title => "Test 5 · Everything at once";
        public override string Instructions => "Have a conversation with someone in view for 45 s. Move your hands now and then.";
        public override string PassRule => "Holds refresh rate · faces ≥ 8/s · audio RTF < 1 · thermal < severe";

        const float FaceHz = 15f, SignHz = 10f;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test5_all_at_once" };
            var duration = ctx.DurationOverride > 0 ? ctx.DurationOverride : 45f;

            var cameraOk = await ctx.WaitForCamera();
            using var stack = new SpeechStack(new SpeechStack.Options());
            var audioOk = stack.Init();
            ctx.Mic.StartCapture();
            void Feed(float[] s, double t) => stack.Push(s, t);
            if (audioOk) ctx.Mic.OnChunk += Feed;

            using var faces = cameraOk ? new FaceDetector(ctx.FaceModel, ctx.FaceAnchors, BackendType.CPU) : null;
            using var signs = new SignClassifierLoad(BackendType.CPU);
            var window = new float[SignClassifierLoad.Frames * 2 * 21 * 3];

            // Warm-up: the first inference compiles shaders / allocates buffers and stalls for seconds.
            // Do it before measuring so the numbers reflect steady state.
            ctx.Hud.Set(Title, "Warming up models…");
            if (faces != null) await faces.Detect(ctx.Camera.GetTexture());
            await signs.Classify(window);

            using var log = new BenchLog(result.Name, "t", "app_fps", "face_hz", "faces", "sign_hz", "audio_rtf",
                "hands_tracked", "cpu_frame_ms", "gpu_frame_ms", "app_cpu_ms", "app_gpu_ms", "thermal", "battery");
            ctx.Perf.Begin();
            var start = Time.realtimeSinceStartup;
            float nextFace = 0, nextSign = 0, nextRow = 1;
            int faceRuns = 0, secFaceRuns = 0, secSignRuns = 0, faceCount = 0;
            bool faceBusy = false, signBusy = false;

            async void RunFace()
            {
                faceBusy = true;
                try { faceCount = (await faces.Detect(ctx.Camera.GetTexture())).Count; faceRuns++; secFaceRuns++; }
                finally { faceBusy = false; }
            }
            async void RunSign()
            {
                signBusy = true;
                try { await signs.Classify(window); secSignRuns++; }
                finally { signBusy = false; }
            }

            while (Elapsed(start) < duration && !ctx.AbortRequested)
            {
                await Awaitable.NextFrameAsync();
                var t = Elapsed(start);

                // Hand capture every frame into a rolling 60-frame window (both hands).
                var tracked = 0;
                System.Array.Copy(window, 126, window, 0, window.Length - 126);
                for (var h = 0; h < 2; h++)
                {
                    var hf = ctx.Hands.Read(h == 0);
                    if (hf.Tracked) tracked++;
                    for (var j = 0; j < 21; j++)
                    {
                        var o = window.Length - 126 + h * 63 + j * 3;
                        window[o] = hf.Points[j].x; window[o + 1] = hf.Points[j].y; window[o + 2] = hf.Points[j].z;
                    }
                }

                if (faces != null && !faceBusy && t >= nextFace) { nextFace = t + 1f / FaceHz; RunFace(); }
                if (!signBusy && t >= nextSign) { nextSign = t + 1f / SignHz; RunSign(); }

                var snap = stack.Snapshot();
                if (snap.Partial.Length > 0) ctx.Hud.Caption(snap.Partial);

                if (t >= nextRow)
                {
                    nextRow += 1f;
                    log.Row(t, ctx.FpsLastSecond, secFaceRuns, faceCount, secSignRuns, snap.TotalRtf, tracked,
                        ctx.Perf.CpuFrameMs, ctx.Perf.GpuFrameMs, F(ctx.Perf.AppCpuMs), F(ctx.Perf.AppGpuMs),
                        PerfMonitor.ThermalName(ctx.Perf.ThermalStatus), SystemInfo.batteryLevel);
                    ctx.Hud.Set(Title, $"{t:F0}/{duration:F0} s\n" +
                                       $"Faces {secFaceRuns}/s ({faceCount} in view) · signs {secSignRuns}/s · hands {tracked}\n" +
                                       $"Audio RTF {snap.TotalRtf:F2}\n\n{ctx.Perf.Summary()}");
                    secFaceRuns = 0;
                    secSignRuns = 0;
                }
            }

            // Let in-flight inference finish before disposing workers.
            while ((faceBusy || signBusy) && Elapsed(start) < duration + 2f) await Awaitable.NextFrameAsync();
            if (audioOk) ctx.Mic.OnChunk -= Feed;
            ctx.Mic.StopCapture();

            var final = stack.Snapshot();
            var faceHz = faceRuns / Mathf.Max(0.001f, Elapsed(start));
            AddPerf(result, ctx.Perf);
            result.Fields["camera_ok"] = cameraOk;
            result.Fields["audio_ok"] = audioOk;
            result.Fields["face_det_per_s"] = faceHz;
            result.Fields["audio_total_rtf"] = final.TotalRtf;

            result.Pass = cameraOk && audioOk && ctx.Perf.HoldsFrameRate && faceHz >= 8f &&
                          final.TotalRtf < 1.0 && ctx.Perf.WorstThermalStatus < 3;
            result.Headline = $"{ctx.Perf.AvgFps:F1}/{ctx.Perf.TargetHz:F0} fps · faces {faceHz:F1}/s · audio RTF {final.TotalRtf:F2} · thermal {PerfMonitor.ThermalName(ctx.Perf.WorstThermalStatus)}" +
                              (audioOk ? "" : $" · audio failed: {stack.Error}") + (cameraOk ? "" : " · camera unavailable");
            log.Summary(result.Fields);
            return result;
        }
    }
}
