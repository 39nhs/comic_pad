// 앱 파일 캐시: 서버가 꺼져 있어도(또는 인터넷이 없어도) 앱 화면이 열리도록.
// 작업 저장은 PC 서버 → 허락한 폴더 → 이 기기 순서로 (core/io.js)
const CACHE = 'comicpad-v2';
const scoped = (p) => new URL(p, self.registration.scope).href;

/** precache.json 에 적힌 앱 파일을 전부 받아 둔다 (버전이 같으면 건너뜀) */
async function precache() {
  const r = await fetch(scoped('precache.json'), { cache: 'no-store' });
  if (!r.ok) return;
  const j = await r.json();
  const c = await caches.open(CACHE);
  const old = await c.match(scoped('__version'));
  if (old && (await old.text()) === j.version) return;
  // 하나가 실패해도 나머지는 받도록 하나씩
  await Promise.all(j.files.map(f => fetch(scoped(f), { cache: 'no-cache' }).then(res => res.ok && c.put(scoped(f), res)).catch(() => {})));
  await c.put(scoped('__version'), new Response(j.version));
}

self.addEventListener('install', (e) => { self.skipWaiting(); e.waitUntil(precache().catch(() => {})); });
self.addEventListener('activate', (e) => e.waitUntil((async () => {
  for (const k of await caches.keys()) if (k !== CACHE) await caches.delete(k);
  await self.clients.claim();
})()));
self.addEventListener('message', (e) => { if (e.data === 'precache') e.waitUntil(precache().catch(() => {})); });

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin) return;
  if (url.pathname.includes('/api/') || url.pathname.includes('/data/')) return;
  // 최신 파일 우선, 안 되면 캐시 (주소 뒤 ?k= ?v= 는 무시하고 찾기)
  e.respondWith(fetch(e.request).then((res) => {
    if (res.ok && !url.pathname.includes('/plugins/')) { const copy = res.clone(); caches.open(CACHE).then(c => c.put(e.request, copy)); }
    return res;
  }).catch(async () => {
    const hit = await caches.match(e.request, { ignoreSearch: true });
    if (hit) return hit;
    if (e.request.mode === 'navigate') return (await caches.match(scoped('./'))) || (await caches.match(scoped('index.html')));
    return Response.error();
  }));
});
