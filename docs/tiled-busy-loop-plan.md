# 計画: Tiled ビューのビジーループを防ぐ

## 背景
- `SimpleImageSlideShow/Components/Pages/Tiled.razor.cs:224` の `RunLoopAsync` は `_lastTickItem` が null でない場合にしか `WaitForNextTickAsync` を待機しません。
- `ApplyPlannedOrStepAsync` がタイル追加に失敗すると `_lastTickItem` が null のままとなり、ループが即座に再実行されてファイルを繰り返し列挙し続け、CPU を消費します。
- 以前は `PeriodicTimer` を使用して毎回待機していたため、この変更によって画像を生成できない状況で UI スレッドが張り付きやすくなる回帰が発生しました。

## 目標
- タイルを生成できない場合でもループに必ず待機時間を挿入し、無限ポーリングを防ぐ。
- 既存のタイミング挙動（`DelaySeconds` に基づくウェイトや音声再生との同期）を維持する。
- 正常時の初回タイル表示は従来どおり即時に行い、体感性能を落とさない。

## 対応方針
1. **常にウェイトを挿入**: 初回のみ待機をスキップし、それ以降は `_lastTickItem` が null の場合でも `WaitForNextTickAsync(null, token)` を呼び出して待つ。
2. **アイドル状態を追跡**: ローカルフラグ（例: `bool shouldWait`）を導入し、初回処理後は常に待機が入るように制御してビジーループを抑止する。
3. **既存ヘルパーを再利用**: `WaitForNextTickAsync` は音声付き/無しのいずれでも適切に待機するため、新たな遅延処理は不要。null を渡すだけで純粋な `Task.Delay` になる。
4. **`_lastTickItem` を維持**: `newItem ?? Items.LastOrDefault()` を継続して使用し、次回待機の対象が常に最新の成功アイテムとなるようにする。

## 実装ステップ
1. ループ開始前に `bool shouldWait = _lastTickItem is not null;` を宣言し、初回は既存どおり待機を飛ばす。
2. ループ内の待機処理を以下のように置き換える:
   ```csharp
   var waitTarget = _lastTickItem;
   if (waitTarget is not null || shouldWait)
   {
       await WaitForNextTickAsync(waitTarget, token);
   }
   shouldWait = true;
   ```
   これにより、前回タイルを生成できなかった場合でも `shouldWait` によってウェイトが挿入される。
3. 遅延時間は既存の `DelaySeconds` をそのまま利用し、必要に応じて別途アイドル専用の短い遅延を導入できるよう TODO コメントを残す。
4. それ以外の処理（`_lastTickItem` の更新や `ApplyPlannedOrStepAsync` 呼び出し）は現行ロジックを維持する。

## 検証
- **空フォルダー**: 空のディレクトリを選択して CPU 使用率が低く保たれるか確認。
- **通常動作**: 画像＋音声付きのフォルダーでタイルが `DelaySeconds` 間隔で切り替わり、音声が従来どおり再生されるか確認。
- **エラー復帰**: 一時的にファイルアクセスを遮断してループがスロットルされること、復旧後にタイル追加が再開されることを確認。

## リスクと対応
- **初回表示の遅延**: 初回のみ待機をスキップする設計により体感遅延を回避。必要ならフラグ初期値を設定変更で調整できる。
- **長いアイドル遅延**: `DelaySeconds` が大きい場合、空フォルダーでのリトライも長くなる。必要なら短い `IdleRetryDelay` を別途導入する計画を検討。
- **音声タイミングの回帰**: 既存ヘルパーを使用するため挙動は変わらない想定だが、音声付きタイルでの手動確認を行う。