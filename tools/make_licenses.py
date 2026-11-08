"""app/LICENSES.txt 만들기: ComicPad 라이선스 + 함께 들어 있는 것들의 라이선스 전문을 한 파일로.
함께 넣는 라이브러리·모델이 바뀌면 THIRD 목록을 고치고 다시 실행:  python tools/make_licenses.py"""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
def rd(*p):
    with open(os.path.join(ROOT, *p), encoding='utf-8') as f:
        return f.read().strip()

# (이름, 버전·위치, 라이선스, 설명, 전문 파일 또는 None)
THIRD = [
    ('ONNX Runtime Web', '1.31.1 · app/vendor/ai/ort', 'MIT',
     'AI 모델 실행기.', ('app', 'vendor', 'ai', 'ort', 'LICENSE')),
    ('MediaPipe Tasks Vision', '1.2.2 · app/vendor/ai/mediapipe', 'Apache-2.0',
     '자세·손·얼굴 인식. 수정: 사용 기록(odml.pa.googleapis.com) 전송 함수가 아무것도 보내지 않도록 바꿈. 그 밖은 원본 그대로. '
     '자세·손·얼굴 모델(.task)은 앱에서 받으며 Apache-2.0.', ('app', 'vendor', 'ai', 'mediapipe', 'LICENSE')),
    ('MobileSAM', 'models_bundled/sam-mobile', 'Apache-2.0',
     '탭 선택 모델 (github.com/ChaoningZhang/MobileSAM). 수정: 공식 가중치를 ONNX 로 변환(구조·값 그대로, 입출력 모양만 SlimSAM 과 같게).', None),
    ('Real-ESRGAN (realesr-animevideov3, RealESRGAN_x4plus_anime_6B)', 'models_bundled/up-animevideo, up-anime6b', 'BSD-3-Clause',
     '크기 키우기 모델. ONNX 로 변환.', ('models_bundled', 'up-anime6b', 'LICENSE')),
    ('Microsoft WebView2 SDK', '1.0.4258.31 · Microsoft.Web.WebView2.*.dll', 'BSD-3-Clause (Microsoft)',
     '전용 창(ComicPad.exe)이 쓰는 라이브러리.', ('launcher', 'lib', 'LICENSE.txt')),
    ('Microsoft Edge WebView2 Runtime', 'redist (설치기)', 'Microsoft 소프트웨어 사용 조건',
     'WebView2 가 없는 PC 에만 설치기가 따로 설치합니다. ComicPad 의 일부가 아닌 별도 프로그램이며 Microsoft 의 조건을 따릅니다.', None),
    ('Python (Windows 내장형)', '3.12.10 · python 폴더', 'PSF License',
     'ComicPad 서버 실행용. 전문은 설치 폴더 안 python 폴더의 LICENSE.txt.', None),
]

LINE = '=' * 78
out = [
    'ComicPad 라이선스 안내', LINE, '',
    rd('NOTICE'), '', LINE,
    '함께 들어 있는 것', LINE, '',
]
for i, (name, where, lic, note, _) in enumerate(THIRD, 1):
    out += [f'{i}. {name}', f'   위치: {where}', f'   라이선스: {lic}', f'   {note}', '']
out += [LINE, 'Apache License 2.0 전문 (ComicPad, MediaPipe, MobileSAM)', LINE, '', rd('LICENSE'), '']
for name, _w, lic, _n, path in THIRD:
    if path and lic != 'Apache-2.0':
        out += [LINE, f'{name} — {lic}', LINE, '', rd(*path), '']
mp = rd('app', 'vendor', 'ai', 'mediapipe', 'LICENSE')
extra = mp.split('limitations under the License.', 1)[-1].strip() if 'limitations under the License.' in mp else ''
if extra:
    out += [LINE, 'MediaPipe 에 함께 들어 있는 추가 고지', LINE, '', extra, '']
with open(os.path.join(ROOT, 'app', 'LICENSES.txt'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(out))
print('app/LICENSES.txt', sum(len(x) + 1 for x in out), 'chars')
