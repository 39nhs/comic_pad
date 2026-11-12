// 기능 검색 (웹 ui/palette.js · .palette-modal): 위쪽(8vh)에 뜨는 560px 창, 큰 검색 칸(42px, 16px 글자), 아래 목록(칸 38px, 고른 칸 주황).
// 메뉴·도구 이름으로 찾고, 한글 초성(ㄱㅅ → 곡선)도 됨. 위·아래 키 + 엔터, 두 번 누르기.
using ComicPadNative.UI.Controls;

namespace ComicPadNative.UI.Dialogs;

public static class CommandPalette
{
    sealed record Item(string Label, string Sub, string Key, Action Run, string Search);

    public static void Show()
    {
        var all = Registry.Menus.Where(m => m.Run != null && !m.Path.Split('/').Last().StartsWith("-") && m.Path.Split('/').Last().Length > 0)
            .Select(m =>
            {
                var parts = m.Path.Split('/');
                string label = parts[^1].TrimEnd('…');
                string sub = string.Join(" › ", parts.Take(parts.Length - 1));
                return new Item(label, sub, m.Shortcut != Keys.None ? new KeysConverter().ConvertToString(m.Shortcut) : null, m.Run, m.Path);
            })
            .Concat(Registry.Tools.Select(t => new Item(t.Name, "도구", t.Key != Keys.None ? t.Key.ToString() : null, () => App.SetTool(t.Id), "도구/" + t.Name)))
            .ToList();
        using var f = new ModalForm("기능 검색", 560, 0);
        var q = new WebInput("") { Placeholder = "기능 이름 검색 (예: 곡선, 톤, ㄱㅅ)…", Margin = Padding.Empty };
        q.Box.Font = Web.F(16);
        var list = new WebList { Height = 495, Margin = new Padding(0, 8, 0, 0), RowH = 38, Bg = Theme.Panel };
        list.Stretch();
        var st = new WebStack(); st.Controls.Add(q); st.Controls.Add(list);
        f.SetContent(st);
        List<Item> shown = all;
        void Fill()
        {
            string s = q.Text.Trim();
            shown = s.Length == 0 ? all : all.Where(x => Fuzzy(x.Search.Replace("/", " "), s)).OrderBy(x => Rank(x, s)).ToList();
            list.Items = shown.Select(x => x.Label).ToList();
            list.Sub = shown.Select(x => x.Sub + (x.Key != null ? "  ·  " + x.Key : "")).ToList();
            list.Selected = shown.Count > 0 ? 0 : -1;
            list.Reset();
        }
        Action chosen = null; string chosenName = null;
        void Go(int i) { if (i >= 0 && i < shown.Count) { chosen = shown[i].Run; chosenName = shown[i].Search; f.DialogResult = DialogResult.OK; f.Close(); } }
        q.TextChanged += (_, _) => Fill();
        q.Box.KeyDown += (_, e) =>
        {
            if (list.Move(e.KeyCode)) { e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { Go(list.Selected); e.Handled = true; e.SuppressKeyPress = true; }
        };
        list.Picked += Go;
        f.Primary = () => Go(list.Selected);
        Fill();
        // 웹: 창이 위쪽 (margin-top 8vh) 에
        f.Shown += (_, _) =>
        {
            var owner = App.Main;
            if (owner != null && owner.Visible) f.Top = owner.Top + Math.Max(40, (int)(owner.Height * 0.08));
            q.Box.Focus();
        };
        f.ShowDialog(App.Main);
        if (chosen != null) MainForm.Run(chosen, chosenName ?? "기능 검색");
    }

    static int Rank(Item x, string s)
    {
        string t = x.Label.Replace(" ", "").ToLowerInvariant(), q = s.Replace(" ", "").ToLowerInvariant();
        return t.StartsWith(q) ? 0 : t.Contains(q) ? 1 : x.Search.ToLowerInvariant().Contains(q) ? 2 : 3;
    }

    static readonly char[] Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ".ToCharArray();
    static string Initials(string s) => new(s.Select(c => c >= '가' && c <= '힣' ? Cho[(c - '가') / 588] : c).ToArray());

    /// <summary>글자들이 순서대로 들어 있으면 (띄어쓰기 무시, 초성만 써도)</summary>
    public static bool Fuzzy(string text, string q)
    {
        text = text.Replace(" ", "").ToLowerInvariant(); q = q.Replace(" ", "").ToLowerInvariant();
        if (text.Contains(q)) return true;
        bool hasCho = q.Any(c => c >= 'ㄱ' && c <= 'ㅎ');
        string t2 = hasCho ? Initials(text) : text;
        if (t2.Contains(q)) return true;
        int i = 0; foreach (char c in t2) if (i < q.Length && c == q[i]) i++;
        return i == q.Length;
    }
}
