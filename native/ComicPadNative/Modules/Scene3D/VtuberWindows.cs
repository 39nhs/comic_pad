// 버튜버 모드 (웹 판 ui/vtuber.js 이식): 그린 캐릭터의 부위(레이어)를 정하고, 마우스·슬라이더·마이크로 움직여 방송 창·사진으로 내보낸다.
//  - 부위 정하기: doc.Extra["vtuber"].parts = { 레이어 id: { role, depth?, key } } (웹 판과 같은 형식, 실행취소 가능)
//  - 스튜디오: 숨쉬기·자동 깜빡임·머리카락 흔들림, 고개 yaw/pitch/roll 시차, 입 모양, 표정 키(1~9)
//  - 얼굴 추적(웹캠)은 MediaPipe 가 있어야 해서 네이티브 판에는 아직 없음 — 대신 마우스로 고개·눈동자, Space/오른쪽 버튼으로 말하기
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Scene3D;

public static class Vtuber
{
    static readonly Dictionary<string, Color?> Bgs = new() { ["green"] = Color.FromArgb(0, 255, 0), ["magenta"] = Color.FromArgb(255, 0, 255), ["blue"] = Color.FromArgb(0, 0x47, 0xbb), ["none"] = null };
    public static Color? BgColor(string id) => Bgs.TryGetValue(id ?? "", out var c) ? c : Bgs["green"];
    static StudioForm live;

    static string SafeName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n.Trim().Length == 0 ? "버튜버" : n; }
    static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
    static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }

    // ---------------------------------------------------------------------- 부위 정하기
    public static void SetupDialog()
    {
        var doc = App.Doc;
        if (doc == null) return;
        var layers = VRig.DrawableLayers(doc); layers.Reverse();          // 위 레이어부터 보여 줌
        if (layers.Count == 0) { App.Toast("부위로 쓸 그림 레이어가 없습니다"); return; }
        var cfg = ((doc.Extra["vtuber"] as JsonObject)?["parts"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        var roleOpts = VRig.Roles.Select(r => (r.id, r.r.Name)).ToArray();

        using var f = new Form { Text = "버튜버 아바타 만들기 (부위 정하기)", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 9f), Size = new Size(720, 640), MinimizeBox = false };
        var help = new Label { Dock = DockStyle.Top, Height = 64, ForeColor = Opt.Muted, Padding = new Padding(10, 8, 10, 0), Text = "레이어마다 어떤 부위인지 고르세요 (이름으로 미리 짐작해 두었습니다). 눈(뜸·감음), 입(닫힘·벌림, 있으면 ㅏㅣㅜㅔㅗ), 앞머리·뒷머리를 따로 그려 두면 깜빡임·입 모양·머리카락 흔들림이 됩니다. PNG 버튜버는 \"PNG: 가만히 / 말할 때 / 눈 감음\" 그림 몇 장이면 됩니다." };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10, 4, 10, 4) };
        var thumbs = new List<Bitmap>();
        foreach (var l in layers)
        {
            if (cfg[l.Id] is not JsonObject c) { c = new JsonObject { ["role"] = VRig.GuessRole(l.Name) }; cfg[l.Id] = c; }
            if (J.Num(c["key"], 0) == 0) c["key"] = 1;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Opt.Bg2, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(3) };
            var th = Thumb(l.Px, 48); thumbs.Add(th);
            row.Controls.Add(new PictureBox { Image = th, Width = 48, Height = 48, BackColor = Color.FromArgb(90, 90, 96), Margin = new Padding(0, 0, 6, 0) });
            row.Controls.Add(new Label { Text = l.Name, AutoSize = false, Width = 180, Height = 48, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
            var sel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, BackColor = Opt.Bg, ForeColor = Opt.Fg, Margin = new Padding(0, 12, 6, 0), MaxDropDownItems = 24 };
            foreach (var (_, n) in roleOpts) sel.Items.Add(n);
            sel.SelectedIndex = Math.Max(0, Array.FindIndex(roleOpts, r => r.id == J.Str(c["role"])));
            var depth = new TextBox { Width = 60, Text = c["depth"] is JsonValue ? J.Num(c["depth"], 0).ToString(System.Globalization.CultureInfo.InvariantCulture) : "", PlaceholderText = "자동", BackColor = Opt.Bg, ForeColor = Opt.Fg, Margin = new Padding(0, 13, 6, 0) };
            new ToolTip().SetToolTip(depth, "깊이 (클수록 고개 방향으로 더 움직임, -1 ~ 1.5)");
            var key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, BackColor = Opt.Bg, ForeColor = Opt.Fg, Margin = new Padding(0, 12, 0, 0) };
            for (int k = 1; k <= 9; k++) key.Items.Add($"키 {k}");
            key.SelectedIndex = Math.Clamp((int)J.Num(c["key"], 1) - 1, 0, 8);
            new ToolTip().SetToolTip(key, "표정 단축키");
            key.Visible = J.Str(c["role"]) == "expr";
            var cc = c;
            sel.SelectedIndexChanged += (_, _) => { cc["role"] = roleOpts[sel.SelectedIndex].id; key.Visible = roleOpts[sel.SelectedIndex].id == "expr"; };
            depth.TextChanged += (_, _) =>
            {
                if (depth.Text.Trim() == "") cc.Remove("depth");
                else if (double.TryParse(depth.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) cc["depth"] = Math.Clamp(v, -1, 1.5);
            };
            key.SelectedIndexChanged += (_, _) => cc["key"] = key.SelectedIndex + 1;
            row.Controls.AddRange(new Control[] { sel, depth, key });
            list.Controls.Add(row);
        }
        bool ok = false;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, BackColor = Opt.Bg2, Padding = new Padding(6) };
        bar.Controls.Add(Ui3.Button("저장하고 스튜디오 열기", () => { ok = true; f.Close(); }, true));
        bar.Controls.Add(Ui3.Button("취소", f.Close));
        f.Controls.Add(list); f.Controls.Add(help); f.Controls.Add(bar);
        f.ShowDialog(App.Main);
        foreach (var t in thumbs) t.Dispose();
        if (!ok) return;
        ExtraEdit.Change(doc, "버튜버 부위", "vtuber", () =>
        {
            var vt = (doc.Extra["vtuber"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
            vt["parts"] = cfg;
            doc.Extra["vtuber"] = vt;
        });
        App.Main?.BeginInvoke(() => Studio());
    }

    /// <summary>레이어 전체를 size×size 안에 맞춘 작은 그림</summary>
    static Bitmap Thumb(Surface s, int size)
    {
        double k = Math.Min((double)size / s.W, (double)size / s.H);
        int w = Math.Max(1, (int)(s.W * k)), h = Math.Max(1, (int)(s.H * k)), ox = (size - w) / 2, oy = (size - h) / 2;
        var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = Px.Unpremul(s.Get((int)((x + 0.5) / k), (int)((y + 0.5) / k)));
                if (c != 0) b.SetPixel(ox + x, oy + y, Color.FromArgb((int)(c >> 24), (int)(c >> 16) & 255, (int)(c >> 8) & 255, (int)c & 255));
            }
        return b;
    }

    // ---------------------------------------------------------------------- 스튜디오
    internal static StudioForm TestStudio(VRigData rig, Document doc) => new(rig, doc);

    public static void Studio(VRigData rigIn = null)
    {
        if (live != null && !live.IsDisposed) { App.Toast("버튜버 스튜디오가 이미 열려 있습니다"); live.Activate(); return; }
        var doc = App.Doc;
        if (rigIn == null && (doc?.Extra["vtuber"] as JsonObject)?["parts"] == null) { SetupDialog(); return; }
        VRigData rig;
        try { rig = rigIn ?? VRig.FromDoc(doc); }
        catch (Exception e) { App.Toast("아바타를 만들지 못했습니다: " + e.Message); return; }
        if (rig.Parts.Count == 0) { App.Toast("부위가 정해진 레이어가 없습니다. 부위를 먼저 정하세요"); SetupDialog(); return; }
        live = new StudioForm(rig, rigIn == null ? doc : null);
        live.FormClosed += (_, _) => live = null;
        live.Show(App.Main);
    }

    /// <summary>.cpvt 아바타 파일 열기 → 스튜디오</summary>
    public static void OpenAvatarFile()
    {
        using var dlg = new OpenFileDialog { Filter = "ComicPad 아바타 (*.cpvt)|*.cpvt;*.json" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        try { Studio(VRig.FromFile(JsonNode.Parse(File.ReadAllText(dlg.FileName)))); }
        catch (Exception e) { App.Toast(e.Message); }
    }

    internal sealed class StudioForm : Form
    {
        readonly VRigData rig;
        readonly Document doc;                 // 문서에서 만든 리그면 닫을 때 opts 를 문서에 (파일 리그는 null)
        readonly Ui3 ui = new();
        readonly Stage view;
        readonly Label status;
        readonly Panel meter = new() { Height = 8, Width = Ui3.W, BackColor = Opt.Bg2, Margin = new Padding(0, 4, 0, 4) };
        readonly Panel meterBar = new() { Height = 8, Width = 0, BackColor = Opt.Accent };
        readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly double seed = Random.Shared.NextDouble() * 10;
        readonly List<int> exprKeys;
        FlowLayoutPanel exprRow;
        VState state = VState.Neutral();
        readonly VState manual = VState.Neutral();       // 슬라이더·마우스로 정한 값
        readonly VPhysics ph = new();
        double last;
        int expr;
        bool autoBlink = true, idle = true, follow, talkKey, talkMouse;
        VMic mic;
        System.Windows.Forms.Button micBtn;
        BroadcastForm popup;
        string Bg { get => rig.Bg; set => rig.Bg = value; }

        public StudioForm(VRigData rig, Document doc)
        {
            this.rig = rig; this.doc = doc;
            Text = "버튜버 스튜디오";
            BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1060, 760); MinimumSize = new Size(760, 520);
            KeyPreview = true;
            exprKeys = rig.Parts.Where(p => p.Role == "expr").Select(p => p.Key).Distinct().OrderBy(k => k).ToList();

            var left = new Panel { Dock = DockStyle.Fill, BackColor = Opt.Bg, Padding = new Padding(8) };
            view = new Stage(this) { Dock = DockStyle.Fill };
            status = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Opt.Muted, Text = "카메라나 마이크를 켜면 따라 움직입니다. 꺼져 있으면 숨쉬기·깜빡임만. (그림을 끌면 고개, Space·오른쪽 버튼을 누르는 동안 말하기)" };
            left.Controls.Add(view); left.Controls.Add(status);
            var host = Ui3.Scroller(out var side);
            host.Dock = DockStyle.Right; host.Width = Ui3.W + 60;
            Controls.Add(left); Controls.Add(host);
            meter.Controls.Add(meterBar);

            var camBtn = Ui3.Button("카메라 얼굴 추적 켜기", () => SetStatus("카메라 얼굴 추적은 네이티브 판에서 아직 쓸 수 없습니다 (MediaPipe 얼굴 모델은 웹 판에서만 돌아갑니다). 마우스·슬라이더로 움직이세요."), true);
            micBtn = Ui3.Button("마이크로 입 움직이기", ToggleMic);
            side.Controls.Add(ui.Section("따라 움직이기", Ui3.Row(camBtn, micBtn), meter,
                ui.Toggle("거울처럼 (내가 오른쪽을 보면 캐릭터도 화면 오른쪽)", () => rig.Mirror, v => rig.Mirror = v)));

            string Pm(double v) => (v >= 0 ? "+" : "") + v.ToString("0.00");
            side.Controls.Add(ui.Section("손으로 움직이기",
                ui.Toggle("마우스를 따라 보기 (그림 위에서)", () => follow, v => follow = v),
                ui.Slider("고개 좌우", -1, 1, 0.01, () => manual.Yaw, v => manual.Yaw = v, Pm),
                ui.Slider("고개 위아래", -1, 1, 0.01, () => manual.Pitch, v => manual.Pitch = v, Pm),
                ui.Slider("고개 기울기", -1, 1, 0.01, () => manual.Roll, v => manual.Roll = v, Pm),
                ui.Slider("입 벌림", 0, 1, 0.01, () => manual.Mouth, v => manual.Mouth = v, v => Math.Round(v * 100) + "%"),
                ui.Slider("웃음", 0, 1, 0.01, () => manual.Smile, v => manual.Smile = v, v => Math.Round(v * 100) + "%"),
                ui.Slider("오므림 (ㅜ·ㅗ)", 0, 1, 0.01, () => manual.Funnel, v => manual.Funnel = v, v => Math.Round(v * 100) + "%"),
                ui.Slider("눈썹", -1, 1, 0.01, () => manual.Brow, v => manual.Brow = v, Pm),
                ui.Slider("눈동자 좌우", -1, 1, 0.01, () => manual.LookX, v => manual.LookX = v, Pm),
                ui.Slider("눈동자 위아래", -1, 1, 0.01, () => manual.LookY, v => manual.LookY = v, Pm),
                ui.Slider("눈 감기", 0, 1, 0.01, () => manual.BlinkL, v => manual.BlinkL = manual.BlinkR = v, v => Math.Round(v * 100) + "%"),
                Ui3.Row(ui.Toggle("자동 깜빡임", () => autoBlink, v => autoBlink = v), ui.Toggle("가만히 흔들림", () => idle, v => idle = v)),
                Ui3.Row(Ui3.Button("가운데로", () => { var t = manual; t.Yaw = t.Pitch = t.Roll = t.Mouth = t.Smile = t.Funnel = t.Brow = t.LookX = t.LookY = t.BlinkL = t.BlinkR = 0; ui.SyncAll(); }))));

            side.Controls.Add(ui.Section("조절",
                ui.Slider("움직임 크기", 0, 2, 0.05, () => rig.Motion, v => rig.Motion = v, v => Math.Round(v * 100) + "%"),
                ui.Slider("입 민감도", 0.2, 4, 0.1, () => rig.MouthGain, v => rig.MouthGain = v, v => v.ToString("0.0") + "배"),
                ui.Slider("부드럽게 (떨림 줄이기)", 0, 0.9, 0.05, () => rig.Smooth, v => rig.Smooth = v, v => Math.Round(v * 100) + "%")));

            exprRow = Ui3.Row();
            SyncExpr();
            side.Controls.Add(ui.Section("표정 (키보드 1~9, 0 = 기본)", exprKeys.Count > 0 ? exprRow : Ui3.Help("부위 정하기에서 \"표정\" 레이어에 키를 주면 여기서 켤 수 있습니다.")));

            side.Controls.Add(ui.Section("방송·녹화", Ui3.Help("배경 색"),
                ui.Segmented(new[] { ("green", "초록"), ("magenta", "자홍"), ("blue", "파랑"), ("none", "투명") }, () => Bg, v => Bg = v),
                Ui3.Row(Ui3.Button("방송 창 열기 (OBS 창 캡처용)", OpenPopup)),
                Ui3.Row(Ui3.Button("녹화 시작", () => SetStatus("녹화는 네이티브 판에서 아직 지원하지 않습니다. 방송 창을 OBS 로 녹화하거나 웹 판을 쓰세요.")), Ui3.Button("사진 저장", Snap)),
                Ui3.Help("방송 창은 캐릭터만 보입니다. OBS 의 \"창 캡처\"로 잡고 색 키로 배경을 빼세요.")));

            side.Controls.Add(ui.Section("아바타", Ui3.Row(
                doc != null ? Ui3.Button("부위 다시 정하기", () => { Close(); App.Main?.BeginInvoke(SetupDialog); }) : null,
                Ui3.Button(".cpvt 로 저장", SaveFile))));

            timer.Tick += (_, _) => Frame();
            Shown += (_, _) => { last = clock.Elapsed.TotalSeconds; timer.Start(); };
            FormClosing += (_, _) => StopAll();
        }

        void SetStatus(string t) => status.Text = t;
        // ---- 자체 점검용 ----
        internal void TestFrame() => Frame();
        internal VState TestManual => manual;
        internal VState TestState => state;
        internal void TestKey(Keys k) { var m = new Message(); ProcessCmdKey(ref m, k); }
        internal int TestExpr => expr;
        internal void TestPopup() => OpenPopup();

        void SyncExpr()
        {
            exprRow.SuspendLayout();
            foreach (Control c in exprRow.Controls.Cast<Control>().ToList()) c.Dispose();
            foreach (int k in new[] { 0 }.Concat(exprKeys))
            {
                int kk = k;
                exprRow.Controls.Add(Ui3.Button(k != 0 ? $"표정 {k}" : "기본 (0)", () => { expr = kk; SyncExpr(); }, expr == k));
            }
            exprRow.ResumeLayout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBoxBase) return base.ProcessCmdKey(ref msg, keyData);
            var k = keyData & Keys.KeyCode;
            if ((keyData & (Keys.Control | Keys.Alt)) == 0 && (k >= Keys.D0 && k <= Keys.D9 || k >= Keys.NumPad0 && k <= Keys.NumPad9))
            {
                int n = k >= Keys.NumPad0 ? k - Keys.NumPad0 : k - Keys.D0;
                expr = n == 0 || exprKeys.Contains(n) ? n : expr;
                SyncExpr();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space && ActiveControl is not TextBoxBase) { talkKey = true; e.Handled = e.SuppressKeyPress = true; } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) talkKey = false; base.OnKeyUp(e); }

        void ToggleMic()
        {
            if (mic != null) { mic.Dispose(); mic = null; micBtn.Text = "마이크로 입 움직이기"; meterBar.Width = 0; return; }
            try { mic = new VMic(); micBtn.Text = "마이크 끄기"; SetStatus("마이크 소리 크기로 입을 움직입니다 (소리는 저장·전송하지 않습니다)"); }
            catch (Exception e) { SetStatus("마이크를 켜지 못했습니다: " + e.Message); }
        }

        // ---- 움직이기 ----
        void Frame()
        {
            if (IsDisposed) return;
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - last; last = now;
            var next = manual.Clone();
            // 마우스: 그림을 끄는 동안 또는 "따라 보기" 일 때 고개·눈동자가 마우스 쪽으로
            var mp = view.PointToClient(Cursor.Position);
            bool over = view.ClientRectangle.Contains(mp);
            if (view.Dragging || (follow && over))
            {
                var fr = view.FitRect();
                double mx = Math.Clamp((mp.X - (fr.X + fr.Width / 2.0)) / (fr.Width / 2.0), -1, 1), my = Math.Clamp((mp.Y - (fr.Y + fr.Height * 0.35)) / (fr.Height / 2.0), -1, 1);
                next.Yaw = Math.Clamp(next.Yaw + mx, -1, 1); next.Pitch = Math.Clamp(next.Pitch + my, -1, 1);
                next.LookX = Math.Clamp(next.LookX + mx, -1, 1); next.LookY = Math.Clamp(next.LookY + my, -1, 1);
            }
            // 추적 없음: 자동 깜빡임 + 아주 작은 흔들림
            if (autoBlink && manual.BlinkL < 0.01) { double b = VRig.AutoBlink(now, seed); next.BlinkL = next.BlinkR = b; }
            if (idle) { next.Yaw += Math.Sin(now * 1000 / 2600) * 0.06; next.Roll += Math.Sin(now * 1000 / 3300) * 0.04; }
            next.Mouth *= rig.MouthGain;
            bool talking = talkKey || talkMouse;
            if (talking) next.Mouth = Math.Max(next.Mouth, 0.3 + 0.55 * Math.Abs(Math.Sin(now * 13)));
            if (mic != null)
            {
                double lv = mic.Level();
                double m = VRig.LevelToMouth(lv, rig.MouthGain);
                next.Mouth = Math.Max(next.Mouth, m);
                next.Talk = m > 0.15 || talking ? 1 : 0;
                meterBar.Width = (int)Math.Min(meter.Width, lv * 6 * meter.Width);
            }
            else next.Talk = next.Mouth > 0.2 ? 1 : 0;
            next.Mouth = Math.Min(1, next.Mouth);
            next.T = now; next.Expr = expr;
            state = VRig.SmoothState(state, next, rig.Smooth);
            state.T = next.T; state.Expr = expr;
            double sway = VRig.StepPhysics(ph, state, dt);
            view.Set(state, sway);
            if (popup != null && !popup.IsDisposed) popup.Set(state, sway);
        }

        void OpenPopup()
        {
            if (popup != null && !popup.IsDisposed) { popup.Activate(); return; }
            int w = Math.Min(1280, rig.W), h = (int)Math.Round((double)w * rig.H / rig.W);
            popup = new BroadcastForm(this) { ClientSize = new Size(w, h) };
            popup.Show();
            SetStatus("방송 창을 열었습니다. OBS 에서 \"창 캡처\" → 이 창, 색 키(크로마 키)로 배경 색을 빼세요");
        }

        void Snap()
        {
            using var bmp = VRig.RenderBitmap(rig, state, 0, 1, BgColor(Bg));
            string name = $"{SafeName(doc?.Name ?? "버튜버")}_버튜버_{Stamp()}.png";
            bmp.Save(Path.Combine(ExportsDir, name), ImageFormat.Png);
            SetStatus($"사진 저장: data/exports/{name}");
            App.Toast($"사진 저장: data/exports/{name}");
        }

        void SaveFile()
        {
            var json = VRig.ToFile(rig, doc?.Name ?? "아바타");
            string name = $"{SafeName((string)json["name"])}_{Stamp()}.cpvt";
            File.WriteAllText(Path.Combine(ExportsDir, name), json.ToJsonString());
            SetStatus($"아바타 파일 저장: data/exports/{name} (다른 작품 없이 이 파일만 열어 방송할 수 있습니다)");
        }

        void StopAll()
        {
            timer.Stop(); timer.Dispose();
            mic?.Dispose(); mic = null;
            if (popup != null && !popup.IsDisposed) popup.Close();
            if (doc != null)
            {
                // 조절 값은 작품에 기억 (웹 판처럼 실행취소 기록 없이)
                var vt = doc.Extra["vtuber"] as JsonObject ?? new JsonObject();
                vt["opts"] = rig.Opts.DeepClone();
                doc.Extra["vtuber"] = vt;
            }
            foreach (var p in rig.Parts) p.Img?.Dispose();
        }

        /// <summary>미리보기 (작게 그림 · 투명 배경은 바둑판)</summary>
        sealed class Stage : Control
        {
            readonly StudioForm f;
            VState st = VState.Neutral(); double sway;
            public bool Dragging;
            public Stage(StudioForm f) { this.f = f; DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); BackColor = Color.FromArgb(36, 36, 40); }
            public void Set(VState s, double sw) { st = s; sway = sw; Invalidate(); }
            public Rectangle FitRect()
            {
                double k = Math.Min((Width - 8.0) / f.rig.W, (Height - 8.0) / f.rig.H);
                int w = Math.Max(1, (int)(f.rig.W * k)), h = Math.Max(1, (int)(f.rig.H * k));
                return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Dragging = true; if (e.Button == MouseButtons.Right) f.talkMouse = true; }
            protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Dragging = false; if (e.Button == MouseButtons.Right) f.talkMouse = false; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(BackColor);
                var fr = FitRect();
                var bg = BgColor(f.Bg);
                if (bg == null) Checker(g, fr);
                double k = (double)fr.Width / f.rig.W;
                var s = g.Save();
                g.SetClip(fr);
                g.TranslateTransform(fr.X, fr.Y);
                // 화면용: 작은 그림에 바로 (Clear 대신 바탕만 칠함)
                using (var tmp = new Bitmap(fr.Width, fr.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var tg = Graphics.FromImage(tmp)) VRig.Render(tg, f.rig, st, sway, k, bg, fast: true);
                    g.ResetTransform();
                    g.DrawImageUnscaled(tmp, fr.X, fr.Y);
                }
                g.Restore(s);
            }
            static void Checker(Graphics g, Rectangle r)
            {
                using var a = new SolidBrush(Color.FromArgb(200, 200, 200)); using var b = new SolidBrush(Color.FromArgb(240, 240, 240));
                for (int y = r.Top; y < r.Bottom; y += 12) for (int x = r.Left; x < r.Right; x += 12)
                    g.FillRectangle(((x - r.Left) / 12 + (y - r.Top) / 12) % 2 == 0 ? a : b, x, y, Math.Min(12, r.Right - x), Math.Min(12, r.Bottom - y));
            }
        }

        /// <summary>방송 창: 캐릭터만 (OBS 창 캡처용)</summary>
        sealed class BroadcastForm : Form
        {
            readonly StudioForm f;
            VState st = VState.Neutral(); double sway;
            public BroadcastForm(StudioForm f)
            {
                this.f = f;
                Text = "ComicPad 버튜버 (방송용)";
                BackColor = Color.Black; DoubleBuffered = true; StartPosition = FormStartPosition.CenterScreen;
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                KeyPreview = true;
                KeyDown += (_, e) => { if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) { int n = e.KeyCode - Keys.D0; f.expr = n == 0 || f.exprKeys.Contains(n) ? n : f.expr; f.SyncExpr(); } if (e.KeyCode == Keys.Space) f.talkKey = true; };
                KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) f.talkKey = false; };
            }
            public void Set(VState s, double sw) { st = s; sway = sw; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Color.Black);
                double k = Math.Min((double)ClientSize.Width / f.rig.W, (double)ClientSize.Height / f.rig.H);
                int w = Math.Max(1, (int)(f.rig.W * k)), h = Math.Max(1, (int)(f.rig.H * k));
                using var tmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var tg = Graphics.FromImage(tmp)) VRig.Render(tg, f.rig, st, sway, k, BgColor(f.Bg));
                g.DrawImageUnscaled(tmp, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2);
            }
        }
    }
}
