# プロジェクトセットアップガイド

このリポジトリは Unity プロジェクトテンプレートから生成され、GitHub Actions によって自動初期化されています (設定済みプロジェクトのリネーム・Unity バージョン固定)。

リポジトリ直下の各ディレクトリがそれぞれ独立した Unity プロジェクトです。生成時には、テンプレートに同梱された設定済みプロジェクト (パッケージ構成・ProjectSettings・URP 設定込み) がリポジトリ名にリネームされて 1 つ配置されています (ProjectSettings の productName もリポジトリ名に設定済み)。**最初のプロジェクトはこの自動作成分で足りるため、Add Another Project を実行する必要はありません** (名前どおり、2 つ目以降を増やすためのものです)。

## はじめかた

1. **初期化の完了を確認** — Actions タブで `Template Init` が完了 (緑) していることを確認します。まだ実行中なら完了を待ってください。`chore: initialize from template` コミットが積まれていれば完了です。
2. **clone / pull** — 初期化コミットが積まれた後に clone してください。先に clone していた場合は `git pull` します。
3. **Unity Hub で開く** — 「Add project from disk」で**プロジェクトディレクトリ** (リポジトリ直下ではなく、その中の各プロジェクトフォルダ) を指定します。指定バージョンが未インストールの場合は Hub がインストールを案内します。
4. 初回起動で生成される `Packages/packages-lock.json` をコミットしておくと、メンバー間でパッケージ解決が揃います。

## プロジェクト名や Unity バージョンを変更したい場合

**Unity バージョンの変更は、対象プロジェクトの初回起動前 (開始時) の実行を想定しています。** ハッシュ (changeset) を調べる必要はありません。

1. Actions タブ →「**Update Project**」→「Run workflow」
2. 変えたい項目だけ入力して実行 (両方同時も可)
   - `project`: 変更対象のプロジェクト名 (現在のディレクトリ名)。プロジェクトが 1 つだけなら空欄のままで自動検出されます
   - `name`: 変更後のプロジェクト名 (ディレクトリ名と ProjectSettings.asset の productName の両方が変わります)
   - `version`: 変更後の Unity バージョン (例: `6000.0.32f1`)
3. 完了後に `git pull`。名前を変えた場合は Unity Hub で新しいディレクトリを「Add project from disk」で指定し直す

バージョン変更では ProjectVersion.txt の書き換えと、manifest.json 内の公式 UPM パッケージ (`com.unity.*`) の対象 Editor 適合版への揃え直しが自動で行われます (OpenUPM 等のサードパーティパッケージと scopedRegistries には触れません)。URP など Editor 同梱系のパッケージは warning を出して自動更新をスキップするため、Unity で開いた際に必要に応じて調整してください。存在しないバージョンを入力した場合はエラーで停止するだけで、リポジトリは変更されません。利用可能なバージョン一覧は `.github/unity-versions.tsv` (生成時点のスナップショット) で確認できます。

プロジェクトを複数追加している場合は候補一覧を表示してエラー停止するので、その中から対象のディレクトリ名を `project` 欄に入力して再実行してください。

> **注意**: 開発が進んだ後に大きくバージョンを下げると `Assets/` 内のシーン・プレハブに非互換が起きる可能性があります。その場合はブランチで検証してから main に取り込んでください。

## プロジェクトを追加したい場合 (2 つ目以降)

最初のプロジェクトは初期化時にリポジトリ名で自動作成されているため、この手順は不要です。別のプロジェクトを増やしたいときだけ実行します。

1. Actions タブ →「**Add Another Project**」→「Run workflow」
2. 新しいプロジェクトディレクトリ名 (既存と重複しない名前) とバージョン番号 (例: `6000.0.32f1`) を入力して実行
3. 完了後に `git pull` し、Unity Hub でそのディレクトリを開く

リポジトリ直下に `Assets/`, `Packages/`, `ProjectSettings/` を備えた骨組みが作成されます (初期プロジェクトのような設定済み構成のコピーではなく最小構成です)。

## AI エージェントから Unity を操作する (Unity CLI)

エージェントからの Unity 操作には公式の Unity CLI (`unity` コマンド) を使います。各プロジェクトには CLI から Editor を操作するための `com.unity.pipeline` パッケージが入っており、Claude Code / Codex 向けの `unity-cli` skill がリポジトリ直下 (`.claude/skills/` `.agents/skills/`) にあります。

1. 各自のマシンに Unity CLI をインストールする (手順は `.claude/skills/unity-cli/SKILL.md` の「Step 1」)
2. `unity open <プロジェクトディレクトリ>` で Editor を開くと、`unity status` / `unity command` でエージェントから操作できます

使い方の要点は `CLAUDE.md` にまとめてあります。

## SDD (仕様駆動開発) を使いたい場合

このリポジトリには SDD ワークフローは入っていません。必要なら [unity-sdd-kit](https://github.com/Hidano-Dev/unity-sdd-kit) の手順で導入してください。

## 困ったときは

- **プロジェクトディレクトリがリポジトリ名になっていない (`TemplateProject/` のまま)** → 初期化コミット前の状態です。`git pull` してください。
- **`unity status` で Editor に接続できない** → コンパイルエラーで Safe Mode になっている可能性があります。`unity pipeline list` で確認し、エラーを直してください。
- **Add Another Project が「既に存在します」で止まる** → リポジトリ名と同じ名前を指定しています。最初のプロジェクトは初期化時に自動作成済みなので、そのまま `git pull` して使ってください。名前やバージョンを変えたい場合は Update Project を使います。
- **プロジェクト更新 / プロジェクト追加が反映されない** → Actions の実行結果を確認してください。bot の push が拒否されている場合は、リポジトリ設定の Workflow permissions を「Read and write permissions」にします。

テンプレートの仕組みの詳細は、テンプレートリポジトリ側の README (仕様・運用手順書) を参照してください。
