#!/usr/bin/env bash
# =============================================================================
# Unity バージョン切替スクリプト (既存プロジェクト用)
# -----------------------------------------------------------------------------
# 使い方: set-unity-version.sh <プロジェクトディレクトリ> <EditorVersion 例: 6000.0.32f1>
#
# 既存の Unity プロジェクトの Editor バージョンを切り替える。
#   1. changeset の解決 (resolve-changeset.sh。解決不能ならエラー停止)
#   2. ProjectSettings/ProjectVersion.txt の書き換え
#   3. update-manifest.sh による manifest.json のその場更新
#      (公式パッケージのみ適合版へ。サードパーティ・scopedRegistries は温存)
#
# 呼び出し元: update-project.yml
# プロジェクトの存在チェックは呼び出し側で行うこと。
# =============================================================================
set -euo pipefail

PROJECT_DIR="$1"
VER="$2"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REV=$(bash "$SCRIPT_DIR/resolve-changeset.sh" "$VER")

cat > "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" <<EOF
m_EditorVersion: $VER
m_EditorVersionWithRevision: $VER ($REV)
EOF

bash "$SCRIPT_DIR/update-manifest.sh" "$VER" "$PROJECT_DIR"

echo "Unity $VER ($REV) : $PROJECT_DIR" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
