# SQLite Settings Profiles

## 目的

設定の永続化を Json ファイルから SQLite に移し、将来の複数設定セット切り替えに備える。

現時点では UI から複数セットを作成・切り替えない。アプリは active profile 1 件だけを既存の `ISettingsService.LoadAsync()` / `SaveAsync()` 経由で読み書きする。

## 保存先

- 保存先: `FileSystem.AppDataDirectory/SimpleImageSlideShow/settings.db`
- 旧 `settings.json` からの移行は行わない。
- SQLite が空の場合は、既定値の active profile `Default` を 1 件作成する。

## スキーマ

`settings_profiles` は設定セットを表す。

- `id`: profile 識別子
- `name`: profile 表示名。現時点では `Default` のみ使用し、UI には出さない。
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

## 将来の複数セット切り替え

複数セット切り替えを実装する場合は、`settings_profiles` をそのまま利用する。

- profile 一覧は `id`, `name`, `is_active` を返す API を追加する。
- 切り替え時は対象 profile を active にし、画像フォルダの mapping と Tiled 状態を再初期化する。
- profile の作成・複製・リネーム・削除は `ISettingsService` を拡張して扱う。
