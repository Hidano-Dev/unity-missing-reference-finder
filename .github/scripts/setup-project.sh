#!/usr/bin/env bash
# =============================================================================
# Unity プロジェクトセットアップスクリプト (共通処理)
# -----------------------------------------------------------------------------
# 使い方: setup-project.sh <プロジェクトディレクトリ> <EditorVersion 例: 6000.0.32f1>
#
# 指定ディレクトリ配下に Unity プロジェクトの骨組みを構成する。
#   1. changeset の解決 (resolve-changeset.sh。解決不能ならエラー停止)
#   2. Assets/ (.gitkeep 付き), Packages/, ProjectSettings/ の作成 (冪等)
#   3. ProjectSettings/ProjectVersion.txt の生成
#   4. resolve-upm.sh による manifest.json の生成
#
# 呼び出し元: add-another-project.yml
# ディレクトリの存在チェック (新規作成のみ) は呼び出し側で行うこと。
# =============================================================================
set -euo pipefail

PROJECT_DIR="$1"
VER="$2"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── changeset 解決 (同梱 TSV → 公式 API) ───────────────────
REV=$(bash "$SCRIPT_DIR/resolve-changeset.sh" "$VER")

# ── 骨組み作成 ─────────────────────────────────────────────
mkdir -p "$PROJECT_DIR/Assets" "$PROJECT_DIR/Packages" "$PROJECT_DIR/ProjectSettings"
# Assets は空のままコミットされうるため .gitkeep で保持する
[ -e "$PROJECT_DIR/Assets/.gitkeep" ] || touch "$PROJECT_DIR/Assets/.gitkeep"

cat > "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" <<EOF
m_EditorVersion: $VER
m_EditorVersionWithRevision: $VER ($REV)
EOF

# ── UPM パッケージ解決 ─────────────────────────────────────
bash "$SCRIPT_DIR/resolve-upm.sh" "$VER" "$PROJECT_DIR"

echo "Unity $VER ($REV) : $PROJECT_DIR" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
