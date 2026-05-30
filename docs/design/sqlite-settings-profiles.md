# SQLite Settings Profiles

## 目的

設定の永続化を SQLite に置き、複数の設定セットを名前付き profile として作成・切り替えできるようにする。

既存の `ISettingsService.LoadAsync()` / `SaveAsync()` は active profile を対象にする互換 API として残し、profile 一覧・作成・リネーム・切り替え・削除は profile 管理 API で扱う。

## 保存先

- 保存先: `FileSystem.AppDataDirectory/SimpleImageSlideShow/settings.db`
- 旧 `settings.json` からの移行は行わない。
- SQLite が空の場合は、既定値の active profile `Default` を 1 件作成する。

## スキーマ

`settings_profiles` は設定セットを表す。

- `id`: profile 識別子
- `name`: profile 表示名。UI から文字入力で変更できる。
- `is_active`: 現在利用中の profile。active は 1 件だけにする。
- `directory_path`: 表示対象フォルダ。設定セットに含める。
- `min_delay_seconds`: タイル追加待機時間の最小秒数。下限は 0。
- `max_delay_seconds`: タイル追加待機時間の最大秒数。下限は 1。
- `window_display_mode`: 表示モード。
- `tiled_min_scale`, `tiled_max_scale`, `tiled_cols`, `min_tile_px`, `tiled_reuse_ttl_seconds`, `random_scale_tries`: Tiled 表示設定。
- `show_tiled_clock`, `tiled_clock_corner`, `tiled_clock_scale`, `avoid_tiled_clock_overlap`: 時計表示設定。
- `background_color`: 背景色。
- `created_at`, `updated_at`: UTC ISO-8601 文字列。

`PRAGMA user_version` は schema version として使う。初期 schema version は `1`。

## 待機時間

設定 UI は秒単位の整数で扱う。実際の待機時間は、各ループごとに `min_delay_seconds` から `max_delay_seconds` の範囲で小数秒の一様ランダムとして抽選する。

- `min=max` の場合は固定秒数として待機する。
- `min=0, max=1` の場合は 0 秒以上 1 秒未満の値を抽選する。
- 抽選値は丸めず `TimeSpan.FromSeconds()` に渡す。

## 音声再生

Tiled 表示と連動する音声再生機能は持たない。

- 音声ファイル探索は行わない。
- 音量設定は保持しない。
- JavaScript 側に音声再生 API は置かない。

## 複数セット切り替え

複数セット切り替えでは、`settings_profiles` をそのまま利用する。

- profile 一覧は `id`, `name`, `is_active` を返す。
- active profile の読み込みでは `id`, `name`, `is_active`, `AppSettings` を返す。
- `SaveAsync()` は active profile の設定だけを更新し、既存の profile 名を保持する。
- profile 作成は現在の UI 設定値を初期値として新規 row を作り、active にできる。
- profile リネームは `name` のみを更新する。空白名は `Default` に正規化する。
- profile 削除は最後の 1 件を削除しない。active profile を削除した場合は残りの profile から新しい active を選ぶ。
- 切り替え時は現在の UI 設定を保存してから対象 profile を active にし、ページを再読み込みして画像フォルダ mapping と Tiled 状態を初期化し直す。

## UI

Tiled 設定パネルの先頭に profile 操作を置く。

- `select`: 登録済み profile を選択する。
- `input type="text"`: active profile の名前を編集する。最大 80 文字。
- `New`: 現在の UI 設定値から新しい profile を作成し、active にする。
- `Delete`: profile が 2 件以上ある場合だけ active profile を削除できる。
- `Save`: active profile 名と現在の設定値を保存する。
