# 생각공작소 근태 위젯 — 설치 (각 사무실 PC에서 1회)
#
# 왜 exe를 안 뿌리고 여기서 컴파일하나:
#   인터넷에서 받은 exe에는 윈도우가 '다운로드 표식'을 붙이고, 서명 없는 exe면 SmartScreen이 막아선다.
#   **그 PC에서 직접 컴파일한 exe에는 그 표식이 없다** → 경고를 아예 만나지 않는다.
#   덤으로 소스(.cs)가 그대로 배포되니 유성이 언제든 읽을 수 있다.
#
# 관리자 권한 불요. 되돌리기 = 시작프로그램 바로가기 삭제 + 폴더 삭제(흔적 0).

$ErrorActionPreference = 'Stop'
$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA '생각공작소\근태위젯'
# 처음 까는 PC인가 — **새로 만들기 전에** 본다(만든 뒤엔 늘 있다). 다시 깔 때는 「작업 표시줄에 고정」 안내를 안 띄운다(유성 10-10:
#   이미 깔린 PC는 고정해 둔 아이콘이 그대로 있고, 안내와 시작 메뉴 폴더가 또 열리면 할 일이 남은 줄 안다).
$fresh = -not (Test-Path (Join-Path $dest '근태위젯.exe'))
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
# 두 가지 꾸러미를 다 받는다(2026-10-09):
#   ①레포 그대로(근태위젯.cs·근태위젯.ico·근태위젯_off.ico)  ②설치 페이지 zip(widget.cs·on.ico·off.ico — 압축이 한글 이름을 깨뜨리지 않게 영문)
function Pick($a, $b) { $p = Join-Path $src $a; if (Test-Path $p) { return $p }; return (Join-Path $src $b) }
$csFile  = Pick 'widget.cs' '근태위젯.cs'
$icoOn   = Pick 'on.ico' '근태위젯.ico'
$icoOff  = Pick 'off.ico' '근태위젯_off.ico'

function Say($m) { Write-Host $m }

Say ''
Say '  생각공작소 프로그램 설치'
Say '  ─────────────────────────'

# 0) 돌고 있으면 먼저 끈다 — 안 그러면 exe가 잠겨 컴파일이 실패한다
Get-Process 근태위젯 -ErrorAction SilentlyContinue | ForEach-Object {
  Say '  - 실행 중인 위젯을 끕니다'
  $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 700

# 1) 컴파일러 확인
if (-not (Test-Path $csc)) {
  Say '  ✗ 윈도우 내장 컴파일러를 못 찾았습니다.'
  Say ('    찾은 경로: ' + $csc)
  Read-Host '  엔터를 누르면 닫습니다'; exit 1
}

# 2) 폴더 준비 + 부품 복사
New-Item -ItemType Directory -Force $dest | Out-Null
Say '  - 파일을 복사합니다'
Get-ChildItem (Join-Path $src 'lib') -Filter *.dll | ForEach-Object {
  # 다운로드 표식 제거 — 이게 남아 있으면 DLL 로드가 막힐 수 있다
  try { Unblock-File $_.FullName } catch { }
  Copy-Item $_.FullName $dest -Force
}
# 아이콘 2개 — exe가 파일로 읽어 **트레이·앱 창을 상태 따라 스왑**한다(GDI 프린지 회피):
#   `근태위젯.ico`(출근 중 = 초록 체크) / `근태위젯_off.ico`(미출근·퇴근 후·휴무 = 초록 원 + 흰 로딩 호)
# ⚠️exe에 **임베드**되는 것(아래 `/win32icon`)도 `_off`다 — 작업 표시줄 고정·바로가기·탐색기가 쓰는 그 그림은
#   프로그램이 꺼져 있을 때도 있어야 하는 자리라 **상태를 따라갈 수 없다**. 따라갈 수 없으면 **기본값이 정직해야** 한다:
#   체크(=출근함)를 박아 두면 출근 안 한 아침에도 체크가 보인다(유성 2026-08-06 "기본이 로딩 표시, 출근했을 때만 체크").
Copy-Item $icoOn  (Join-Path $dest '근태위젯.ico') -Force
Copy-Item $icoOff (Join-Path $dest '근태위젯_off.ico') -Force

# 3) 이 PC에서 직접 컴파일
Say '  - 이 컴퓨터에서 프로그램을 만듭니다'
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $dest '근태위젯.exe'
$refs = @('System.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Core.dll','System.Security.dll','netstandard.dll') |
        ForEach-Object { '/r:' + (Join-Path $fw $_) }
$refs += '/r:' + (Join-Path $dest 'Microsoft.Web.WebView2.Core.dll')
$refs += '/r:' + (Join-Path $dest 'Microsoft.Web.WebView2.WinForms.dll')

# ⚠️새 exe는 **임시 이름으로 만들고** 성공(종료 코드 0 + 파일)을 확인한 뒤에 바꿔 끼운다.
#   csc는 실패해도 옛 exe를 안 지운다 → 구판은 `Test-Path $exe`만 보고 실패를 「다 됐습니다」로 말했다(2026-10-09 실측).
$tmp = Join-Path $dest '근태위젯.new.exe'
if (Test-Path $tmp) { Remove-Item $tmp -Force }
$out = & $csc /nologo /codepage:65001 /target:winexe /platform:x64 `
  ('/win32manifest:' + (Join-Path $src 'app.manifest')) `
  ('/win32icon:' + $icoOff) `
  ('/out:' + $tmp) $refs $csFile 2>&1
$built = ($LASTEXITCODE -eq 0) -and (Test-Path $tmp)
if ($built) {
  Get-ChildItem $dest -Filter '*.old' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  if (Test-Path $exe) { Remove-Item $exe -Force }
  Move-Item $tmp $exe
}

if (-not $built) {
  Say '  ✗ 만들기에 실패했습니다. 아래 내용을 유성에게 보여주세요.'
  $out | ForEach-Object { Say ('    ' + $_) }
  Read-Host '  엔터를 누르면 닫습니다'; exit 1
}

# 4) 바로가기 2개 — **컴파일이 성공한 뒤에만**(실패한 채 자동실행을 걸면 매 부팅마다 조용히 실패한다)
$sh = New-Object -ComObject WScript.Shell
function MakeLnk($path) {
  # ⚠️있으면 **지우고 새로 만든다**: `CreateShortcut`은 기존 파일을 열어 고치는 방식이라, 예전에 박아 둔
  #   아이콘 경로가 그대로 살아남는다. 2026-08-06에 상태별 아이콘을 박는 시도를 했다가 실패했는데,
  #   그때 값이 남으면 **출근 안 한 아침에도 체크**가 보인다. (`IconLocation=''`는 ArgumentException이라 못 쓴다.)
  if (Test-Path $path) { Remove-Item $path -Force }
  $s = $sh.CreateShortcut($path)
  $s.TargetPath = $exe
  $s.WorkingDirectory = $dest
  $s.Description = '생각공작소'
  $s.Save()   # 아이콘은 안 건드린다 = exe에 박힌 그림(_off, 로딩 호)을 따라간다
}
# 이름 = 「생각공작소」(D12, 4판 10-10). 옛 판이 만든 「생각공작소 근태」는 지운다(같은 프로그램이 둘로 보이지 않게).
#   이미 깔린 PC는 4판 exe가 켜질 때 스스로 이름을 바꾼다(Names.Fix) — 여기는 새로 까는 PC와 다시 까는 PC 몫.
function DropOld($dir) { $o = Join-Path $dir '생각공작소 근태.lnk'; if (Test-Path $o) { Remove-Item $o -Force } }
# ① 시작프로그램 — PC를 켜면 자동으로
$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup '생각공작소.lnk'
DropOld $startup
MakeLnk $lnk
Say '  - PC를 켤 때 자동으로 뜨도록 했습니다'

# ② 시작 메뉴 — **작업 표시줄에 고정하기 위한 발판**(2026-08-05)
#   왜 필요한가: 자동 실행은 윈도우가 로그온 뒤 한참 있다가 띄우는 일이 있고(현아쌤 08-05: 5분),
#   그동안 알약도 트레이 아이콘도 없어서 **출근을 찍을 방법이 하나도 없다**.
#   작업 표시줄에 고정해 두면 그 아이콘 한 번으로 즉시 띄울 수 있다(이미 떠 있으면 근태 앱이 열린다).
#   ⚠️'작업 표시줄에 고정'은 윈도우가 프로그램적 등록을 막아 뒀다(Win10 1809+) → 사람이 1회 눌러야 한다.
$programs = [Environment]::GetFolderPath('Programs')
$menuLnk = Join-Path $programs '생각공작소.lnk'
DropOld $programs
MakeLnk $menuLnk
Say '  - 시작 메뉴에 넣었습니다'

# 4.5) 지킴이 — 5분마다 `--watch`로 부른다(D11①: 꺼져 있으면 다시 켜고, 떠 있으면 아무것도 안 한다)
#   관리자 권한 불요(내 계정, 로그온해 있을 때만). 실패해도 설치는 계속(바로가기 자동 실행이 기본선).
$task = '생각공작소 근태 지킴이'
try {
  & schtasks.exe /Create /F /SC MINUTE /MO 5 /TN $task /TR ('"' + $exe + '" --watch') /IT 2>&1 | Out-Null
  if ($LASTEXITCODE -eq 0) { Say '  - 프로그램이 꺼지면 5분 안에 스스로 다시 켜지게 했습니다' }
  else { Say '  - (지킴이 등록은 건너뜀, 동작엔 지장 없음)' }
} catch { Say '  - (지킴이 등록은 건너뜀, 동작엔 지장 없음)' }

# 5) 실행
Start-Process $exe
Say ''
Say '  ✓ 다 됐습니다. 화면 오른쪽 아래를 보세요.'
Say ''
# 작업 표시줄 고정 안내 + 시작 메뉴 폴더 열기 = **처음 까는 PC에서만**(위 $fresh). 다시 깔면 고정해 둔 아이콘이 그대로라 할 일이 없다.
if ($fresh) {
  Say '  ★ 마지막으로 한 번만 해 주세요 - 작업 표시줄에 고정'
  Say '     방금 열린 창에서 [생각공작소]를 작업 표시줄로 끌어다 놓으세요.'
  Say '     (또는 시작 메뉴에서 "생각공작소" 검색 > 오른쪽 클릭 > 작업 표시줄에 고정)'
  Say ''
}
Say '  - 평소에는 오른쪽 아래 초록 알약을 누르면 됩니다'
Say '  - 알약이 안 보이면 작업 표시줄의 생각공작소 아이콘을 누르세요'
Say ('  - 지우려면: 이 폴더와 아래 두 파일을 지우고, 작업 스케줄러에서 「' + $task + '」를 지우세요')
Say ('      ' + $lnk)
Say ('      ' + $menuLnk)
Say ''
if ($fresh) { try { Start-Process explorer.exe ('/select,"' + $menuLnk + '"') } catch { } }
Read-Host '  엔터를 누르면 닫습니다' | Out-Null
