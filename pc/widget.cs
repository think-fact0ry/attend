// 생각공작소 근태 데스크톱 위젯 — 껍데기(2026-07-30)
//
// 이 exe가 하는 일은 **창 관리와 세션 보관뿐**이다. 보이는 것은 전부 Pages에서 온다
//   → 화면·문구·계산을 고칠 땐 `attend/widget/index.html`만 고쳐 push하면 전 PC가 다음 실행부터 반영.
//   exe를 다시 뿌리지 않아도 되게 만드는 게 이 구조의 요점이다(08 반출납의 "Pages 수정=즉시 라이브"와 같은 결).
//
// 빌드: 윈도우 내장 컴파일러만 쓴다(.NET SDK 설치 0). `설치.bat` 참고.
//   csc.exe /target:winexe /platform:x64 ... 근태위젯.cs
// 문법: **C# 5**(내장 csc 한계) — 문자열 보간·식 본문 멤버·nameof·?. 금지.
//
// 설계 근거 = docs/4_설계_2026-07-30_근태위젯_유저플로우_사전구현.md
//   · 알약은 **읽기 전용**(도장·연차는 앱 창이 PIN 게이트를 거쳐 한다)
//   · 모서리·테두리색·그림자는 **윈도우가 그린다**(DWM) — CSS로 또 그리면 경계 픽셀이 어긋난다(21차 교훈)
//   · 타이머로 상태를 재지 않는다 — 페이지가 서버 시각으로 매번 계산한다(PC가 꺼져 있으면 타이머는 안 돈다)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Cfg {
    public const string PILL_URL = "https://think-fact0ry.github.io/attend/widget/";
    public const string APP_URL = "https://think-fact0ry.github.io/attend/";
    public const string APP_ADMIN_URL = "https://think-fact0ry.github.io/attend/?admin=1";
    public const int APP_W = 460;   // Chrome 창 하한(~500px) 밖이라 폰 목업(480px 미디어쿼리)이 안 켜진다
    public const int APP_H = 860;
    public const int PILL_H = 34;
    public const int GAP = 8;
    // 알약 창의 제목 — 테두리가 없어 **화면엔 안 보이지만**, 두 번째 인스턴스가 우리를 찾아내는 이름이다.
    //   앱 창 제목('생각공작소 근태')과 겹치면 엉뚱한 창을 찾으므로 다르게 둔다.
    public const string PILL_TITLE = "생각공작소 근태 알약";
    // ── 판 번호·자동 업데이트(2026-10-09, 문의 알리미 1-c — 유성 「업데이트해도 재설치 안 하게」) ──
    //   ⚠️이 파일을 고치면 **SHELL_VER를 올리고** `node tools/위젯배포/publish.js`로 올린다(안 올리면 스크립트가 거부).
    //   각 PC가 6시간마다 attend/pc/ver.txt를 보고, 서명이 맞는 더 높은 판이면 받아서 **그 PC에서 만들고** 쉬는 틈에 바꿔 끼운다.
    public const int SHELL_VER = 2;
    public const string PC_URL = "https://think-fact0ry.github.io/attend/pc/";
    // 서명 확인용 **공개** 열쇠(RSA 3072). 비밀 열쇠는 레포 밖(유성 노트북 %USERPROFILE%\.saenggak\pc-sign.pem) —
    //   깃허브 계정만 털려서는 가짜 판을 못 만든다. 열쇠를 바꾸면 이 줄이 바뀌므로 전 PC 재설치 1회.
    public const string SIGN_PUB = "<RSAKeyValue><Modulus>xvr81ueM/4A9/i4xtaOXkI6T/iQJSp+VKhEF+4iO4M+KN6mRRdOEcdZfqMgLyxaELJvUkDNtK4bi4wLfWJmzTO6MJfWil721BU5gcAJOsB5zHkPprnzZwrk1X1eT4oFUwShUQ7xRkQvxytxNMbC2kfD6lr4d0VTacpH07zCRhNHG4VbeBLVUdI8VLKFJw45Y5Lp8nsLtxK/Cd2g9Evu+ffrd8Mb53JmS6lbYcOSeExbyoZ/7MML+tFZvXiJyG4l5obw48TM2bigPRSQO1eeF28a/Ejmwk/Jg20eIF0duDfihnBVH6VV/0EAAw0hj4kph4z12zShch3enc+2B+h5vJalLJmEDmVpsR2ceToib/f+iufC7sGl/UcgVLlQBPx7k039HV+UijO8j2eS2dyGRpob9CFHxnbw7fOzfiPH5sQyK4kTckbqt2exEr8klhbhIYwtayNxVD1u0Yovq3vO5t+Af5zy5/IhCg/KZJaVESq3E03r0yH4SsdBWlSvtJvuV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    public static string ExeDir() { return Path.GetDirectoryName(Application.ExecutablePath); }
    public static string DataDir() {
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "생각공작소\\근태위젯");
        Directory.CreateDirectory(p);
        return p;
    }
    // 문서 로드 URL에 캐시버스터(08-01 실사고): Pages HTTP 캐시(max-age 10분)가 push 직후의 옛 HTML을
    //   돌려줘 위젯을 재시작해도 옛 코드가 떴다(sw network-first의 fetch도 HTTP 캐시를 탄다).
    //   URL이 매번 유니크하면 캐시 미스가 확정이라 상주 문서가 항상 신선하다.
    public static string Bust(string url) {
        return url + (url.IndexOf('?') >= 0 ? "&" : "?") + "b=" + DateTime.Now.Ticks;
    }
}

static class Native {
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage; public int uEdge; public RECT rc; public int lParam; }
    public const int ABM_GETTASKBARPOS = 5, ABE_TOP = 1, ABE_BOTTOM = 3;
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(int m, ref APPBARDATA d);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    // 전체화면 게임·발표·집중모드에서는 알약을 감춘다(문서화된 API)
    public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    // 테두리 없는 창을 사용자가 끌어 옮기게 하는 표준 수법 — 마우스 캡처를 놓고
    //   "제목 표시줄을 눌렀다"고 윈도우에 알린다. 그 뒤 이동은 윈도우가 알아서 한다.
    public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, int wp, int lp);

    // 항상-위를 **실제 Z순서로** 다시 올린다. WinForms의 `TopMost` 속성은 우리가 저장한 bool이라
    //   창이 실제로 다른 창 밑으로 밀려나도 계속 true를 돌려준다 → `if(!TopMost) TopMost=true`는 영영 안 돈다.
    //   (2026-07-31 유성 "알약이 갑자기 없어졌는데" — 창은 보이는 상태로 제자리에 있었고 최상위 플래그도 켜져 있었지만,
    //    그 좌표의 맨 위 창을 물으면 VS Code가 나왔다. 플래그만 남고 순서를 잃은 상태.)
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);

    // 앱 창 가장자리 인셋(③ 08-01) — 시스템 프레임 두께를 그대로 쓴다(96dpi=8px, 그중 7px는 화면에 안 보이는 영역)
    public const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    // 마지막 입력(키보드·마우스) 시각 — 업데이트를 사람이 안 쓰는 틈에만 적용하려고(1-c)
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);

    // 프로세스끼리 한 마디 주고받기(2026-08-05) — 작업표시줄에 고정한 아이콘을 눌렀을 때
    //   **이미 떠 있는 알약**에게 "네가 나와라"를 전한다. 파이프·소켓을 들일 일이 아니다.
    //   `RegisterWindowMessage`는 이름이 같으면 시스템 전체에서 같은 번호를 준다 = 약속된 채널.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
}

// ── 약속된 창 메시지 ────────────────────────────────────────────
//   `TaskbarCreated`는 **윈도우가 정한 이름**이다 — 탐색기가 작업표시줄을 (다시) 만들 때 모든 최상위 창에
//   뿌린다. 부팅 중 셸이 아직 트레이를 못 만든 시점에 우리가 아이콘을 넣었다면 그 등록은 실패했을 수 있고,
//   이 신호가 "지금 다시 넣어라"는 유일한 공식 통보다.
static class Msg {
    public static readonly int Show = Native.RegisterWindowMessage("생각공작소_근태위젯_SHOW");
    public static readonly int TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
}

// ── 언제 떴나 ───────────────────────────────────────────────────
//   2026-08-05 현아쌤 "근태관리 어디서 찍나요? 숨겨진 아이콘에는 안 보이고" → 5분 뒤 "방금 생겼어요".
//   트레이 아이콘은 프로세스가 뜨는 즉시 만들어지므로(BuildTray는 Application.Run 전) 그 5분은
//   **프로세스가 아직 없던 시간**이다. 그런데 우리는 시작 시각을 한 번도 안 남겨서 사후 확인이 불가능했다.
//   → 부팅 후 몇 초에 떴는지를 로그와 서버에 남긴다. 사람 기억에 묻지 않기 위한 계측이다.
static class Boot {
    public static readonly DateTime At = DateTime.Now;
    public static readonly int UpSec = UpSeconds();
    public static bool Sent = false;   // 페이지에 한 번만 넘긴다(다시 읽기로 ready가 또 와도 재전송 안 함)
    static int UpSeconds() {
        // `Environment.TickCount`는 int라 24.9일에 음수로 넘어간다 — 그때도 양수 초가 나오게 편다.
        long t = Environment.TickCount;
        if (t < 0) t += 4294967296L;
        return (int)(t / 1000);
    }
}

// ── 알약 위치 기억 ──────────────────────────────────────────────
//   유성이 직접 옮긴 자리가 있으면 그걸 쓰고, 없으면 트레이 왼쪽에 자동 배치한다.
//   비밀이 아니므로 평문(암호화 불요). 화면 밖이면 무시하고 자동 배치로 되돌린다.
static class Pos {
    static string File_() { return Path.Combine(Cfg.DataDir(), "pos.txt"); }
    public static bool Has = false;
    public static int X = 0, Y = 0;
    public static void Load() {
        try {
            if (!File.Exists(File_())) return;
            string[] p = File.ReadAllText(File_()).Split(',');
            if (p.Length == 2 && int.TryParse(p[0], out X) && int.TryParse(p[1], out Y)) Has = true;
        } catch (Exception) { Has = false; }
    }
    public static void Save(int x, int y) {
        X = x; Y = y; Has = true;
        try { File.WriteAllText(File_(), x + "," + y); } catch (Exception) { }
    }
    public static void Clear() {
        Has = false;
        try { if (File.Exists(File_())) File.Delete(File_()); } catch (Exception) { }
    }
}

// ── 앱 창 위치·크기 기억 ────────────────────────────────────────
//   유성 07-31 "앱 창이 이전 크기·위치를 기억하게" — 알약 Pos와 같은 결(평문·화면 밖이면 버림).
//   저장은 사용자가 창을 옮기거나 늘렸을 때(ResizeEnd)와 닫을 때. 흔적은 이 파일 하나 = 폴더 삭제로 소멸.
static class AppPos {
    static string File_() { return Path.Combine(Cfg.DataDir(), "appwin.txt"); }
    public static bool Has = false;
    public static int X, Y, W, H;
    public static void Load() {
        try {
            if (!File.Exists(File_())) return;
            string[] p = File.ReadAllText(File_()).Split(',');
            if (p.Length == 4 && int.TryParse(p[0], out X) && int.TryParse(p[1], out Y)
                && int.TryParse(p[2], out W) && int.TryParse(p[3], out H)
                && W >= 200 && H >= 300) Has = true;   // 깨진 값이면 기억 없음으로(자동 배치 폴백)
        } catch (Exception) { Has = false; }
    }
    public static void Save(int x, int y, int w, int h) {
        X = x; Y = y; W = w; H = h; Has = true;
        try { File.WriteAllText(File_(), x + "," + y + "," + w + "," + h); } catch (Exception) { }
    }
}

// ── 세션 보관 ───────────────────────────────────────────────────
//   위젯 토큰은 **앱의 localStorage와 따로** 호스트가 들고 있다. 그래야 "앱은 잠겨 PIN을 묻는데
//   알약은 계속 보인다"(유성 확정)가 성립한다. 자정이 지나면 버린다(하루 단위 리셋).
static class Session {
    static string File_() { return Path.Combine(Cfg.DataDir(), "state.dat"); }
    public static string Token = "", Emp = "", Day = "";

    public static void Load() {
        try {
            if (!File.Exists(File_())) return;
            byte[] enc = File.ReadAllBytes(File_());
            byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            string[] p = Encoding.UTF8.GetString(raw).Split('\n');
            if (p.Length >= 3) { Token = p[0]; Emp = p[1]; Day = p[2]; }
            if (Day != DateTime.Now.ToString("yyyy-MM-dd")) Clear();   // 자정 리셋
        } catch (Exception) { Clear(); }
    }
    public static void Save(string token, string emp) {
        if (token == null || token == "") { Clear(); return; }   // 빈 토큰을 저장하면 "로그인한 척"이 남는다
        Token = token; Emp = emp; Day = DateTime.Now.ToString("yyyy-MM-dd");
        try {
            byte[] raw = Encoding.UTF8.GetBytes(Token + "\n" + Emp + "\n" + Day);
            File.WriteAllBytes(File_(), ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
        } catch (Exception) { }
    }
    public static void Clear() {
        Token = ""; Emp = ""; Day = "";
        try { if (File.Exists(File_())) File.Delete(File_()); } catch (Exception) { }
    }
    public static bool RolledOver() { return Day != "" && Day != DateTime.Now.ToString("yyyy-MM-dd"); }
}

// ── 아주 작은 JSON 읽기 (필요한 필드만) ──────────────────────────
//   직렬화기를 들이지 않는다 — 주고받는 건 필드 5개뿐이고, 의존을 늘리면 배포가 무거워진다.
static class J {
    public static string Str(string json, string key) {
        string k = "\"" + key + "\"";
        int i = json.IndexOf(k);
        if (i < 0) return null;
        i = json.IndexOf(':', i + k.Length);
        if (i < 0) return null;
        i++;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length) return null;
        if (json[i] != '"') {              // 숫자·true·false
            int e = i;
            while (e < json.Length && json[e] != ',' && json[e] != '}') e++;
            return json.Substring(i, e - i).Trim();
        }
        i++;
        var sb = new StringBuilder();
        while (i < json.Length && json[i] != '"') {
            if (json[i] == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i] == 'n' ? '\n' : json[i]); }
            else sb.Append(json[i]);
            i++;
        }
        return sb.ToString();
    }
    public static bool Bool(string json, string key) { return Str(json, key) == "true"; }
    // 로그 꼬리표 — 값이 있으면 " (값)"을, 없으면 빈 문자열을. ⚠️토큰·PIN을 담는 키에는 쓰지 말 것(0-10).
    public static string Tail(string json, string key) {
        string v = Str(json, key);
        return (v == null || v == "") ? "" : (" (" + v + ")");
    }
    public static int Int(string json, string key, int dflt) {
        int v; string s = Str(json, key);
        return (s != null && int.TryParse(s, out v)) ? v : dflt;
    }
    public static string Esc(string s) { return (s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"")); }
}

// ── 자동 업데이트 (2026-10-09, 1-c) ─────────────────────────────
//   흐름: 확인(6시간마다) → ver.txt 서명 확인 → 파일 받고 해시 확인 → **이 PC에서 만든다**(받은 exe는 SmartScreen에 막힌다 —
//   설치와 같은 이유) → 만든 exe를 `--selftest`로 한 번 돌려 본다 → 쉬는 틈(패널 접힘·앱 창 닫힘·손 1분 안 닿음)에
//   파일을 바꿔 끼우고 새 판으로 다시 켠다 → 새 판이 알약을 띄우면(ready) 확정, 3분 안에 못 띄우거나 죽으면 옛 판으로 되돌린다.
//   ⚠️판 파일 이름은 영문(zip 압축이 한글 이름을 깨뜨릴 수 있다) — 설치 폴더 이름과의 대응은 DestOf 한 곳.
//   설계·위험 = docs/4_설계_2026-10-08_문의알리미_1단계.md §3(10-09 엄격 검토).
static class Upd {
    static void Log(string m) { AppWin.EdgeLog("업데이트: " + m); }
    public static volatile string Ready = null;   // 만들고 시험까지 끝난 새 판 폴더 — 쉬는 틈에 적용
    public static int ReadyVer = 0;
    static int busy = 0, failedVer = 0;           // failedVer = 이 판은 실패 → 더 높은 판이 올라올 때까지 다시 안 만든다
    static string Marker() { return Path.Combine(Cfg.DataDir(), "updated.txt"); }

    // 판 파일 이름 → 설치 폴더 이름. null = 만들 때만 쓰고 설치 폴더엔 안 둔다(widget.cs·app.manifest)
    public static string DestOf(string n) {
        if (n == "on.ico") return "근태위젯.ico";
        if (n == "off.ico") return "근태위젯_off.ico";
        if (n.StartsWith("lib/") && n.EndsWith(".dll")) return n.Substring(4);
        return null;
    }

    public static void CheckAsync() {
        if (Ready != null || Interlocked.Exchange(ref busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(delegate {
            try { Check(); } catch (Exception ex) { Log("확인 실패 " + ex.Message); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }
    static byte[] Get(string rel) {
        using (var w = new WebClient()) {
            w.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return w.DownloadData(new Uri(Cfg.PC_URL + rel + "?b=" + DateTime.Now.Ticks));   // Pages 10분 캐시 회피(Cfg.Bust와 같은 이유)
        }
    }
    static void Check() {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string txt = Encoding.UTF8.GetString(Get("ver.txt")).Replace("\r\n", "\n");
        int si = txt.IndexOf("\nsig=");
        if (si < 0) { Log("ver.txt에 서명 줄이 없음"); return; }
        string body = txt.Substring(0, si + 1);   // 서명 대상 = sig 줄 앞 전부(마지막 줄바꿈 포함) — publish.js와 같은 규칙
        string sig = txt.Substring(si + 5).Trim();
        int ver = 0; var files = new List<string[]>();
        foreach (string line in body.Split('\n')) {
            if (line.StartsWith("ver=")) int.TryParse(line.Substring(4), out ver);
            else if (line.StartsWith("f=")) { string[] p = line.Substring(2).Split('|'); if (p.Length == 2) files.Add(p); }
        }
        if (ver <= Cfg.SHELL_VER || ver == failedVer) return;   // 새 판 없음 = 조용히
        if (!Verify(Encoding.UTF8.GetBytes(body), sig)) { Log("서명이 안 맞음 — 판 " + ver + " 거절"); failedVer = ver; return; }
        Log("새 판 " + ver + " (지금 " + Cfg.SHELL_VER + ") — 받는 중");
        string stage = Path.Combine(Path.Combine(Cfg.DataDir(), "upd"), ver.ToString());
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(Path.Combine(stage, "lib"));
        bool hasCs = false;
        foreach (string[] f in files) {
            string n = f[0];
            if (n.IndexOf("..") >= 0 || n.IndexOf('\\') >= 0 || n.IndexOf(':') >= 0 || n.StartsWith("/")) { Log("이상한 파일 이름 거절 " + n); failedVer = ver; return; }
            byte[] b = Get(n);
            if (Hex(SHA256.Create().ComputeHash(b)) != f[1].Trim().ToLowerInvariant()) { Log("해시가 안 맞음 " + n); failedVer = ver; return; }
            File.WriteAllBytes(Path.Combine(stage, n.Replace('/', '\\')), b);
            if (n.StartsWith("lib/")) File.WriteAllBytes(Path.Combine(stage, n.Substring(4)), b);   // 시험 실행용 — exe 옆에 있어야 로드된다
            if (n == "widget.cs") hasCs = true;
        }
        if (!hasCs) { Log("소스가 없음"); failedVer = ver; return; }
        string exe = Path.Combine(stage, "근태위젯.exe"), err;
        if (!Compile(stage, exe, out err)) { Log("만들기 실패 " + err); failedVer = ver; return; }
        if (!SelfTest(exe)) { Log("시험 실행 실패"); failedVer = ver; return; }
        Log("판 " + ver + " 준비 끝 — 쉬는 틈에 바꿔 끼움");
        ReadyVer = ver; Ready = stage;
    }
    static bool Verify(byte[] data, string sigB64) {
        try {
            using (var rsa = new RSACryptoServiceProvider(new CspParameters(24))) {   // 24 = PROV_RSA_AES(SHA256 지원)
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(Cfg.SIGN_PUB);
                return rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(sigB64));
            }
        } catch (Exception) { return false; }
    }
    static string Hex(byte[] h) { var sb = new StringBuilder(); foreach (byte x in h) sb.Append(x.ToString("x2")); return sb.ToString(); }

    // install.ps1과 같은 명령(참조·아이콘·매니페스트). ⚠️종료 코드와 파일 둘 다 본다 —
    //   csc는 실패해도 옛 파일을 지우지 않는다(10-09 실측: install.ps1이 이걸로 「다 됐습니다」를 오판했다).
    public static bool Compile(string src, string outExe, out string err) {
        string fw = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319";
        var a = new StringBuilder("/nologo /codepage:65001 /target:winexe /platform:x64");
        a.Append(" \"/win32manifest:" + Path.Combine(src, "app.manifest") + "\"");
        a.Append(" \"/win32icon:" + Path.Combine(src, "off.ico") + "\"");
        a.Append(" \"/out:" + outExe + "\"");
        foreach (string r in new string[] { "System.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Core.dll", "System.Security.dll", "netstandard.dll" })
            a.Append(" \"/r:" + Path.Combine(fw, r) + "\"");
        a.Append(" \"/r:" + Path.Combine(src, @"lib\Microsoft.Web.WebView2.Core.dll") + "\"");
        a.Append(" \"/r:" + Path.Combine(src, @"lib\Microsoft.Web.WebView2.WinForms.dll") + "\"");
        a.Append(" \"" + Path.Combine(src, "widget.cs") + "\"");
        try { if (File.Exists(outExe)) File.Delete(outExe); } catch (Exception) { }
        var psi = new ProcessStartInfo(Path.Combine(fw, "csc.exe"), a.ToString());
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        using (var p = Process.Start(psi)) {
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120000);
            err = o.Length > 300 ? o.Substring(0, 300) : o;
            return p.HasExited && p.ExitCode == 0 && File.Exists(outExe);
        }
    }
    static bool SelfTest(string exe) {
        var psi = new ProcessStartInfo(exe, "--selftest");
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = Path.GetDirectoryName(exe);
        using (var p = Process.Start(psi)) {
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch (Exception) { } return false; }
            return p.ExitCode == 0;
        }
    }
    // 새 exe가 `--selftest`로 불렸을 때: 뜨지 않고, WebView2 부품이 로드되는지만 보고 끝낸다(뮤텍스·창·트레이 0)
    public static int SelfTestMain() {
        try {
            string v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrEmpty(v) ? 3 : 0;
        } catch (Exception) { return 2; }
    }

    // 바꿔 끼우기(UI 스레드, 쉬는 틈에). 돌고 있는 exe·dll도 **이름 바꾸기는 된다**(지우기·덮어쓰기는 안 됨) —
    //   그래서 옛 것을 .old로 옮기고 새 것을 그 자리에 둔다. 하나라도 실패하면 그 자리에서 전부 되돌린다.
    public static bool Apply() {
        string stage = Ready, dest = Cfg.ExeDir();
        if (stage == null) return false;
        var pairs = new List<string[]>();
        pairs.Add(new string[] { Path.Combine(stage, "근태위젯.exe"), Path.Combine(dest, "근태위젯.exe") });
        pairs.Add(new string[] { Path.Combine(stage, "on.ico"), Path.Combine(dest, DestOf("on.ico")) });
        pairs.Add(new string[] { Path.Combine(stage, "off.ico"), Path.Combine(dest, DestOf("off.ico")) });
        foreach (string d in Directory.GetFiles(Path.Combine(stage, "lib"), "*.dll"))
            pairs.Add(new string[] { d, Path.Combine(dest, DestOf("lib/" + Path.GetFileName(d))) });
        var done = new List<string>();
        try {
            foreach (string[] p in pairs) {
                if (!File.Exists(p[0])) continue;
                if (File.Exists(p[1]) && SameFile(p[0], p[1])) continue;
                string old = p[1] + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(p[1])) File.Move(p[1], old);
                done.Add(p[1]);
                File.Copy(p[0], p[1]);
            }
        } catch (Exception ex) {
            Log("바꿔 끼우기 실패 — 되돌림: " + ex.Message);
            Undo(done); Ready = null; failedVer = ReadyVer; return false;
        }
        File.WriteAllText(Marker(), ReadyVer + "\n" + string.Join("\n", done.ToArray()));
        Log("판 " + ReadyVer + "으로 바꿔 끼움(" + done.Count + "개) — 다시 켬");
        Program.Relaunch();
        return true;
    }
    static bool SameFile(string a, string b) {
        try {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return Hex(SHA256.Create().ComputeHash(File.ReadAllBytes(a))) == Hex(SHA256.Create().ComputeHash(File.ReadAllBytes(b)));
        } catch (Exception) { return false; }
    }
    static void Undo(List<string> done) {
        foreach (string f in done) {
            try {
                if (File.Exists(f)) { string bad = f + ".bad"; if (File.Exists(bad)) File.Delete(bad); File.Move(f, bad); }
                if (File.Exists(f + ".old")) File.Move(f + ".old", f);
            } catch (Exception ex) { Log("되돌리기 실패 " + f + " " + ex.Message); }
        }
    }
    // 새 판으로 막 바뀐 상태인가(아직 알약이 한 번도 안 떴다)
    public static bool Unconfirmed() { return File.Exists(Marker()); }
    // 새 판이 알약을 띄웠다 = 확정. 옛 파일 정리
    public static void Confirm() {
        try {
            if (!File.Exists(Marker())) return;
            string[] l = File.ReadAllText(Marker()).Split('\n');
            for (int i = 1; i < l.Length; i++) { try { if (l[i] != "" && File.Exists(l[i] + ".old")) File.Delete(l[i] + ".old"); } catch (Exception) { } }
            File.Delete(Marker());
            Log("판 " + Cfg.SHELL_VER + " 정상 확인 — 옛 파일 정리");
            try { Directory.Delete(Path.Combine(Cfg.DataDir(), "upd"), true); } catch (Exception) { }
        } catch (Exception) { }
    }
    // 새 판이 죽었거나 3분 안에 알약을 못 띄웠다 → 옛 판으로 되돌리고 그걸로 다시 켠다
    public static void Rollback(string why) {
        try {
            string[] l = File.ReadAllText(Marker()).Split('\n');
            Log("판 " + l[0] + " 되돌림 — " + why);
            var done = new List<string>();
            for (int i = 1; i < l.Length; i++) if (l[i] != "") done.Add(l[i]);
            Undo(done);
            File.Delete(Marker());
            File.WriteAllText(Path.Combine(Cfg.DataDir(), "upd-bad.txt"), l[0]);   // 이 판은 다시 시도하지 않는다(옛 판이 읽음)
        } catch (Exception ex) { Log("되돌리기 오류 " + ex.Message); }
        Program.Relaunch();
    }
    // 옛 판이 되돌림 뒤 켜졌을 때 — 실패한 판 번호를 기억해 6시간마다 같은 판을 다시 깔지 않는다
    public static void LoadBad() {
        try { string f = Path.Combine(Cfg.DataDir(), "upd-bad.txt"); if (File.Exists(f)) int.TryParse(File.ReadAllText(f).Trim(), out failedVer); } catch (Exception) { }
    }
}

// ══════════════════════════════════════════════════════════════
class Pill : Form {
    WebView2 web;
    NotifyIcon tray;
    System.Windows.Forms.Timer tick;
    AppWin app;
    int pillW = 120;
    bool on = false, dim = false, warn = false, ready = false;
    System.Windows.Forms.Timer trayGuard;   // 트레이 아이콘 재적용(부팅 직후 등록 실패 대비)
    int trayGuardN = 0;
    System.Windows.Forms.Timer navRetry;    // 알약 페이지 재시도(부팅 직후 네트워크 없음 대비)
    int navFails = 0;
    static void Log(string m) { AppWin.EdgeLog(m); }   // 로그는 한 파일에 쌓는다(AppWin이 그 파일의 주인)
    System.Windows.Forms.Timer updTimer, confirmT;   // 업데이트 확인(6시간) · 새 판 확정 마감(3분)
    bool dragging = false;   // 끄는 중엔 1초 틱이 자리를 되돌리지 못하게 막는다(안 그러면 손과 싸운다)
    bool userDrag = false;   // **3초 꾹 눌러 시작한 끌기**일 때만 true — 시스템이 옮긴 것과 구분한다
    bool placed = false;     // 자동 배치를 한 번이라도 했나
    bool expanded = false;   // 패널이 펼쳐져 있나 — 그동안엔 자동 배치가 끼어들지 않는다
    int lastX = -99999, lastY = -99999;   // 마지막으로 계산한 자리 — 같으면 손대지 않는다(알트탭·F11 흔들림 차단)
    string lastBorder = "";

    // ── 끌기: 윈도우에 맡기지 않고 **우리가 직접** 옮긴다 ──────────────
    //   07-30 1차엔 `WM_NCLBUTTONDOWN`+`WM_EXITSIZEMOVE`에 맡겼는데 두 가지가 다 어긋났다:
    //     ①`WM_EXITSIZEMOVE`는 사용자가 끌지 않아도 온다(시스템 이동에도) → 엉뚱한 좌표가 저장됐고
    //     ②반대로 우리가 시작한 끌기에서는 안 오는 경우가 있어 **옮겨도 저장이 안 돼 제자리로 돌아왔다**(유성 신고).
    //   마우스를 따라 창을 옮기고 버튼을 놓으면 끝 — 이건 어긋날 구석이 없다.
    System.Windows.Forms.Timer dragTimer;
    Point grabOffset;
    void BeginUserDrag() {
        userDrag = true; dragging = true;
        grabOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
        if (dragTimer == null) {
            dragTimer = new System.Windows.Forms.Timer();
            dragTimer.Interval = 16;
            dragTimer.Tick += delegate {
                if ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.None) { EndUserDrag(); return; }
                Location = new Point(Cursor.Position.X - grabOffset.X, Cursor.Position.Y - grabOffset.Y);
            };
        }
        dragTimer.Start();
    }
    void EndUserDrag() {
        if (dragTimer != null) dragTimer.Stop();
        if (!userDrag) return;
        userDrag = false; dragging = false;
        Pos.Save(Location.X, Location.Y);   // 놓은 그 자리에 고정(유성 "클릭을 놓으면 고정되게")
        Send("{\"type\":\"dragend\"}");
    }

    // 포커스를 훔치지 않고, Alt+Tab·작업표시줄에도 안 뜬다(알약은 창이 아니라 장식이다)
    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    // ── 밖에서 오는 두 마디 ──────────────────────────────────────
    protected override void WndProc(ref Message m) {
        // ①작업표시줄에 고정한 아이콘을 눌렀다(두 번째 인스턴스가 보내고 스스로 끝난다)
        if (Msg.Show != 0 && m.Msg == Msg.Show) { OnPinClick(); return; }
        // ②탐색기가 작업표시줄을 (다시) 만들었다 → 트레이 아이콘을 다시 넣는다
        if (Msg.TaskbarCreated != 0 && m.Msg == Msg.TaskbarCreated) { Log("TaskbarCreated 수신 — 트레이 재등록"); ReassertTray(); }
        base.WndProc(ref m);
    }

    // 트레이 아이콘을 **다시 넣는다**(멱등 재적용).
    //   ⚠️`NotifyIcon`은 Shell_NotifyIcon이 실패해도 내부 `added` 플래그를 true로 세운다 = 실패를 삼킨다.
    //     그래서 부팅 중 셸이 아직 트레이를 못 만든 시점에 넣었으면 **아이콘이 영영 안 뜨는데 코드는 넣은 줄 안다.**
    //     플래그를 읽어 분기하지 말고 원하는 상태를 다시 만든다 — 24차-2 `TopMost` 캐시 bool과 정확히 같은 처방.
    void ReassertTray() {
        if (tray == null) return;
        try { tray.Visible = false; tray.Visible = true; } catch (Exception) { }
    }

    // 작업표시줄 고정 아이콘을 눌렀을 때 — **항상 무언가는 한다**.
    //   구판은 두 번째 인스턴스가 뮤텍스에 막혀 조용히 죽어서 "눌러도 아무 일 없음"이었다(현아쌤에겐 고장과 같다).
    //   이제 이 한 번의 클릭이 ①알약을 보이게 ②맨 위로 ③트레이 아이콘 되살리기 ④제자리로 ⑤근태 앱 열기를 한다.
    void OnPinClick() {
        Log("고정 아이콘 클릭 — 이미 실행 중");
        try {
            if (!Visible) Visible = true;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            ReassertTray();
            if (!dragging && !expanded) { placed = false; Reposition(); }   // 화면 밖으로 밀렸으면 이 클릭이 구조 신호
            OpenApp(Cfg.APP_URL);   // Env가 아직 없으면 조용히 무시된다(부팅 중이면 알약이 곧 뜬다)
        } catch (Exception) { }
    }

    public static CoreWebView2Environment Env;   // 알약·앱 창이 **같은 프로필**을 공유해야 로그인·서비스워커가 함께 산다

    public Pill() {
        Text = Cfg.PILL_TITLE;   // 화면엔 안 보인다(테두리 없는 창) — 두 번째 인스턴스가 우리를 찾는 이름
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        ClientSize = new Size(pillW, Cfg.PILL_H);

        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.White;   // 투명 불가(알파 0/255만) — 흰색 고정
        Controls.Add(web);
        web.CoreWebView2InitializationCompleted += OnWebReady;

        BuildTray();

        tick = new System.Windows.Forms.Timer();
        tick.Interval = 1000;
        tick.Tick += OnTick;

        Load += delegate {
            ApplyCorner();
            ApplyBorder();
            Reposition();
            tick.Start();
            Log("── 시작 (부팅 후 " + Boot.UpSec + "초 · 판 " + Cfg.SHELL_VER + ")");
            // 자동 업데이트 확인: 켠 뒤 3분(부팅 직후 네트워크·CPU를 피함), 그 뒤 6시간마다
            updTimer = new System.Windows.Forms.Timer();
            updTimer.Interval = 3 * 60 * 1000;
            updTimer.Tick += delegate { updTimer.Interval = 6 * 60 * 60 * 1000; Upd.CheckAsync(); };
            updTimer.Start();
            // 막 새 판으로 바뀌었다 → 3분 안에 알약이 떠야(ready) 확정. 못 뜨면 옛 판으로
            if (Upd.Unconfirmed()) {
                Log("새 판 첫 실행 — 알약이 뜨면 확정");
                confirmT = new System.Windows.Forms.Timer();
                confirmT.Interval = 3 * 60 * 1000;
                confirmT.Tick += delegate { confirmT.Stop(); if (Upd.Unconfirmed()) Upd.Rollback("3분 안에 알약이 안 뜸"); };
                confirmT.Start();
            }
            // 부팅 직후엔 셸이 트레이를 만드는 중일 수 있다 → 두 번 더 넣어 본다(이미 있으면 그대로 다시 들어갈 뿐).
            //   `TaskbarCreated` 수신이 본선이고 이건 그 신호를 놓쳤을 때의 백스톱이다.
            trayGuard = new System.Windows.Forms.Timer();
            trayGuard.Interval = 8000;
            trayGuard.Tick += delegate {
                trayGuardN++;
                ReassertTray();
                if (trayGuardN >= 2) { trayGuard.Stop(); return; }
                trayGuard.Interval = 45000;
            };
            trayGuard.Start();
            // ⚠️환경 생성은 **폼이 뜬 뒤에** 시작한다 — Application.Run 전에는 WinForms 동기화 컨텍스트가
            //   없어 FromCurrentSynchronizationContext()가 터진다. 여기선 BeginInvoke로 UI 스레드에 되돌린다.
            string dataDir = Path.Combine(Cfg.DataDir(), "web");
            CoreWebView2Environment.CreateAsync(null, dataDir, null).ContinueWith(
                delegate (System.Threading.Tasks.Task<CoreWebView2Environment> t) {
                    if (IsDisposed) return;
                    try {
                        BeginInvoke((MethodInvoker)delegate {
                            if (t.IsFaulted || t.Result == null) {
                                MessageBox.Show("WebView2 런타임을 찾지 못했어요.\n엣지가 설치돼 있는지 확인해 주세요.", "생각공작소 근태");
                                Application.Exit();
                                return;
                            }
                            Env = t.Result;
                            web.EnsureCoreWebView2Async(Env);
                            // 🔧진단 전용: `diag-open.txt`가 있으면 시작 직후 앱 창을 한 번 연다(주입 로그를 채우기 위함).
                            //   마커는 바로 지운다 = 평소 동작에 영향 0. 진단이 끝나면 이 블록도 지운다.
                            try {
                                string mk = Path.Combine(Cfg.DataDir(), "diag-open.txt");
                                if (File.Exists(mk)) {
                                    File.Delete(mk);
                                    var dt = new System.Windows.Forms.Timer();
                                    dt.Interval = 2500;
                                    dt.Tick += delegate { dt.Stop(); OpenApp(Cfg.APP_URL); };
                                    dt.Start();
                                }
                            } catch (Exception) { }
                        });
                    } catch (Exception) { }
                });
        };
    }

    void BuildTray() {
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("근태 열기", delegate { OpenApp(Cfg.APP_URL); }));
        menu.MenuItems.Add(new MenuItem("근태 닫기", delegate { if (app != null && !app.IsDisposed) app.FadeClose(); }));   // 다른 닫는 길(퇴근·알약)과 같은 페이드
        menu.MenuItems.Add(new MenuItem("관리자", delegate { OpenApp(Cfg.APP_ADMIN_URL); }));
        menu.MenuItems.Add(new MenuItem("-"));
        menu.MenuItems.Add(new MenuItem("제자리로", delegate { Pos.Clear(); Reposition(); }));   // 옮긴 자리를 잊고 트레이 옆으로
        menu.MenuItems.Add(new MenuItem("다시 읽기", delegate { if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(Cfg.Bust(Cfg.PILL_URL)); }));   // Reload는 같은 URL이라 HTTP 캐시를 탈 수 있다 — 새 buster로 확정 신선
        menu.MenuItems.Add(new MenuItem("-"));
        menu.MenuItems.Add(new MenuItem("로그아웃", delegate {
            Session.Clear(); PushAuth();
        }));
        menu.MenuItems.Add(new MenuItem("끝내기", delegate {
            tray.Visible = false; Application.Exit();
        }));
        tray = new NotifyIcon();
        tray.Icon = (trayOff != null ? trayOff : (trayOff = MakeIcon(false)));   // 시작=미출근(조회 전엔 회색이 정직)
        tray.Text = "생각공작소";   // D12 — 근태만이 아니라 문의도 받는 「생각공작소 프로그램」(내부 이름·폴더는 유지)
        tray.ContextMenu = menu;
        tray.Visible = true;
        Program.BeforeExit = delegate { try { tray.Visible = false; } catch (Exception) { } };
        // 알약을 못 찾거나 화면 밖으로 나갔을 때의 탈출 해치 — 트레이 더블클릭으로 앱을 연다
        tray.DoubleClick += delegate { OpenApp(Cfg.APP_URL); };
    }

    // 트레이·앱 창 아이콘 = **상태로 말한다**(유성 픽 08-01·정정 3건 반영):
    //   출근 중=A 초록 체크(단색 green600 — 그라데이션 뺌) / 미출근·퇴근 후·휴무=회색 C(열린 호 — "로딩하는 것처럼").
    //   원 지름=캔버스의 98%(생각공작소 로고 실측 98.0%에 맞춤 — pdfpng와 통일, 유성 08-01).
    //   ⚠️**GDI로 직접 그리지 않고 ico 파일을 읽는다** — GDI+는 투명 배경 위 안티앨리어싱에서 검은 프린지를
    //     만든다(유성 "얇은 테두리가 픽셀로 나와서 안티에일리징 안 먹은 느낌"). ico(브라우저 렌더 PNG)는 알파가 깨끗하다.
    //   지각(warn)은 따로 색을 주지 않는다 — 실시간 경고 0(확정 결정).
    static Icon trayOn, trayOff;
    static Icon MakeIcon(bool working) {
        try {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string f = Path.Combine(dir, working ? "근태위젯.ico" : "근태위젯_off.ico");
            if (File.Exists(f)) return new Icon(f, 32, 32);   // 멀티사이즈 ico에서 32px 엔트리
        } catch (Exception) { }
        // 폴백(ico가 없을 때만) — 단색 근사. 프린지가 남지만 아이콘이 아예 없는 것보단 낫다.
        //   미출근도 원은 초록(유성 08-01 재정정 "회색이 우리 어플 같지 않다") — 구분은 체크 vs 로딩 호.
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(ColorTranslator.FromHtml("#3a8a5f"))) g.FillEllipse(b, 0, 0, 31, 31);
            if (working) {
                using (var p = new Pen(Color.White, 5f)) {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(p, new Point[] { new Point(9, 17), new Point(14, 22), new Point(23, 11) });
                }
            } else {
                using (var p = new Pen(Color.White, 5.5f)) {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawArc(p, 8f, 8f, 15f, 15f, -90f, 252f);
                }
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
    // ❌**작업 표시줄 고정 아이콘을 상태에 따라 바꾸는 것 = 2026-08-06 실패로 확정, 재시도 금지.**
    //   시도한 것: 상태가 바뀔 때 고정 lnk의 `IconLocation`을 바꿔 쓰고 `SHChangeNotify`로 탐색기에 알림.
    //   결과: **파일은 정확히 바뀌었는데 화면이 안 따라왔다**(로그·lnk 실측 확인 — `IconLocation=근태위젯.ico,0`,
    //     수정 시각까지 도장 시각과 일치). `SHCNE_UPDATEITEM`도 `SHCNE_ASSOCCHANGED`(셸 전체)도 안 먹었다.
    //   원인: 윈도우가 **고정 항목의 아이콘을 자체 캐시로 들고 있고 lnk 변경으로는 안 버린다**(탐색기 재시작·재고정만 반영).
    //   ⇒ 이 자리는 **상태를 못 따라간다**가 결론이고, 그래서 exe 임베드 아이콘을 `_off`(로딩 호)로 둔다 —
    //     따라갈 수 없으면 **기본값이 정직해야** 한다(유성 "기본이 로딩 표시, 출근했을 때만 체크").
    //   상태의 본체는 알약(초록/회색+시간)과 트레이 아이콘(체크↔로딩 호)이 말한다 — 기능 손실은 없다.
    //   재개 조건: 알약을 작업표시줄에 띄우기로 **설계가 바뀌면**(23차 "알약은 창이 아니라 장식" 폐기) 그때는
    //     창 아이콘이 그 자리를 덮으므로 공짜로 된다. 그 전엔 다시 시도하지 말 것.
    void SyncTrayIcon() {
        if (trayOn == null) trayOn = MakeIcon(true);
        if (trayOff == null) trayOff = MakeIcon(false);
        var want = on ? trayOn : trayOff;
        if (tray != null && tray.Icon != want) tray.Icon = want;
        // 작업표시줄(앱 창 버튼)도 같은 상태를 따라간다 — 유성 08-01 "근무전인데 체크표시야":
        //   exe에 박힌 아이콘(정적 A)은 바로가기·설치 폴더 몫이고, 열려 있는 창의 아이콘은 여기서 실시간 교체.
        if (app != null && !app.IsDisposed) { try { app.Icon = want; } catch (Exception) { } }
    }

    void OnWebReady(object s, CoreWebView2InitializationCompletedEventArgs e) {
        if (!e.IsSuccess) { MessageBox.Show("위젯을 띄우지 못했어요.\n" + e.InitializationException, "생각공작소 근태"); return; }
        var c = web.CoreWebView2;
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.WebMessageReceived += OnMessage;
        c.NavigationCompleted += OnPillNav;
        c.ProcessFailed += delegate (object o, CoreWebView2ProcessFailedEventArgs pe) {
            Log("WebView2 프로세스 죽음: " + pe.ProcessFailedKind);
            BeginInvoke((MethodInvoker)delegate {
                if (pe.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) Program.Relaunch();   // 환경째 죽음 = 새로 켠다
                else { try { web.CoreWebView2.Navigate(Cfg.Bust(Cfg.PILL_URL)); } catch (Exception) { Program.Relaunch(); } }
            });
        };
        c.Navigate(Cfg.Bust(Cfg.PILL_URL));
    }

    // 알약 페이지를 못 받아 왔을 때 — **다시 건다**(2026-08-05 신설).
    //   부팅 직후엔 랜선·와이파이·DNS가 아직 안 붙어 있는 게 정상이다. 구판은 첫 Navigate 한 방이 전부라
    //   그때 실패하면 트레이 '다시 읽기'를 누를 때까지 알약이 그대로 굳었다(현아쌤은 그 메뉴를 모른다).
    //   상주 창이므로 포기하지 않는다 — 30초 간격까지 늘렸다가 계속 두드린다(네트워크가 돌아오면 스스로 산다).
    void OnPillNav(object s, CoreWebView2NavigationCompletedEventArgs e) {
        if (e.IsSuccess) {
            if (navFails > 0) Log("알약 로드 성공 (재시도 " + navFails + "회 만에)");
            navFails = 0;
            if (navRetry != null) navRetry.Stop();
            return;
        }
        navFails++;
        int sec = navFails <= 3 ? 3 : (navFails <= 6 ? 10 : 30);
        Log("알약 로드 실패 " + navFails + "회 — " + sec + "초 뒤 다시");
        if (navRetry == null) {
            navRetry = new System.Windows.Forms.Timer();
            navRetry.Tick += delegate {
                navRetry.Stop();
                if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(Cfg.Bust(Cfg.PILL_URL));
            };
        }
        navRetry.Interval = sec * 1000;
        navRetry.Start();
    }

    void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e) {
        string j;
        try { j = e.TryGetWebMessageAsString(); } catch (Exception) { return; }
        if (j == null) return;
        string type = J.Str(j, "type");
        // 알약 쪽 로그가 **하나도 없던 것**이 2026-08-06 진단의 공백이었다(앱 창만 남기고 있었다).
        //   `state`는 초당 여러 번 오므로 뺀다 — 나머지는 드물게 오고 전부 진단 가치가 있다.
        if (type != "state") Log("알약: " + (type == null ? "(type 없음)" : type) + J.Tail(j, "m"));
        if (type == "ready") { if (Upd.Unconfirmed()) { if (confirmT != null) confirmT.Stop(); Upd.Confirm(); } ready = true; PushAuth(); SendBoot(); appOpenSent = false; PushAppState(); return; }   // Send는 ready 전엔 조용히 버려진다 → 페이지가 뜬 뒤 현재 상태를 **강제로** 한 번 맞춰준다(새 페이지는 appOpen=false로 시작하므로, 트레이 '다시 읽기'·재시작 포함)
        if (type == "diag") return;   // 페이지가 남기고 싶은 한 줄(위에서 이미 기록했다)
        if (type == "open") { ToggleApp(Cfg.APP_URL); return; }
        if (type == "need-auth") { Session.Clear(); return; }
        if (type == "alert") {   // 도장이 실패했을 때 — 위젯은 이미 접혀 있으므로 트레이 풍선으로 알린다
            try { tray.ShowBalloonTip(4000, "생각공작소 근태", J.Str(j, "msg"), ToolTipIcon.Warning); } catch (Exception) { }
            return;
        }
        if (type == "drag") { BeginUserDrag(); return; }   // 3초 꾹 누름 → 마우스를 따라 옮긴다
        // 알약에서 퇴근을 찍어 세션이 끝났다 → 열려 있는 앱 창을 지금 닫는다.
        //   앱 창에 심은 후킹은 **그 문서의** localStorage만 본다. 알약은 다른 문서라 그쪽에선 안 보인다
        //   → 이 메시지가 없으면 1초 백스톱 폴링이 발견할 때까지 잠금 화면이 남는다.
        if (type == "locked") { if (app != null && !app.IsDisposed) app.FadeClose(); return; }
        if (type == "state") {
            int w = J.Int(j, "w", pillW);
            int h = J.Int(j, "h", Cfg.PILL_H);
            bool nOn = J.Bool(j, "on"), nWarn = J.Bool(j, "warn"), nDim = J.Bool(j, "dim");
            bool borderChanged = (nOn != on || nWarn != warn || nDim != dim);
            pillW = Math.Max(60, Math.Min(420, w));
            int newH = Math.Max(Cfg.PILL_H, Math.Min(420, h));
            on = nOn; warn = nWarn; dim = nDim;
            if (borderChanged) SyncTrayIcon();   // 트레이도 상태로 말한다(출근=A 초록 체크 / 아니면 회색 링)
            // 패널이 열리면 **아래를 고정한 채 위로 자란다** — 작업표시줄 위에 붙어 있으므로
            //   위로 자라야 알약이 제자리에 있는 것처럼 보인다.
            int bottom = Location.Y + Height;
            expanded = newH > Cfg.PILL_H;
            ClientSize = new Size(pillW, newH);
            Location = new Point(Location.X, bottom - Height);
            if (borderChanged) ApplyBorder();
            // 펼친 동안·끄는 동안엔 자동 배치가 끼어들지 않는다.
            //   ⚠️`dragging` 가드가 없던 구판은, 3초 꾹 눌러 옮기는 **도중에 분이 바뀌면**(글자 폭 변화 → state 전송)
            //     여기서 Reposition이 돌아 알약이 손을 뿌리치고 제자리로 튀었다(1초 틱에는 이미 있던 가드).
            if (!expanded && !dragging) Reposition();
        }
    }

    void Send(string json) {
        if (!ready || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsString(json); } catch (Exception) { }
    }
    void PushAuth() {
        // ⚠️토큰 **값**은 절대 안 남긴다(0-10) — 있고 없고와 사번만.
        Log("auth 보냄: 토큰 " + (Session.Token == "" ? "없음" : "있음") + " · emp=" + (Session.Emp == "" ? "(없음)" : Session.Emp) + " · ready=" + ready);
        Send("{\"type\":\"auth\",\"token\":\"" + J.Esc(Session.Token) + "\",\"emp\":\"" + J.Esc(Session.Emp) + "\"}");
    }
    // 시작 시각을 페이지에 한 번 넘긴다 — 페이지가 서버에 올려 **관리자 화면에서 보이게** 한다.
    //   호스트가 직접 GAS를 부르지 않는 이유: 기기 토큰은 WebView2 저장소에 있고(호스트는 모른다),
    //   네트워크가 늦으면 페이지가 붙을 때까지 들고 있다가 보내야 하는데 그 재시도는 페이지가 이미 잘한다.
    void SendBoot() {
        if (Boot.Sent) return;
        Boot.Sent = true;
        Send("{\"type\":\"boot\",\"up\":" + Boot.UpSec + ",\"at\":\"" + Boot.At.ToString("yyyy-MM-dd HH:mm:ss") + "\",\"v\":" + Cfg.SHELL_VER + "}");   // v = exe 판(관리자 화면에서 PC별 판 확인)
    }
    void Wake() { Send("{\"type\":\"wake\"}"); }
    public void WakePill(string kind) { Send("{\"type\":\"wake\",\"kind\":\"" + J.Esc(kind == null ? "" : kind) + "\"}"); }   // 앱 창이 "도장이 서버에 닿았다"를 알려올 때 — 알약을 지금 다시 읽힌다

    public void OnAppAuth(string token, string emp) {
        if (token == null) token = "";
        if (token == "") Session.Clear(); else Session.Save(token, emp);
        PushAuth();
    }

    // 알약을 다시 누르면 닫힌다 — 앱 창에 ×가 없으므로(유성 "네모 창만") **닫는 길이 보이게** 이걸 둔다.
    //   ESC·트레이 메뉴·헤더 −와 함께 4중. 하나라도 남아야 창에 갇히지 않는다.
    // ⭐26차-11(유성 08-13 "앱이 열린 상태면 '근태 앱 닫기'로 표기되어야 맞음"): 여기는 원래부터 토글이었고
    //   **글자만** 늘 '열기'였다. 라벨과 동작이 각자 판정하면 그 순간 두 진실이 되므로(26차-7·10 부류),
    //   판정식을 `AppOpen` 한 곳으로 모아 토글도 라벨도 **같은 식**을 보게 한다.
    public bool AppOpen { get { return app != null && !app.IsDisposed && !app.IsClosing && app.Visible; } }
    // ⭐26차-12(유성 08-13 실기기): **바뀔 때만 보내되, 1초 틱이 매번 불러 스스로 낫는다.**
    //   원인이 됐던 것=닫는 길마다 따로 알리는 구조였는데 `FormClosed`가 **창이 아직 Dispose 전**이라
    //   AppOpen을 '열림'으로 읽었다(작업표시줄 창닫기·Alt+F4처럼 FadeClose를 안 지나는 길에서만 드러남).
    //   ⇒ 24차-2 최대 수확의 재적용: **플래그를 읽어 분기하지 말고 매번 원하는 상태로 만든다.**
    //   우리가 못 떠올린 경로(크래시·예기치 못한 Dispose)까지 1초 안에 자동 교정된다.
    bool appOpenLast = false, appOpenSent = false;
    public void PushAppState() {
        if (!ready) return;                                     // 페이지가 아직 없다 — ready에서 강제로 다시 부른다
        bool now = AppOpen;
        if (appOpenSent && now == appOpenLast) return;           // 안 바뀌었으면 조용히(1초마다 불러도 값싸다)
        appOpenSent = true; appOpenLast = now;
        Send("{\"type\":\"appwin\",\"open\":" + (now ? "true" : "false") + "}");
    }
    void ToggleApp(string url) {
        if (AppOpen) { app.FadeClose(); return; }
        OpenApp(url);
    }
    public void OpenApp(string url) {
        if (Env == null) return;   // 아직 준비 전 — 다음 클릭에 열린다
        // ⚠️페이드아웃 중인 창은 "닫힌 것"으로 친다 — 그 창을 재사용하면 몇 프레임 뒤 스스로 Close()돼
        //   클릭이 먹히지 않은 것처럼 보인다. 새로 만들고 옛 창은 제 갈 길(Close)을 가게 둔다.
        bool fresh = (app == null || app.IsDisposed || app.IsClosing);
        if (fresh) {
            app = new AppWin(Env, this);
            // ⚠️26차-12: 여기서 `app`을 **비워야** 한다 — FormClosed 시점의 Form은 아직 Dispose 전이라
            //   AppOpen이 '열림'으로 읽힌다(작업표시줄 창닫기로 닫으면 버튼이 '닫기'인 채 굳었다).
            //   단 **자기 창일 때만** 비운다: FadeClose 도중에 새 창이 열리면 `app`은 이미 새 창을 가리키므로
            //   무턱대고 null을 넣으면 살아 있는 새 창의 참조를 죽인다.
            AppWin mine = app;
            mine.FormClosed += delegate { if (app == mine) app = null; Wake(); PushAppState(); };   // 창을 닫으면 즉시 다시 맞춘다(도장 직후가 여기 다 포함) + 버튼 글자도 '열기'로(26차-11)
            app.Show();   // 자리·크기는 AppWin.Load의 PlaceOnOpen()이 정한다(기억 복원 or 중앙)
        }
        // 이미 같은 화면이면 다시 읽지 않는다 — 트레이 '근태 열기'를 또 눌렀다고 보던 화면이 날아가면 안 된다.
        //   (구판은 매번 Navigate + FitToScreen이라 ①화면 상태 소실 ②옮겨둔 창이 중앙·기본크기로 리셋 — 유성 지시 ⑤)
        //   ⚠️단 **관리자는 같은 주소여도 다시 연다** — 관리자 진입은 화면 유지가 아니라 PIN 게이트 재실행이 목적이라
        //     이 가드가 "관리자 → ← → 관리자"에서 두 번째 클릭을 무시하게 만들었다(유성 08-01 실기기 적발 회귀).
        if (app.LastUrl != url || url == Cfg.APP_ADMIN_URL) app.Go(url);
        if (app.WindowState != FormWindowState.Normal) app.WindowState = FormWindowState.Normal;   // 최소화로 열리는 경우가 있었다(실측)
        if (!fresh && app.IsGone()) app.FitToScreen();   // 열려 있는데 화면 밖(모니터 변경 등)일 때만 재배치
        SyncTrayIcon();   // 새 창의 작업표시줄 아이콘을 현재 상태로 — exe 기본(A 체크)이 근무 전에 잠깐 보이지 않게
        app.Activate();
        app.BringToFront();
        PushAppState();   // 어느 경로로 열렸든(알약·트레이·작업표시줄 고정·두 번째 인스턴스) 알약 버튼이 '닫기'가 된다
    }

    void OnTick(object s, EventArgs e) {
        // 새 판이 준비돼 있고 지금 아무도 안 쓰면 바꿔 끼운다(패널 접힘·앱 창 닫힘·끄는 중 아님·손 1분 안 닿음)
        if (Upd.Ready != null && !expanded && !dragging && !AppOpen && IdleMs() > 60000) { Upd.Apply(); return; }
        // 자정을 넘겼으면 세션을 버리고 페이지에도 알린다(하루 단위 리셋 — 유성 확정)
        if (Session.RolledOver()) { Session.Clear(); PushAuth(); }
        // ⭐26차-12 백스톱: 앱 창 열림/닫힘을 **매 초 실제 상태로** 다시 맞춘다(바뀌었을 때만 보낸다).
        //   닫는 길이 6개+α라 하나하나 알리는 구조는 언젠가 새는데, 이건 우리가 못 떠올린 경로까지 1초 안에 낫는다.
        //   `TopMost`를 매 초 실제 Z순서로 되돌리는 바로 아래 코드와 같은 사상이다(24차-2).
        PushAppState();
        // 전체화면 게임·발표·집중모드면 감춘다(문서화된 API — 토스트와 z순서 싸움을 하지 않는다)
        int st;
        bool hide = (Native.SHQueryUserNotificationState(out st) == 0)
            && (st == Native.QUNS_RUNNING_D3D_FULL_SCREEN || st == Native.QUNS_PRESENTATION_MODE);
        if (hide == Visible) { Visible = !hide; }
        if (!Visible) return;
        // ⭐항상-위를 **실제 Z순서로** 매 초 되돌린다 — 자리 계산보다 **먼저**, 그리고 펼친 중·끄는 중에도.
        //   속성(`TopMost`)만 보고 판단하면 영영 못 고친다(위 Native 주석 = 유성 "알약이 갑자기 없어졌는데").
        //   이미 맨 위면 윈도우가 사실상 무시하므로 값싸고, 위치·크기·포커스는 안 건드린다.
        if (!TopMost) TopMost = true;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (dragging || expanded) return;   // 끄는 중·펼친 중엔 **자리**만 안 건드린다(위 항상-위는 계속 유지)
        // 좌표를 매 초 다시 계산한다 → DPI 변경·해상도 변경·작업표시줄 이동·모니터 착탈을
        //   한 줄도 처리하지 않고 자동 회복한다(부팅 직후 트레이가 아직 안 잡히는 경우 포함).
        Reposition();
    }

    static uint IdleMs() {
        var li = new Native.LASTINPUTINFO(); li.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
        if (!Native.GetLastInputInfo(ref li)) return 0;
        return unchecked((uint)Environment.TickCount - li.dwTime);
    }
    void ApplyCorner() {
        int pref = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
    }
    void ApplyBorder() {
        // COLORREF = 0x00BBGGRR. 근무 중 green600 / 지각 warn / 그 외 g300. 휴무일은 더 흐리게.
        int rgb = on ? 0x3A8A5F : (warn ? 0xF5A623 : 0xD1D6DB);
        if (dim) rgb = 0xE5E8EB;
        string sig = rgb.ToString() + (dim ? "d" : "");
        if (sig == lastBorder) return;
        lastBorder = sig;
        int bgr = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref bgr, 4);
    }

    void Reposition() {
        // 유성이 직접 옮긴 자리가 있으면 그대로 존중한다(3초 꾹 눌러 이동 → 놓으면 고정).
        //   단 화면 밖으로 나갔으면(모니터를 뺐다든지) 조용히 틀리지 말고 자동 배치로 되돌린다.
        if (Pos.Has) {
            // ⚠️07-30 유성 신고 "옮긴 뒤 알트탭하면 이전 자리로 돌아간다": 구판은 창이 조금이라도
            //   작업 영역을 벗어나면(작업표시줄 위에 살짝 걸치기만 해도) 저장을 **버리고** 자동 배치로 돌아갔다.
            //   → **화면 밖으로 완전히 나갔을 때만** 버린다. 살짝 걸친 건 그대로 존중한다.
            Rectangle b0 = Screen.PrimaryScreen.Bounds;
            bool gone = (Pos.X + Width < b0.Left + 24) || (Pos.X > b0.Right - 24)
                     || (Pos.Y + Height < b0.Top + 24) || (Pos.Y > b0.Bottom - 24);
            if (!gone) {
                if (Location.X != Pos.X || Location.Y != Pos.Y) Location = new Point(Pos.X, Pos.Y);
                return;
            }
            Pos.Clear();
        }
        var abd = new Native.APPBARDATA();
        abd.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
        Screen scr = Screen.PrimaryScreen;
        bool okBar = Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero;
        // ⚠️알트탭·F11(전체화면)에서 트레이 폭·작업표시줄 상태가 잠깐 달라져 **알약이 왔다갔다했다**(유성 신고 07-30).
        //   1초마다 계산해서 옮기는 게 원인 → **계산 결과가 실제로 달라졌을 때만** 움직인다.
        //   읽기에 실패하면 아무것도 하지 않는다(폴백으로 튀느니 그 자리에 있는 게 낫다).
        if (!okBar) return;

        // 트레이 아이콘 영역 왼쪽에 붙인다. **미문서 API라 실패를 전제**하고, 못 읽으면 그냥 가만히 둔다.
        int trayLeft = -1;
        Native.RECT tr;
        IntPtr tw = Native.FindWindowEx(Native.FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null);
        if (tw != IntPtr.Zero && Native.GetWindowRect(tw, out tr) && tr.right > tr.left) trayLeft = tr.left;
        if (trayLeft <= 0 && placed) return;   // 한 번 자리를 잡은 뒤엔 못 읽었다고 튀지 않는다

        int x, y;
        if (okBar && abd.uEdge == Native.ABE_BOTTOM) {
            y = abd.rc.top - Height - Cfg.GAP;
            x = (trayLeft > 0 ? trayLeft : abd.rc.right - 260) - Width - Cfg.GAP;
        } else if (okBar && abd.uEdge == Native.ABE_TOP) {
            y = abd.rc.bottom + Cfg.GAP;
            x = (trayLeft > 0 ? trayLeft : abd.rc.right - 260) - Width - Cfg.GAP;
        } else {                                   // 작업표시줄이 좌·우에 있거나 못 읽었을 때
            y = scr.WorkingArea.Bottom - Height - Cfg.GAP;
            x = scr.WorkingArea.Right - Width - Cfg.GAP;
        }
        // 화면 밖으로 나가면 조용히 틀리지 말고 우하단으로 되돌린다
        if (x < scr.Bounds.Left || x + Width > scr.Bounds.Right) x = scr.WorkingArea.Right - Width - Cfg.GAP;
        if (y < scr.Bounds.Top || y + Height > scr.Bounds.Bottom) y = scr.WorkingArea.Bottom - Height - Cfg.GAP;
        // **계산 결과가 실제로 달라졌을 때만** 옮긴다. 매 초 같은 값을 다시 세팅하면 알트탭·F11 때
        //   순간적으로 다른 값이 나올 때마다 알약이 왔다갔다한다(유성 신고).
        if (placed && x == lastX && y == lastY) return;
        lastX = x; lastY = y; placed = true;
        if (Location.X != x || Location.Y != y) Location = new Point(x, y);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        if (tray != null) tray.Visible = false;
        base.OnFormClosed(e);
    }
}

// ══════════════════════════════════════════════════════════════
class AppWin : Form {
    WebView2 web;
    Pill pill;
    string pending;

    // 표시줄은 없애되 **크기 조절과 그림자는 살린다**(유성 07-30).
    //   `WS_THICKFRAME`을 style에 넣으면 제목 표시줄 없이도 ①가장자리를 잡아 크기 조절 ②DWM 그림자를 얻는다.
    //   유성 "그림자 되면 제일 베스트" — 초록 테두리 대신 이걸 쓴다.
    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x00C00000;   // WS_CAPTION — 그림자·스냅을 얻기 위해 '있는 척'만 한다(아래에서 안 그림)
            cp.Style |= 0x00040000;   // WS_THICKFRAME — 크기 조절 + 그림자
            cp.Style |= 0x00020000;   // WS_MINIMIZEBOX — 작업표시줄에서 최소화·복원
            return cp;
        }
    }
    // ③(08-01 유성 GO — 스파이크 실측 확정): 6px 색칠 띠를 **원천 제거**하고, 비클라이언트를
    //   **좌·우·아래만 프레임 두께로 남긴다**(Windows Terminal 방식). 그 밴드는 대부분(7/8px) 화면에
    //   안 보이는 영역이라 시각적으론 투명이고, DefWindowProc가 **7방향 리사이즈+네이티브 커서**를
    //   코드 0줄로 처리한다 — 페이지·주입이 죽어도 크기 조절은 산다. 위쪽은 0(캡션·흰 선 없음) —
    //   창 이동은 앱 페이지 상단 12px 그립이 `edge` 메시지로 맡는다(주입 아닌 페이지 내장 = 실패 지점 축소).
    //   그림자는 WS_CAPTION|WS_THICKFRAME이 남아 있어 DWM이 계속 그려 준다.
    const int WM_NCCALCSIZE = 0x0083;
    [StructLayout(LayoutKind.Sequential)]
    struct NCCALCSIZE_PARAMS { public Native.RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }
    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) {
            var p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
            int pad = Native.GetSystemMetrics(Native.SM_CXPADDEDBORDER);
            int bx = Native.GetSystemMetrics(Native.SM_CXFRAME) + pad;
            int by = Native.GetSystemMetrics(Native.SM_CYFRAME) + pad;
            p.rgrc0.left += bx; p.rgrc0.right -= bx; p.rgrc0.bottom -= by;   // 위는 안 건드림
            Marshal.StructureToPtr(p, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        // WM_NCHITTEST는 오버라이드하지 않는다 — 인셋으로 남긴 비클라이언트 밴드는 DefWindowProc가 판정한다.
    }

    public AppWin(CoreWebView2Environment env, Pill p) {
        pill = p;
        Text = "생각공작소 근태";
        // 작업표시줄 버튼은 **남긴다** — 이게 없으면 Alt+Tab·최소화가 죽는다. 없애기로 한 건
        //   '고정된 런처 아이콘'(진입점 중복)이지 '열려 있는 창'이 아니다.
        ShowInTaskbar = true;
        // 유성 확정(07-30): **─ ▢ × 표시줄 자체를 없앤다. 그냥 네모 창만.**
        //   닫는 길 — ①앱 헤더 −(08-01 유성 지시 ② — 페이지가 close 메시지) ②알약 다시 누르기 ③트레이 메뉴.
        //   ESC도 걸어 두지만 WebView2가 포커스를 쥐면 폼 KeyDown이 안 와 사실상 예비용이다(정직).
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;   // CenterScreen은 1080 화면에서 창을 아래로 흘려보냈다(실측: 아래 470px 잘림)
        ClientSize = new Size(Cfg.APP_W, Cfg.APP_H);
        // ⚠️화면보다 큰 창은 아래쪽 버튼을 못 누르게 만든다. 작업표시줄을 뺀 실제 영역에 맞춰 줄이고 가운데 둔다.
        Load += delegate {
            PlaceOnOpen();   // 기억해 둔 자리·크기가 있으면 복원, 없거나 화면 밖이면 FitToScreen(유성 지시 ④). DWM(둥근 모서리·테두리 없음)도 안에서.
        };
        KeyPreview = true;
        KeyDown += delegate (object o, KeyEventArgs k) { if (k.KeyCode == Keys.Escape) FadeClose(); };   // 다른 닫는 길과 같은 페이드
        // 사용자가 창을 옮기거나 크기를 바꾼 결과만 저장한다 — ResizeEnd는 시스템 이동/크기 루프가 끝날 때만
        //   오고 프로그램이 옮긴 것(FitToScreen)엔 안 온다. 닫을 때 한 번 더 저장(마지막 상태 보존).
        ResizeEnd += delegate { SaveBounds(); };
        BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        // ③(08-01): 구판의 6px 색칠 띠(Padding)는 제거 — WebView2가 클라이언트를 꽉 채운다.
        //   크기 조절은 인셋으로 남긴 비클라이언트 밴드(위 WndProc)가, 이동은 페이지 상단 그립이 맡는다.
        //   BackColor·DefaultBackgroundColor는 `bg` 메시지로 페이지색을 따라간다(보이는 1px 경계·리사이즈 랙 흰 번쩍임 커버).
        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);
        web.CoreWebView2InitializationCompleted += OnReady;
        web.EnsureCoreWebView2Async(env);

        // ⚡**폴링을 걷어냈다(2026-07-31 — 유성 "앱 창 쓰는 동안 버벅임").**
        //   구판은 이 창에 `ExecuteScriptAsync`를 **잠금 150ms + 배경색 500ms = 초당 약 9회** 밀어넣었고,
        //   둘 다 겹침 방지가 없어 렌더러가 바쁘면 쌓였다. 그 렌더러가 곧 **앱의 애니메이션을 그리는 스레드**다.
        //   → 배경색은 페이지가 스스로 보고하고(EDGE_JS), 잠금은 이미 있던 이벤트 경로로 받는다.
        //     남은 폴링은 **1초 백스톱** 하나뿐이고 그마저 겹침 가드를 단다.
        lockPoll = new System.Windows.Forms.Timer();
        // ⚠️**전이**만 본다: 잠겨 있지 않던 창이 잠기는 순간(=퇴근 직후)에만 닫는다.
        //   "잠겨 있으면 닫는다"로 만들었더니 **이미 잠긴 앱을 열면 열리자마자 꺼졌다**(유성 신고 07-30).
        //   구판이 150ms까지 당긴 이유는 "PIN 화면이 번쩍인다"였는데, 진짜 원인은 주기가 아니라
        //   **즉시 경로(`close` 메시지)가 그냥 `Close()`로 연결돼 있던 것**이었다 → 그걸 FadeClose로 고쳐 폴링을 늦춘다.
        lockPoll.Interval = 1000;
        lockPoll.Tick += delegate {
            if (web == null || web.CoreWebView2 == null || closing || lockBusy) return;
            lockBusy = true;   // 앞선 조회가 안 끝났으면 새로 보내지 않는다(구판엔 이 가드가 없었다)
            try {
                web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('att_locked')")
                  .ContinueWith(delegate (System.Threading.Tasks.Task<string> t) {
                      if (IsDisposed) return;
                      string v = t.IsFaulted ? null : t.Result;
                      bool locked = (v != null && v.IndexOf('1') >= 0);
                      try {
                          BeginInvoke((MethodInvoker)delegate {
                              lockBusy = false;
                              if (closing || t.IsFaulted) return;
                              if (!locked) { sawUnlocked = true; }           // 열려서 쓰이는 중
                              else if (sawUnlocked) { FadeClose(); }         // 쓰던 창이 잠겼다 = 퇴근했다
                          });
                      } catch (Exception) { lockBusy = false; }
                  });
            } catch (Exception) { lockBusy = false; }
        };
        Shown += delegate { lockPoll.Start(); };
    }
    System.Windows.Forms.Timer lockPoll, fade;
    bool sawUnlocked = false, closing = false, lockBusy = false;
    public bool IsClosing { get { return closing; } }   // 페이드아웃 중 — OpenApp이 "닫힌 것"으로 치게 (Form.Closing 이벤트와 이름 충돌 회피)
    public string LastUrl = null;                     // 마지막으로 연 주소 — 같은 화면 재탐색(상태 소실) 방지

    // ── 유성 지시 ④: 창 위치·크기 기억 ──
    void SaveBounds() {
        if (closing || WindowState != FormWindowState.Normal) return;   // 최대화·페이드 중 값은 기억할 자리가 아니다
        AppPos.Save(Location.X, Location.Y, Width, Height);
    }
    // 화면 밖 판정 — 어느 화면에도 "잡을 수 있는 만큼"(120×80) 걸쳐 있지 않으면 gone
    static bool GoneRect(Rectangle r) {
        foreach (Screen s in Screen.AllScreens) {
            Rectangle ix = Rectangle.Intersect(s.WorkingArea, r);
            if (ix.Width >= 120 && ix.Height >= 80) return false;
        }
        return true;
    }
    public bool IsGone() { return GoneRect(Bounds); }
    public void PlaceOnOpen() {
        if (AppPos.Has) {
            Rectangle r = new Rectangle(AppPos.X, AppPos.Y, AppPos.W, AppPos.H);
            if (!GoneRect(r)) {
                // 폭 하한·DPI 보정은 FitToScreen과 같은 규칙으로 지키되, 사용자가 만든 크기는 존중한다.
                float scale = 1f;
                try { using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f; } catch (Exception) { }
                int wantW = (int)Math.Round(Cfg.APP_W * scale);
                MinimumSize = new Size(wantW + (Width - ClientSize.Width), 380);
                MaximumSize = Size.Empty;
                Bounds = r;
                // 화면이 줄었는데(해상도 변경) gone까지는 아닌 엣지 — 세로만 작업영역에 맞춘다(위치·폭은 존중)
                Rectangle wa = Screen.FromRectangle(r).WorkingArea;
                if (Height > wa.Height) Height = wa.Height - 8;
                if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
                ApplyDwm();
                return;
            }
        }
        FitToScreen();
        ApplyDwm();
    }
    void ApplyDwm() {
        // 테두리를 없앴으니 창이 배경에 붙어 보인다 → 윈도우가 그리는 둥근 모서리 + 그림자로 경계를 준다.
        int pref = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        // 유성 "초록 테두리는 별로. 투명으로. 그림자 되면 제일 베스트" → 테두리 없음(DWMWA_COLOR_NONE)
        int none = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref none, 4);
    }
    // 유성 "퇴근 인터랙션 → 갑자기 키패드 → 갑자기 꺼짐"이 불편하다 →
    //   감지 즉시 화면을 숨겨 PIN 화면이 보이지 않게 하고, 창을 부드럽게 사라지게 한다.
    public void FadeClose() {   // 알약이 퇴근을 찍었을 때 Pill이 직접 부른다(그 문서의 잠금은 이 창이 못 본다)
        if (closing) return;
        closing = true;
        // 페이드가 시작된 순간부터 `AppOpen`은 false다(IsClosing) → 버튼 글자를 지금 '열기'로 되돌린다.
        //   안 하면 페이드 도는 ~0.4초 동안 '닫기'가 남아, 그걸 또 누르면 창이 다시 열린다(26차-11).
        if (pill != null) pill.PushAppState();
        if (lockPoll != null) lockPoll.Stop();
        try { web.Visible = false; } catch (Exception) { }   // 잠금 화면이 번쩍이지 않게 즉시 감춘다
        fade = new System.Windows.Forms.Timer();
        fade.Interval = 16;
        fade.Tick += delegate {
            Opacity -= 0.12;
            if (Opacity <= 0.02) { fade.Stop(); Close(); }
        };
        fade.Start();
    }
    // "rgb(242, 244, 246)" 같은 문자열(따옴표 포함)을 색으로. 실패하면 Empty를 돌려 손대지 않는다.
    static Color ParseRgb(string s) {
        try {
            int a = s.IndexOf('('), b = s.IndexOf(')');
            if (a < 0 || b < a) return Color.Empty;
            string[] p = s.Substring(a + 1, b - a - 1).Split(',');
            if (p.Length < 3) return Color.Empty;
            return Color.FromArgb(int.Parse(p[0].Trim()), int.Parse(p[1].Trim()), int.Parse(p[2].Trim()));
        } catch (Exception) { return Color.Empty; }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { if (lockPoll != null) lockPoll.Stop(); base.OnFormClosed(e); }

    void OnReady(object s, CoreWebView2InitializationCompletedEventArgs e) {
        if (!e.IsSuccess) { MessageBox.Show("앱을 띄우지 못했어요.\n" + e.InitializationException, "생각공작소 근태"); return; }
        var c = web.CoreWebView2;
        c.Settings.AreDefaultContextMenusEnabled = false;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.WebMessageReceived += OnMessage;
        // ⚠️`AddScriptToExecuteOnDocumentCreatedAsync`는 **비동기 등록**이라 바로 뒤의 Navigate와 경합한다 →
        //   첫 문서에 안 실려 "가장자리에 올려도 커서가 안 바뀐다"가 됐다(유성 07-30 재신고).
        //   그래서 이동 완료 때마다 한 번 더 넣는다. 스크립트는 `__tfEdge` 가드로 두 번 걸려도 무해.
        try { c.AddScriptToExecuteOnDocumentCreatedAsync(EDGE_JS); EdgeLog("doc-created 등록"); }
        catch (Exception ex) { EdgeLog("doc-created 등록 실패: " + ex.Message); }
        c.NavigationCompleted += delegate {
            // ⚠️2026-07-31: 주입이 **한 번도 안 됐다**는 게 드러났다(`edge.log`가 아예 없었다 = edgeready 미수신).
            //   스크립트 자체는 헤드리스 Edge로 실제 페이지에 넣어 검증했고 예외 0·edgeready 발신까지 확인했다.
            //   그래서 이제 **부르는 쪽을 계측한다** — 실행 결과와 "정말 실렸나(`__tfEdge`)"를 따로 남긴다.
            //   결과를 안 보고 성공을 가정하지 않는다(이 파일에서 세 번 반복된 실수).
            try {
                c.ExecuteScriptAsync(EDGE_JS).ContinueWith(delegate (System.Threading.Tasks.Task<string> t) {
                    if (IsDisposed) return;
                    EdgeLog(t.IsFaulted ? ("주입 예외: " + t.Exception.GetBaseException().Message) : "주입 실행됨");
                    try {
                        BeginInvoke((MethodInvoker)delegate {
                            if (web == null || web.CoreWebView2 == null) return;
                            // 실렸는지 **직접 물어본다**. postMessage 가 막혀도 이건 답이 온다 → 두 실패를 구분한다.
                            // 후킹이 **정말** 걸렸는지 각각 확인한다(플래그만 보고 성공을 가정하지 않는다 — 그게 이 버그였다).
                            web.CoreWebView2.ExecuteScriptAsync("(function(){return 'tf='+String(!!window.__tfEdge)+' fetch='+String(String(window.fetch).indexOf('bump')>=0)+' setItem='+String(String(localStorage.setItem).indexOf('att_locked')>=0);})()")
                              .ContinueWith(delegate (System.Threading.Tasks.Task<string> t2) {
                                  EdgeLog(t2.IsFaulted ? ("확인 예외: " + t2.Exception.GetBaseException().Message)
                                                       : ("설치 확인 " + t2.Result));
                              });
                        });
                    } catch (Exception) { }
                });
            } catch (Exception ex) { EdgeLog("주입 호출 실패: " + ex.Message); }
        };
        // ⚠️ESC로 닫기는 넣지 않았다. `AcceleratorKeyPressed`는 `CoreWebView2Controller`에만 있고
        //   WinForms 래퍼가 그걸 노출하지 않는다(실측: 이벤트 목록에 없음). 억지로 뚫으면 **앱이 이미 쓰는
        //   ESC 동작(시트·모달 닫기)과 싸운다.** 닫는 길은 ①알약 다시 누르기 ②트레이 '근태 닫기' 둘로 간다.
        // 앱 안에서 외부 링크를 열면 기본 브라우저로 — 앱 창이 딴 페이지로 끌려가지 않게
        c.NewWindowRequested += delegate (object o, CoreWebView2NewWindowRequestedEventArgs a) {
            a.Handled = true;
            // ⚠️http/https만 연다. 구판은 받은 주소를 그대로 실행해 file:·임의 프로토콜로 **PC 프로그램을 띄울 수 있었다**
            //   (= 깃허브 계정이 사무실 PC 열쇠 — 2026-10-09 엄격 검토). 업데이트 서명은 이 구멍이 막혀야 의미가 있다.
            Uri u;
            if (Uri.TryCreate(a.Uri, UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)) {
                try { Process.Start(u.AbsoluteUri); } catch (Exception) { }
            } else EdgeLog("외부 열기 거절(웹 주소 아님): " + (u != null ? u.Scheme : "형식 오류"));
        };
        if (pending == null) LastUrl = Cfg.APP_URL;
        c.Navigate(Cfg.Bust(pending != null ? pending : Cfg.APP_URL));
        pending = null;
    }

    // 진단 로그 — 덮어쓰지 않고 **쌓는다**(한 줄짜리 덮어쓰기는 "언제 무엇이" 를 못 남긴다).
    //   토큰·PIN 같은 값은 절대 넣지 않는다(0-10). 타입 이름과 결과만.
    internal static void EdgeLog(string msg) {
        try {
            string f = Path.Combine(Cfg.DataDir(), "edge.log");
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            File.AppendAllText(f, line, Encoding.UTF8);
        } catch (Exception) { }
    }

    void OnMessage(object s, CoreWebView2WebMessageReceivedEventArgs e) {
        string j;
        try { j = e.TryGetWebMessageAsString(); } catch (Exception) { return; }
        if (j == null) return;
        string type = J.Str(j, "type");
        EdgeLog("메시지 수신: " + (type == null ? "(type 없음)" : type));
        if (type == "auth") { pill.OnAppAuth(J.Str(j, "token"), J.Str(j, "emp")); return; }
        if (type == "edgeready") { EdgeLog("edgeready 수신"); return; }   // 주입 성공 증거
        // 퇴근 뒤 앱이 잠기는 그 순간(주입한 setItem 후킹) — **여기가 즉시 경로**다.
        //   ⚠️구판은 맨 `Close()`라 잠금(PIN) 화면이 한 프레임 번쩍이고 창이 뚝 끊겼다. 그걸 메우려고
        //     잠금 폴링을 150ms까지 당겼던 것이다(=렉의 절반). FadeClose가 화면을 먼저 감추므로 폴링이 불필요해진다.
        if (type == "close") { BeginInvoke((MethodInvoker)delegate { FadeClose(); }); return; }
        // 앱에서 도장·신청이 **서버까지 다녀온 뒤** 오는 신호 → 알약을 지금 갱신시킨다.
        //   `storage` 이벤트에만 기대지 않는 이유 = 컨트롤 두 개 사이 전파를 우리가 확인한 적 없다(호스트 경유는 검증된 길).
        if (type == "punched") { pill.WakePill(J.Str(j, "kind")); return; }
        // 페이지가 자기 배경색을 **바뀔 때만** 알려준다(구판은 호스트가 500ms마다 캐물었다).
        //   가장자리 6px 띠를 그 색으로 칠해 "테두리는 그림자만"(유성 확정)을 지킨다.
        if (type == "bg") {
            Color c = ParseRgb(J.Str(j, "color"));
            if (c != Color.Empty && BackColor != c) {   // WebMessageReceived는 UI 스레드다
                BackColor = c;
                try { web.DefaultBackgroundColor = c; } catch (Exception) { }   // 리사이즈 랙에 흰색이 비치지 않게(③)
            }
            return;
        }
        // ③(08-01): 창 **이동**만 페이지가 알린다(상단 12px 그립 — 앱 페이지 내장, attend/index.html).
        //   좌·우·아래 크기 조절은 비클라이언트 밴드를 DefWindowProc가 직접 처리하므로 메시지가 안 온다.
        if (type == "edge") {
            if (J.Str(j, "dir") == "t") { Native.ReleaseCapture(); Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, 0); }
            return;
        }
    }

    // 앱 페이지(index.html)를 건드리지 않고 가장자리 조작만 얹는다 — 레포의 앱 코드는 그대로.
    const string EDGE_JS =
      // ⚠️**성공 플래그는 맨 끝에서 세운다**(2026-07-31 실측으로 잡은 결함).
      //   구판은 첫 줄에서 `__tfEdge=1`을 세웠는데, 이 스크립트는 **문서 생성 시점**에도 한 번 돈다.
      //   그때는 `document.documentElement`가 **아직 null**이라 아래 `appendChild`에서 터지고 —
      //   **플래그만 남은 채** 나머지(가장자리 커서·setItem 후킹·fetch 후킹·배경색 보고)가 전부 안 실렸다.
      //   그리고 이동 완료 때의 **재주입은 그 플래그를 보고 매번 그냥 돌아갔다** = 영영 복구 불가.
      //   실측 증거: `tf=true`인데 `fetch후킹=false setItem후킹=false`.
      //   (07-30에 "가장자리 커서가 안 바뀐다"가 세 번 재신고된 것도 같은 뿌리 — 그때 넣은 재주입을 이 가드가 무력화했다.)
      //   → ①DOM이 없으면 **아무 흔적도 남기지 않고** 물러난다(다음 주입이 다시 시도할 수 있게)
      //     ②전부 성공한 **뒤에만** 플래그를 세운다.
      // ③(08-01): 가장자리 그립(커서·8방향 판정·edge 메시지)은 이 주입에서 **뺐다** —
      //   좌·우·아래 리사이즈=비클라이언트 밴드(DefWindowProc), 위 이동=앱 페이지 내장 그립(attend/index.html).
      //   주입에 남는 것은 도장 감지(fetch)·잠금 감춤(setItem)·배경색 보고(bg)뿐 = 실패해도 창 조작은 산다.
      "(function(){if(window.__tfEdge)return;" +
      "if(!document.documentElement||!document.body)return;" +
      "var P=function(o){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify(o));};" +
      // 유성 "근태 앱에서 퇴근까지 누르면 앱이 꺼지게 해줘" — 앱은 퇴근 도장 뒤 스스로 잠근다(att_locked).
      //   그 순간을 가로채 창을 닫는다. 앱 레포 코드는 건드리지 않는다.
      // 🔑**여기가 PIN 화면이 안 보이게 하는 자리다**(2026-07-31 유성 "pin 입력 창이 안나왔으면 좋겠어").
      //   이 후킹은 앱이 `att_locked`를 쓰는 **그 순간 동기적으로** 실행된다 = 앱이 잠금 화면을 그리기 **전**이다.
      //   그래서 여기서 페이지를 바로 흐리게 시작하면, 뒤이어 그려지는 PIN 화면은 이미 투명해져 눈에 안 띈다.
      //   ⚠️호스트에 맡기면 늦는다 — postMessage→호스트→창 숨김은 왕복이라 그 사이에 렌더러가 PIN을 한 프레임 그린다
      //     (구판이 폴링을 150ms까지 당겼던 이유가 이거였고, 그래도 번쩍였다).
      //   유성 말대로 순서가 "부드럽게 사라지고 → 그 다음 잠금"이 된다. 잠금 자체는 그대로 걸린다(공용 PC라 생략 불가).
      "var _si=localStorage.setItem.bind(localStorage);" +
      "localStorage.setItem=function(k,v){_si(k,v);if(k==='att_locked'&&v){" +
      //   ⚠️여기서 **서서히** 흐리게 하면 안 된다 — 페이드는 불투명도 1에서 시작하므로 바로 다음 프레임(≈16ms)에
      //     PIN 화면이 90% 불투명도로 그려진다. 부드러움은 **창 페이드**가 이미 맡고 있고(유성 "부드럽게 사라지긴 하는데"),
      //     페이지는 그 전에 **즉시** 사라져 있어야 한다.
      "try{var r=document.documentElement;r.style.visibility='hidden';}catch(_e){}P({type:'close'});}};" +
      // ⚡앱에서 찍은 도장을 알약이 **즉시** 안다(2026-07-31 유성 신고 "앱은 로그인 상태인데 알약은 근무전").
      //   🔴**07-31 1차의 실패와 그 원인**: 요청을 *보내기 전에* 알렸다 → 알약이 **출근이 서버에 닿기 전에** 물어봤고,
      //     서버는 당연히 "열린 출근 없음"을 돌려줘 회색 그대로였다(그리고 다음 조회까지 60초). 낙관적 알림의 전형적 실패다.
      //     → **응답이 온 뒤에** 알린다. 그때는 서버에 이미 기록돼 있으므로 조회가 반드시 새 상태를 본다.
      //   🔴그리고 `storage` 이벤트 하나에만 기대지 않는다 — WebView2 컨트롤 두 개 사이의 전파는 우리가 **확인한 적 없는 사실**이다.
      //     호스트 경유(`punched` → Wake)는 이 앱의 다른 모든 것이 이미 쓰는 **검증된 길**이라 그쪽을 본선으로 둔다.
      //   ⚡`kind`도 같이 넘긴다(2026-07-31 유성 "출근 눌렀을 때 알약이 초록 되는 게 살짝 느리다").
      //     알약이 이걸 받으면 **서버에 다시 묻지 않고** 바로 초록으로 바꾼다 → 왕복 한 번(1.2~1.5초)이 사라진다.
      "var ACT=['punch','amend','extwork','request','cancel'];var _f=window.fetch;" +
      "window.fetch=function(){var hit=false,kind='';try{var o=arguments[1],b=o&&o.body;if(typeof b==='string'){" +
      "for(var i=0;i<ACT.length;i++){if(b.indexOf(ACT[i])>=0){hit=true;break;}}" +
      "if(hit){if(b.indexOf('\\\"kind\\\":\\\"in\\\"')>=0)kind='in';else if(b.indexOf('\\\"kind\\\":\\\"out\\\"')>=0)kind='out';}}" +
      "}catch(_e){}" +
      //   ⚠️`this`가 아니라 **window로 못 박아** 부른다 — 네이티브 fetch는 잘못된 수신자로 부르면 즉시 예외라
      //     여기서 헛디디면 앱의 **모든** 요청이 죽는다. 곁다리 기능 때문에 본체를 걸지 않는다.
      "var p=_f.apply(window,arguments);" +
      //   ⚠️알림 때문에 앱의 흐름을 바꾸지 않는다 — 원래 프라미스를 **그대로** 돌려주고(체인 교체 금지),
      //     성공·실패 양쪽에 곁가지로만 붙인다. 여기서 예외가 나도 앱은 영향받지 않는다.
      "if(hit&&p&&p.then){p.then(function(){bump(kind);},function(){bump(kind);});}" +
      "return p;};" +
      "function bump(k){try{localStorage.setItem('att_bump',String(Date.now()));}catch(_e){}P({type:'punched',kind:k||''});}" +
      // 가장자리 6px 띠 색 — **페이지가 스스로 보고한다**(구판은 호스트가 500ms마다 캐물어 렌더러를 방해했다).
      //   바뀔 때만 보내므로 실제 전송은 화면 전환 때 몇 번뿐이다.
      "function bg1(e){if(!e)return '';var v=getComputedStyle(e).backgroundColor;if(!v||v==='transparent')return '';" +
      "var p=v.split(',');if(p.length>3&&parseFloat(p[3])===0)return '';return v;}var lastBg='';" +
      "function bgTick(){try{var v=bg1(document.body)||bg1(document.documentElement)||bg1(document.getElementById('app'))||'';" +
      "if(v&&v!==lastBg){lastBg=v;P({type:'bg',color:v});}}catch(_e){}}setInterval(bgTick,1000);bgTick();" +
      // 여기까지 왔으면 전부 설치됐다 — **이제서야** 플래그를 세운다(중간에 터지면 다음 주입이 다시 한다).
      "window.__tfEdge=1;P({type:'edgeready'});" +
      "})();";

    public void Go(string url) {
        LastUrl = url;   // 비교는 base URL로(버스터가 붙으면 매번 다르니 여기 저장하는 건 원본)
        if (web.CoreWebView2 == null) { pending = url; return; }
        web.CoreWebView2.Navigate(Cfg.Bust(url));
    }

    // 작업표시줄을 뺀 실제 화면 안으로 밀어넣는다. 1080 화면에서 860 클라이언트+타이틀바가
    //   그냥은 안 들어가서 아래가 잘렸다(실측 07-30). 모니터가 바뀌어도 이 계산이 다시 돈다.
    public void FitToScreen() {
        // 최소화 상태로 열리는 경우가 실측됐다(07-30) — 위치·크기를 재기 전에 반드시 보통 상태로 되돌린다.
        //   최소화된 창은 GetWindowRect가 -32000을 돌려줘 아래 계산도 전부 무의미해진다.
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        // ⚠️왼쪽에 미세한 회색 줄(유성 신고 07-30, 스크린샷 183942): 앱 본문이 `--maxw:460px`인데
        //   창이 그보다 조금 넓으면 남는 폭에 **몸통 배경(회색)이 비친다**. 배율이 100%가 아니면
        //   460 물리픽셀 ≠ 460 CSS픽셀이라 어긋난다 → **배율을 곱해 CSS 460px에 정확히 맞춘다.**
        float scale = 1f;
        try { using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f; } catch (Exception) { }
        int wantW = (int)Math.Round(Cfg.APP_W * scale);
        if (ClientSize.Width != wantW) ClientSize = new Size(wantW, ClientSize.Height);
        MinimumSize = new Size(wantW + (Width - ClientSize.Width), 380);
        MaximumSize = Size.Empty;   // 세로는 자유롭게(크기 조절을 열었으므로)
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int maxClientH = wa.Height - (Height - ClientSize.Height) - 16;
        if (maxClientH < 400) maxClientH = 400;
        // ⚠️구판 버그: 여기서 폭을 `Cfg.APP_W`(460 고정)로 되돌려 125/150% PC에서 위 DPI 보정(wantW)이 무효화됐다.
        if (ClientSize.Height > maxClientH) ClientSize = new Size(wantW, maxClientH);
        int x = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        int y = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        Location = new Point(x, y);
    }
}

// ══════════════════════════════════════════════════════════════
static class Program {
    // 다시 켜기(업데이트 적용·되돌리기·처리 못 한 예외) — 새 프로세스는 `--wait <나>`로 내가 끝날 때까지 기다렸다가
    //   뮤텍스를 잡는다(안 기다리면 「이미 떠 있음」으로 조용히 물러난다).
    public static MethodInvoker BeforeExit;   // 알약이 등록 — 트레이 아이콘을 치운다(안 치우면 Exit 뒤 유령 아이콘이 남는다)
    public static void Relaunch() {
        try { if (BeforeExit != null) BeforeExit(); } catch (Exception) { }
        try {
            Process.Start(Application.ExecutablePath, "--wait " + Process.GetCurrentProcess().Id);
        } catch (Exception ex) { AppWin.EdgeLog("다시 켜기 실패: " + ex.Message + " — 지킴이(5분)가 켠다"); }
        Environment.Exit(0);
    }
    static int crashes = 0;
    static void OnCrash(Exception ex) {
        AppWin.EdgeLog("처리 못 한 예외: " + (ex == null ? "(없음)" : ex.GetType().Name + " " + ex.Message));
        if (Upd.Unconfirmed()) { Upd.Rollback("새 판에서 예외"); return; }
        // 켜자마자 죽는 걸 무한 반복하지 않게 — 첫 1분 안의 죽음은 지킴이(5분)에게 맡긴다
        if (++crashes > 1 || (DateTime.Now - Boot.At).TotalSeconds < 60) Environment.Exit(1);
        Relaunch();
    }

    [STAThread]
    static int Main(string[] args) {
        // 업데이트가 새로 만든 exe를 시험할 때 — 아무것도 띄우지 않고 부품 로드만 보고 끝(Upd.SelfTest)
        if (Array.IndexOf(args, "--selftest") >= 0) return Upd.SelfTestMain();
        int wi = Array.IndexOf(args, "--wait"), pid;
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out pid)) {
            try { Process.GetProcessById(pid).WaitForExit(20000); } catch (Exception) { }   // 이미 끝났으면 예외 = 그냥 진행
        }
        // 지킴이(작업 스케줄러 5분, D11①)가 부른 것 — 살아 있으면 **아무것도 안 한다**(알약을 부르거나 앱을 열지 않는다)
        bool watch = Array.IndexOf(args, "--watch") >= 0;
        // 단일 인스턴스 — 두 번 실행하면 알약이 두 개 뜬다
        bool fresh;
        using (var mtx = new Mutex(true, "Global\\생각공작소_근태위젯", out fresh)) {
            if (!fresh && watch) return 0;
            if (!fresh) {
                // 이미 돌고 있다 → **먼저 뜬 알약을 부르고** 조용히 물러난다.
                //   구판은 그냥 return이라 작업표시줄 고정 아이콘을 눌러도 아무 반응이 없었다(2026-08-05).
                //   쓰는 사람에게 "반응 없음"은 고장과 구별되지 않는다.
                // ⚠️본선은 **창을 직접 찾아 보내는 것**이다(실측으로 확인한 길). 브로드캐스트(HWND_BROADCAST)는
                //   이 PC에서 알약에 도달하지 않았다(원인 미확인 — UIPI/브로드캐스트 필터 추정). 확인 안 된 길에
                //   본선을 걸지 않는다(24차-2 `storage` 이벤트에서 이미 같은 실수를 했다). 브로드캐스트는 백스톱으로만 남긴다.
                IntPtr first = Native.FindWindow(null, Cfg.PILL_TITLE);
                if (first != IntPtr.Zero) Native.PostMessage(first, Msg.Show, IntPtr.Zero, IntPtr.Zero);
                else if (Msg.Show != 0) Native.PostMessage(Native.HWND_BROADCAST, Msg.Show, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
            if (watch) AppWin.EdgeLog("지킴이가 다시 켬(꺼져 있었음)");
            // 처리 못 한 예외 = 기록하고 다시 켠다(구판은 .NET 오류 창이 떠 사람이 눌러야 했다). 새 판이면 옛 판으로 되돌린다
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { OnCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { OnCrash(e.ExceptionObject as Exception); };
            Upd.LoadBad();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Session.Load();
            Pos.Load();
            AppPos.Load();
            Application.Run(new Pill());   // 환경 생성은 Pill.Load가 시작한다(동기화 컨텍스트가 선 뒤여야 함)
        }
        return 0;
    }
}
