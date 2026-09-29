# claude-usage-widget

Claude Code 남은 사용량을 보여주는 Windows 11 바탕화면 위젯 + 트레이 앱. WPF(.NET 10) + SkiaSharp. 비공식 프로젝트.

## 구조

| 파일 | 역할 |
|---|---|
| `App.xaml.cs` | 단일 인스턴스(Mutex), 소프트웨어 렌더링 설정, 시작 프로그램 경로 보정, 창·트레이 생성 |
| `MainWindow.xaml.cs` | 폴링 타이머, 페이지 상태, 히트 테스트, 클릭 처리, Skia → `WriteableBitmap` 페인트 |
| `WidgetRenderer.cs` | **UI 전체를 Skia로 그림**. 사용량 / 간단히 보기 / 설정 페이지, 클릭 영역(`HitRegion`) 계산 |
| `UsageService.cs` | 토큰 읽기, 사용량 API 호출·파싱 |
| `CliAuth.cs` | `claude auth status`(토큰 갱신), `claude auth login`(로그인)을 창 없이 실행 |
| `TrayIcon.cs` | WinForms `NotifyIcon`, 메뉴, Skia로 그린 % 아이콘 |
| `AppSettings.cs` | `%APPDATA%\ClaudeUsageWidget\settings.json` |
| `StartupHelper.cs` | `HKCU\...\Run` 자동 시작 |
| `tools/RenderPreview` | 앱의 `WidgetRenderer`로 PNG를 뽑는 별도 콘솔 프로젝트 |

UI는 XAML 컨트롤이 아니라 `WidgetRenderer`가 전부 그린다. 버튼 추가 흐름: `HitId`에 id 추가 → 렌더러에서 `hits.Add(new HitRegion(...))` + 그리기 → `MainWindow.HandleClickAsync`에서 처리. `Draw(null, ...)`는 그리지 않고 높이·클릭 영역만 계산하므로, 레이아웃 계산은 canvas null 분기 전에 끝내야 한다.

## 반드시 지킬 것

- **토큰을 위젯이 직접 갱신하지 말 것.** 리프레시 토큰이 바뀌면 CLI 로그인이 풀린다. 갱신은 항상 `claude auth status`로 CLI에 맡긴다.
- 토큰 원천은 `~/.claude/.credentials.json`(`CLAUDE_CONFIG_DIR` 우선)뿐이다. Claude 데스크톱 앱은 여기에 토큰을 쓰지 않으므로 사용자는 CLI 로그인이 따로 필요하다. 데스크톱 앱 내부 저장소에서 토큰을 꺼내는 방식은 쓰지 않는다.
- 토큰은 로그·설정 파일·출력 어디에도 남기지 않는다. 확인이 필요하면 길이만 본다.
- 사용량 API(`GET https://api.anthropic.com/api/oauth/usage`, 헤더 `anthropic-beta: oauth-2025-04-20`)는 공개 문서가 없고 요청 제한이 엄격하다. 갱신 주기 최소 3분, 429 응답이면 `Retry-After`만큼 기다린다. 테스트할 때도 반복 호출하지 말 것.
- 응답 필드: `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet` → `{ utilization(0–100, 사용한 %), resets_at }`. 남은 % = 100 − utilization.

## 빌드 · 실행

```bash
dotnet build -c Release
```

- 실행 파일: `bin\Release\net10.0-windows\win-x64\ClaudeUsageWidget.exe`
- **빌드 전에 실행 중인 위젯을 먼저 끌 것**(`Stop-Process -Name ClaudeUsageWidget`). 안 끄면 exe가 잠겨서 복사 오류(MSB3027)가 난다.
- 자동 시작이 켜져 있으면 앱이 켜질 때 등록 경로를 현재 exe로 스스로 고친다(`StartupHelper.RepairPath`).

## UI 확인

화면 캡처는 멀티 모니터 + 레이어드 창이라 잘 안 된다. 대신 같은 렌더러로 PNG를 뽑아서 본다:

```bash
dotnet run --project tools/RenderPreview -- docs
```

`docs/*.png`(README 스크린샷)가 이 명령으로 다시 만들어진다. 예시 값만 쓰고 실제 사용량은 커밋하지 않는다.

## 알려진 함정

- **Skia 셰이더와 알파**: 셰이더 색에 paint의 알파가 곱해진다. 반투명 그림자·글로우를 그린 뒤 같은 paint로 그라데이션을 그리면 흐려진다 → 그 전에 `paint.Color = SKColors.White`.
- 투명 배경이라 폰트는 서브픽셀 AA 대신 `SKFontEdging.Antialias`를 쓴다(색 번짐 방지).
- 폰트는 exe에 포함된 Pretendard TTF(OFL, `Fonts/LICENSE`)다. 폰트 객체는 `Font()` 캐시에서 받아 쓰고 dispose하지 않는다.
- `SKPath.MoveTo/LineTo`는 SkiaSharp 4에서 사용 중지 경고가 뜬다. 간단한 아이콘은 `DrawLine`으로 그린다.
- 소프트웨어 렌더링(`RenderMode.SoftwareOnly`)은 일부러 켠 것이다. GPU 렌더링을 켜면 그래픽 드라이버를 불러와서 메모리가 약 300MB 늘어난다.
- **PowerShell 5.1의 `Get-Content`/`Set-Content`는 BOM 없는 UTF-8을 ANSI로 읽고 써서 한글이 깨진다.** 소스 수정은 Edit/Write 도구로 하거나 `-Encoding utf8`을 명시할 것.
- Bash 도구의 heredoc이 긴 스크립트에서 깨지는 경우가 있었다. 긴 패치 스크립트는 파일로 쓴 뒤 실행한다.
