// 채우기/자동선택 영역 계산을 화면과 따로 돌리는 워커 (큰 그림에서도 화면이 멈추지 않게)
import { fillMask } from './raster.js';

self.onmessage = (e) => {
  const { id, buf, w, h, x, y, opt } = e.data;
  try {
    const img = { width: w, height: h, data: new Uint8ClampedArray(buf) };
    const { mask, bounds } = fillMask(img, x, y, opt);
    self.postMessage({ id, mask, bounds }, [mask.buffer]);
  } catch (err) {
    self.postMessage({ id, error: String(err && err.message || err) });
  }
};
