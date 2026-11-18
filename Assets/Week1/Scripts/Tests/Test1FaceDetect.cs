using Unity.InferenceEngine;
using UnityEngine;

namespace Week1Bench
{
    /// <summary>
    /// Test 1 · Passthrough camera + face detection.
    /// Runs BlazeFace on the GPU, then on the CPU, as fast as camera frames arrive.
    /// PASS: one backend reaches ≥ 10 face detections/s while the app holds its refresh rate.
    /// </summary>
    public class Test1FaceDetect : BenchTest
    {
        public override string Id => "1";
        public override string Title => "Test 1 · Passthrough camera + face detect";
        public override string Instructions => "Sit facing 1–3 people (or a video of faces).\nRuns 15 s on GPU, then 15 s on CPU.";
        public override string PassRule => "≥ 10 face detections/s while holding refresh rate";

        const float TargetDetFps = 10f;

        public override async Awaitable<TestResult> Run(BenchContext ctx)
        {
            var result = new TestResult { Name = "test1_face_detect" };
            if (!await ctx.WaitForCamera())
            {
                result.Headline = "Passthrough camera not available — check HEADSET_CAMERA permission (Quest 3/3S, Horizon OS v74+).";
                return result;
            }

            var phase = ctx.DurationOverride > 0 ? ctx.DurationOverride / 2 : 15f;
            using var log = new BenchLog(result.Name, "t", "backend", "det_fps", "inference_ms", "faces", "app_fps", "cpu_frame_ms", "gpu_frame_ms", "thermal");
            var best = (backend: "", detFps: 0f, appFps: 0f, holds: false);
            ctx.Perf.Begin();

            foreach (var backend in new[] { BackendType.GPUCompute, BackendType.CPU })
            {
                using var detector = new FaceDetector(ctx.FaceModel, ctx.FaceAnchors, backend);
                ctx.Hud.Set(Title, $"Warming up {backend}…");
                await detector.Detect(ctx.Camera.GetTexture()); // first run compiles shaders; not measured
                var start = Time.realtimeSinceStartup;
                var phasePerf = new PerfMonitor();
                phasePerf.Begin();
                void TickPhase(float dt) => phasePerf.Tick(dt);
                ctx.FrameTick += TickPhase;
                int detections = 0, secDetections = 0, faces = 0;
                double secInfMs = 0;
                var secStart = start;
                var lastStamp = default(System.DateTime);

                while (Elapsed(start) < phase && !ctx.AbortRequested)
                {
                    if (ctx.Camera.Timestamp != lastStamp)
                    {
                        lastStamp = ctx.Camera.Timestamp;
                        var found = await detector.Detect(ctx.Camera.GetTexture());
                        faces = found.Count;
                        detections++;
                        secDetections++;
                        secInfMs += detector.LastInferenceMs;
                    }
                    else
                    {
                        await Awaitable.NextFrameAsync();
                    }
                    if (Time.realtimeSinceStartup - secStart >= 1f)
                    {
                        var detFps = secDetections / (Time.realtimeSinceStartup - secStart);
                        log.Row(Elapsed(start), backend.ToString(), detFps, secDetections > 0 ? secInfMs / secDetections : 0,
                            faces, ctx.FpsLastSecond, ctx.Perf.CpuFrameMs, ctx.Perf.GpuFrameMs,
                            PerfMonitor.ThermalName(ctx.Perf.ThermalStatus));
                        ctx.Hud.Set(Title, $"Backend: {backend}   ({Elapsed(start):F0}/{phase:F0} s)\n" +
                                           $"Face detections/s: {detFps:F1}   inference {(secDetections > 0 ? secInfMs / secDetections : 0):F1} ms\n" +
                                           $"Faces in view: {faces}\n\n{ctx.Perf.Summary()}");
                        secDetections = 0;
                        secInfMs = 0;
                        secStart = Time.realtimeSinceStartup;
                    }
                }

                ctx.FrameTick -= TickPhase;
                var avgDet = detections / Mathf.Max(0.001f, Elapsed(start));
                result.Fields[$"{backend}_det_fps"] = avgDet;
                result.Fields[$"{backend}_app_fps"] = phasePerf.AvgFps;
                result.Fields[$"{backend}_holds_frame_rate"] = phasePerf.HoldsFrameRate;
                var ok = avgDet >= TargetDetFps && phasePerf.HoldsFrameRate;
                if (ok && (!best.holds || avgDet > best.detFps) || (!best.holds && avgDet > best.detFps))
                    best = (backend.ToString(), avgDet, phasePerf.AvgFps, ok);
                if (ctx.AbortRequested) break;
            }

            AddPerf(result, ctx.Perf);
            result.Fields["camera_resolution"] = ctx.Camera.CurrentResolution.ToString();
            result.Fields["best_backend"] = best.backend;
            result.Pass = best.holds;
            result.Headline = $"Best: {best.backend} · {best.detFps:F1} det/s at {best.appFps:F1} fps";
            log.Summary(result.Fields);
            return result;
        }
    }
}
