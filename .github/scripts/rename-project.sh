#!/usr/bin/env bash
# =============================================================================
# プロジェクトリネームスクリプト
# -----------------------------------------------------------------------------
# 使い方: rename-project.sh <現ディレクトリ> <新しいプロジェクト名>
#
# ディレクトリ名 (git mv) と ProjectSettings.asset の productName を
# どちらも <新しいプロジェクト名> へ揃える。
# 現ディレクトリと新名が同じ場合は productName の書き換えだけ行う (冪等)。
#
# ※ productName の置換は環境変数経由で行うため記号入りでも安全だが、
#    ディレクトリ名にもなるため OS で使えない文字や '/' は不可。
#
# 呼び出し元: template-init.yml / update-project.yml
# =============================================================================
set -euo pipefail

CUR_DIR="$1"
NEW_NAME="$2"

case "$NEW_NAME" in
  ''|.|..|*[/\\]*)
    echo "::error::'$NEW_NAME' はプロジェクト名 (ディレクトリ名) として使えません" >&2
    exit 1
    ;;
esac

if [ ! -f "$CUR_DIR/ProjectSettings/ProjectSettings.asset" ]; then
  echo "::error::'$CUR_DIR' は Unity プロジェクトとして見つかりません" >&2
  exit 1
fi

# ── ディレクトリのリネーム ─────────────────────────────────
if [ "$CUR_DIR" != "$NEW_NAME" ]; then
  if [ -e "$NEW_NAME" ]; then
    echo "::error::'$NEW_NAME' は既に存在します" >&2
    exit 1
  fi
  git mv "$CUR_DIR" "$NEW_NAME"
fi

# ── productName の書き換え ─────────────────────────────────
FILE="$NEW_NAME/ProjectSettings/ProjectSettings.asset"
PRODUCT_NAME="$NEW_NAME" perl -pi -e 's/^(\s*productName:\s*).*/$1.$ENV{PRODUCT_NAME}/e' "$FILE"

if ! grep -F "productName: $NEW_NAME" "$FILE" >/dev/null; then
  echo "::error::productName の書き換えに失敗しました ($FILE に productName 行が見つからない可能性)" >&2
  exit 1
fi

echo "renamed: $CUR_DIR -> $NEW_NAME (productName: $NEW_NAME)" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
