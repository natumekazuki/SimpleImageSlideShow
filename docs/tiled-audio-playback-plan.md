# Tileモード音声連動計画

> この計画は履歴として残す。現在の仕様では Tiled の音声再生機能と音量設定は削除済みで、音声連動は実装対象外。

## 背景と目的
- Tiledモード (`SimpleImageSlideShow/Components/Pages/Tiled.razor.cs`) は `DelaySeconds` ごとに `PeriodicTimer` で `ApplyPlannedOrStepAsync` を呼び出し（同ファイル:214-233）、新しい画像タイルを追加している。
- 画像と同名の音声ファイルがあっても無視されるため、画像切り替えが音声より先に進んでしまう。
- 目的: 画像と同じディレクトリに同じファイル名の音声データが存在する場合に自動再生し、音声再生完了までは次の画像追加を待機する。ただし音声が設定した表示時間より短い場合は従来通り `DelaySeconds` 経過時点で次の画像へ進む。

## 現状整理
- `TiledItem` / `PlannedStep`（同ファイル:32-120）は画像パスや描画サイズのみ保持し、音声に関する情報は存在しない。
- 待機制御は `StartAsync` が生成する `PeriodicTimer` に固定化されており（:214-233）、音声長に応じてスケジュールを伸縮させることができない。
- クライアント JS (`SimpleImageSlideShow/wwwroot/index.html`) はリサイズと画像プリロードのみを提供しており、音声再生 API が無い。

## 実装計画

### 1. 音声ファイル検出ヘルパー
- `Models` フォルダーに `AudioExtensions` などの新規 static クラスを追加し、`.mp3/.m4a/.wav/.wma/.aac/.ogg` 等を許容する一覧と `IsAudioFile` を用意する。
- `Tiled` コンポーネント内に `FindCompanionAudioPath(string imagePath)` を実装し、`Path.GetDirectoryName` と `Path.GetFileNameWithoutExtension` から候補パスを組み立てて `File.Exists` で探索する。
- 見つかったパスは既存の `BuildVirtualHostUrl`（:1214-1219）で WebView2 から参照できる URL に変換し、後段に回す。これにより Windows 固有サービスの変更は不要。

### 2. タイルおよび計画キューの拡張
- `TiledItem` と `PlannedStep` に `string? AudioSrc`（および必要なら `string? AudioPath`）プロパティを追加する。
- `AddOneAsync` / `AddWithFifoRemovalAsync` / `ComputeOnePlanAsync` で画像パス確定時に `FindCompanionAudioPath` を呼び、`AudioSrc` をセットする。計画生成後には `PreloadImageUrlAsync` と同様に、音声 URL を JS 側でプリロードしないが、null 判定のみで OK。
- `ApplyPlannedOrStepAsync` の戻り値を `TileStepResult`（新規 private record）に変更し、最後に追加された `TiledItem` とその `AudioSrc` を呼び出し元に返せるようにする。計画なしで `StepAsync` を走らせた場合も同じ情報を返す。

### 3. 待機ロジックとループ制御
- `StartAsync`（:209-233）を `RunLoopAsync` ベースに書き換え、`PeriodicTimer` を廃止する。ループの 1 周で `ApplyPlannedOrStepAsync` を呼び（UI スレッドへ `InvokeAsync` しつつ `StateHasChanged` を維持）、結果を受け取る。
- ループ終了後に `await WaitForNextTickAsync(stepResult, token)` のような新規メソッドを呼び出し、`DelaySeconds` と音声長のいずれか長い方を待機する。
- `WaitForNextTickAsync` 内では `DelaySeconds` を TimeSpan に変換しつつ、`AudioSrc` が null でなければ JS 側の音声再生（次項）を起動し、その完了値（double 秒）を取得。`Math.Max(delaySeconds, audioSeconds)` を計算して `Task.Delay` をキャンセル対応で待機する。
- ループ中で JS を叩く必要があるため、`InvokeAsync` を使って UI スレッドから `JS.InvokeAsync<double>(\"window.app.playAudioAndWait\", audioUrl)` を呼ぶ。

### 4. JavaScript 音声再生ユーティリティ
- `wwwroot/index.html` に `window.app.playAudioAndWait = function(url){ ... }` を追加。
    - `new Audio(url)` で HTMLAudioElement を生成し、`play()` を開始。
    - `ended` / `error` / `abort` で resolve し、resolve 時に `audio.duration` もしくは `audio.currentTime` を返す。
    - ユーザー操作不要で自動再生できるよう `audio.play().catch(() => resolve(0))` のようにエラー処理を入れる。
- Promise で再生完了を待てるようにし、Blazor 側は double 秒数として待機に利用する。

### 5. 付随するコードの調整
- `StopAsync`（:235-249）は `PeriodicTimer` 除去後もキャンセル/待機ロジックが成り立つよう `_loopTask` の終了を待つのみで問題無いが、`RunLoopAsync` 内で `Task.Delay` が `OperationCanceledException` を投げるので try/catch を追加する。
- 設定保存 (`SaveAndApplyAsync` :820-852 付近) に変更は不要だが、`DelaySeconds` の最小値チェックは現状のまま使用する。

### 6. 音声ボリューム設定の追加
- 要件: アプリ起動時はミュート状態（0%）で開始し、ユーザーが 0〜100% のスライダーで音量を設定できるようにする。
- `Models/AppSettings.cs` に `double AudioVolumePercent`（0〜100、初期値 0）を追加し、`SettingsService` で永続化する。
- `Tiled` コンポーネントへ `double AudioVolume` プロパティを追加し、`OnInitializedAsync` で設定値を読み込みつつ 0〜1 に正規化（UI 表示は 0〜100）。`SaveAndApplyAsync` で設定へ書き戻す。
- `Tiled.razor` の設定パネルに「Audio Volume」スライダーを追加し、`@oninput` で `OnVolumeInput` を呼んで即時適用可能にする。起動時は常に 0% で描画し、必要なら `SettingsService` の値を 0 にリセット。
- JS 側 `playAudioAndWait` は `Audio` インスタンス生成時に `audio.volume = window.app.audioVolume ?? 0;` を参照するよう変更し、Blazor 側から `window.app.setAudioVolume(volume0to1)` を呼び出せる API を追加する。
- `Tiled` コンポーネントで音量変更時に `JS.InvokeVoidAsync("window.app.setAudioVolume", AudioVolume)` を実行し、音声再生タスクで最新値が使われるようにする。

## 動作確認計画
1. 画像のみのフォルダーで Tiled モードを起動し、従来通り `DelaySeconds` 間隔で配置されることを確認。
2. 同じフォルダーに `sample01.jpg` と `sample01.mp3` の組を置き、Tiled モードで当該画像が表示された際に音声が自動再生され、音声尺（設定値より長い場合）だけ次のタイルが遅延することを確認。
3. 音声が設定値より短い場合に `DelaySeconds` から外れないこと、および音声ファイルが存在しない画像が混在してもループが止まらないことを確認。
4. フォルダー変更や設定更新 (`Save`/`Change Folder`) 後も音声が再生されることを手動確認。
5. アプリ起動直後はミュート状態であり、スライダーを操作すると即座に音声出力レベルが変わること、設定保存後の再起動でも前回設定値が復元されることを確認。
