// 근태 서비스워커 — 설치 가능(앱) 조건 충족 + 오프라인 폴백. tablet/sw.js 패턴.
// 전략: network-first(항상 최신), 실패 시 캐시 폴백. 배포마다 CACHE 버전 올릴 것.
var CACHE = 'tf-attend-v9'; // 07-23 실기기 5차: 날짜시트 탭 버그(포인터캡처)·g-pad 키패드 이식·관리자 신기능(부여취소·기록취소·구성원추가)·용어·타이틀·로그아웃 소형
var SHELL = [
  './', './index.html', './approve.html', './근태엔진.js', './install.js', './manifest.json',
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
  e.respondWith(
    fetch(req).then(function(res){
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
