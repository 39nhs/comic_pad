// 그림 파일 만들기 (외부 라이브러리 없이): PNG · APNG (웹 animexport.js) · GIF (웹 anim.js 의 인코더 — 미디언 컷 + 오차 확산 + LZW)
using System.IO.Compression;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

/// <summary>그림 한 장 (미리 곱한 알파 BGRA)</summary>
public sealed class Img
{
    public int W, H; public uint[] P;
    public Img(int w, int h, uint[] p = null) { W = w; H = h; P = p ?? new uint[w * h]; }
    /// <summary>RGBA 바이트 (미리 곱하지 않은)</summary>
    public byte[] Rgba()
    {
        var o = new byte[W * H * 4];
        for (int i = 0; i < P.Length; i++)
        {
            uint u = Px.Unpremul(P[i]);
            o[i * 4] = (byte)(u >> 16); o[i * 4 + 1] = (byte)(u >> 8); o[i * 4 + 2] = (byte)u; o[i * 4 + 3] = (byte)(u >> 24);
        }
        return o;
    }
    public static Img FromRgba(byte[] d, int w, int h)
    {
        var im = new Img(w, h);
        for (int i = 0; i < w * h; i++) im.P[i] = Px.Premul(Px.Pack(d[i * 4 + 3], d[i * 4], d[i * 4 + 1], d[i * 4 + 2]));
        return im;
    }
    public Img Crop(Rect r)
    {
        var o = new Img(r.W, r.H);
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                int sx = r.X + x, sy = r.Y + y;
                if ((uint)sx < (uint)W && (uint)sy < (uint)H) o.P[y * r.W + x] = P[sy * W + sx];
            }
        return o;
    }
    /// <summary>투명한 가장자리를 잘라낸 영역 (없으면 1x1)</summary>
    public Rect OpaqueBounds()
    {
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if ((P[y * W + x] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        return x1 < 0 ? new Rect(0, 0, 1, 1) : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }
    public Bitmap ToBitmap()
    {
        var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[W];
            for (int y = 0; y < H; y++) { for (int x = 0; x < W; x++) row[x] = (int)Px.Unpremul(P[y * W + x]); System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, W); }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }
    public static Img FromBitmap(Bitmap bmp)
    {
        var im = new Img(bmp.Width, bmp.Height);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[bmp.Width];
            for (int y = 0; y < bmp.Height; y++) { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bmp.Width); for (int x = 0; x < bmp.Width; x++) im.P[y * bmp.Width + x] = Px.Premul((uint)row[x]); }
        }
        finally { bmp.UnlockBits(bd); }
        return im;
    }
}

public static class Png
{
    static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    public static uint Crc32(byte[] b, int off, int len, uint crc = 0xFFFFFFFF)
    {
        for (int i = off; i < off + len; i++) crc = crcTable[(crc ^ b[i]) & 255] ^ (crc >> 8);
        return crc;
    }
    static void BE32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    public static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    public static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };

    public static void Chunk(Stream s, string type, byte[] data)
    {
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        BE32(s, (uint)data.Length);
        s.Write(body);
        BE32(s, Crc32(body, 0, body.Length) ^ 0xFFFFFFFF);
    }

    public static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    public static byte[] Ihdr(int w, int h) => Be32((uint)w).Concat(Be32((uint)h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();

    /// <summary>RGBA 8비트 그림을 zlib 로 묶은 IDAT 내용 (줄마다 가장 작은 필터)</summary>
    public static byte[] ImageData(Img im)
    {
        int w = im.W, h = im.H, stride = w * 4;
        var rgba = im.Rgba();
        var raw = new byte[(stride + 1) * h];
        var cand = new byte[5][];
        for (int f = 0; f < 5; f++) cand[f] = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int ro = y * stride, po = (y - 1) * stride;
            long best = long.MaxValue; int bf = 0;
            for (int f = 0; f < 5; f++)
            {
                var c = cand[f]; long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int x = rgba[ro + i], a = i >= 4 ? rgba[ro + i - 4] : 0, b = y > 0 ? rgba[po + i] : 0, cc = y > 0 && i >= 4 ? rgba[po + i - 4] : 0;
                    int v = f switch { 0 => x, 1 => x - a, 2 => x - b, 3 => x - ((a + b) >> 1), _ => x - Paeth(a, b, cc) };
                    c[i] = (byte)v;
                    sum += (sbyte)c[i] < 0 ? -(sbyte)c[i] : (sbyte)c[i];
                }
                if (sum < best) { best = sum; bf = f; }
            }
            raw[y * (stride + 1)] = (byte)bf;
            Buffer.BlockCopy(cand[bf], 0, raw, y * (stride + 1) + 1, stride);
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw);
        return ms.ToArray();
    }
    static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }

    public static byte[] Encode(Img im)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Chunk(ms, "IHDR", Ihdr(im.W, im.H));
        Chunk(ms, "IDAT", ImageData(im));
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>APNG: acTL / fcTL / fdAT (지연 = ms/1000, 투명이면 다음 프레임 전에 지우기)</summary>
    public static byte[] EncodeApng(IList<(Img img, int ms)> frames, int loop = 0, bool transparent = false)
    {
        if (frames.Count == 0) throw new InvalidOperationException("프레임이 없습니다");
        int w = frames[0].img.W, h = frames[0].img.H;
        using var ms = new MemoryStream();
        ms.Write(Signature);
        Chunk(ms, "IHDR", Ihdr(w, h));
        Chunk(ms, "acTL", Be32((uint)frames.Count).Concat(Be32((uint)loop)).ToArray());     // 반복 횟수 (0 = 무한)
        uint seq = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var fc = Be32(seq++).Concat(Be32((uint)w)).Concat(Be32((uint)h)).Concat(Be32(0)).Concat(Be32(0))
                .Concat(Be16(Math.Min(65535, frames[i].ms))).Concat(Be16(1000)).Concat(new byte[] { (byte)(transparent ? 1 : 0), 0 }).ToArray();
            Chunk(ms, "fcTL", fc);
            var data = ImageData(frames[i].img);
            if (i == 0) Chunk(ms, "IDAT", data);
            else Chunk(ms, "fdAT", Be32(seq++).Concat(data).ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}

public static class Gif
{
    static int K15(int r, int g, int b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    public sealed class Pal { public byte[] P = new byte[256 * 3]; public int Size; }

    /// <summary>미디언 컷 팔레트 (data = RGBA 바이트)</summary>
    public static Pal BuildPalette(byte[] data, int maxColors = 256)
    {
        int n = data.Length / 4, step = Math.Max(1, n / 120000);
        var hist = new Dictionary<int, int>();
        for (int i = 0; i < n; i += step)
        {
            int p = i * 4;
            if (data[p + 3] < 128) continue;                      // 투명한 곳은 팔레트에 넣지 않음
            int k = K15(data[p], data[p + 1], data[p + 2]);
            hist[k] = hist.GetValueOrDefault(k) + 1;
        }
        var cols = hist.Select(kv => new[] { ((kv.Key >> 10) & 31) * 8 + 4, ((kv.Key >> 5) & 31) * 8 + 4, (kv.Key & 31) * 8 + 4, kv.Value }).ToList();
        if (cols.Count == 0) cols.Add(new[] { 255, 255, 255, 1 });
        var boxes = new List<List<int[]>> { cols };
        int Range(List<int[]> box, int ch) { int lo = 255, hi = 0; foreach (var x in box) { if (x[ch] < lo) lo = x[ch]; if (x[ch] > hi) hi = x[ch]; } return hi - lo; }
        int target = Math.Max(2, Math.Min(256, maxColors));
        while (boxes.Count < target)
        {
            // 가장 넓고 픽셀 많은 상자를 자른다
            int best = -1, bestCh = 0; double bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Count < 2) continue;
                long count = 0; foreach (var x in box) count += x[3];
                for (int ch = 0; ch < 3; ch++) { double sc = Range(box, ch) * Math.Sqrt(count); if (sc > bestScore) { bestScore = sc; best = i; bestCh = ch; } }
            }
            if (best < 0) break;
            int bc = bestCh;
            var sorted = boxes[best].OrderBy(x => x[bc]).ToList();
            long total = 0; foreach (var x in sorted) total += x[3];
            long acc = 0; int cut = 1;
            for (int i = 0; i < sorted.Count - 1; i++) { acc += sorted[i][3]; if (acc >= total / 2.0) { cut = i + 1; break; } }
            boxes.RemoveAt(best);
            boxes.Insert(best, sorted.GetRange(cut, sorted.Count - cut));
            boxes.Insert(best, sorted.GetRange(0, cut));
        }
        var pal = new Pal { Size = boxes.Count };
        for (int i = 0; i < boxes.Count; i++)
        {
            double r = 0, g = 0, b = 0, c = 0;
            foreach (var x in boxes[i]) { r += x[0] * x[3]; g += x[1] * x[3]; b += x[2] * x[3]; c += x[3]; }
            pal.P[i * 3] = (byte)Math.Round(r / c); pal.P[i * 3 + 1] = (byte)Math.Round(g / c); pal.P[i * 3 + 2] = (byte)Math.Round(b / c);
        }
        return pal;
    }

    /// <summary>팔레트 번호로 (오차 확산 디더링, transparent ≥ 0 이면 반 넘게 투명한 곳은 그 번호)</summary>
    public static byte[] Quantize(byte[] data, int w, int h, Pal P, bool dither = true, int transparent = -1)
    {
        var pal = P.P; int size = P.Size;
        var cache = new short[32768]; Array.Fill(cache, (short)-1);
        int Nearest(int r, int g, int b)
        {
            int k = K15(r, g, b);
            if (cache[k] >= 0) return cache[k];
            int bi = 0; long bd = long.MaxValue;
            for (int i = 0; i < size; i++)
            {
                int dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b;
                long dd = dr * dr * 2 + dg * dg * 4 + db * db * 3;
                if (dd < bd) { bd = dd; bi = i; }
            }
            cache[k] = (short)bi;
            return bi;
        }
        var o = new byte[w * h];
        var err = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = i * 4;
                if (transparent >= 0 && data[p + 3] < 128) { o[i] = (byte)transparent; continue; }
                float r = Math.Clamp(data[p] + err[i * 3], 0, 255), g = Math.Clamp(data[p + 1] + err[i * 3 + 1], 0, 255), b = Math.Clamp(data[p + 2] + err[i * 3 + 2], 0, 255);
                int ci = Nearest((int)r, (int)g, (int)b);
                o[i] = (byte)ci;
                if (!dither) continue;
                float er = r - pal[ci * 3], eg = g - pal[ci * 3 + 1], eb = b - pal[ci * 3 + 2];
                void Spread(int dx, int dy, float f)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || xx >= w || yy >= h) return;
                    int j = (yy * w + xx) * 3;
                    err[j] += er * f; err[j + 1] += eg * f; err[j + 2] += eb * f;
                }
                Spread(1, 0, 7 / 16f); Spread(-1, 1, 3 / 16f); Spread(0, 1, 5 / 16f); Spread(1, 1, 1 / 16f);
            }
        return o;
    }

    public static List<byte> Lzw(byte[] indices, int minSize = 8)
    {
        int clear = 1 << minSize, eoi = clear + 1;
        int size = minSize + 1, next = eoi + 1;
        var dict = new Dictionary<int, int>();
        var o = new List<byte>(indices.Length / 2);
        int cur = 0, bits = 0;
        void Emit(int code) { cur |= code << bits; bits += size; while (bits >= 8) { o.Add((byte)(cur & 255)); cur >>= 8; bits -= 8; } }
        Emit(clear);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i], key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out int hit)) { prefix = hit; continue; }
            Emit(prefix);
            if (next == 4096) { Emit(clear); dict.Clear(); size = minSize + 1; next = eoi + 1; }
            else { if (next >= (1 << size)) size++; dict[key] = next++; }
            prefix = k;
        }
        Emit(prefix);
        Emit(eoi);
        if (bits > 0) o.Add((byte)(cur & 255));
        return o;
    }

    /// <summary>GIF 만들기. loop: 0 = 무한, n = n번. colors 2~256. transparent = 배경 투명</summary>
    public static byte[] Encode(IList<(Img img, int ms)> frames, int loop = 0, bool dither = true, int colors = 256, bool transparent = false)
    {
        if (frames.Count == 0) throw new InvalidOperationException("프레임이 없습니다");
        int w = frames[0].img.W, hh = frames[0].img.H;
        colors = Math.Max(2, Math.Min(256, colors));
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)(v & 255)); bytes.Add((byte)((v >> 8) & 255)); }
        void Str(string s) { foreach (var c in s) bytes.Add((byte)c); }
        Str("GIF89a"); U16(w); U16(hh); bytes.Add(0x70); bytes.Add(0); bytes.Add(0);   // 전역 팔레트 없음 (프레임마다 자기 팔레트)
        if (loop != 1) { bytes.Add(0x21); bytes.Add(0xff); bytes.Add(0x0b); Str("NETSCAPE2.0"); bytes.Add(3); bytes.Add(1); U16(loop == 0 ? 0 : loop - 1); bytes.Add(0); }
        foreach (var (img, ms) in frames)
        {
            var data = img.Rgba();
            var P = BuildPalette(data, transparent ? colors - 1 : colors);
            int ti = transparent ? P.Size : -1;
            if (transparent) P.Size = Math.Min(256, P.Size);
            var idx = Quantize(data, w, hh, P, dither, ti);
            // 그래픽 제어: 투명이면 다음 프레임 전에 지우기(2) + 투명색 번호
            bytes.Add(0x21); bytes.Add(0xf9); bytes.Add(4); bytes.Add((byte)(transparent ? 0x09 : 0x04)); U16(Math.Max(2, (int)Math.Round(ms / 10.0))); bytes.Add((byte)(transparent ? ti : 0)); bytes.Add(0);
            bytes.Add(0x2c); U16(0); U16(0); U16(w); U16(hh); bytes.Add(0x87);
            for (int k = 0; k < 256; k++) { bytes.Add(P.P[k * 3]); bytes.Add(P.P[k * 3 + 1]); bytes.Add(P.P[k * 3 + 2]); }
            bytes.Add(8);
            var lz = Lzw(idx, 8);
            for (int p = 0; p < lz.Count; p += 255)
            {
                int len = Math.Min(255, lz.Count - p);
                bytes.Add((byte)len);
                for (int k = 0; k < len; k++) bytes.Add(lz[p + k]);
            }
            bytes.Add(0);
        }
        bytes.Add(0x3b);
        return bytes.ToArray();
    }
}
