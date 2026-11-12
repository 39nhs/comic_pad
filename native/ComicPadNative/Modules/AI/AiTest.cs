// AI 자체 점검 (창 없이): ComicPadNative.exe --ai-test 입력.cpad 출력폴더
//  - 크기 키우기 (설치 파일에 든 Real-ESRGAN animevideo) → 4배 크기·밝기 범위 확인
//  - AI 고르기 (MobileSAM) → 점을 찍은 곳이 선택되고 반대쪽 구석은 안 되는지
using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.AI;

public static class AiTest
{
    public static int Run(string cpad, string outDir, double px, double py)
    {
        Directory.CreateDirectory(outDir);
        var d = CpadIO.Load(cpad);
        var buf = App.Comp.Render(d, new Rect(0, 0, d.W, d.H), new RenderOptions { ExcludeDraft = true });
        var s0 = new Surface(d.W, d.H); s0.Write(s0.Full, buf.Select(c => Px.Over(c, 0xFFFFFFFF)).ToArray());
        using var bmp = CpadIO.ToBitmap(s0, s0.Full);
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "OK   " : "FAIL ") + what); if (!ok) fails++; }

        // 1) 크기 키우기
        var up = AiRuntime.Registry().First(m => m.Id == "up-animevideo");
        AiRuntime.CopyBundled(up);
        Check(up.Installed, "up-animevideo 모델 있음 (models_bundled → data/models)");
        if (up.Installed)
        {
            var s = AiRuntime.Session(up, "model.onnx");
            var pxs = AiRuntime.ReadArgb(bmp);
            var t0 = DateTime.Now;
            var big = AiRuntime.RunTiled(s, pxs, d.W, d.H, 192, 12, 4, 3, 1,
                (c, b, i, n) => { b[i] = ((c >> 16) & 255) / 255f; b[n + i] = ((c >> 8) & 255) / 255f; b[2 * n + i] = (c & 255) / 255f; },
                (od, q, on) => 0xFF000000u | ((uint)Math.Clamp((int)(od[q] * 255), 0, 255) << 16) | ((uint)Math.Clamp((int)(od[on + q] * 255), 0, 255) << 8) | (uint)Math.Clamp((int)(od[2 * on + q] * 255), 0, 255), null, CancellationToken.None);
            Console.WriteLine($"     키우기 {d.W}x{d.H} → {d.W * 4}x{d.H * 4}, {(DateTime.Now - t0).TotalSeconds:0.0}초, {AiRuntime.LastEp}");
            using var ob = AiRuntime.FromArgb(big, d.W * 4, d.H * 4);
            ob.Save(Path.Combine(outDir, "upscale_x4.png"));
            // 원본 (100,100) 근처 색과 키운 그림 (400,400) 색이 비슷해야
            uint a = pxs[100 * d.W + 100], bb = big[400 * d.W * 4 + 400];
            int diff = Math.Abs((int)((a >> 16) & 255) - (int)((bb >> 16) & 255)) + Math.Abs((int)((a >> 8) & 255) - (int)((bb >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(bb & 255));
            Check(diff < 60, $"키운 그림 색이 원본과 비슷함 (차이 {diff})");
        }

        // 2) AI 고르기
        var sam = AiRuntime.Registry().First(m => m.Id == "sam-mobile");
        AiRuntime.CopyBundled(sam);
        Check(sam.Installed, "MobileSAM 모델 있음");
        if (sam.Installed)
        {
            var t0 = DateTime.Now;
            var (data, box) = AiRuntime.ToCHW(bmp, 1024, 1024, AiRuntime.ImMean, AiRuntime.ImStd, contain: true, alignStart: true, padZero: true);
            var enc = AiRuntime.Session(sam, "vision_encoder.onnx");
            using var r = enc.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(data, new[] { 1, 3, 1024, 1024 })) });
            var e = r.First(x => x.Name == "image_embeddings").AsTensor<float>(); var p = r.First(x => x.Name == "image_positional_embeddings").AsTensor<float>();
            double scale = box.Width / (double)d.W;
            var dec = AiRuntime.Session(sam, "prompt_encoder_mask_decoder.onnx");
            using var o = dec.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_points", new DenseTensor<float>(new[] { (float)(px * scale), (float)(py * scale) }, new[] { 1, 1, 1, 2 })),
                NamedOnnxValue.CreateFromTensor("input_labels", new DenseTensor<long>(new long[] { 1 }, new[] { 1, 1, 1 })),
                NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(e.ToArray(), e.Dimensions.ToArray())),
                NamedOnnxValue.CreateFromTensor("image_positional_embeddings", new DenseTensor<float>(p.ToArray(), p.Dimensions.ToArray())),
            });
            var iou = o.First(x => x.Name == "iou_scores").AsTensor<float>().ToArray();
            var masks = o.First(x => x.Name == "pred_masks").AsTensor<float>().ToArray();
            int best = 0; for (int k = 1; k < 3; k++) if (iou[k] > iou[best]) best = k;
            var sharp = new float[65536]; for (int i = 0; i < 65536; i++) sharp[i] = masks[best * 65536 + i] * 3;
            var mask = AiRuntime.MaskToSize(sharp, 0, 256, 256, new RectangleF(0, 0, box.Width / 4f, box.Height / 4f), d.W, d.H, sigmoid: true);
            Console.WriteLine($"     AI 고르기 {(DateTime.Now - t0).TotalSeconds:0.0}초, {AiRuntime.LastEp}, 점수 {iou[best]:0.00}");
            int sel = mask.Count(v => v > 127);
            Check(mask[(int)py * d.W + (int)px] > 127, "탭한 곳이 선택됨");
            Check(mask[2 * d.W + 2] < 128, "구석은 선택 안 됨");
            Check(sel > 200 && sel < d.W * d.H * 0.8, $"선택 크기 적당 ({sel}px)");
            var vis = new uint[mask.Length]; for (int i = 0; i < vis.Length; i++) vis[i] = 0xFF000000u | ((uint)mask[i] * 0x010101u);
            using var mb = AiRuntime.FromArgb(vis, d.W, d.H); mb.Save(Path.Combine(outDir, "aiselect_mask.png"));
        }
        Console.WriteLine(fails == 0 ? "AI 점검 통과" : $"AI 점검 실패 {fails}개");
        return fails;
    }
}
