# ComicPad 내장 AI — 상업용 후보 목록 (작업 전 조사만)

조사일: 2026-11-05. **2026-11-06 갱신: A·B·C 의 모델을 모두 앱에 넣었습니다** (AI > AI 모델 관리, 목록은 `app/ai-models.json`). 비상업 모델은 쓸 때 경고·작품 기록·내보낼 때 경고. Simo-Serra 는 재배포 금지·연구 전용이라 넣지 않았고, BRIA RMBG-1.4 는 약관 동의가 필요해 "파일로 추가"로만 넣을 수 있습니다.

기준: 이 앱을 쓰는 사람이 작품을 팔거나, 앱을 상업적으로 배포해도 문제없는 모델만 고릅니다.
"코드 라이선스"와 "학습된 모델 파일(가중치) 라이선스"는 다를 수 있으니 둘 다 봐야 합니다.
아래 내용은 공개 자료를 정리한 것이고 법률 자문이 아닙니다. 넣기 전에는 원본 저장소의 LICENSE 파일을 다시 확인하세요.

실행 방식(예정): 브라우저 안에서 ONNX Runtime Web (MIT)으로 실행. WebGPU를 쓰고, 안 되면 CPU(WASM)로 돌립니다.
모델 파일은 설치 파일에 같이 넣고, 인터넷 연결이나 외부 전송은 하지 않습니다.

---

## A. 상업용 가능 (확인된 편)

| 기능 | 모델 | 라이선스 | 크기(대략) | 메모 |
|---|---|---|---|---|
| 사진 → 3D 인형 포즈 | MediaPipe Pose Landmarker (BlazePose GHUM) | Apache 2.0 (Google 모델 카드) | Lite 3MB / Full 6MB / Heavy 26MB | 1순위. 데생 인형에 바로 연결 |
| 사진 → 손 포즈 | MediaPipe Hand Landmarker | Apache 2.0 (MediaPipe 모델) | 수 MB | 손 모델 손가락 굽힘에 연결 |
| 사진·그림 → 깊이 (원근·3D 배경 참고) | Depth Anything V2 **Small** | Apache 2.0 | 약 25M 파라미터 | ⚠ Base/Large/Giant는 CC-BY-NC(비상업) → **Small만** 사용 |
| 해상도 키우기 | Real-ESRGAN (realesr-animevideov3, x4plus 등) | BSD-3-Clause | animevideov3 약 1.2MB | 저작권 표시와 면책 문구를 함께 넣어야 함 |
| 캐릭터만 따기 (배경 지우기) | SkyTNT anime-segmentation (isnetis) | Apache 2.0 (HF 표기) | 수십 MB | 원 구조(ISNet) 쪽 조건도 확인 |
| 탭해서 물체 고르기 (자동 선택 강화) | MobileSAM | Apache 2.0 | 인코더 약 5M 파라미터 | 선택 도구에 "AI로 고르기" |

## B. 상업용 가능해 보이나 추가 확인 필요

| 기능 | 모델 | 알려진 라이선스 | 확인할 것 |
|---|---|---|---|
| 그림 → 선화 추출 | Anime2Sketch | 코드 MIT | 가중치는 구글 드라이브로 따로 받는데, 가중치에 대한 별도 표기가 없음 |
| 사진/그림 → 선화 | informative-drawings | 원 저장소 확인 못 함 (HF 변환본은 MIT 표기) | 원 저장소 LICENSE |
| 만화 구조선 추출 | MangaLineExtraction | 원 저장소 확인 못 함 (HF 변환본은 MIT 표기) | 원 저장소 LICENSE |
| 배경 지우기 (고품질) | BiRefNet | MIT (GitHub, HF) | HF 토론에 "가중치도 MIT인가" 질문이 답 없이 남아 있음 |
| 해상도 키우기 (애니풍) | Real-CUGAN | 변환본·포트들은 MIT | bilibili/ailab 원 저장소 LICENSE |
| 해상도 키우기 (애니풍) | waifu2x | MIT | 모델 파일 쪽 표기 |

## C. 상업용 불가 → 제외

| 모델 | 이유 |
|---|---|
| Sketch Simplification (Simo-Serra, 러프 → 깨끗한 선) | 코드·모델 모두 비상업 전용 |
| 선 굵기 모델 (같은 연구실) | CC BY-NC-SA 4.0 |
| BRIA RMBG-1.4 / RMBG-2.0 (배경 지우기) | 비상업. 상업 사용은 BRIA와 별도 계약 필요 |
| Depth Anything V2 Base / Large / Giant | CC-BY-NC 4.0 |

## D. 상업용 후보를 못 찾은 기능

- **선화 자동 채색 (색 힌트 점 찍기)**: 쓸 만한 연구(MangaNinja, inkn'hue 등)는 대부분 큰 생성형 모델 기반이라 이 노트북에서 돌리기 무겁고, 라이선스도 확인되지 않았습니다.
  → 지금 있는 "밑색 한 번에 칠하기"(AI 없음)에 MobileSAM 영역 고르기를 더하는 쪽이 현실적입니다.
- **러프 → 깨끗한 선화**: 가장 잘 알려진 모델(Simo-Serra)은 비상업이라 제외했습니다. B의 Anime2Sketch·MangaLineExtraction 가중치 라이선스가 확인되면 그것으로 대신합니다.

## 넣을 때 지킬 것

1. 원 저장소의 LICENSE 파일, 그리고 가중치 라이선스를 넣는 시점에 다시 확인합니다.
2. 설치 폴더에 `third_party_licenses/`를 두고, 각 모델의 LICENSE·NOTICE·저작권 표시를 넣습니다. Apache 2.0과 BSD는 이것이 의무입니다.
3. 설명서에 "어떤 AI 모델을 쓰는지, 그림이 밖으로 나가지 않는다는 것"을 적습니다.
4. 학습 데이터 문제: 애니 그림 모델 중에는 출처가 불분명한 그림 모음으로 학습된 것이 있습니다. 라이선스가 상업용이어도 이 부분은 사용자가 판단하도록 설명서에 적어 둡니다.
5. 모델은 "보조"(정리·고르기·포즈·크기 키우기)에만 쓰고, 그림을 새로 생성하는 기능은 넣지 않습니다. 생성은 지금처럼 외부 WebUI 연결로만 둡니다.

## 추천 순서 (구현할 때)

1. MediaPipe Pose + Hand → 사진에서 3D 인형 포즈
2. MobileSAM → 탭해서 고르기 / 밑색 영역
3. Real-ESRGAN animevideov3 → 인쇄용 크기 키우기
4. anime-segmentation → 캐릭터만 따기
5. Depth Anything V2 Small → 배경 사진에서 원근 자·3D 참고
6. (가중치 라이선스 확인 후) Anime2Sketch 또는 MangaLineExtraction → 선화 추출

## 출처

- MediaPipe BlazePose GHUM 모델 카드: https://developers.google.cn/static/ml-kit/images/vision/pose-detection/pose_model_card.pdf
- MediaPipe 포즈 모델 상업 사용 질문(포럼): https://discuss.ai.google.dev/t/commercial-use-of-mediapipe-pose-landmarker-full/184033
- Depth Anything V2 (Small = Apache-2.0, Base/Large = CC-BY-NC-4.0): https://huggingface.co/spaces/depth-anything/Depth-Anything-V2/commit/9ebc8354fd72c259a7ba84c0eaa8bbe9148eefd3
- Real-ESRGAN 가중치 (BSD-3-Clause): https://huggingface.co/edgetools/realesrgan
- SkyTNT anime-seg: https://huggingface.co/skytnt/anime-seg/blob/main/README.md
- MobileSAM: https://huggingface.co/Uminosachi/MobileSAM/blob/main/README.md
- BiRefNet 라이선스 / 가중치 질문: https://huggingface.co/ZhengPeng7/BiRefNet/blob/main/README.md?code=true , https://huggingface.co/ZhengPeng7/BiRefNet/discussions/23
- BRIA RMBG-2.0 (비상업): https://github.com/Bria-AI/RMBG-2.0
- Sketch Simplification (비상업): https://www.github.com/bobbens/sketch_simplification
- Anime2Sketch: https://awesome.ecosyste.ms/projects/github.com%2FMukosame%2FAnime2Sketch
- informative-drawings: https://gh.hhj520.top/carolineec/informative-drawings
- MangaLineExtraction (HF 변환본): https://huggingface.co/p1atdev/MangaLineExtraction-hf/raw/6afbcc3d3c6abb49de3c4d69544aa3b4c0884f7c/README.md
- Real-CUGAN: https://github.com/bilibili/ailab/blob/main/Real-CUGAN/README_EN.md
- waifu2x: https://en.wikipedia.org/wiki/Waifu2x
- 만화 채색 연구 (MangaNinja, inkn'hue): https://awesome.ecosyste.ms/projects/github.com%2Fali-vilab%2FMangaNinjia , https://ar5iv.labs.arxiv.org/html/2311.01804
