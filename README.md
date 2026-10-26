# Unity プロジェクトテンプレート 仕様・運用手順書

> **この README はテンプレートリポジトリ専用です。** テンプレートから生成されたリポジトリでは、初期化ワークフローによって生成先用のスリムな README (`.github/PROJECT_README.md`) に自動で差し替えられます。

## 1. 概要

GitHub のテンプレートリポジトリ機能を使い、新規 Unity プロジェクトのリポジトリを自動セットアップする仕組みです。テンプレートには**設定済みの Unity プロジェクト (`TemplateProject/`)** を同梱しており、パッケージ構成 (manifest.json の手動キュレーション・scopedRegistries)・ProjectSettings・URP 設定などをそのまま引き継ぎます。鮮度が必要なもの (Unity の changeset、バージョン切替時の UPM 適合解決) はリポジトリ生成時の GitHub Actions で解決します。

AI エージェントからの Unity 操作には公式の [Unity CLI](https://unity.com/) (`unity` コマンド) を標準ツールとして使います。各プロジェクトには Editor を CLI から操作するための `com.unity.pipeline` パッケージを入れてあり、Claude Code / Codex 向けの `unity-cli` skill をリポジトリ直下に同梱しています。

本テンプレートは SDD (仕様駆動開発) などの開発ワークフローには依存しません。SDD を使う場合は、生成したリポジトリに [unity-sdd-kit](https://github.com/Hidano-Dev/unity-sdd-kit) を後から導入します。

リポジトリは**マルチプロジェクト構成**を前提とします。リポジトリ直下に Assets 等は置かず、プロジェクトごとにディレクトリを設けます。初期化時には同梱の `TemplateProject/` が**リポジトリ名にリネーム** (ディレクトリ名 + ProjectSettings の productName) され、以後は Actions から既存プロジェクトの更新 (名前・Unity バージョン) とプロジェクトの追加を実行できます。**最初のプロジェクトは初期化で自動作成されるため、Add Another Project を実行する必要はありません** (名前どおり、2 つ目以降を増やすときに使います)。

Unity Editor は運用安定化のため標準バージョン (ステークホルダー間で最も使われている安定版) に固定し、UPM パッケージは各プロジェクトの Editor バージョンに適合する最新版を採用します。Editor バージョンの上げ下げはプロジェクト単位で、生成後のリポジトリの Actions タブ (Update Project) から誰でも実行できます。

## 2. ファイル構成

```
(テンプレートリポジトリ)
├─ .github/
│   ├─ workflows/
│   │   ├─ unity-versions-update.yml   … バージョン:ハッシュ一覧の日次更新 (テンプレート側のみ稼働)
│   │   ├─ template-init.yml           … 生成時に 1 回だけ走る初期化 (実行後に自己削除)
│   │   ├─ update-project.yml          … 既存プロジェクトの更新 (名前 + productName / Unity バージョン。生成先に常駐)
│   │   └─ add-another-project.yml     … 2 つ目以降のプロジェクト追加 (生成先に常駐)
│   ├─ scripts/
│   │   ├─ resolve-changeset.sh        … Unity changeset の解決 (TSV → 公式 API。常駐)
│   │   ├─ set-unity-version.sh        … 既存プロジェクトのバージョン切替 (常駐)
│   │   ├─ update-manifest.sh          … manifest.json の公式パッケージその場更新 (常駐)
│   │   ├─ rename-project.sh           … ディレクトリ名 + productName の一括リネーム (常駐)
│   │   ├─ setup-project.sh            … 新規プロジェクト骨組み構成 (常駐)
│   │   └─ resolve-upm.sh              … 骨組み用 manifest.json の生成 (常駐)
│   ├─ PROJECT_README.md               … 生成先用のスリム README (初期化時に README.md へ差し替え)
│   └─ unity-versions.tsv              … バージョン<TAB>changeset<TAB>stream の一覧 (自動生成)
├─ .claude/skills/unity-cli/           … Unity CLI の agent skill (Claude Code 用。生成先にもそのまま入る)
├─ .agents/skills/unity-cli/           … 同上 (Codex 用。中身は .claude 側と同一)
├─ TemplateProject/                    … 設定済みの同梱 Unity プロジェクト (初期化時にリネームされる)
│   ├─ Assets/Settings/                … URP 設定アセット等
│   ├─ Packages/manifest.json          … 手動キュレーションのパッケージ構成 + scopedRegistries
│   │                                    (Unity CLI 連携用の com.unity.pipeline を含む)
│   └─ ProjectSettings/                … ProjectSettings.asset, ProjectVersion.txt ほか
├─ .gitignore                          … Unity 生成物の除外 (全プロジェクトに適用)
├─ CLAUDE.md                           … 開発メモ (Unity CLI の使い方・-automated など。生成先にもコピーされる)
└─ README.md                          … 本書 (テンプレート専用。生成先では上記に置換される)

(生成されたリポジトリ ※初期化後)
├─ .github/                            … 常駐ワークフロー・スクリプト・TSV
├─ .claude/skills/ .agents/skills/     … Unity CLI の agent skill
├─ <リポジトリ名>/                      … TemplateProject のリネーム (以後 Actions で追加可能)
├─ .gitignore
├─ CLAUDE.md                           … 開発メモ (テンプレートからコピー)
└─ README.md                           … 生成先用スリム README
```

## 3. 各ワークフロー・スクリプトの仕様

### 3.1 unity-versions-update.yml (テンプレート側・日次)

毎日 06:00 JST に Unity 公式 Editor Release API (認証不要) を全ページ取得し、`.github/unity-versions.tsv` を再生成します。差分がある時だけコミットするため、Unity のリリースが無い日はリポジトリは変化しません。全件を毎回取り直す冪等な作りなので、実行が数日止まっても次回で自己修復します。`is_template == true` の条件により、生成先リポジトリにコピーされても空振りします (さらに初期化時に削除されます)。

補足: GitHub の仕様でスケジュールワークフローはリポジトリが 60 日間無更新だと自動停止しますが、本ワークフロー自身が TSV 更新コミットを打つため、Unity のリリースが続く限り実質的に稼働し続けます。

### 3.2 template-init.yml (生成先で 1 回だけ)

テンプレートから新規リポジトリを生成すると、その最初の push をトリガーに実行されます。処理内容は次の 2 つです。

1. **同梱プロジェクトのリネーム** — `rename-project.sh` で `TemplateProject/` をリポジトリ名へ `git mv` し、ProjectSettings の productName もリポジトリ名に書き換えます。名前を変えたい場合は、初期化後に Update Project を実行するか `env: PROJECT_DIR` を書き換えます。
2. **自己削除とコミット** — `template-init.yml` と `unity-versions-update.yml` を削除し、テンプレート専用の README を生成先用 (`.github/PROJECT_README.md`) に差し替えたうえで、全変更を `chore: initialize from template` としてコミット・push します。`update-project.yml` / `add-another-project.yml` / scripts は以後も使うため残します。

Unity バージョンとパッケージ構成は**同梱 TemplateProject のものがそのまま使われ、初期化時には変換されません**。fork したテンプレートで TemplateProject を好みのバージョン・パッケージ構成にしておけば、それがそのまま生成先の初期状態になります。採用された Unity バージョンとプロジェクトディレクトリは Actions の実行サマリーに表示されます。

デバッグ時は `workflow_dispatch` (手動実行) を使うと自己削除がスキップされるため、テスト用リポジトリ 1 つで修正→再実行のループを回せます。

### 3.3 update-project.yml (生成先に常駐)

Actions タブから手動実行し、リポジトリ直下の**既存** Unity プロジェクトを更新します。入力は次の 2 つで、どちらか一方でも両方同時でも指定できます (両方空欄はエラー)。

- **`name`** — ディレクトリ名と ProjectSettings.asset の productName を、入力した 1 つの名前へまとめて変更します (`rename-project.sh`)。
- **`version`** — ProjectVersion.txt を書き換え、manifest.json の公式 UPM パッケージを対象 Editor 適合版へその場更新します (`set-unity-version.sh`)。changeset は TSV → API の順で自動解決するため、**利用者がハッシュを調べる必要はありません**。存在しないバージョン (タイポ含む) はエラーで停止し、壊れた設定がコミットされることはありません。現行と同じバージョンなら何も変換しません。

両方指定した場合はリネーム → バージョン変更の順に処理し、1 つのコミットにまとめます。

対象プロジェクトは `project` 入力を空欄にすると自動検出します (プロジェクトが 1 つだけの通常運用ではこれで足ります)。プロジェクトを複数追加している場合は候補一覧を表示してエラー停止するので、その中から `project` 入力にディレクトリ名を指定して再実行してください。存在しないディレクトリを指定した場合も候補一覧付きでエラー停止します。

> **補足**: GitHub の `workflow_dispatch` はドロップダウン (`type: choice`) の選択肢を YAML に静的に書く必要があり、リポジトリ内のディレクトリから動的に生成できません。選択肢を自動更新するにはワークフローファイル自体を Actions から書き換える必要があり、それは `GITHUB_TOKEN` では拒否される (PAT が必要) ため、自動検出 + 候補提示で代替しています。

### 3.4 add-another-project.yml (生成先に常駐)

Actions タブから手動実行し、リポジトリ直下に新しい Unity プロジェクトディレクトリ (`Assets/.gitkeep`, `Packages/manifest.json`, `ProjectSettings/ProjectVersion.txt`) を追加します。changeset は TSV → API の順で自動解決するため、**利用者がハッシュを調べる必要はありません**。存在しないバージョン (タイポ含む) はエラーで停止し、同名ディレクトリが既に存在する場合もエラーで停止します。同梱 TemplateProject のコピーではなく最小構成の骨組みです (対象パッケージは `resolve-upm.sh` の `PACKAGES` 変数で定義)。

**最初のプロジェクトは Template Init がリポジトリ名で自動作成している**ため、生成直後にリポジトリ名と同じ名前で実行すると「既に存在します」で停止します。このワークフローは 2 つ目以降のプロジェクトを増やすためのものです。

### 3.5 Unity CLI 連携

AI エージェントからの Unity 操作 (Editor の起動・ライブ操作・テスト・ビルド) には公式の Unity CLI (`unity` コマンド) を使います。

- **CLI 本体** — マシンごとにインストールする単体バイナリで、リポジトリには含みません。インストール手順は同梱 skill の「Step 1: Install the CLI」にあります。
- **`com.unity.pipeline` パッケージ** — CLI が起動中の Editor に接続して操作するためのパッケージ (Unity 6.0 以降)。同梱 TemplateProject に入っているほか、Add Another Project で追加するプロジェクトにも `resolve-upm.sh` の `PACKAGES` 経由で入ります。現時点では実験版 (`-exp`) しか公開されていないため、スクリプトの `PRERELEASE_PACKAGES` で pre/exp 版の採用を許可しています。
- **agent skill** — `unity skill install claude-code --local` で生成した `unity-cli` skill をリポジトリ直下の `.claude/skills/unity-cli/` に置き、同じ内容を Codex 用に `.agents/skills/unity-cli/` にも置いています。マルチプロジェクト構成でもリポジトリ直下に 1 セットだけ置けば足ります (CLI はコマンド引数でプロジェクトを指定するため)。

### 3.6 共通スクリプト

| スクリプト | 役割 | 呼び出し元 |
|---|---|---|
| `resolve-changeset.sh <バージョン>` | changeset を TSV → 公式 API の順で解決し stdout へ出力 | setup-project.sh / set-unity-version.sh |
| `set-unity-version.sh <ディレクトリ> <バージョン>` | 既存プロジェクトの ProjectVersion.txt 書き換え + manifest その場更新 | update-project.yml |
| `update-manifest.sh <バージョン> <ディレクトリ>` | 既存 manifest.json の公式パッケージのみを Editor 適合版へ書き換え (再生成しない) | set-unity-version.sh |
| `rename-project.sh <現ディレクトリ> <新名>` | ディレクトリ名 (git mv) + productName の一括リネーム (同名なら productName のみ) | template-init.yml / update-project.yml |
| `setup-project.sh <ディレクトリ> <バージョン>` | 新規プロジェクトの骨組み構成 (Assets/Packages/ProjectSettings) | add-another-project.yml |
| `resolve-upm.sh <バージョン> <ディレクトリ>` | 骨組み用 manifest.json を `PACKAGES` 変数の構成で生成 | setup-project.sh |

パッケージの適合判定は共通で、Unity 公式レジストリのメタデータにある要求最小 Editor バージョン (`unity` フィールド) を参照して「対象 Editor で使える中で最も新しい安定版」を選定します。プレリリース版 (-pre / -exp) は除外します。ただし `PRERELEASE_PACKAGES` 変数 (resolve-upm.sh / update-manifest.sh の両方で同じ値にする) に列挙したパッケージは、安定版が未公開のため pre/exp 版も候補に含めます (現在は `com.unity.pipeline` のみ)。

`update-manifest.sh` は manifest.json の**公式 UPM パッケージ (`com.unity.*`) のみ**を対象 Editor 適合版へその場更新し、`com.unity.modules.*` / `com.unity.feature.*`、git URL・file: 参照、OpenUPM 等のサードパーティパッケージと scopedRegistries には触れません。現行バージョンが公式レジストリに存在しないパッケージ (URP 11+ や uGUI 2.x など **Editor 同梱系**) は誤ダウングレードを避けるため warning を出してスキップし、適合版を解決できなかったパッケージも現行バージョンのまま維持します。`packages-lock.json` は削除され、Unity 初回起動時に再生成されます。

## 4. 運用手順

### 4.1 新規リポジトリの開始

1. テンプレートリポジトリで「Use this template」→「Create a new repository」。
2. Actions タブで「Template Init」の完了 (緑) を確認する。数分で同梱プロジェクトがリポジトリ名にリネーム (ディレクトリ + productName) された初期化コミットが積まれます。
3. 初期化コミットが積まれた**後に** clone する (先に clone した場合は pull)。
4. Unity Hub の「Add project from disk」で**プロジェクトディレクトリ** (リポジトリ直下ではない) を指定して開く。標準バージョンが未インストールなら Hub がインストールを案内します (changeset 入りなので正確なビルドに誘導されます)。
5. 初回起動で生成される `Packages/packages-lock.json` をコミットしておくと、メンバー間でパッケージ解決が揃います。
6. AI エージェントから Unity を操作する場合は、各自のマシンに Unity CLI をインストールします (手順は `.claude/skills/unity-cli/SKILL.md` の「Step 1」)。Unity Hub の代わりに `unity open <プロジェクトディレクトリ>` で開くこともできます。
7. SDD (仕様駆動開発) を使う場合は、[unity-sdd-kit](https://github.com/Hidano-Dev/unity-sdd-kit) の手順で導入します。

### 4.2 既存プロジェクトの更新 (名前 / Unity バージョン)

**Unity バージョンの変更は、対象プロジェクトの初回起動前 (開始時) に行うことを想定しています。**

1. Actions タブ →「Update Project」→「Run workflow」。
2. 変えたい項目だけ入力して実行 (両方同時も可)。
   - `project`: 変更対象のプロジェクト名 (現在のディレクトリ名)。プロジェクトが 1 つなら空欄のままで自動検出されます。複数ある場合は候補一覧付きでエラー停止するので、その中から対象のディレクトリ名を入れて再実行してください。
   - `name`: 変更後のプロジェクト名 (ディレクトリ名と productName の両方に適用)。
   - `version`: 変更後の Unity バージョン (例: `6000.0.32f1`)。ハッシュは不要です。
3. 完了後に pull する。名前を変えた場合、Unity Hub には旧パスで登録されているため、新しいディレクトリを「Add project from disk」で指定し直してください。

利用可能なバージョンの一覧はリポジトリ内 `.github/unity-versions.tsv` (生成時点のスナップショット) か、テンプレートリポジトリ側の最新 TSV で確認できます。テンプレート側で TSV を更新しても**生成済みリポジトリの TSV には反映されません**が、TSV に無いバージョン (生成後にリリースされた新バージョン等) は Unity 公式 API の全件走査フォールバックで自動解決されるため、通常は意識する必要はありません。

> **注意**: バージョン変更で書き換わるのは ProjectVersion.txt と manifest.json のみで、`Assets/` 内のシリアライズ済みデータは変換されません。開発が進んだ後に大きくバージョンを下げる場合は、シーン・プレハブの非互換が起きうるためブランチで検証してから main に取り込んでください。

### 4.3 プロジェクトの追加 (2 つ目以降)

最初のプロジェクトは初期化時にリポジトリ名で自動作成されているため、この手順は不要です。別のプロジェクトを増やしたいときだけ実行します。

1. Actions タブ →「Add Another Project」→「Run workflow」。
2. 新しいプロジェクトディレクトリ名 (既存と重複しない名前) とバージョン番号 (例: `6000.0.32f1`) を入力して実行。ハッシュは不要です。
3. 完了後に pull し、Unity Hub でそのディレクトリを開く。

### 4.4 テンプレート自体のメンテナンス

- **同梱プロジェクトの更新**: `TemplateProject/` を Unity で直接開いて編集し、コミットします (パッケージ構成・ProjectSettings・共通アセットなど)。ここが生成先の初期状態になります。
- **標準バージョンの変更**: `TemplateProject/` を対象バージョンの Unity で開き直してコミットします。ワークフロー側にバージョン設定は無いため、`TemplateProject/ProjectSettings/ProjectVersion.txt` が唯一の基準です。
- **プロジェクト追加時の骨組みパッケージ構成の変更**: `resolve-upm.sh` の `PACKAGES` 変数を編集します。
- **Unity CLI skill の更新**: Unity CLI を最新化 (`unity upgrade`) してから、リポジトリ直下で `unity skill install claude-code --local --yes` を実行し、生成された `.claude/skills/unity-cli/` を `.agents/skills/unity-cli/` にもコピーしてコミットします。
- **`com.unity.pipeline` の更新**: `unity pipeline upgrade --project-path TemplateProject` を実行し、Unity で開いて packages-lock.json を更新してからコミットします。
- 上記以外の定期メンテナンスは不要です (TSV 更新は自動)。

## 5. 前提条件・トラブルシューティング

| 事象 | 原因 / 対処 |
|---|---|
| 生成直後に初期化が走らない | Organization 設定で Actions が無効の可能性。有効化のうえ「Template Init」を手動実行する (手動実行は自己削除しないため、完了後にワークフロー 2 本の削除と README 差し替えを手で行う) |
| bot の push が拒否される | リポジトリ設定の Workflow permissions を「Read and write permissions」にする。ワークフローファイル削除まで拒否される組織設定の場合は、Fine-grained PAT (contents + workflows) を secret に置き checkout の `token` に渡す |
| 「〜の changeset を解決できません」エラー | バージョン表記の確認 (例: `6000.0.32f1` のようにサフィックスまで含める)。表記が正しいのに失敗する場合は Unity Release API 側の問題の可能性があるため時間をおいて再実行する |
| 「〜は Unity プロジェクトとして見つかりません」エラー | Update Project の `project` 入力のタイポ。エラーに表示される候補一覧から選び直す |
| 「プロジェクトが複数あるため対象を自動判別できません」エラー | Update Project の `project` 入力が空欄で、プロジェクトが複数ある。エラーに表示される候補一覧から対象を `project` 入力に指定して再実行する |
| 「name と version のどちらも空欄です」エラー | Update Project で変更項目を何も入力していない。少なくとも一方を入力する |
| 「〜は既に存在します」エラー | Add Another Project で既存名 (多くは初期化時に自動作成されたリポジトリ名のプロジェクト) を指定している。追加なら別名を指定し、既存プロジェクトの名前・バージョン変更なら Update Project を使う |
| 「〜に対応バージョンが見つかりません」エラー | Add Another Project で指定した Editor が古すぎて対象パッケージの適合版が存在しない。Editor バージョンか `PACKAGES` の構成を見直す |
| 「〜の適合バージョンを解決できない / レジストリに存在しない」warning | Update Project (または Template Init) のバージョン切替で該当パッケージだけ自動更新をスキップした (それ以外は正常に更新されている)。Editor 同梱系 (URP 等) は Unity で開いた際に調整する |
| clone したのにプロジェクトディレクトリがリポジトリ名になっていない (`TemplateProject/` のまま) | 初期化コミット前に clone している。`git pull` する |
| `unity status` / `unity command` が Editor に接続できない | プロジェクトに `com.unity.pipeline` が入っているか確認する (無ければ `unity pipeline install --project-path <ディレクトリ>`)。入っているのに繋がらない場合はコンパイルエラーで Safe Mode になっている可能性があるため、`unity pipeline list` で確認してエラーを直す |
