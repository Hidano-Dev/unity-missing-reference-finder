#!/usr/bin/env bash
# =============================================================================
# UPM manifest その場更新スクリプト
# -----------------------------------------------------------------------------
# 使い方: update-manifest.sh <EditorVersion 例: 6000.0.32f1> <プロジェクトディレクトリ>
#
# 既存の <プロジェクトディレクトリ>/Packages/manifest.json を「その場で」更新する
# (resolve-upm.sh と異なり再生成はしない。scopedRegistries やサードパーティは温存)。
#
#   - 対象: com.unity.* の公式レジストリ (packages.unity.com) パッケージのみ。
#     各パッケージの要求最小 Editor バージョン (`unity` フィールド) を参照し、
#     「対象 Editor で使える中で最も新しい安定版」へ書き換える。
#   - 除外: com.unity.modules.* / com.unity.feature.* (Editor 組み込み)、
#     git URL・file: 参照、com.unity.* 以外 (OpenUPM 等のサードパーティ)。
#   - 現行バージョンがレジストリに存在しないパッケージ (URP や uGUI など、
#     近年の版がレジストリに公開されない Editor 同梱系) は誤った旧版への
#     ダウングレードを避けるため warning を出してスキップする。
#   - 適合版を解決できないパッケージも warning を出して現行バージョンを維持する。
#   - packages-lock.json は削除し、Unity 初回起動時に再生成させる。
#   - PRERELEASE_PACKAGES に列挙したパッケージは安定版が未公開のため、
#     pre/exp 版も候補に含める (例: Unity CLI 連携用の com.unity.pipeline)。
# =============================================================================
set -euo pipefail

EDITOR_VER="$1"
PROJECT_DIR="$2"
MANIFEST="$PROJECT_DIR/Packages/manifest.json"
EDITOR_MM=$(echo "$EDITOR_VER" | awk -F. '{print $1"."$2}')  # 例: 6000.0
# 安定版が未公開で pre/exp 版を採用するパッケージ (resolve-upm.sh と揃えること)
PRERELEASE_PACKAGES="com.unity.pipeline"

if [ ! -f "$MANIFEST" ]; then
  echo "::error::$MANIFEST が見つかりません" >&2
  exit 1
fi

UPDATES='{}'
# tr -d '\r' : Windows 環境の jq が CRLF を出力しても壊れないようにする
for pkg in $(jq -r '.dependencies | keys[]' "$MANIFEST" | tr -d '\r'); do
  case "$pkg" in
    com.unity.modules.*|com.unity.feature.*) continue ;;
    com.unity.*) ;;
    *) continue ;;
  esac
  cur=$(jq -r --arg p "$pkg" '.dependencies[$p]' "$MANIFEST" | tr -d '\r')
  case "$cur" in
    file:*|git*|http*|ssh:*|*/*) continue ;;  # レジストリ外参照はそのまま
  esac
  META=$(curl -sf "https://packages.unity.com/$pkg" || true)
  if [ -z "$META" ]; then
    echo "::warning::$pkg: 公式レジストリから取得できないため現行 ($cur) を維持します"
    continue
  fi
  # 現行バージョンがレジストリに無い = Editor 同梱系 (URP 11+ 等は未公開)。
  # レジストリ基準で選ぶと大幅な旧版へ誤ダウングレードするためスキップする。
  CUR_KNOWN=$(echo "$META" | jq -r --arg c "$cur" '.versions? // {} | has($c)' 2>/dev/null | tr -d '\r' || echo false)
  if [ "$CUR_KNOWN" != "true" ]; then
    echo "::warning::$pkg: 現行 $cur がレジストリに存在しない (Editor 同梱系) ため自動更新をスキップします。必要なら Unity で開いて調整してください"
    continue
  fi
  ALLOW_PRE=false
  case " $PRERELEASE_PACKAGES " in *" $pkg "*) ALLOW_PRE=true ;; esac
  ver=$(echo "$META" | jq -r --arg em "$EDITOR_MM" --argjson pre "$ALLOW_PRE" '
    def mm: tostring | split(".") | [(.[0] // "0" | tonumber? // 0), (.[1] // "0" | tonumber? // 0)];
    .versions? // {} | if type == "object" then . else {} end | to_entries
    | map(select($pre or (.key | test("-") | not)))               # pre/exp 版を除外 (PRERELEASE_PACKAGES は除く)
    | map(select(((.value.unity? // "0.0") | mm) <= ($em | mm)))  # Editor 要求を満たす版のみ
    | map(.key)
    # 本体バージョン順。同じ本体なら安定版 > pre/exp 版、pre/exp 同士は文字列順
    | sort_by([(split("-")[0] | split(".") | map(tonumber? // 0)), (if test("-") then 0 else 1 end), .])
    | last // empty
  ' 2>/dev/null | tr -d '\r' || true)
  if [ -z "$ver" ]; then
    echo "::warning::$pkg: Unity $EDITOR_VER の適合バージョンを解決できないため現行 ($cur) を維持します"
    continue
  fi
  if [ "$ver" != "$cur" ]; then
    echo "$pkg: $cur -> $ver"
    UPDATES=$(jq -nc --argjson u "$UPDATES" --arg p "$pkg" --arg v "$ver" '$u + {($p): $v}')
  fi
done

if [ "$UPDATES" != '{}' ]; then
  TMP=$(mktemp)
  jq --argjson u "$UPDATES" '.dependencies += $u' "$MANIFEST" > "$TMP"
  mv "$TMP" "$MANIFEST"
fi

rm -f "$PROJECT_DIR/Packages/packages-lock.json"
