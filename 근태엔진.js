/**
 * 근태엔진 — 순수 파생 계산 (GAS API 0 의존 → node로 단위테스트 가능).
 * 설계 정본 = docs/4_설계_2026-07-14_근태관리_시스템설계.md §4.
 *
 * 원칙: 근태이력(append-only 이벤트)이 단일 진실. 잔여 연차·조퇴 공제·미승인 연장·개근은
 *       전부 여기서 "읽을 때" 파생한다(확정 불가 판정을 행으로 박지 않음 — 재출근·익일 보정으로 사후 변동).
 *
 * 이벤트 형태: { at:ISO, emp, date:'YYYY-MM-DD', type, val, src, flag, cid }
 *   출근/퇴근      val='HH:MM:SS'
 *   보정퇴근       val='HH:MM' (flag='보정')
 *   연차/병가      val={hours:4|2, half:'front'|'back'|''}  (병가=즉시 확정, 사후 등록 허용)
 *   부여           val={hours:N, note}           (보너스 연차 — 관리자)
 *   근무일변경     val={off:'YYYY-MM-DD', on:'YYYY-MM-DD'}  (스왑 — on날짜가 off날짜의 시각을 물려받음)
 *   정정           val={target:'YYYY-MM-DD', in:'HH:MM'|null, out:'HH:MM'|null, note} (그 날 도장을 통째 대체)
 *
 * ── 승인 플로우 v6 (2026-07-21, docs/4 §10) — 상태 저장 없이 전부 파생 ──
 *   신청           val={rid, kind:'연차', hours:4|2, half:'front'|'back'|''} date=휴가일
 *                  → 활성(반려·취소 아님)이면 즉시 차감. 반려·취소=파생 재계산이 곧 복원.
 *   승인/반려/취소 val={target:rid, memo?}       (반려 memo=유성 원문)
 *   외부근무       val={}                        (그 날 소정근로 자동 인정·개근 유지·지각 없음)
 *   반려확인       val={target:rid}              (반려 시트 [확인했어요] — 미확인분만 표시)
 *   통보           val={target:rid, kind:'자동확정'} (카톡 dedup 마커 — 상태 아님)
 *   자동 확정 = 파생 판정: now ≥ 휴가 시작(하루·앞반차=시업, 뒤반차=종업−2h) && 미처리 → 확정.
 *   시간 트리거 없음(코어 원칙 — 저장하지 않고 읽을 때 계산).
 *
 * settings: { emp, name, schedule:{'1':['11:00','15:00'],...}(0=일…6=토), graceMin:3,
 *             holidays:['YYYY-MM-DD',…], accrualFrom:'2026-08', anchor:'2025-10-01',
 *             startFrom:'YYYY-MM-DD'(옵션 — 근태 시작일, 관리자 설정. 그 전 날짜는 근무일 자체가 아님:
 *             도입 전 테스트 도장·런치 지연분이 결근·미기록·부족분 차감을 오염시키지 않게. 미설정=기존 동작) }
 *
 * ⚖️ 법리 가드(코드 불변):
 *   - 지각은 어떤 공제·차감도 만들지 않는다(카운트만). "지각 N회=결근/연차차감" 로직 영구 금지(법무 811-4808).
 *   - 연차 신청은 이미 발생분+보너스 한도 내에서만(미발생 선사용 하드 차단 — §60⑤).
 *   - 병가·조퇴는 잔여 연차까지만 차감, 초과분은 무급(unpaidMin)으로만 집계.
 */
var AttEngine = (function () {
  var DAY_MS = 24 * 60 * 60 * 1000;
  var MONTH_GRANT_MIN = 240;      // 월 개근 시 4시간(하루치)
  var LOT_LIFE_MONTHS = 12;       // 발생 월부터 1년 사용

  // ── 시간 유틸 (전부 문자열 연산 — 타임존 함정 회피) ──
  function pad2(n) { return (n < 10 ? '0' : '') + n; }
  function ymd(d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }
  function parseYmd(s) { var p = s.split('-'); return new Date(+p[0], +p[1] - 1, +p[2]); }
  function addDays(s, n) { var d = parseYmd(s); d.setDate(d.getDate() + n); return ymd(d); }
  function weekday(s) { return parseYmd(s).getDay(); }
  function toMin(hm) { if (!hm) return null; var p = String(hm).split(':'); return (+p[0]) * 60 + (+p[1]); } // 초는 버림(유예 판정은 분 단위, 2:59까지 정상=시업+2분대까지)
  function toMinSec(hms) { if (!hms) return null; var p = String(hms).split(':'); return (+p[0]) * 3600 + (+p[1]) * 60 + (+(p[2] || 0)); }
  function fmtMin(min) { // 표시용 "N시간 M분"
    var h = Math.floor(min / 60), m = min % 60;
    if (h && m) return h + '시간 ' + m + '분';
    if (h) return h + '시간';
    return m + '분';
  }
  function ymOf(dateStr) { return dateStr.slice(0, 7); }
  function firstOfNextMonth(ym) { var y = +ym.slice(0, 4), m = +ym.slice(5, 7); m++; if (m > 12) { m = 1; y++; } return y + '-' + pad2(m) + '-01'; }
  function addMonths(dateStr, n) {
    var y = +dateStr.slice(0, 4), m = +dateStr.slice(5, 7), d = +dateStr.slice(8, 10);
    m += n; while (m > 12) { m -= 12; y++; } while (m < 1) { m += 12; y--; }
    var last = new Date(y, m, 0).getDate(); if (d > last) d = last;
    return y + '-' + pad2(m) + '-' + pad2(d);
  }
  function cmpYm(a, b) { return a < b ? -1 : a > b ? 1 : 0; }
  function monthDates(ym) {
    var y = +ym.slice(0, 4), m = +ym.slice(5, 7), n = new Date(y, m, 0).getDate(), out = [];
    for (var i = 1; i <= n; i++) out.push(ym + '-' + pad2(i));
    return out;
  }

  // ── 정정 반영: 정정 이벤트는 target 날짜의 도장(출근·퇴근·보정퇴근)을 통째 대체 ──
  function corrections(events) {
    var map = {};
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.type === '정정' && e.val && e.val.target) map[e.val.target] = e.val; // 뒤 행이 이김(append 순서)
    }
    return map;
  }

  // ── 신청 인덱스: rid → { ev, decision, memo, decidedAt, acked, notifiedAuto } ──
  function requestIndex(events) {
    var map = {};
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.type === '신청' && e.val && e.val.rid) {
        map[e.val.rid] = { ev: e, decision: null, memo: '', decidedAt: '', acked: false, notifiedAuto: false };
      }
    }
    for (var j = 0; j < events.length; j++) {
      var d = events[j];
      var t = d.val && d.val.target, r = t && map[t];
      if (!r) continue;
      if (d.type === '승인' || d.type === '반려' || d.type === '취소') { r.decision = d.type; r.memo = String(d.val.memo || ''); r.decidedAt = d.date; }
      else if (d.type === '반려확인') r.acked = true;
      else if (d.type === '통보') r.notifiedAuto = true;
    }
    return map;
  }
  function reqActive(rec) { return rec.decision !== '반려' && rec.decision !== '취소'; }
  function reqHalf(val) { // 구 이벤트는 half를 start 필드에 담았음('front'|'back') — 둘 다 허용
    var h = val.half || val.start || '';
    return (h === 'front' || h === 'back') ? h : '';
  }
  // 휴가 시작(분): 하루·앞반차=시업 / 뒤반차=종업−차감시간 (유성 07-21: 시업+2h 파생 — 스케줄 추종)
  function leaveStartMin(plan, hours, half) {
    if (half === 'back' && hours < 4) return toMin(plan.end) - hours * 60;
    return toMin(plan.start);
  }
  // 신청 상태(파생): 대기 / 승인 / 자동확정 / 반려 / 취소
  //   자동확정 = 시작 시각 도달 && 미처리. nowHms 없이 오늘 날짜면 보수적으로 '대기'(차감엔 영향 없음 — 활성이면 차감).
  function reqStatus(rec, settings, swaps, todayStr, nowHms) {
    if (rec.decision) return rec.decision;
    var d = rec.ev.date;
    var plan = dayPlan(d, settings, swaps);
    if (d < todayStr) return '자동확정';
    if (d === todayStr && plan.work && nowHms != null) {
      var startMin = leaveStartMin(plan, rec.ev.val.hours, reqHalf(rec.ev.val));
      if (Math.floor(toMinSec(nowHms) / 60) >= startMin) return '자동확정';
    }
    return '대기';
  }
  function fmtHm(min) { return pad2(Math.floor(min / 60)) + ':' + pad2(min % 60); }
  // 신청 전체 목록(카드·관리자·알림 스윕 공용)
  function listRequests(events, settings, todayStr, nowHms) {
    var swaps = swapOverrides(events, settings);
    var idx = requestIndex(events);
    var out = Object.keys(idx).map(function (rid) {
      var r = idx[rid];
      var v = r.ev.val;
      var plan = dayPlan(r.ev.date, settings, swaps);
      var startMin = plan.work ? leaveStartMin(plan, v.hours, reqHalf(v)) : null;
      return {
        rid: rid, date: r.ev.date, hours: v.hours, half: reqHalf(v),
        kind: v.hours === 2 ? '반차' : '연차',
        status: reqStatus(r, settings, swaps, todayStr, nowHms),
        memo: r.memo, decidedAt: r.decidedAt, acked: r.acked, notifiedAuto: r.notifiedAuto,
        reqAt: r.ev.at, startHm: startMin != null ? fmtHm(startMin) : null
      };
    });
    out.sort(function (a, b) { return a.date < b.date ? -1 : a.date > b.date ? 1 : 0; });
    return out;
  }

  // ── 스왑: 근무일변경 이벤트 → 날짜 오버라이드 맵 ──
  //    off 날짜=휴무 강제, on 날짜=근무 강제(+off 날짜의 요일 시각을 물려받음 — "시간도 함께 이동")
  function swapOverrides(events, settings) {
    var map = {};
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.type !== '근무일변경' || !e.val) continue;
      var offTimes = settings.schedule[String(weekday(e.val.off))] || null;
      map[e.val.off] = { work: false };
      map[e.val.on] = { work: true, times: offTimes };
    }
    return map;
  }

  // ── 하루 계획: 근무일인가, 몇 시부터 몇 시인가 ──
  //    우선순위: 스왑 > 공휴일(회사 휴무) > 요일 스케줄
  function dayPlan(dateStr, settings, swaps) {
    if (settings.startFrom && dateStr < settings.startFrom) return { work: false, why: '시작전' }; // 결근·개근·차감·캘린더 전 파생이 이 한 곳으로 일관
    var sw = swaps[dateStr];
    if (sw) {
      if (!sw.work) return { work: false, why: '스왑휴무' };
      var t = sw.times || settings.schedule[String(weekday(dateStr))];
      if (!t) return { work: false, why: '스왑시각없음' };
      return { work: true, start: t[0], end: t[1], planMin: toMin(t[1]) - toMin(t[0]), why: '스왑근무' };
    }
    if (settings.holidays.indexOf(dateStr) >= 0) return { work: false, why: '공휴일' };
    var times = settings.schedule[String(weekday(dateStr))];
    if (!times) return { work: false, why: '휴무일' };
    return { work: true, start: times[0], end: times[1], planMin: toMin(times[1]) - toMin(times[0]), why: '소정' };
  }

  // ── 하루 상태: 구간·지각·부족분·연장·미체크 ──
  //    todayStr = 서버 기준 오늘. dateStr < todayStr 이어야 '마감된 날'(부족분 확정).
  function dayStatus(dateStr, events, settings, swaps, corr, todayStr, nowHms) {
    var plan = dayPlan(dateStr, settings, swaps);
    var reqIdx = requestIndex(events);
    var ins = [], outs = [], leaves = [], amended = false, external = false;
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.date !== dateStr) continue;
      if (e.type === '출근') ins.push(e.val);
      else if (e.type === '퇴근') outs.push(e.val);
      else if (e.type === '보정퇴근') { outs.push(e.val + ':00'); amended = true; }
      else if (e.type === '연차' || e.type === '병가') leaves.push({ type: e.type, hours: e.val.hours, half: reqHalf(e.val), pending: false });
      else if (e.type === '외부근무') external = true;
      else if (e.type === '신청' && e.val && e.val.rid && reqIdx[e.val.rid] && reqActive(reqIdx[e.val.rid])) {
        var st6 = reqStatus(reqIdx[e.val.rid], settings, swaps, todayStr, nowHms);
        leaves.push({ type: e.val.hours === 2 ? '반차' : '연차', hours: e.val.hours, half: reqHalf(e.val), pending: st6 === '대기', rid: e.val.rid });
      }
    }
    if (external) { // 외부 근무(행복센터 셀프체크) = 소정근로 자동 인정·개근 유지·지각 없음(§10.14·16)
      return {
        date: dateStr, plan: plan, segments: [], open: null, firstIn: null, lastOut: null,
        workedMin: plan.work ? plan.planMin : 0, late: false, lateMin: 0, leaves: [], leaveMin: 0,
        amended: false, missingOut: false, absent: false, deficitMin: 0, overtimeMin: 0,
        closed: dateStr < todayStr, external: true
      };
    }
    var c = corr[dateStr];
    if (c) { // 정정=그 날 도장 대체 (null이면 삭제)
      ins = c.in ? [c.in + ':00'] : [];
      outs = c.out ? [c.out + ':00'] : [];
    }
    ins.sort(); outs.sort();
    // 구간 짝짓기: in[i] ↔ 그 뒤 첫 out. 남는 in=열린 구간.
    var segs = [], oi = 0, open = null;
    for (var k = 0; k < ins.length; k++) {
      while (oi < outs.length && toMinSec(outs[oi]) < toMinSec(ins[k])) oi++;
      if (oi < outs.length) { segs.push([ins[k], outs[oi]]); oi++; }
      else open = ins[k];
    }
    var workedSec = 0;
    for (var s = 0; s < segs.length; s++) workedSec += toMinSec(segs[s][1]) - toMinSec(segs[s][0]);
    var isToday = dateStr === todayStr;
    if (open && isToday && nowHms) workedSec += Math.max(0, toMinSec(nowHms) - toMinSec(open));
    var workedMin = Math.floor(workedSec / 60);

    var firstIn = ins.length ? ins[0] : null;
    var late = false, lateMin = 0;
    if (plan.work && firstIn) {
      // 앞반차(연차·병가 공통)면 지각 기준이 반차 끝(시업+차감시간)으로 이동(§10.16)
      var lateBase = toMin(plan.start);
      for (var lb = 0; lb < leaves.length; lb++) {
        var lv = leaves[lb];
        if (lv.hours < 4 && lv.half === 'front') lateBase = Math.max(lateBase, toMin(plan.start) + lv.hours * 60);
      }
      var inMin = Math.floor(toMinSec(firstIn) / 60);            // 초 버림 → 기준+3분(=+2:59까지) 정상
      if (inMin >= lateBase + settings.graceMin) { late = true; lateMin = inMin - lateBase; }
    }
    var leaveMin = 0;
    for (var l = 0; l < leaves.length; l++) leaveMin += leaves[l].hours * 60;
    if (plan.work) leaveMin = Math.min(leaveMin, plan.planMin);

    var closed = dateStr < todayStr;
    var missingOut = !!open && closed;                            // 지난 날 열린 구간 = 퇴근 미체크
    var absent = plan.work && closed && !ins.length && !leaves.length;
    var deficitMin = 0, overtimeMin = 0;
    if (plan.work && closed && !missingOut && !absent) {
      deficitMin = Math.max(0, plan.planMin - workedMin - leaveMin); // 조퇴·중간외출 부족분(분) → 연차 차감 후보
      overtimeMin = Math.max(0, workedMin - plan.planMin);           // 미승인 연장(플래그·월마감 집계만, 경고 0)
    }
    return {
      date: dateStr, plan: plan, segments: segs, open: open, firstIn: firstIn,
      lastOut: outs.length ? outs[outs.length - 1] : null, workedMin: workedMin,
      late: late, lateMin: lateMin, leaves: leaves, leaveMin: leaveMin, amended: amended,
      missingOut: missingOut, absent: absent, deficitMin: deficitMin, overtimeMin: overtimeMin,
      closed: closed, external: false
    };
  }

  // ── 미기록일(케이스 A=퇴근만 없음 / B=둘 다 없음) — 닫기 불가 시트 소스, 오래된 날부터 ──
  //    판정 시작 = max(발생 시작월 1일, 첫 도장 날짜): 도입 전 기간(시드 부여만 있던 7월, 런치 지연분)을
  //    미기록으로 몰아 첫 화면을 시트 큐로 막던 런치 블로커 수정(2026-07-22). 도장 0이면 판정 자체를 안 시작.
  function missingDays(events, settings, todayStr) {
    var corr = corrections(events);
    var swaps = swapOverrides(events, settings);
    var firstPunch = null;
    for (var fp = 0; fp < events.length; fp++) {
      var t6 = events[fp].type;
      if (t6 === '출근' || t6 === '퇴근' || t6 === '보정퇴근') { firstPunch = events[fp].date; break; }
    }
    if (!firstPunch) return [];
    var from = settings.accrualFrom + '-01';
    if (firstPunch > from) from = firstPunch;
    if (settings.startFrom && settings.startFrom > from) from = settings.startFrom; // 시작 전 열린 출근(missingOut은 plan 무관)도 차단
    var out = [];
    for (var d = from; d < todayStr; d = addDays(d, 1)) {
      var st = dayStatus(d, events, settings, swaps, corr, todayStr, null);
      if (st.external) continue;
      if (st.missingOut) out.push({ date: d, kind: 'out', end: st.plan.end || null, firstIn: st.firstIn });
      else if (st.absent) out.push({ date: d, kind: 'both' });
    }
    return out;
  }

  // ── 월 개근 판정: 그 달 모든 소정근로일에 결근이 없다 ──
  //    지각·조퇴=개근 유지(법무 811-4808 결) / 연차·병가(차감)=출근 간주 / 미체크(보정 없음)=출근은 했으므로 개근 유지
  //    ⚠️ 무급 병가(잔여 0 상태의 병가)도 "절차 밟은 병가"라 v1은 개근 유지로 본다 —
  //       행정해석 원문 확인(빌드 체크) 후 달라지면 이 함수만 고침.
  function perfectMonth(ym, events, settings, swaps, corr, todayStr) {
    var days = monthDates(ym);
    for (var i = 0; i < days.length; i++) {
      var st = dayStatus(days[i], events, settings, swaps, corr, todayStr, null);
      if (st.absent) return false;
    }
    return true;
  }

  // ── 연차 원장: 발생 lot(월할) + 보너스 lot → 차감 시뮬레이션 ──
  //    발생: accrualFrom부터, 달이 다 지나고 개근이면 다음달 1일에 240분 발생, 발생일+12개월 만료.
  //    차감 순서: 보너스 먼저 → 만료 임박 lot 먼저.
  function buildLots(events, settings, swaps, corr, todayStr) {
    var lots = [];
    var ym = settings.accrualFrom;
    while (firstOfNextMonth(ym) <= todayStr) { // 달이 다 지나야 발생 (8월분=9/1)
      if (perfectMonth(ym, events, settings, swaps, corr, todayStr)) {
        var grantAt = firstOfNextMonth(ym);
        lots.push({ kind: '월차', label: ym + ' 개근', at: grantAt, min: MONTH_GRANT_MIN, expires: addMonths(grantAt, LOT_LIFE_MONTHS) });
      }
      ym = firstOfNextMonth(ym).slice(0, 7);
    }
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (e.type === '부여' && e.val) {
        lots.push({ kind: '보너스', label: e.val.note || '관리자 부여', at: e.date, min: e.val.hours * 60, expires: addMonths(e.date, LOT_LIFE_MONTHS) });
      }
    }
    lots.sort(function (a, b) { return a.at < b.at ? -1 : a.at > b.at ? 1 : 0; });
    return lots;
  }

  // 차감 목록(시간순): 신청형(연차·병가=이벤트 날짜) + 마감된 날의 조퇴 부족분
  function buildDeductions(events, settings, swaps, corr, todayStr) {
    var ded = [];
    var seen = {};
    var reqIdx = requestIndex(events);
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if ((e.type === '연차' || e.type === '병가') && e.val) {
        ded.push({ at: e.date, min: e.val.hours * 60, type: e.type, label: e.date + ' ' + e.type + ' ' + e.val.hours + '시간' });
      }
      // 신청=즉시 차감(§10.3) — 반려·취소면 목록에서 빠져 파생 재계산이 곧 lot 복원
      if (e.type === '신청' && e.val && e.val.rid && reqIdx[e.val.rid] && reqActive(reqIdx[e.val.rid])) {
        var kd = e.val.hours === 2 ? '반차' : '연차';
        ded.push({ at: e.date, min: e.val.hours * 60, type: kd, label: e.date + ' ' + kd + ' ' + e.val.hours + '시간' });
      }
    }
    // 마감된 소정근로일의 부족분 — 이벤트가 있는 날만 훑으면 결근일 부족분을 놓치므로 계획일 전체 훑기
    //   (결근=absent은 차감이 아니라 무급 — 부족분 차감은 "출근은 했는데 4h 미만"만)
    var scanFrom = settings.accrualFrom + '-01';
    if (events.length) { var d0 = events[0].date; if (d0 < scanFrom) scanFrom = d0; }
    if (settings.startFrom && settings.startFrom > scanFrom) scanFrom = settings.startFrom; // 시작 전 테스트 도장의 부족분 차감 오염 차단
    for (var d = scanFrom; d < todayStr; d = addDays(d, 1)) {
      if (seen[d]) continue; seen[d] = 1;
      var st = dayStatus(d, events, settings, swaps, corr, todayStr, null);
      if (st.deficitMin > 0) ded.push({ at: d, min: st.deficitMin, type: '조퇴', label: d + ' 부족 ' + fmtMin(st.deficitMin) });
    }
    ded.sort(function (a, b) { return a.at < b.at ? -1 : a.at > b.at ? 1 : 0; });
    return ded;
  }

  // lot 시뮬레이션. 반환: 잔여(base/bonus)·무급분·원장 행들
  function applyLedger(lots, deductions, todayStr) {
    var work = lots.map(function (l) { return { kind: l.kind, label: l.label, at: l.at, min: l.min, left: l.min, expires: l.expires }; });
    var rows = [], unpaidMin = 0;
    work.forEach(function (l) { rows.push({ at: l.at, kind: l.kind === '보너스' ? '부여' : '발생', label: l.label, deltaMin: +l.min }); });
    for (var i = 0; i < deductions.length; i++) {
      var d = deductions[i], need = d.min;
      var avail = work.filter(function (l) { return l.at <= d.at && l.expires > d.at && l.left > 0; });
      avail.sort(function (a, b) { // 보너스 먼저 → 만료 임박 먼저
        if (a.kind !== b.kind) return a.kind === '보너스' ? -1 : 1;
        return a.expires < b.expires ? -1 : 1;
      });
      var took = 0;
      for (var j = 0; j < avail.length && need > 0; j++) {
        var take = Math.min(avail[j].left, need);
        avail[j].left -= take; need -= take; took += take;
      }
      if (took > 0) rows.push({ at: d.at, kind: '차감', label: d.label, deltaMin: -took });
      if (need > 0) { unpaidMin += need; rows.push({ at: d.at, kind: '무급', label: d.label + ' (연차 부족분)', deltaMin: 0, unpaidMin: need }); }
    }
    var baseLeft = 0, bonusLeft = 0, expiredMin = 0, liveLots = [];
    work.forEach(function (l) {
      var live = l.expires > todayStr;
      if (!live && l.left > 0) { expiredMin += l.left; rows.push({ at: l.expires, kind: '소멸', label: l.label, deltaMin: -l.left }); return; }
      if (l.left > 0) liveLots.push({ kind: l.kind, label: l.label, at: l.at, leftMin: l.left, expires: l.expires });
      if (l.kind === '보너스') bonusLeft += l.left; else baseLeft += l.left;
    });
    rows.sort(function (a, b) { return a.at < b.at ? -1 : a.at > b.at ? 1 : 0; });
    return { baseMin: baseLeft, bonusMin: bonusLeft, unpaidMin: unpaidMin, expiredMin: expiredMin, rows: rows, liveLots: liveLots };
  }

  // 종합 잔액
  function balance(events, settings, todayStr) {
    var corr = corrections(events);
    var swaps = swapOverrides(events, settings);
    var lots = buildLots(events, settings, swaps, corr, todayStr);
    var ded = buildDeductions(events, settings, swaps, corr, todayStr);
    return applyLedger(lots, ded, todayStr);
  }

  // 신청 검증+미리보기: 연차=잔여 한도 하드 차단 / 병가=잔여 초과분 무급 안내
  function previewRequest(events, settings, todayStr, kind, dateStr, hours) {
    var before = balance(events, settings, todayStr);
    var plan = dayPlan(dateStr, settings, swapOverrides(events, settings));
    if (!plan.work) return { ok: false, reason: '근무일이 아니에요', baseMin: before.baseMin, bonusMin: before.bonusMin };
    var after = balance(events.concat([{ at: '', emp: settings.emp, date: dateStr, type: kind, val: { hours: hours } }]), settings, todayStr);
    var unpaidAdd = Math.max(0, after.unpaidMin - before.unpaidMin);
    if (kind === '연차' && unpaidAdd > 0) { // 미발생 선사용 하드 차단(§60⑤) — 그 날짜 기준 쓸 수 있는 lot으로 판정
      return { ok: false, reason: '남은 연차가 부족해요 (' + fmtMin(before.baseMin + before.bonusMin) + ')', baseMin: before.baseMin, bonusMin: before.bonusMin };
    }
    return {
      ok: true, baseMin: before.baseMin, bonusMin: before.bonusMin,
      afterBaseMin: after.baseMin, afterBonusMin: after.bonusMin,
      unpaidAddMin: unpaidAdd
    };
  }

  // 월 요약(캘린더·프로그레스·나의 근태 공용)
  function monthView(ym, events, settings, todayStr, nowHms) {
    var corr = corrections(events);
    var swaps = swapOverrides(events, settings);
    var reqIdx = requestIndex(events);
    var days = monthDates(ym), out = [], planTotal = 0, workedTotal = 0;
    var lateCount = 0, absentCount = 0, leaveCount = 0, leaveMinTotal = 0, overtimeTotal = 0, deficitTotal = 0;
    var amendCount = 0, missingOutCount = 0;
    for (var i = 0; i < days.length; i++) {
      var st = dayStatus(days[i], events, settings, swaps, corr, todayStr, days[i] === todayStr ? nowHms : null);
      if (st.plan.work) planTotal += st.plan.planMin;
      workedTotal += st.workedMin;
      if (st.late) lateCount++;
      if (st.absent) absentCount++;
      if (st.leaves.length) { leaveCount++; leaveMinTotal += st.leaveMin; }
      if (st.amended) amendCount++;
      if (st.missingOut) missingOutCount++;
      overtimeTotal += st.overtimeMin; deficitTotal += st.deficitMin;
      // 그 날짜 신청들(상태 포함 — 캘린더 대기 점·확정 텍스트·처리 내역의 소스)
      var reqs = [];
      Object.keys(reqIdx).forEach(function (rid) {
        var r = reqIdx[rid];
        if (r.ev.date !== days[i]) return;
        reqs.push({
          rid: rid, hours: r.ev.val.hours, half: reqHalf(r.ev.val),
          kind: r.ev.val.hours === 2 ? '반차' : '연차',
          status: reqStatus(r, settings, swaps, todayStr, nowHms)
        });
      });
      // 캘린더 점: ok(정상)·la(지각)·lv(휴가병가)·mx(미체크 링)·pd(대기) — 근무+휴가면 2개
      var dots = [];
      if (st.segments.length || st.open) dots.push(st.missingOut ? 'mx' : (st.late ? 'la' : 'ok'));
      if (st.leaves.some(function (l) { return !l.pending; })) dots.push('lv');
      if (st.leaves.some(function (l) { return l.pending; })) dots.push('pd');
      out.push({
        date: days[i], work: st.plan.work, hol: st.plan.why === '공휴일',
        holBadge: st.plan.why === '공휴일' && !!settings.schedule[String(weekday(days[i]))], // 원래 근무 요일의 공휴일만 '휴무' 배지
        dots: dots, requests: reqs, external: st.external,
        segs: st.segments, open: st.open, leaves: st.leaves, late: st.late, amended: st.amended,
        missingOut: st.missingOut, absent: st.absent, workedMin: st.workedMin,
        planStart: st.plan.start || null, planEnd: st.plan.end || null
      });
    }
    return {
      ym: ym, days: out, planMin: planTotal, workedMin: workedTotal,
      lateCount: lateCount, absentCount: absentCount, leaveCount: leaveCount, leaveMin: leaveMinTotal,
      overtimeMin: overtimeTotal, deficitMin: deficitTotal,
      amendCount: amendCount, missingOutCount: missingOutCount,
      perfect: perfectMonth(ym, events, settings, swaps, corr, todayStr)
    };
  }

  return {
    ymd: ymd, addDays: addDays, toMin: toMin, fmtMin: fmtMin, fmtHm: fmtHm, monthDates: monthDates,
    firstOfNextMonth: firstOfNextMonth, addMonths: addMonths,
    corrections: corrections, swapOverrides: swapOverrides, dayPlan: dayPlan, dayStatus: dayStatus,
    perfectMonth: perfectMonth, buildLots: buildLots, buildDeductions: buildDeductions,
    applyLedger: applyLedger, balance: balance, previewRequest: previewRequest, monthView: monthView,
    requestIndex: requestIndex, reqActive: reqActive, reqStatus: reqStatus, reqHalf: reqHalf,
    leaveStartMin: leaveStartMin, listRequests: listRequests, missingDays: missingDays
  };
})();
if (typeof module !== 'undefined' && module.exports) module.exports = AttEngine; // node 테스트용 (GAS에선 무시)
