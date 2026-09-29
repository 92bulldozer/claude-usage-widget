# claude-usage-widget

Claude Code 남은 사용량(5시간 세션 · 주간 한도)을 바탕화면 위젯과 트레이 아이콘으로 보여주는 Windows 11 앱.

> **비공식 프로젝트입니다.** Anthropic과 무관한 개인 프로젝트이며, Anthropic이 보증하거나 지원하지 않습니다.

## 기능

- **바탕화면 위젯**: 5시간 세션 / 주간(모델별 한도가 있으면 Opus · Sonnet 포함) 남은 %, 진행 막대, 리셋 시각
- **간단히 보기**: 한 줄짜리 막대만 보이는 작은 모드
- **트레이 아이콘**: 5시간 세션 남은 %를 숫자와 색(초록 · 주황 · 빨강)으로 표시, 마우스를 올리면 요약
- 작업표시줄과 Alt+Tab에는 나타나지 않음
- **설정**: Windows 시작 시 실행, 항상 위에 표시, 위치 고정, 갱신 주기(3 / 5 / 10 / 30분)
- 닫기(×)는 위젯만 숨김 — 완전히 끄려면 트레이 아이콘 우클릭 → 종료

## 요구 사항

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) (`claude` 명령) — 로그인과 토큰 갱신에 사용

## 빌드 · 실행

```bash
dotnet build -c Release
```

```bash
bin\Release\net10.0-windows\win-x64\ClaudeUsageWidget.exe
```

## 로그인

위젯은 Claude Code CLI의 로그인 정보(`%USERPROFILE%\.claude\.credentials.json`)를 읽습니다.
Claude **데스크톱 앱**만 쓰고 있다면 이 파일이 비어 있으므로 CLI로 한 번 로그인해야 합니다.

- 위젯의 "로그인 필요" 문구를 클릭하거나, 트레이 메뉴 → **로그인**
- 또는 직접: `claude auth login`

데스크톱 앱과 같은 계정으로 로그인하면 됩니다. 사용량은 계정 단위라 데스크톱 앱 사용분까지 모두 반영됩니다.

## 동작 방식

- Claude Code의 `/usage`와 같은 엔드포인트(`GET https://api.anthropic.com/api/oauth/usage`)를 호출합니다.
  **공개 문서가 없는 API**라 예고 없이 바뀌거나 막힐 수 있습니다.
- 토큰이 만료되기 전 `claude auth status`를 백그라운드로 실행해 **CLI가 직접** 토큰을 갱신하게 합니다.
  위젯이 토큰을 직접 갱신하지 않으므로 CLI 로그인이 풀리지 않습니다.
- 이 API는 요청 제한이 엄격해서 최소 갱신 주기는 3분이며, 429 응답을 받으면 서버가 알려준 시간만큼 기다립니다.
- 설정과 마지막 사용량은 `%APPDATA%\ClaudeUsageWidget\settings.json`에 저장됩니다. 토큰은 저장하지 않습니다.

## 기술 스택

- WPF (.NET 10) — 창, 트레이(WinForms `NotifyIcon`)
- [SkiaSharp](https://github.com/mono/SkiaSharp) — 위젯 UI와 트레이 아이콘 전체를 직접 렌더링
- 소프트웨어 렌더링 + 단일 x64 네이티브로 메모리 · 용량 최소화

## 라이선스

- 폰트: [Pretendard](https://github.com/orioncactus/pretendard) — SIL Open Font License 1.1 ([Fonts/LICENSE](Fonts/LICENSE))
