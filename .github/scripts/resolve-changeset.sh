#!/usr/bin/env bash
# =============================================================================
# Unity changeset 解決スクリプト (共通処理)
# -----------------------------------------------------------------------------
# 使い方: resolve-changeset.sh <EditorVersion 例: 6000.0.32f1>
#
# 指定バージョンの changeset (shortRevision) を stdout に出力する。
#   1) 同梱 TSV (生成時点のスナップショット。無い/古い場合がある)
#   2) 公式 Editor Release API の全件走査フォールバック
# の順に解決し、どちらでも見つからなければエラー停止する。
#
# 呼び出し元: setup-project.sh / set-unity-version.sh
# =============================================================================
set -euo pipefail

VER="$1"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TSV="$SCRIPT_DIR/../unity-versions.tsv"

# 1) 同梱 TSV
REV=$(awk -F'\t' -v v="$VER" '$1==v{print $2; exit}' "$TSV" 2>/dev/null || true)

# 2) 公式 API の全件走査フォールバック
#    (unity-versions-update.yml と同一のページング呼び出し形式。
#     応答が想定外の型でも .results?[]? がエラーにせず読み飛ばす)
if [ -z "$REV" ]; then
  LIMIT=25
  OFFSET=0
  TOTAL=1
  while [ -z "$REV" ] && [ "$OFFSET" -lt "$TOTAL" ]; do
    RESP=$(curl -sf "https://services.api.unity.com/unity/editor/release/v1/releases?limit=$LIMIT&offset=$OFFSET") || break
    T=$(echo "$RESP" | jq -r '.total? // 0' 2>/dev/null || echo 0)
    case "$T" in (*[!0-9]*|'') T=0 ;; esac
    TOTAL=$T
    REV=$(echo "$RESP" \
      | jq -r --arg v "$VER" '.results?[]? | select(.version? == $v) | .shortRevision? // empty' 2>/dev/null \
      | head -n1 || true)
    OFFSET=$((OFFSET + LIMIT))
    sleep 0.2
  done
fi

if [ -z "$REV" ]; then
  echo "::error::'$VER' の changeset を解決できません。バージョン表記 (例: 6000.0.32f1) を確認してください。表記が正しい場合は .github/unity-versions.tsv の更新か Unity Release API の応答を確認してください" >&2
  exit 1
fi

echo "$REV"
