// 근태 서비스워커 — 설치 가능(앱) 조건 충족 + 오프라인 폴백. tablet/sw.js 패턴.
// 전략: network-first(항상 최신), 실패 시 캐시 폴백. 배포마다 CACHE 버전 올릴 것.
var CACHE = 'tf-attend-v126'; // 10-28 받은함 상담 완료(오른쪽 클릭 「상담 완료」 → 숫자 자리 초록 체크, 줄 연하게, 대화방 한 줄 — 26 @26과 같이). 옛 v125 = 받은함 〔마무리 날짜〕 = 오늘 + 14일 바닥(이용계약서 제2조 ④ 2 기관 해약 14일 전 통지). 옛 v124 = 띠 종결 아이 「종결 (계약종료)」
// 옛 줄: 'tf-attend-v123'; // 10-26 받은함 「끝나도 계속할 수 있나요?」 갈래 6판(유성 「나머지는 권장대로」) — 26 s_faq fv 2(인사 줄 1달 선, 〔칸〕을 아이 카드로 채움, 못 채우면 보내기 멈춤, 서비스 이름 줄이기, 띠 아이에 맞는 갈래 위로, ⑮-6 보내면 띠 「한유성님께」) — 26 @24와 같이
// 옛 줄: 'tf-attend-v122'; // 10-24 유성 10-23 저녁 피드백 — 받은함 자주 쓰는 답변 2판(넣기 = 칸을 비우고, 찾기 칸, 기본 / 저장한 답변, 새 답변 만들기, 고치기, 빼기 확인 창) + 한유성 보낸 답장 고치기(s_edit) + 아이 찾기 띠(신청 없음 위계, 끝자리, 막대 없이 흐림, 13번 보내면 선생님 연락 줄) — 26 @20과 같이
// 옛 줄: 'tf-attend-v121'; // 10-23 받은함 자주 쓰는 답변(유성 10-15 사전계획 확정 → 10-23 「이어서 진행하자」 — 입력 칸 오른쪽 말풍선 단추 = 목록(이 대화 학부모 글에 맞는 답이 위로), 누르면 입력 칸에, 내 말풍선 오른쪽 클릭 「자주 쓰는 답변에 넣기」, 26 s_faq·s_faqadd·s_faqdel·s_faquse). (v120 = 10-21 받은함 대기 중 답 카드 A(유성 「go」 — 대기 아이 방이면 입력 칸 위 한 줄 = 상황 한 단어 + 근거 + 확인, 초안은 흐린 글, 26 s_wait·s_waitgo). (v119 = 10-18 맡기, 대기로 돌려놓기도 지키기(유성 「아직 안 고친 것은 고칠것」 — 맡기만 해도 줄이 2.7초에 사라졌다가 7.7초에 돌아오던 것). (v118 = 10-18 이름 붙이기, 고정이 저장 직후 옛 목록에 덮여 몇 초 옛 이름으로 돌아가던 것(유성 「이름을 수정했을 때 그 즉시 수정되지 않고 조금 딜레이」) = 서버가 따라올 때까지 내 값을 지킴. (v117 = 10-18 문의 채팅 날짜 줄 = 대화 폭 전체 가로 선 + 가운데 달력 + 「2026년 10월 12일 월요일」(유성 4안 중 b). (v116 = 10-18 한유성 문의 폰(유성 「문의채팅-관리자 모바일도입 급함 … 관리자인 한유성 이름으로」): 폰에서 한유성 계정이면 메뉴 「문의 채팅」, 처음 한 번 관리자 PIN으로 이 폰 열쇠(19 inq_phone) → 받은함. 폰 뒤로가기 = 받은함 대화방부터, 문의 채팅에서만 키패드만큼 화면 줄임, 폰 엔터 = 줄바꿈. (v115 = 10-12 문의 2차 거두기·이용 중·아이 찾기 띠))))))
var SHELL = [
  './', './index.html', './approve.html', './근태엔진.js', './install.js', './manifest.json',
  './widget/', './widget/index.html',   // 데스크톱 위젯(exe가 로드) — 캐시에 있어야 오프라인에도 알약이 뜬다
  './inbox/', './inbox/index.html',     // 문의 받은함(앱 창 안 — 알약 「문의 N」이 연다, 10-09)

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
      var pth = new URL(req.url).pathname;
      var home = /\/widget\//.test(pth) ? './widget/index.html' : /\/inbox\//.test(pth) ? './inbox/index.html' : './index.html';
      return caches.match(req, { ignoreSearch: true }).then(function(r){
        return r || caches.match(home, { ignoreSearch: true });
      });
    })
  );
});
