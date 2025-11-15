# フルスクリーン/ウインドウ切替対応計画

## 背景と現状
- Slide 画面 (`SimpleImageSlideShow/Components/Pages/Home.razor`) と Tiled 画面 (`SimpleImageSlideShow/Components/Pages/Tiled.razor`) の設定パネルには、モード切替や各種スライダーのみが存在し、画面表示方法を変更するボタンがない。
- `IWindowService` は `ToggleFullScreen()` と `Exit()` のみを公開しており、UI からも呼び出されていないため常にフルスクリーンで稼働する。
- Windows 実装 (`Platforms/Windows/WindowService.cs`) では `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)` / `Overlapped` を単純に切り替えているが、ウインドウ状態でのサイズ復元や現在のディスプレイの判定を行っていない。

## 目的
1. Slide/Tiled いずれの画面でも、既存の設定パネル内に「フルスクリーン/ウインドウ表示」ボタンを追加し、利用者が即座に表示モードを切り替えられるようにする。
2. フルスクリーン化は、現在ウインドウが存在しているディスプレイ上で行い、意図せず別モニターへ移動しないようにする。
3. UI ボタンの表示テキストや状態を実際のウインドウ状態と同期させる（例: フルスクリーン時は「ウインドウ表示」ボタンとして表示）。

## 非対象
- 複数ウインドウ管理や、スライド画面とタイル画面を同時に表示する機能。
- macOS / Android など Windows 以外の TFM でのウインドウ操作。

## 対応方針概要
1. `IWindowService` に表示モードの参照/通知機能を追加し、Blazor 側が現在状態を把握できるようにする。
2. Windows 実装で `AppWindow` / `DisplayArea` を使って、現在のディスプレイを尊重しながらフルスクリーン/ウインドウ状態を適切に切替・復元する。
3. Slide/Tiled の設定パネルにトグルボタンを追加し、サービスを経由して表示モードを切り替える UI を実装する。
4. （任意だが推奨）`AppSettings` に最後に利用した表示モードとウインドウ矩形を保存し、アプリ再起動時に復元する。

## 詳細タスク

### 1. Window サービス契約と状態管理
- `WindowDisplayMode`（例: `Windowed`, `FullScreen`）の列挙型を `Services` に新設する。
- `IWindowService` に以下を追加: `WindowDisplayMode CurrentMode { get; }`、`event EventHandler<WindowDisplayMode>? ModeChanged`、`Task ToggleModeAsync()`、`Task InitializeAsync()`（AppWindow を捕捉して初期状態をブロードキャストする用途）。
- 可能であれば `Task SetModeAsync(WindowDisplayMode target)` も公開し、UI 側が明示的に指定できるようにする。
- Blazor から同期しやすいよう、`ModeChanged` は UI スレッド上で発火させる or `MainThread.BeginInvokeOnMainThread` を経由させる。

### 2. Windows 実装 (`Platforms/Windows/WindowService.cs`)
- `Application.Current.Windows.FirstOrDefault()` から `WindowId`/`AppWindow` を取得する処理を一度だけ行い、以降再利用できるように `_appWindow` フィールドを保持する。
- `_appWindow.PresenterChanged` または `AppWindow.Changed` を購読して現在の `AppWindow.Presenter.Kind` を監視し、`CurrentMode` を更新して `ModeChanged` を発火させる。
- フルスクリーン化前に `DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary)` を用いて現在のディスプレイを取得し、`SetPresenter(AppWindowPresenterKind.FullScreen)` 呼び出し直前に `MoveAndResize` で作業領域いっぱいに広げる（そうすることでウインドウの位置が別ディスプレイに飛ばない）。
- ウインドウ表示に戻す際は、直近の `AppWindow.Position/Size` を記録した `_lastWindowRect` を `OverlappedPresenter` に渡して復元する。`OverlappedPresenter.SetBorderAndTitleBar(true, true)` などで標準のウインドウクロムを確保する。
- `ToggleModeAsync` では `CurrentMode` を参照しながら `SetModeAsync` を呼ぶのみとし、例外はロギングした上で UI に伝わるよう Result/Task を失敗させる。

### 3. Blazor UI 変更
- Slide (`Home.razor`/`.razor.cs`) と Tiled (`Tiled.razor`/`.razor.cs`) の設定パネル Row に、既存の `buttons` セクションの先頭へ `@if (WindowModeSupported)` ブロックでトグルボタンを追加する。ラベルは `IsFullScreen ? "ウインドウ表示" : "フルスクリーン表示"` として状態を明示する。
- `.razor.cs` 側で `bool IsFullScreen` と `WindowDisplayMode CurrentWindowMode => WindowService.CurrentMode` を保持し、`OnInitializedAsync` で `await WindowService.InitializeAsync()` → `WindowService.ModeChanged += HandleModeChanged` を行う。
- ハンドラー内で `IsFullScreen = mode == WindowDisplayMode.FullScreen;` を更新し、`InvokeAsync(StateHasChanged)` で UI を再描画する。
- ボタンの `@onclick` で `await WindowService.ToggleModeAsync()` を呼び出す。処理中は二重押下を避けるため `disabled` を制御する（`bool ChangingWindowMode` など）。
- 可能であれば `IDisposable` 実装内で `ModeChanged` の購読を解除し、メモリリークを防ぐ。

### 4. 設定と起動時復元（任意）
- `Models/AppSettings` に `bool? LastWindowed` と `WindowRectangle? LastWindowBounds`（position + size）を追加し、`SettingsService` で保存/復元を行う。
- アプリ起動時（`MauiProgram` or `App.xaml.cs`）で `IWindowService` を初期化し、設定に応じて `SetModeAsync` / `MoveAndResize` を呼び出すことで前回のモードを再現する。
- 設定の保存は、モード切替完了イベントで `SettingsService.SaveAsync` を呼び出すようにして UI からの制御ロジックを分離する。

## 動作確認
1. アプリ起動直後はウインドウ表示のままでも操作できることを確認し、ボタン押下で即座にフルスクリーンへ移行することを確認する。
2. Slide/Tiled それぞれでフルスクリーン⇔ウインドウ切替ができ、ボタンラベルが状態に合わせて変化することを確認する。
3. 複数ディスプレイ環境でウインドウを別モニターへ移動後にフルスクリーンへ切り替えても同じモニターで全画面化されることを確認する。
4. Alt+Enter など OS 依存のショートカット（もし実装する場合）で状態が変わった場合でも UI 表示が追従することを確認する。
5. 必要に応じてアプリ再起動後に前回のウインドウ状態が復元されるか（設定保存を実装した場合）を確認する。
