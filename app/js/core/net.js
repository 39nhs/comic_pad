// 휴대폰/태블릿 접속용 연결 키: 주소의 ?k= 를 기억해 두고 모든 요청에 붙인다
export const KEY = (() => {
  try {
    const u = new URL(location.href);
    const k = u.searchParams.get('k');
    if (k) {
      localStorage.setItem('comicpad.key', k);
      u.searchParams.delete('k');
      history.replaceState(null, '', u.pathname + u.search + u.hash);
    }
    return localStorage.getItem('comicpad.key') || '';
  } catch { return ''; }
})();

const needsKey = (url) => /^\/(api|data)\//.test(url);

/** 이미지 src 처럼 헤더를 못 붙이는 주소에는 쿼리로 */
export function withKey(url) {
  if (!KEY || !needsKey(url)) return url;
  return url + (url.includes('?') ? '&' : '?') + 'k=' + encodeURIComponent(KEY);
}

const origFetch = window.fetch.bind(window);
window.fetch = (input, init = {}) => {
  const url = typeof input === 'string' ? input : input.url;
  if (KEY && needsKey(url)) init = { ...init, headers: { ...(init.headers || {}), 'X-ComicPad-Key': KEY } };
  return origFetch(input, init);
};
