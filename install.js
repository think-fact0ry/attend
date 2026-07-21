// install.js — '홈 화면에 앱으로 추가' 안내 바 + 설치 방법 카드. 진입 페이지(허브·로그인 등) 공용 드롭인.
//   페이지 끝에 <script src="install.js"></script> 한 줄. tfnav.js와 같은 자가주입(CSS+DOM+로직) 패턴.
//   동작: 이미 앱으로 설치(standalone)면 아무것도 안 함. 브라우저로 열렸으면 하단 바를 띄우고,
//         '추가' = beforeinstallprompt 있으면 네이티브 설치창 / 없으면(크롬 휴리스틱 미충족·iOS) 기기별 수동 안내.
//   입력 포커스 시 바를 내려 하단 footbar(로그인 '인증번호 받기' 등)와 겹치지 않게 한다.
(function(){
  if (window.__tfinstall) return; window.__tfinstall = true;

  // 서비스워커 등록 = 설치 가능(WebAPK) 요건. 이게 없는 페이지에서 '추가'를 누르면 크롬이 *앱*이 아니라 *바로가기*만 만든다.
  // (허브 index는 <head> 리다이렉트가 먼저라 body의 SW 등록이 안 돌 수 있어, 진입 페이지마다 여기서 보장.)
  if ('serviceWorker' in navigator) { try { navigator.serviceWorker.register('sw.js'); } catch(e){} }

  function installed(){
    return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;
  }
  if (installed()) return;   // 이미 앱 = 안내 불필요

  var css = document.createElement('style');
  css.textContent =
  '.tfi-bar{position:fixed;left:12px;right:12px;bottom:calc(14px + env(safe-area-inset-bottom));z-index:600;display:none;align-items:center;gap:10px;background:#3a8a5f;color:#fff;border-radius:16px;padding:13px 14px 13px 18px;box-shadow:0 8px 24px rgba(58,138,95,.35);font-family:inherit;}'+
  '.tfi-bar.show{display:flex;}'+
  '.tfi-bar .t{flex:1;font-size:14.5px;font-weight:600;letter-spacing:-.3px;line-height:1.4;}'+
  '.tfi-bar button{font-family:inherit;cursor:pointer;border:none;}'+
  '.tfi-bar .add{font-size:14px;font-weight:700;border-radius:11px;padding:10px 16px;background:#fff;color:#2f6f4d;}'+
  '.tfi-bar .add:active{background:#eef0f3;}'+
  '.tfi-bar .x{background:transparent;color:#fff;font-size:20px;padding:4px 6px;opacity:.85;}'+
  '.tfi-dim{position:fixed;inset:0;z-index:700;background:rgba(0,0,0,.45);opacity:0;pointer-events:none;transition:opacity .2s;}'+
  '.tfi-dim.on{opacity:1;pointer-events:auto;}'+
  '.tfi-card{position:fixed;left:50%;top:50%;transform:translate(-50%,-50%) scale(.96);z-index:701;width:min(380px,88vw);background:#fff;border-radius:20px;padding:24px 22px calc(20px + env(safe-area-inset-bottom));opacity:0;pointer-events:none;transition:opacity .2s,transform .2s;box-shadow:0 12px 40px rgba(0,0,0,.2);font-family:inherit;}'+
  '.tfi-card.on{opacity:1;pointer-events:auto;transform:translate(-50%,-50%) scale(1);}'+
  '.tfi-card h3{font-size:18px;font-weight:700;letter-spacing:-.4px;margin:0 0 14px;}'+
  '.tfi-card ol{margin:0 0 18px;padding-left:22px;}'+
  '.tfi-card li{font-size:15px;color:#4e5968;font-weight:500;line-height:1.75;word-break:keep-all;}'+
  '.tfi-card li b{color:#2f6f4d;font-weight:700;}'+
  '.tfi-note{font-size:13px;color:#8b95a1;font-weight:500;line-height:1.5;margin:-8px 0 16px;word-break:keep-all;}'+
  '.tfi-card .done{width:100%;font-size:17px;font-weight:700;padding:17px;border-radius:15px;border:none;background:#3a8a5f;color:#fff;cursor:pointer;}'+
  '.tfi-card .done:active{background:#2f6f4d;}';
  document.head.appendChild(css);

  var bar = document.createElement('div'); bar.className = 'tfi-bar';
  bar.innerHTML = '<div class="t">홈 화면에 앱으로 추가할까요?</div><button class="add" type="button">추가</button><button class="x" type="button" aria-label="닫기">×</button>';
  var dim = document.createElement('div'); dim.className = 'tfi-dim';
  var card = document.createElement('div'); card.className = 'tfi-card';
  card.innerHTML = '<h3>앱으로 추가하기</h3><p class="tfi-note" style="display:none"></p><ol></ol><button class="done" type="button">알겠어요</button>';
  document.body.appendChild(bar); document.body.appendChild(dim); document.body.appendChild(card);

  var btn = bar.querySelector('.add'), x = bar.querySelector('.x');
  var note = card.querySelector('.tfi-note'), steps = card.querySelector('ol'), done = card.querySelector('.done');
  var deferred = null;

  var ua = navigator.userAgent;
  var isIOS = /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  var iosNotSafari = isIOS && /CriOS|FxiOS|EdgiOS|OPiOS/.test(ua);   // 아이패드인데 크롬·기타 = 사파리로 열어야 함

  function openGuide(){
    var arr, nt = '';
    if (isIOS){
      nt = iosNotSafari ? '아이패드·아이폰은 크롬이 아니라 사파리로 열어야 앱처럼 추가돼요.' : '';
      arr = ['아래 <b>공유 버튼</b>(네모에 화살표)을 눌러요', "<b>'홈 화면에 추가'</b>를 눌러요", "오른쪽 위 <b>'추가'</b>를 누르면 끝이에요"];
    } else {
      arr = ['오른쪽 위 <b>⋮</b>(점 3개)를 눌러요', "<b>'앱 설치'</b>를 눌러요", "<b>'설치'</b>를 누르면 끝이에요"];
    }
    note.innerHTML = nt; note.style.display = nt ? '' : 'none';
    steps.innerHTML = arr.map(function(s){ return '<li>'+s+'</li>'; }).join('');
    dim.classList.add('on'); card.classList.add('on');
  }
  function closeGuide(){ dim.classList.remove('on'); card.classList.remove('on'); }
  done.addEventListener('click', closeGuide);
  dim.addEventListener('click', closeGuide);

  window.addEventListener('beforeinstallprompt', function(e){ e.preventDefault(); deferred = e; if (!installed()) bar.classList.add('show'); });
  btn.addEventListener('click', function(){
    if (deferred){ deferred.prompt(); deferred.userChoice.then(function(){ deferred = null; bar.classList.remove('show'); }); }
    else { openGuide(); }
  });
  x.addEventListener('click', function(){ bar.classList.remove('show'); try{ sessionStorage.setItem('tf_install_x','1'); }catch(e){} });
  window.addEventListener('appinstalled', function(){ bar.classList.remove('show'); deferred = null; });

  // 상호작용(인풋·버튼·footbar·링크 탭) 시 바를 내림 — 로그인 진행 등에서 하단 footbar와 겹치지 않게.
  //   ※ position:fixed footbar는 offsetParent가 null이라 그걸로 못 잡음 → 이벤트 기반이 확실(focusin은 헤드리스서 불안정).
  document.addEventListener('pointerdown', function(e){
    var t = e.target; if (!t || !t.closest) return;
    if (t.closest('.tfi-bar, .tfi-card')) return;                 // 설치 UI 자체는 제외
    if (t.closest('input, textarea, button, a, .footbar')) bar.classList.remove('show');
  }, true);

  // 하단 액션바(footbar=다음·확인 등)가 보이는 화면이면 설치 바가 그 위를 덮으므로 안 띄운다.
  function footbarVisible(){
    var fbs = document.querySelectorAll('.footbar');
    for (var i=0;i<fbs.length;i++){ var r = fbs[i].getBoundingClientRect(); if (r.height > 0 && r.width > 0) return true; }   // fixed라도 rect로 판별
    return false;
  }
  // 폴백: beforeinstallprompt가 안 떠도(크롬 휴리스틱·iOS·시크릿) 브라우저면 바를 띄움.
  function maybeShow(){
    var dismissed = false; try{ dismissed = sessionStorage.getItem('tf_install_x') === '1'; }catch(e){}
    var ae = document.activeElement;
    if (ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA')) return;   // 입력 중이면 안 띄움
    if (footbarVisible()) return;                                              // 하단 버튼바 있는 화면이면 안 띄움(겹침 방지)
    if (!installed() && !dismissed) bar.classList.add('show');
  }
  setTimeout(maybeShow, 1200);
})();
