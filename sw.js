// 근태 서비스워커 — 설치 가능(앱) 조건 충족 + 오프라인 폴백. tablet/sw.js 패턴.
// 전략: network-first(항상 최신), 실패 시 캐시 폴백. 배포마다 CACHE 버전 올릴 것.
var CACHE = 'tf-attend-v72'; // 08-03 퇴근 직후 PIN 없이 도장 화면이 열리던 것 + 잠금 화면에 창 없애기(−)
var SHELL = [
  './', './index.html', './approve.html', './근태엔진.js', './install.js', './manifest.json',
  './widget/', './widget/index.html',   // 데스크톱 위젯(exe가 로드) — 캐시에 있어야 오프라인에도 알약이 뜬다

  '/attend/favicon-32x32.png',
  '/attend/android-icon-192x192.png',
  '/attend/android-icon-512x512.png',
  '/attend/apple-icon-180x180.png'
];

self.addEventListener('install', function(e){
  e.waitUntil(
    caches.open(CACHE)
      .then(function(c){ return c.addAll(SHELL); })
      .then(function(){ return self.skipWaiting(); })
  );
});

self.addEventListener('activate', function(e){
  e.waitUntil(
    caches.keys()
      .then(function(keys){ return Promise.all(keys.map(function(k){ if (k !== CACHE) return caches.delete(k); })); })
      .then(function(){ return self.clients.claim(); })
  );
});

self.addEventListener('fetch', function(e){
  var req = e.request;
  if (req.method !== 'GET') return; // 도장 POST는 절대 캐시 안 탐
  // HTML은 HTTP 캐시를 재검증하고 받는다(08-01 실사고: Pages max-age 10분 캐시가 v68 문서를 돌려줘
  //   위젯 재시작에도 옛 코드가 떴다 — network-first의 fetch(req)도 HTTP 캐시를 탄다).
  var isDoc = req.mode === 'navigate' || /\.html$|\/$/.test(new URL(req.url).pathname);
  e.respondWith(
    fetch(req, isDoc ? { cache: 'no-cache' } : undefined).then(function(res){
      if (res && res.ok && new URL(req.url).origin === self.location.origin){
        var copy = res.clone();
        caches.open(CACHE).then(function(c){ c.put(req, copy); });
      }
      return res;
    }).catch(function(){
      return caches.match(req).then(function(r){ return r || caches.match('./index.html'); });
    })
  );
});
