"""ComicPad 아이콘(ComicPad.ico, app/icon.png) 생성. Pillow 필요."""
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
S = 1024

im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
# 주황 둥근 사각 바탕
d.rounded_rectangle([40, 40, S - 40, S - 40], radius=210, fill=(232, 163, 61, 255))
# 말풍선
d.ellipse([150, 150, 760, 600], fill=(255, 255, 255, 255), outline=(30, 30, 34, 255), width=34)
d.polygon([(300, 540), (250, 760), (440, 580)], fill=(255, 255, 255, 255))
d.line([(300, 560), (250, 760), (430, 590)], fill=(30, 30, 34, 255), width=34, joint='curve')
# 말풍선 안 점 세 개
for cx in (330, 455, 580):
    d.ellipse([cx - 38, 337, cx + 38, 413], fill=(30, 30, 34, 255))
# 펜 (오른쪽 아래에서 대각선)
pen = Image.new('RGBA', (S, S), (0, 0, 0, 0))
p = ImageDraw.Draw(pen)
p.rounded_rectangle([430, 450, 560, 980], radius=40, fill=(30, 30, 34, 255))
p.polygon([(430, 450), (560, 450), (495, 300)], fill=(30, 30, 34, 255))
p.polygon([(470, 400), (520, 400), (495, 330)], fill=(255, 255, 255, 255))
pen = pen.rotate(-35, resample=Image.BICUBIC, center=(495, 640))
pen = pen.transform(pen.size, Image.AFFINE, (1, 0, -170, 0, 1, -40), resample=Image.BICUBIC)
im.alpha_composite(pen)

os.makedirs(os.path.join(ROOT, 'app'), exist_ok=True)
im.resize((256, 256), Image.LANCZOS).save(os.path.join(ROOT, 'app', 'icon.png'))
im.resize((192, 192), Image.LANCZOS).save(os.path.join(ROOT, 'app', 'icon-192.png'))
im.resize((512, 512), Image.LANCZOS).save(os.path.join(ROOT, 'app', 'icon-512.png'))
im.save(os.path.join(ROOT, 'ComicPad.ico'), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print('ok')
