// 버튜버 추적: 웹캠 얼굴(MediaPipe FaceLandmarker) + 마이크 소리 크기. 영상·소리는 이 PC 안에서만 쓰고 저장·전송하지 않는다.
import * as R from '../ai/runtime.js';
import { withKey } from '../core/net.js';
import { rms } from './rig.js';

let visionP = null;
const vision = () => (visionP ||= (async () => {
  const v = await import('/vendor/ai/mediapipe/vision_bundle.mjs');
  const files = await v.FilesetResolver.forVisionTasks(new URL('/vendor/ai/mediapipe/wasm', location.href).href);
  return { v, files };
})());

/** 얼굴 추적기 만들기 (그래픽 가속 → 안 되면 CPU) */
export async function faceLandmarker() {
  if (!(await R.installed('face')).some(m => m.id === 'face-mp')) throw new Error('얼굴 추적 모델이 없습니다. AI > AI 모델 관리에서 "MediaPipe 얼굴" 을 받으세요');
  const { v, files } = await vision();
  const base = { modelAssetPath: withKey(R.modelUrl('face-mp', 'face_landmarker.task')) };
  const opts = { runningMode: 'VIDEO', numFaces: 1, outputFaceBlendshapes: true, outputFacialTransformationMatrixes: true };
  try { return await v.FaceLandmarker.createFromOptions(files, { baseOptions: { ...base, delegate: 'GPU' }, ...opts }); }
  catch { return v.FaceLandmarker.createFromOptions(files, { baseOptions: { ...base, delegate: 'CPU' }, ...opts }); }
}

/** 한 장(영상 프레임·그림)에서 얼굴 값 → { shapes, matrix } 또는 null */
export function detect(lm, source, ts) {
  const r = lm.detectForVideo(source, ts);
  if (!r?.faceBlendshapes?.length) return null;
  return { shapes: r.faceBlendshapes[0].categories, matrix: r.facialTransformationMatrixes?.[0]?.data || null };
}

/** 웹캠 켜기 → video 요소 */
export async function startCamera() {
  if (!navigator.mediaDevices?.getUserMedia) throw new Error('이 창에서는 카메라를 쓸 수 없습니다 (https 또는 이 PC 의 ComicPad 창에서만)');
  const stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 640 }, height: { ideal: 480 }, frameRate: { ideal: 30 } }, audio: false });
  const video = document.createElement('video');
  video.muted = true; video.playsInline = true; video.srcObject = stream;
  await video.play();
  return { video, stream, stop: () => stream.getTracks().forEach(t => t.stop()) };
}

/** 마이크 켜기 → { level(): 0~1 RMS, stream, stop } */
export async function startMic() {
  if (!navigator.mediaDevices?.getUserMedia) throw new Error('이 창에서는 마이크를 쓸 수 없습니다');
  const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true }, video: false });
  const ac = new AudioContext();
  const src = ac.createMediaStreamSource(stream);
  const an = ac.createAnalyser();
  an.fftSize = 1024;
  src.connect(an);
  const buf = new Float32Array(an.fftSize);
  return {
    stream,
    level: () => { an.getFloatTimeDomainData(buf); return rms(buf); },
    stop: () => { stream.getTracks().forEach(t => t.stop()); ac.close().catch(() => {}); },
  };
}
