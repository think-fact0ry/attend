// 근태 서비스워커 — 설치 가능(앱) 조건 충족 + 오프라인 폴백. tablet/sw.js 패턴.
// 전략: network-first(항상 최신), 실패 시 캐시 폴백. 배포마다 CACHE 버전 올릴 것.
var CACHE = 'tf-attend-v84'; // 08-13 알약 버튼이 실제 동작을 말한다(앱이 열려 있으면 '근태 앱 닫기') / v82 미기록일 시트 낙관적 렌더 / v81 회색 '근태' 고착
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
        // ⚠️문서는 **버스터를 뗀 주소로** 저장한다. 알약·앱 창은 열 때마다 `?b=<틱>`을 새로 붙이므로(exe Cfg.Bust)
        //   요청 그대로 저장하면 실행할 때마다 캐시에 새 항목이 하나씩 쌓이고, 정작 오프라인 때는
        //   그 어느 것도 다시 안 맞는다(다음 부팅은 또 다른 버스터로 물어보므로).
        var key = isDoc ? new URL(req.url).pathname : req;
        caches.open(CACHE).then(function(c){ c.put(key, copy); });
      }
      return res;
    }).catch(function(){
      // 부팅 직후처럼 네트워크가 아직 없을 때 — 캐시에서 꺼내 준다.
      //   ⚠️`ignoreSearch`가 없으면 버스터 때문에 **무조건 빗나가서** 아래 폴백으로 떨어졌다.
      //   그리고 폴백은 **그 창이 원래 열려던 것**이어야 한다. 구판은 알약 창에도 앱 본체(./index.html)를 줘서
      //   120×34 창에 앱이 통째로 뜨고, 상태 메시지가 영영 안 와 알약이 하얗게 굳었다(2026-08-05 검수).
      var home = /\/widget\//.test(new URL(req.url).pathname) ? './widget/index.html' : './index.html';
      return caches.match(req, { ignoreSearch: true }).then(function(r){
        return r || caches.match(home, { ignoreSearch: true });
      });
    })
  );
});
