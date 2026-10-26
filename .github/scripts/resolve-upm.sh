#!/usr/bin/env bash
# =============================================================================
# UPM パッケージ解決スクリプト
# -----------------------------------------------------------------------------
# 使い方: resolve-upm.sh <EditorVersion 例: 6000.0.32f1> <プロジェクトディレクトリ>
#
# Unity 公式レジストリ (packages.unity.com) のメタデータに含まれる
# 各パッケージバージョンの要求最小 Editor バージョン (`unity` フィールド) を参照し、
# 「対象 Editor で使える中で最も新しい安定版」を選んで
# <プロジェクトディレクトリ>/Packages/manifest.json を再生成する。
# packages-lock.json は削除し、Unity 初回起動時に再生成させる。
#
# ※ 対象は公式レジストリのパッケージのみ。git URL 直指定や OpenUPM 等の
#    サードパーティパッケージは各自の判断で追加・管理すること。
# =============================================================================
set -euo pipefail

EDITOR_VER="$1"
PROJECT_DIR="$2"
EDITOR_MM=$(echo "$EDITOR_VER" | awk -F. '{print $1"."$2}')  # 例: 6000.0

# ─────────────────────────────────────────────
# プロジェクトに含めたい公式パッケージはここに列挙する
# ─────────────────────────────────────────────
PACKAGES="com.unity.inputsystem com.unity.test-framework com.unity.pipeline"
# 安定版が未公開で pre/exp 版を採用するパッケージ (update-manifest.sh と揃えること)
# com.unity.pipeline は Unity CLI からエディタを操作するために必要
PRERELEASE_PACKAGES="com.unity.pipeline"

mkdir -p "$PROJECT_DIR/Packages"
{
  echo '{ "dependencies": {'
  FIRST=true
  for pkg in $PACKAGES; do
    ALLOW_PRE=false
    case " $PRERELEASE_PACKAGES " in *" $pkg "*) ALLOW_PRE=true ;; esac
    ver=$(curl -sf "https://packages.unity.com/$pkg" | jq -r --arg em "$EDITOR_MM" --argjson pre "$ALLOW_PRE" '
      def mm: tostring | split(".") | [(.[0] // "0" | tonumber? // 0), (.[1] // "0" | tonumber? // 0)];
      .versions? // {} | if type == "object" then . else {} end | to_entries
      | map(select($pre or (.key | test("-") | not)))              # pre/exp 版を除外 (PRERELEASE_PACKAGES は除く)
      | map(select(((.value.unity? // "0.0") | mm) <= ($em | mm)))  # Editor 要求を満たす版のみ
      | map(.key)
      # 本体バージョン順。同じ本体なら安定版 > pre/exp 版、pre/exp 同士は文字列順
      | sort_by([(split("-")[0] | split(".") | map(tonumber? // 0)), (if test("-") then 0 else 1 end), .])
      | last // empty
    ' 2>/dev/null || true)
    if [ -z "$ver" ]; then
      echo "::error::$pkg に Unity $EDITOR_VER 対応バージョンが見つかりません" >&2
      exit 1
    fi
    $FIRST || echo ','
    printf '    "%s": "%s"' "$pkg" "$ver"
    FIRST=false
  done
  echo ''
  echo '  } }'
} > "$PROJECT_DIR/Packages/manifest.json"
rm -f "$PROJECT_DIR/Packages/packages-lock.json"
