// 클립 스튜디오 벡터 레이어 근사 그리기. 최근 CSP 는 벡터 레이어의 그림(픽셀)을 저장하지 않는 일이 많아서
// (dobrokot/clip_to_psd README 참고) 선 데이터로 직접 다시 그린다. 브러시 엔진은 공개되지 않았으므로 둥근 펜촉 근사.
// 형식 출처: nanikasheila/csp-format (MIT) docs/spec/05-vector.md — 설명만 보고 직접 구현.
//   VectorObjectList(LayerId, VectorData=외부 ID) → 외부 데이터: (u64 길이) 뒤에 객체들. 빅엔디언.
//   객체 머리: +0 u32 머리 크기, +8 u32 점 크기, +16 u32 점 개수, +20 u32 표시, +24 i32×4 외접 사각형,
//             +40 u32×3 주 색(위 8비트), +64 f64 불투명도, 머리 88: +76 f64 브러시 크기(반지름), 92: +80, 96(도형): +84 선 굵기
//   점: +0 f64 x, +8 f64 y, +36 f32 필압. 좌표는 레이어 픽셀 → 캔버스 = + LayerOffset.
//   점 잇기: 머리 88 은 3차 스플라인(접선 0.7×(다음−이전)), 92 는 직선. 표시 0x1000 = 닫힌 도형.
using System.Buffers.Binary;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.ClipImport;

public static class ClipVector
{
    /// <summary>layerId 의 벡터 선들을 s 에 그림. 그린 선 개수</summary>
    public static int Render(SqliteReader db, ClipContainer c, long layerId, Surface s, int offX, int offY, Action<string> note)
    {
        var t = db.Table("VectorObjectList");
        if (t == null || t.Col("LayerId") < 0 || t.Col("VectorData") < 0) return 0;
        int n = 0;
        foreach (var r in t.Rows)
        {
            if (r[t.Col("LayerId")] is not long lid || lid != layerId) continue;
            string id = r[t.Col("VectorData")] switch { string str => str.TrimEnd('\0'), byte[] b => System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0'), _ => null };
            byte[] d;
            try { d = c.ExtaData(id); } catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            if (d == null) continue;
            try { n += DrawObjects(d, s, offX, offY); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { note("벡터 데이터 일부를 읽지 못했습니다"); }
        }
        return n;
    }

    public static int DrawObjects(byte[] d, Surface s, int offX, int offY)
    {
        int p = 0, n = 0;
        while (p + 72 <= d.Length)
        {
            int hs = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            int ps = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 8));
            int cnt = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 16));
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 20));
            if (hs < 72 || hs > 4096 || ps < 40 || ps > 4096 || cnt < 0 || (long)p + hs + (long)cnt * ps > d.Length) break;
            uint cr = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 40)) >> 24, cg = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 44)) >> 24, cb = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 48)) >> 24;
            double op = BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p + 64));
            if (!(op >= 0 && op <= 1)) op = 1;
            double size = 0; bool spline = false; bool shape = false;
            if (hs == 88) { size = F64(d, p + 76); spline = true; }
            else if (hs == 92) size = F64(d, p + 80);
            else if (hs == 96)
            {
                uint st = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 72));
                shape = true; size = F64(d, p + 84) / 2;
                if (st == 0x410) size = 0;      // 컷 테두리 (폴더에서 다시 만드는 것) → 그리지 않음
            }
            if (!(size > 0 && size < 2000)) size = hs == 96 ? 0 : 1.5;
            var pts = new List<(double x, double y, double pr)>(cnt);
            for (int i = 0; i < cnt; i++)
            {
                int q = p + hs + i * ps;
                double x = F64(d, q), y = F64(d, q + 8);
                float pr = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(q + 36));
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                pts.Add((x + offX, y + offY, pr > 0 && pr <= 1 ? pr : 1));
            }
            bool closed = (flags & 0x1000) != 0 || shape;
            if (size > 0 && pts.Count > 0) { Stroke(s, pts, size, closed, spline, Px.Pack(255, cr, cg, cb), op); n++; }
            p += hs + cnt * ps;
        }
        return n;
    }

    static double F64(byte[] d, int o) => BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(o));

    /// <summary>둥근 펜촉 도장을 촘촘히 찍어 한 획 (획 안에서는 겹쳐도 진해지지 않게 최댓값)</summary>
    static void Stroke(Surface s, List<(double x, double y, double pr)> pts, double size, bool closed, bool spline, uint color, double op)
    {
        // 필압 → 굵기 (CSP 의 필압 곡선은 브러시마다 달라 근사: 30% + 70%×필압)
        double R(double pr) => Math.Max(0.5, size * (0.3 + 0.7 * pr));
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y, _) in pts) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        int pad = (int)Math.Ceiling(size) + 3;
        var box = Rect.FromLTRB((int)Math.Floor(minX) - pad, (int)Math.Floor(minY) - pad, (int)Math.Ceiling(maxX) + pad, (int)Math.Ceiling(maxY) + pad).Clip(s.W, s.H);
        if (box.IsEmpty) return;
        var cov = new byte[box.W * box.H];
        void Stamp(double cx, double cy, double rr)
        {
            int x0 = Math.Max(box.X, (int)Math.Floor(cx - rr - 1)), x1 = Math.Min(box.R - 1, (int)Math.Ceiling(cx + rr + 1));
            int y0 = Math.Max(box.Y, (int)Math.Floor(cy - rr - 1)), y1 = Math.Min(box.B - 1, (int)Math.Ceiling(cy + rr + 1));
            for (int y = y0; y <= y1; y++)
            {
                double dy = y + 0.5 - cy;
                int row = (y - box.Y) * box.W - box.X;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x + 0.5 - cx;
                    double v = rr + 0.5 - Math.Sqrt(dx * dx + dy * dy);
                    if (v <= 0) continue;
                    byte b = v >= 1 ? (byte)255 : (byte)(v * 255);
                    if (b > cov[row + x]) cov[row + x] = b;
                }
            }
        }
        int m = pts.Count;
        if (m == 1) Stamp(pts[0].x, pts[0].y, R(pts[0].pr));
        int segs = closed && m > 2 ? m : m - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % m];
            // 에르미트 접선 (스플라인일 때)
            (double x, double y) Tan(int k)
            {
                if (!spline) return (0, 0);
                bool hasPrev = closed || k > 0, hasNext = closed || k < m - 1;
                var pk = pts[(k + m) % m];
                if (hasPrev && hasNext) { var pp = pts[(k - 1 + m) % m]; var pn = pts[(k + 1) % m]; return (0.7 * (pn.x - pp.x) / 2, 0.7 * (pn.y - pp.y) / 2); }
                if (hasNext) { var pn = pts[(k + 1) % m]; return (0.55 * (pn.x - pk.x), 0.55 * (pn.y - pk.y)); }
                if (hasPrev) { var pp = pts[(k - 1 + m) % m]; return (0.55 * (pk.x - pp.x), 0.55 * (pk.y - pp.y)); }
                return (0, 0);
            }
            var ta = Tan(i); var tb = Tan((i + 1) % m);
            double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
            double rr0 = R(a.pr), rr1 = R(b.pr);
            double step = Math.Max(0.35, Math.Min(rr0, rr1) * 0.3);
            int k = Math.Max(1, (int)Math.Ceiling(len / step));
            for (int j = 0; j <= k; j++)
            {
                double u = j / (double)k, u2 = u * u, u3 = u2 * u;
                double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
                double x = h00 * a.x + h10 * ta.x + h01 * b.x + h11 * tb.x;
                double y = h00 * a.y + h10 * ta.y + h01 * b.y + h11 * tb.y;
                Stamp(x, y, rr0 + (rr1 - rr0) * u);
            }
        }
        // 덮어 그리기 (표준 합성, 획 불투명도)
        var buf = s.Read(box);
        bool any = false;
        for (int i = 0; i < buf.Length; i++)
        {
            int cv = cov[i];
            if (cv == 0) continue;
            any = true;
            uint k = (uint)Math.Round(cv * op);
            buf[i] = Px.Over(Px.Scale(color, k), buf[i]);
        }
        if (any) s.Write(box, buf);
    }
}
