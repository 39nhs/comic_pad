"""공식 PyTorch 가중치 → ONNX 변환 (ComicPad 내장 AI 용).

사용: python tools/convert_models.py <kind> <원본 파일> <결과 model.onnx>
kind: srvgg (Real-ESRGAN animevideov3) / rrdb6 (Real-ESRGAN anime 6B) / a2s (Anime2Sketch) / mle (MangaLineExtraction) / p2p (스케치 채색 pix2pix)

PyTorch 와 onnx 패키지가 필요합니다 (pip install torch onnx).
가중치는 weights_only=True 로만 읽습니다 (파일 속 코드를 실행하지 않음).
"""
import functools
import json
import struct
import sys

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F


# ---------------------------------------------------------------- Real-ESRGAN
class SRVGGNetCompact(nn.Module):
    def __init__(self, num_feat=64, num_conv=16, upscale=4):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList([nn.Conv2d(3, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)])
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            self.body.append(nn.PReLU(num_parameters=num_feat))
        self.body.append(nn.Conv2d(num_feat, 3 * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out = x
        for m in self.body:
            out = m(out)
        out = self.upsampler(out)
        return out + F.interpolate(x, scale_factor=self.upscale, mode="nearest")


class RDB(nn.Module):
    def __init__(self, nf=64, gc=32):
        super().__init__()
        self.conv1 = nn.Conv2d(nf, gc, 3, 1, 1)
        self.conv2 = nn.Conv2d(nf + gc, gc, 3, 1, 1)
        self.conv3 = nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1)
        self.conv4 = nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1)
        self.conv5 = nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, nf, gc=32):
        super().__init__()
        self.rdb1, self.rdb2, self.rdb3 = RDB(nf, gc), RDB(nf, gc), RDB(nf, gc)

    def forward(self, x):
        return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x


class RRDBNet(nn.Module):
    def __init__(self, num_feat=64, num_block=6, num_grow_ch=32):
        super().__init__()
        self.conv_first = nn.Conv2d(3, num_feat, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(num_feat, num_grow_ch) for _ in range(num_block)])
        self.conv_body = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_hr = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_last = nn.Conv2d(num_feat, 3, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, True)

    def forward(self, x):
        feat = self.conv_first(x)
        feat = feat + self.conv_body(self.body(feat))
        feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
        feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
        return self.conv_last(self.lrelu(self.conv_hr(feat)))


# ---------------------------------------------------------------- Anime2Sketch
class UnetSkip(nn.Module):
    def __init__(self, outer_nc, inner_nc, input_nc=None, submodule=None, outermost=False, innermost=False):
        super().__init__()
        norm = functools.partial(nn.InstanceNorm2d, affine=False, track_running_stats=False)
        self.outermost = outermost
        input_nc = input_nc or outer_nc
        downconv = nn.Conv2d(input_nc, inner_nc, 4, 2, 1, bias=True)
        if outermost:
            model = [downconv, submodule, nn.ReLU(True), nn.ConvTranspose2d(inner_nc * 2, outer_nc, 4, 2, 1), nn.Tanh()]
        elif innermost:
            model = [nn.LeakyReLU(0.2, True), downconv, nn.ReLU(True), nn.ConvTranspose2d(inner_nc, outer_nc, 4, 2, 1, bias=True), norm(outer_nc)]
        else:
            model = [nn.LeakyReLU(0.2, True), downconv, norm(inner_nc), submodule, nn.ReLU(True),
                     nn.ConvTranspose2d(inner_nc * 2, outer_nc, 4, 2, 1, bias=True), norm(outer_nc)]
        self.model = nn.Sequential(*model)

    def forward(self, x):
        return self.model(x) if self.outermost else torch.cat([x, self.model(x)], 1)


class UnetGenerator(nn.Module):
    def __init__(self, input_nc=3, output_nc=1, num_downs=8, ngf=64):
        super().__init__()
        b = UnetSkip(ngf * 8, ngf * 8, innermost=True)
        for _ in range(num_downs - 5):
            b = UnetSkip(ngf * 8, ngf * 8, submodule=b)
        b = UnetSkip(ngf * 4, ngf * 8, submodule=b)
        b = UnetSkip(ngf * 2, ngf * 4, submodule=b)
        b = UnetSkip(ngf, ngf * 2, submodule=b)
        self.model = UnetSkip(output_nc, ngf, input_nc=input_nc, submodule=b, outermost=True)

    def forward(self, x):
        return self.model(x)


# ---------------------------------------------------------------- pix2pix 스케치 채색 (initial_down/down1~6/up1~7 구조)
class P2PBlock(nn.Module):
    def __init__(self, cin, cout, down=True, act="relu", use_dropout=False):
        super().__init__()
        self.conv = nn.Sequential(
            nn.Conv2d(cin, cout, 4, 2, 1, bias=False, padding_mode="reflect") if down else nn.ConvTranspose2d(cin, cout, 4, 2, 1, bias=False),
            nn.BatchNorm2d(cout),
            nn.ReLU() if act == "relu" else nn.LeakyReLU(0.2))
        self.dropout = nn.Dropout(0.5) if use_dropout else nn.Identity()

    def forward(self, x):
        return self.dropout(self.conv(x))


class P2PGenerator(nn.Module):
    def __init__(self, cin=3, f=64):
        super().__init__()
        self.initial_down = nn.Sequential(nn.Conv2d(cin, f, 4, 2, 1, padding_mode="reflect"), nn.LeakyReLU(0.2))
        self.down1 = P2PBlock(f, f * 2, True, "leaky")
        self.down2 = P2PBlock(f * 2, f * 4, True, "leaky")
        self.down3 = P2PBlock(f * 4, f * 8, True, "leaky")
        self.down4 = P2PBlock(f * 8, f * 8, True, "leaky")
        self.down5 = P2PBlock(f * 8, f * 8, True, "leaky")
        self.down6 = P2PBlock(f * 8, f * 8, True, "leaky")
        self.bottleneck = nn.Sequential(nn.Conv2d(f * 8, f * 8, 4, 2, 1), nn.ReLU())
        self.up1 = P2PBlock(f * 8, f * 8, False, "relu", True)
        self.up2 = P2PBlock(f * 16, f * 8, False, "relu", True)
        self.up3 = P2PBlock(f * 16, f * 8, False, "relu", True)
        self.up4 = P2PBlock(f * 16, f * 8, False, "relu")
        self.up5 = P2PBlock(f * 16, f * 4, False, "relu")
        self.up6 = P2PBlock(f * 8, f * 2, False, "relu")
        self.up7 = P2PBlock(f * 4, f, False, "relu")
        self.final_up = nn.Sequential(nn.ConvTranspose2d(f * 2, cin, 4, 2, 1), nn.Tanh())

    def forward(self, x):
        d1 = self.initial_down(x); d2 = self.down1(d1); d3 = self.down2(d2); d4 = self.down3(d3)
        d5 = self.down4(d4); d6 = self.down5(d5); d7 = self.down6(d6); b = self.bottleneck(d7)
        u1 = self.up1(b); u2 = self.up2(torch.cat([u1, d7], 1)); u3 = self.up3(torch.cat([u2, d6], 1))
        u4 = self.up4(torch.cat([u3, d5], 1)); u5 = self.up5(torch.cat([u4, d4], 1)); u6 = self.up6(torch.cat([u5, d3], 1))
        u7 = self.up7(torch.cat([u6, d2], 1))
        return self.final_up(torch.cat([u7, d1], 1))


# ---------------------------------------------------------------- MangaLineExtraction
MLE = dict(eps=0.001, k=3, patch=24, cin=1, last=16, slope=0.2, enc=[2, 3, 5, 7, 12], dec=[7, 5, 3, 2, 2], up=2)


class BNAct(nn.Module):
    def __init__(self, c):
        super().__init__()
        self.norm = nn.BatchNorm2d(c, eps=MLE["eps"])
        self.act_fn = nn.LeakyReLU(MLE["slope"])

    def forward(self, x):
        return self.act_fn(self.norm(x))


class ResBlock(nn.Module):
    def __init__(self, cin, cout, stride):
        super().__init__()
        k = MLE["k"]
        self.norm1, self.conv1 = BNAct(cin), nn.Conv2d(cin, cout, k, stride=stride, padding=k // 2)
        self.norm2, self.conv2 = BNAct(cout), nn.Conv2d(cout, cout, k, stride=1, padding=k // 2)
        self.resize = nn.Conv2d(cin, cout, 1, stride=stride) if (cin != cout or stride != 1) else None

    def forward(self, x):
        o = self.conv2(self.norm2(self.conv1(self.norm1(x))))
        return o + (self.resize(x) if self.resize is not None else x)


class EncLayer(nn.Module):
    def __init__(self, cin, cout, n, strides):
        super().__init__()
        self.blocks = nn.ModuleList([ResBlock(cin if i == 0 else cout, cout, strides[i]) for i in range(n)])

    def forward(self, x):
        for b in self.blocks:
            x = b(x)
        return x


class UpBlock(nn.Module):
    def __init__(self, cin, cout):
        super().__init__()
        self.norm, self.conv = BNAct(cin), nn.Conv2d(cin, cout, MLE["k"], 1, MLE["k"] // 2)
        self.upsample = nn.Upsample(scale_factor=MLE["up"])

    def forward(self, x):
        return self.upsample(self.conv(self.norm(x)))


class UpResBlock(nn.Module):
    def __init__(self, cin, cout):
        super().__init__()
        self.upsample = UpBlock(cin, cout)
        self.norm, self.conv = BNAct(cout), nn.Conv2d(cout, cout, MLE["k"], 1, MLE["k"] // 2)
        self.resize = nn.Sequential(nn.Conv2d(cin, cout, 1, 1), nn.Upsample(scale_factor=MLE["up"])) if cin != cout else None

    def forward(self, x):
        o = self.conv(self.norm(self.upsample(x)))
        return o + self.resize(x) if self.resize is not None else o


class DecLayer(nn.Module):
    def __init__(self, cin, cout, n):
        super().__init__()
        self.blocks = nn.ModuleList([ResBlock(cout, cout, 1) if i > 0 else UpResBlock(cin, cout) for i in range(n)])

    def forward(self, x, short):
        for b in self.blocks:
            x = b(x)
        return x + short


class DecHead(nn.Module):
    def __init__(self, n):
        super().__init__()
        self.layer = EncLayer(MLE["patch"], MLE["last"], n, [1] * n)
        self.norm = BNAct(MLE["last"])
        self.conv = nn.Conv2d(MLE["last"], 1, 1, 1)

    def forward(self, x):
        return self.conv(self.norm(self.layer(x)))


class MLEEncoder(nn.Module):
    def __init__(self):
        super().__init__()
        c, p, u = MLE["cin"], MLE["patch"], MLE["up"]
        self.layers = nn.ModuleList([
            EncLayer(c if i == 0 else c * p * u ** (i - 1), c * p * u ** i, n,
                     [1 if j < n - 1 else 2 for j in range(n)] if i > 0 else [1] * n)
            for i, n in enumerate(MLE["enc"])])

    def forward(self, x):
        hs = []
        for l in self.layers:
            x = l(x)
            hs.append(x)
        return x, hs


class MLEDecoder(nn.Module):
    def __init__(self):
        super().__init__()
        c, p, u = MLE["cin"], MLE["patch"], MLE["up"]
        top = c * p * u ** (len(MLE["enc"]) - 1)
        n = MLE["dec"]
        self.layers = nn.ModuleList([DecLayer(top // u ** i, top // u ** (i + 1), k) if i < len(n) - 1 else DecHead(k) for i, k in enumerate(n)])

    def forward(self, x, hs):
        for i, l in enumerate(self.layers):
            x = l(x, hs[len(hs) - 2 - i]) if i < len(self.layers) - 1 else l(x)
        return x


class MLEModel(nn.Module):
    def __init__(self):
        super().__init__()
        self.encoder, self.decoder = MLEEncoder(), MLEDecoder()

    def forward(self, x):
        h, hs = self.encoder(x)
        return torch.clamp(self.decoder(h, hs), 0, 255)


class MLEWrap(nn.Module):
    def __init__(self):
        super().__init__()
        self.model = MLEModel()

    def forward(self, x):
        return self.model(x)


def read_safetensors(path):
    """safetensors 를 직접 읽기 (bf16/f16/f32)"""
    with open(path, "rb") as f:
        n = struct.unpack("<Q", f.read(8))[0]
        head = json.loads(f.read(n))
        base = 8 + n
        out = {}
        for k, v in head.items():
            if k == "__metadata__":
                continue
            a, b = v["data_offsets"]
            f.seek(base + a)
            raw = f.read(b - a)
            if v["dtype"] == "BF16":
                arr = (np.frombuffer(raw, np.uint16).astype(np.uint32) << 16).view(np.float32)
            elif v["dtype"] == "F16":
                arr = np.frombuffer(raw, np.float16).astype(np.float32)
            elif v["dtype"] == "F32":
                arr = np.frombuffer(raw, np.float32)
            elif v["dtype"] == "I64":
                arr = np.frombuffer(raw, np.int64)
            else:
                raise ValueError(v["dtype"])
            out[k] = torch.from_numpy(arr.copy().reshape(v["shape"]))
        return out


# ---------------------------------------------------------------- 변환
def build(kind, src):
    if kind == "srvgg":
        net, sd = SRVGGNetCompact(64, 16, 4), torch.load(src, map_location="cpu", weights_only=True)
        sd = sd.get("params", sd)
        return net, sd, (1, 3, 64, 64), {2: "h", 3: "w"}
    if kind == "rrdb6":
        net, sd = RRDBNet(64, 6, 32), torch.load(src, map_location="cpu", weights_only=True)
        sd = sd.get("params_ema", sd.get("params", sd))
        return net, sd, (1, 3, 64, 64), {2: "h", 3: "w"}
    if kind == "a2s":
        net, sd = UnetGenerator(), torch.load(src, map_location="cpu", weights_only=True)
        sd = {k.replace("module.", ""): v for k, v in sd.items()}
        return net, sd, (1, 3, 512, 512), {2: "h", 3: "w"}
    if kind == "p2p":
        ck = torch.load(src, map_location="cpu", weights_only=True)
        sd = {k[len("generator."):]: v for k, v in ck.get("state_dict", ck).items() if k.startswith("generator.")}
        return P2PGenerator(), sd, (1, 3, 256, 256), {2: "h", 3: "w"}
    if kind == "mle":
        net, sd = MLEWrap(), read_safetensors(src)
        return net, sd, (1, 1, 256, 256), {2: "h", 3: "w"}
    raise SystemExit(f"모르는 종류: {kind}")


def main():
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    kind, src, dst = sys.argv[1:]
    net, sd, shape, dyn = build(kind, src)
    missing, unexpected = net.load_state_dict(sd, strict=False)
    missing = [m for m in missing if not m.endswith("num_batches_tracked")]
    if missing or unexpected:
        raise SystemExit(f"가중치가 맞지 않습니다: 없음 {missing[:5]} / 남음 {unexpected[:5]}")
    net.eval()
    x = torch.rand(*shape)
    with torch.no_grad():
        ref = net(x).numpy()
    torch.onnx.export(net, x, dst, dynamo=False, opset_version=17, input_names=["input"], output_names=["output"],
                      dynamic_axes={"input": dyn, "output": dyn})
    # 확인: onnxruntime 이 있으면 같은 값이 나오는지
    try:
        import onnxruntime as ort
        got = ort.InferenceSession(dst, providers=["CPUExecutionProvider"]).run(None, {"input": x.numpy()})[0]
        err = float(np.abs(got - ref).max())
        print(json.dumps({"ok": True, "maxDiff": err, "out": list(got.shape)}))
    except ImportError:
        print(json.dumps({"ok": True}))


if __name__ == "__main__":
    main()
