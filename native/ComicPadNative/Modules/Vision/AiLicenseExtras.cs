// 웹 판 ai/license.js 의 나머지 + ui/aimodels.js 의 "AI 기능 고르기·받기":
//  - 내보내기 전에 비상업 AI 결과가 있으면 한 번 더 묻기 (exportGuard / wrapExports)
//  - 비상업 AI 기록이 있는 작품을 열면 알림 (웹 판 상태 표시줄 배지 대신)
//  - 기능 단위로 골라 상업 가능 모델 받기 (네이티브에서 돌아가는 모델로)
using System.Text.Json.Nodes;
using ComicPadNative.Modules.AI;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Vision;

public static class AiLicenseExtras
{
    public static List<JsonNode> NcUsed(Core.Document d) => (d?.Extra["aiLog"] as JsonArray)?.Where(x => (bool?)x?["nc"] == true).ToList() ?? new();

    /// <summary>내보내기 전에: 비상업 AI 결과가 있으면 한 번 더 묻기. 계속하면 true</summary>
    public static bool ExportGuard()
    {
        var used = NcUsed(App.Doc);
        if (used.Count == 0) return true;
        var names = string.Join(", ", used.Select(x => (string)x?["name"]).Where(x => x != null).Distinct());
        return MessageBox.Show(App.Main, $"이 작품에는 비상업용 AI 로 만든 부분이 있습니다 ({names}).\n\n판매·연재·외주 등 상업용으로 내보내는 것이라면 라이선스 위반이 될 수 있습니다. 개인 공부·취미용이면 계속하세요.", "⚠ 비상업 AI 사용", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    static readonly HashSet<string> wrapped = new();

    /// <summary>내보내기 메뉴를 ExportGuard 로 감쌈 (이미 감싼 것은 건너뜀). 감싼 수</summary>
    public static int WrapExports()
    {
        int n = 0;
        foreach (var m in Registry.Menus.ToList())
        {
            if (m.Run == null || wrapped.Contains(m.Path)) continue;
            string leaf = m.Path[(m.Path.LastIndexOf('/') + 1)..];
            bool export = (leaf.Contains("내보내기") || leaf.StartsWith("PSD로 저장")) && !leaf.Contains("세팅") && !leaf.Contains("밑그림") && !leaf.Contains("팔레트");
            if (!export) continue;
            var run = m.Run;
            Registry.Menu(m.Path, () => { if (ExportGuard()) run(); }, m.Shortcut, m.Enabled, m.Checked, m.Order);
            wrapped.Add(m.Path);
            n++;
        }
        return n;
    }

    public static void Hook()
    {
        // 이름 순으로 앞선 모듈(파일·필터·만화·애니 …)의 메뉴는 지금, 뒤에 등록되는 것은 처음 쉴 때 (단축키·기능 검색용)
        WrapExports();
        void Once(object s, EventArgs e) { Application.Idle -= Once; WrapExports(); }
        Application.Idle += Once;
        App.DocChanged += () =>
        {
            var used = NcUsed(App.Doc);
            if (used.Count > 0) App.Toast($"⚠ 이 작품에는 비상업용 AI 로 만든 부분이 있습니다 ({string.Join(", ", used.Select(x => (string)x?["name"]).Distinct())}). 상업용으로 쓰지 마세요.");
        };
    }

    // ---------------------------------------------------------------- AI 기능 고르기·받기
    static readonly (string id, string name, string desc, string[] models, bool on)[] Bundles =
    {
        ("pose", "사진 → 인형 자세·손 모양", "3D 소재 창에서 사진 한 장으로 데생 인형 자세 잡기", new[] { VisionModels.Pose, VisionModels.Hand }, true),
        ("lineart", "선화 추출", "채색 그림·사진에서 선만 뽑기 (만화 전용 모델은 PyTorch 변환이 필요해 웹 판에서)", new[] { "line-informative" }, true),
        ("face", "버튜버 얼굴 추적", "웹캠으로 내 표정·고개·눈동자 움직임을 그린 캐릭터에 입히기 (버튜버 스튜디오)", new[] { VisionModels.Face, VisionModels.Iris }, false),
        ("upscale", "크기 키우기 (2·4배)", "작은 그림을 인쇄용으로 크게 (설치 파일에 들어 있어 바로 됨)", new[] { "up-animevideo" }, true),
        ("cutout", "캐릭터 따기", "애니·만화 캐릭터만 선택하거나 오려 내기", new[] { "seg-anime" }, false),
        ("depth", "깊이 추정", "명암·원근 참고용 깊이 그림", new[] { "depth-s" }, false),
        ("select", "탭해서 고르기", "탭한 물체를 선택 (MobileSAM, 설치 파일에 들어 있어 바로 됨)", new[] { "sam-mobile" }, true),
    };

    public static void SetupDialog()
    {
        var reg = AiRuntime.Registry().ToDictionary(m => m.Id);
        foreach (var m in reg.Values) AiRuntime.CopyBundled(m);
        using var f = new Form { Text = "AI 기능 고르기·받기", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        UI.Dialogs.Modals.Adopt(f);
        var p = Opt.Panel(Opt.Help("고른 기능의 모델만 이 PC 에 받습니다 (data\\models). 모두 상업용으로 쓸 수 있는 모델이며(일부는 \"확인 필요\"), 비상업 모델은 AI 모델 관리에서 따로 받습니다. 받은 뒤에는 인터넷 없이 이 PC 안에서만 돌아갑니다."));
        var picks = new Dictionary<string, bool>();
        foreach (var b in Bundles)
        {
            var ms = b.models.Where(reg.ContainsKey).Select(id => reg[id]).ToList();
            bool have = ms.Count > 0 && ms.All(m => m.Installed);
            long size = ms.Where(m => !m.Installed).Sum(m => m.Files.Sum(x => x.size));
            string worst = ms.Any(m => m.Commercial == "check") ? "확인 필요" : "상업 가능";
            picks[b.id] = !have && b.on;
            var id = b.id;
            var c = Opt.Check($"{b.name} — {(have ? "받아 둠" : $"{size / 1e6:0.0}MB")} · {worst}", () => have || picks[id], v => picks[id] = v);
            c.Enabled = !have;
            p.Controls.Add(c);
            p.Controls.Add(Opt.Help(b.desc));
        }
        bool ok = false;
        p.Controls.Add(Opt.Row(Opt.Button("고른 것 받기", () => { ok = true; f.Close(); }, true), Opt.Button("닫기", f.Close)));
        f.Controls.Add(p);
        f.ShowDialog(App.Main);
        if (!ok) return;
        var todo = Bundles.Where(b => picks[b.id]).SelectMany(b => b.models).Distinct().Where(reg.ContainsKey).Select(id => reg[id]).Where(m => !m.Installed).ToList();
        int got = 0;
        foreach (var m in todo)
        {
            if (m.Manual || m.Convert) continue;
            if (VisionUi.DownloadWithProgress(m, App.Main)) got++;
        }
        App.Toast(todo.Count == 0 ? "받을 모델이 없습니다" : $"AI 모델 {got}/{todo.Count}개를 받았습니다 (AI > AI 모델 관리에서 확인)");
    }
}
