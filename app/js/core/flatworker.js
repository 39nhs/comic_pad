// 밑색 영역 계산 워커
import { flatLabels } from './autoflat.js';

self.onmessage = (e) => {
  const { buf, w, h, opt } = e.data;
  try {
    const { labels, count, edge } = flatLabels(new Uint8ClampedArray(buf), w, h, opt);
    self.postMessage({ labels, count, edge }, [labels.buffer]);
  } catch (err) {
    self.postMessage({ error: String(err && err.message || err) });
  }
};
