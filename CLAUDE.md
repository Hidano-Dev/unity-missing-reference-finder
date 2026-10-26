# 開発メモ

## リポジトリ構成

リポジトリ直下の各ディレクトリ（`ProjectSettings/ProjectVersion.txt` を持つもの）がそれぞれ独立した Unity プロジェクト。リポジトリ直下に `Assets/` は無いため、Unity 関連のコマンドは対象プロジェクトのディレクトリを指定して実行する。

## Unity の操作は Unity CLI で行う

Unity Editor の起動・操作・テスト・ビルドは Unity CLI（`unity` コマンド）で行う。詳しい使い方はリポジトリ直下の `unity-cli` skill（`.claude/skills/unity-cli/`、Codex 向けは `.agents/skills/unity-cli/`）を参照。

- 未インストールなら skill の「Step 1: Install the CLI」の手順で入れる（マシンごとに 1 回。リポジトリには含まれない）。
- 各プロジェクトには Editor を CLI から操作するための `com.unity.pipeline` パッケージが入っている（Unity 6.0 以降）。無いプロジェクトには `unity pipeline install --project-path <プロジェクトディレクトリ>` で入れる。
- よく使うコマンド:

```
unity open <プロジェクトディレクトリ> --args "-automated"   # 正しいバージョンの Editor で開く（自動化用）
unity status                                               # 接続中の Editor 一覧（state が ready なら操作可能）
unity command                                              # Editor が公開しているコマンド一覧
unity test <プロジェクトディレクトリ> --mode EditMode        # テスト実行（結果は test-results.xml）
```

- `unity status` で接続できないときは、コンパイルエラーで Safe Mode になっていないかを `unity pipeline list` で確認する（Safe Mode では pipeline パッケージが読み込まれない）。

## 自動化で Editor を起動するときは `-automated` を付ける

CLI から Unity Editor を起動して自動化処理（ビルド、テスト、エージェント操作など）を行う場合は、Editor に **`-automated` フラグを渡す**（`unity open` なら `--args "-automated"`、Unity.exe を直接起動するなら引数に追加）。

- 未保存シーンの確認などの**ブロッキングダイアログが表示されなくなり**、各ダイアログの既定アクションが自動選択されるため、処理が途中で停止しない。
- `-batchmode` と違い GUI ありの起動でも使える（Unity 6 系で確認）。
- 注意: 「すべて OK が押される」のではなく**各ダイアログの既定動作**に従う。意図しない変更が起きる可能性があるため、自動化専用の起動にのみ付け、実行前に git がクリーンな状態であることを確認する。普段の手作業用 Editor には付けない。
