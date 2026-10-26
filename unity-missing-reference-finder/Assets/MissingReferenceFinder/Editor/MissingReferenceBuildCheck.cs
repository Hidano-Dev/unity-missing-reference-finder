using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Hidano.MissingReferenceFinder.Editor
{
    /// <summary>
    /// ビルド開始前に Build Settings に登録された各シーンファイル (.unity) と、
    /// それらが依存する Prefab ファイル (.prefab) を直接読み、
    /// 参照先アセットの GUID が AssetDatabase に存在しないもの (= Missing) を検出してビルドを中止する。
    ///
    /// ビルド中に読み込まれるシーンでは Missing 参照が解決済みになっており instanceID ベースの判定が効かないため、
    /// メモリ上の走査 (<see cref="MissingReferenceScanner"/>) ではなくファイル上の GUID を検査する。
    ///
    /// ログには「どのファイルの / どの GameObject (階層パス) の / 上から何番目のどのコンポーネントの / どのフィールド」が
    /// 壊れているかと、その対処法を出力する。Prefab インスタンス内のオブジェクトは元 Prefab を読み込んで名前を解決する。
    ///
    /// 有効/無効はメニュー (Hidano/Tools/Check Missing References On Build) または
    /// <see cref="MissingReferenceFinderWindow"/> から切り替えられる。
    /// </summary>
    public sealed class MissingReferenceBuildCheck : IPreprocessBuildWithReport
    {
        private const string ToggleMenuPath = "Tools/Hidano/Check Missing References On Build";
        private const string RunMenuPath = "Tools/Hidano/Run Pre-Build Missing Reference Check";
        private const string EditorPrefsKey = "Hidano.MissingReferenceBuildCheck.Enabled";
        private const string LogPrefix = "[MissingReferenceBuildCheck]";

        /// <summary>階層パス解決時の無限ループ防止用。</summary>
        private const int MaxPathDepth = 64;

        // 例: "--- !u!114 &123456789" / "--- !u!1 &123 stripped"
        private static readonly Regex DocumentHeaderRegex =
            new Regex(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);

        // 例: "  prefab: {fileID: 123, guid: 0123456789abcdef0123456789abcdef, type: 3}"
        private static readonly Regex GuidRegex =
            new Regex(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

        // 例: "  m_GameObject: {fileID: 123456}"
        private static readonly Regex GameObjectRefRegex =
            new Regex(@"^\s*m_GameObject:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // 例: "  m_CorrespondingSourceObject: {fileID: 123, guid: ..., type: 3}"
        private static readonly Regex CorrespondingSourceRegex =
            new Regex(@"^\s*m_CorrespondingSourceObject:\s*\{fileID:\s*(-?\d+)", RegexOptions.Compiled);

        // 例: "  m_PrefabInstance: {fileID: 123}"
        private static readonly Regex PrefabInstanceRefRegex =
            new Regex(@"^\s*m_PrefabInstance:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // Transform / RectTransform の親 Transform。例: "  m_Father: {fileID: 123}"
        private static readonly Regex FatherRegex =
            new Regex(@"^\s*m_Father:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // GameObject が持つコンポーネント一覧。例: "  - component: {fileID: 123}"
        private static readonly Regex ComponentListItemRegex =
            new Regex(@"^\s*-\s*component:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // MonoBehaviour のスクリプト。例: "  m_Script: {fileID: 11500000, guid: ..., type: 3}"
        private static readonly Regex ScriptRegex =
            new Regex(@"^\s*m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

        // PrefabInstance の元 Prefab。例: "  m_SourcePrefab: {fileID: 100100000, guid: ..., type: 3}"
        private static readonly Regex SourcePrefabRegex =
            new Regex(@"^\s*m_SourcePrefab:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

        // PrefabInstance の親 Transform。例: "    m_TransformParent: {fileID: 123}"
        private static readonly Regex TransformParentRegex =
            new Regex(@"^\s*m_TransformParent:\s*\{fileID:\s*(-?\d+)\}", RegexOptions.Compiled);

        // PrefabInstance の m_Modifications 各要素。例: "    - target: {fileID: 123, guid: ..., type: 3}"
        private static readonly Regex ModificationTargetRegex =
            new Regex(@"^\s*-\s*target:\s*\{fileID:\s*(-?\d+)", RegexOptions.Compiled);

        private static readonly Regex ModificationPropertyPathRegex =
            new Regex(@"^\s*propertyPath:\s*(.*)$", RegexOptions.Compiled);

        private static readonly Regex ModificationValueRegex =
            new Regex(@"^\s*value:\s*(.*)$", RegexOptions.Compiled);

        // 例: "  m_Name: Foo"
        private static readonly Regex NameRegex =
            new Regex(@"^\s*m_Name:\s*(.*)$", RegexOptions.Compiled);

        // ドキュメント直下 (インデント 2) のキー。MonoBehaviour のフィールド名列挙に使う
        private static readonly Regex TopLevelKeyRegex =
            new Regex(@"^  ([A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Compiled);

        private static readonly Regex ArrayIndexRegex =
            new Regex(@"\[\d*\]", RegexOptions.Compiled);

        // Unity の SerializedProperty パス表記 "_list.Array.data[3]" → "_list[3]"
        private static readonly Regex ArrayDataRegex =
            new Regex(@"\.Array\.data\[", RegexOptions.Compiled);

        /// <summary>
        /// Unity YAML の行をインデントに基づいて追跡し、現在行の完全パス ("_entries[1]._material") と末尾キーを返す。
        /// Unity は list 要素を親キーと同じインデントの "- " で書き、要素の中身はその 2 桁右に置く。
        /// </summary>
        private sealed class YamlPathTracker
        {
            private sealed class Frame
            {
                public int Indent;
                public string Key;
                public int Index = -1;
            }

            // 例: "    - target: {...}" → indent=4, item="- ", rest="target: {...}"
            private static readonly Regex LineRegex =
                new Regex(@"^( *)(- +)?(.*)$", RegexOptions.Compiled);

            private static readonly Regex RestKeyRegex =
                new Regex(@"^([A-Za-z_][A-Za-z0-9_]*):", RegexOptions.Compiled);

            private readonly List<Frame> _frames = new List<Frame>();

            public string Path { get; private set; } = "?";
            public string LeafKey { get; private set; } = "?";

            public void Reset()
            {
                _frames.Clear();
                Path = "?";
                LeafKey = "?";
            }

            public void Feed(string line)
            {
                var match = LineRegex.Match(line);
                var indent = match.Groups[1].Length;
                var isListItem = match.Groups[2].Success;
                var rest = match.Groups[3].Value;

                // より深い階層から戻ってきたので、子フレームを捨てる
                PopWhile(frame => frame.Indent > indent);

                if (isListItem)
                {
                    if (_frames.Count > 0 && _frames[_frames.Count - 1].Indent == indent)
                    {
                        _frames[_frames.Count - 1].Index++;
                    }

                    indent += match.Groups[2].Length;
                }

                var keyMatch = RestKeyRegex.Match(rest);
                if (keyMatch.Success)
                {
                    PopWhile(frame => frame.Indent >= indent);

                    // インデント 0 は "MonoBehaviour:" のようなクラス名行なのでパスには含めない
                    if (indent > 0)
                    {
                        _frames.Add(new Frame { Indent = indent, Key = keyMatch.Groups[1].Value });
                    }
                }

                var builder = new StringBuilder();
                foreach (var frame in _frames)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('.');
                    }

                    builder.Append(frame.Key);
                    if (frame.Index >= 0)
                    {
                        builder.Append('[').Append(frame.Index).Append(']');
                    }
                }

                Path = builder.Length > 0 ? builder.ToString() : "?";
                LeafKey = _frames.Count > 0 ? _frames[_frames.Count - 1].Key : "?";
            }

            private void PopWhile(Func<Frame, bool> predicate)
            {
                while (_frames.Count > 0 && predicate(_frames[_frames.Count - 1]))
                {
                    _frames.RemoveAt(_frames.Count - 1);
                }
            }
        }

        /// <summary>Unity 組み込みリソースや null を表す GUID。AssetDatabase 上に存在しないので除外する。</summary>
        private static readonly HashSet<string> BuiltinGuids = new HashSet<string>
        {
            "0000000000000000f000000000000000", // Resources/unity_builtin_extra
            "0000000000000000e000000000000000", // Library/unity default resources
            "00000000000000000000000000000000", // null (m_SceneGUID など)
        };

        /// <summary>
        /// Prefab インスタンス由来の内部参照。元 Prefab が消えている場合は PrefabInstance 側で 1 件にまとめて報告するので除外する。
        /// </summary>
        private static readonly HashSet<string> IgnoredKeys = new HashSet<string>
        {
            "m_CorrespondingSourceObject",
        };

        /// <summary>MonoBehaviour が標準で持つキー。Missing Script の手掛かりとしてフィールド名を列挙する際に除外する。</summary>
        private static readonly HashSet<string> StandardMonoBehaviourKeys = new HashSet<string>
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
            "m_Enabled",
            "m_EditorHideFlags",
            "m_Script",
            "m_Name",
            "m_EditorClassIdentifier",
            "serializedVersion",
        };

        /// <summary>組み込みコンポーネントの ClassID → 型名。実オブジェクトを解決できない場合の表示用。</summary>
        private static readonly Dictionary<int, string> BuiltinClassNames = new Dictionary<int, string>
        {
            { 1, "GameObject" }, { 4, "Transform" }, { 20, "Camera" }, { 21, "Material" }, { 23, "MeshRenderer" },
            { 33, "MeshFilter" }, { 54, "Rigidbody" }, { 64, "MeshCollider" }, { 65, "BoxCollider" },
            { 81, "AudioListener" }, { 82, "AudioSource" }, { 95, "Animator" }, { 96, "TrailRenderer" },
            { 108, "Light" }, { 111, "Animation" }, { 114, "MonoBehaviour" }, { 120, "LineRenderer" },
            { 135, "SphereCollider" }, { 136, "CapsuleCollider" }, { 137, "SkinnedMeshRenderer" },
            { 198, "ParticleSystem" }, { 199, "ParticleSystemRenderer" }, { 212, "SpriteRenderer" },
            { 215, "ReflectionProbe" }, { 218, "Terrain" }, { 220, "LightProbeGroup" }, { 222, "CanvasRenderer" },
            { 223, "Canvas" }, { 224, "RectTransform" }, { 225, "CanvasGroup" }, { 328, "VideoPlayer" },
            { 1001, "PrefabInstance" },
        };

        private const int GameObjectClassId = 1;
        private const int TransformClassId = 4;
        private const int MonoBehaviourClassId = 114;
        private const int RectTransformClassId = 224;
        private const int PrefabInstanceClassId = 1001;

        private const string SourcePrefabKey = "m_SourcePrefab";
        private const string ScriptKey = "m_Script";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EditorPrefsKey, true);
            set => EditorPrefs.SetBool(EditorPrefsKey, value);
        }

        public int callbackOrder => -100;

        [MenuItem(ToggleMenuPath)]
        private static void ToggleEnabled()
        {
            Enabled = !Enabled;
        }

        [MenuItem(ToggleMenuPath, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(ToggleMenuPath, Enabled);
            return true;
        }

        /// <summary>ビルドせずに同じチェックを手動実行する。</summary>
        [MenuItem(RunMenuPath)]
        private static void RunManually()
        {
            var missingCount = CheckBuildScenes(out _);
            if (missingCount == 0)
            {
                Debug.Log($"{LogPrefix} Missing 参照はありませんでした。");
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!Enabled)
            {
                return;
            }

            var missingCount = CheckBuildScenes(out var breakdown);
            if (missingCount > 0)
            {
                throw new BuildFailedException(
                    $"{LogPrefix} ビルド対象シーン (および依存 Prefab) に Missing 参照が {missingCount} 件あるためビルドを中止しました。\n" +
                    $"内訳: {breakdown}\n" +
                    "Console で \"[Missing\" を検索し、各エラーログを選択すると「GameObject / コンポーネント / フィールド / 対処」の詳細が表示されます。" +
                    "ログをクリックすると該当オブジェクトが選択されます (シーンを開いている場合)。\n" +
                    $"修正後は {RunMenuPath} でビルドせずに再確認できます。");
            }
        }

        /// <summary>Build Settings の有効なシーンと依存 Prefab を全て検査し、Missing の総数を返す。</summary>
        private static int CheckBuildScenes(out string breakdown)
        {
            var total = 0;
            var scenePaths = new List<string>();
            var prefabPaths = new List<string>();
            var prefabUsedBy = new Dictionary<string, List<string>>();
            var fileCounts = new List<(string Path, int Count)>();

            foreach (var buildScene in EditorBuildSettings.scenes)
            {
                if (!buildScene.enabled)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(buildScene.path) || !File.Exists(buildScene.path))
                {
                    Debug.LogWarning($"{LogPrefix} Build Settings に登録されたシーンが見つかりません: {buildScene.path}");
                    continue;
                }

                scenePaths.Add(buildScene.path);

                foreach (var dependency in AssetDatabase.GetDependencies(buildScene.path, true))
                {
                    if (!dependency.EndsWith(".prefab") || !File.Exists(dependency))
                    {
                        continue;
                    }

                    if (!prefabUsedBy.TryGetValue(dependency, out var usedBy))
                    {
                        usedBy = new List<string>();
                        prefabUsedBy.Add(dependency, usedBy);
                        prefabPaths.Add(dependency);
                    }

                    usedBy.Add(Path.GetFileNameWithoutExtension(buildScene.path));
                }
            }

            PrefabAssetCache.Clear();
            try
            {
                var index = 0;
                var totalFiles = scenePaths.Count + prefabPaths.Count;

                foreach (var scenePath in scenePaths)
                {
                    EditorUtility.DisplayProgressBar("Missing Reference Build Check", scenePath, (float)index++ / totalFiles);
                    var count = CheckYamlFile(scenePath, true, null);
                    if (count > 0)
                    {
                        fileCounts.Add((scenePath, count));
                    }

                    total += count;
                }

                foreach (var prefabPath in prefabPaths)
                {
                    EditorUtility.DisplayProgressBar("Missing Reference Build Check", prefabPath, (float)index++ / totalFiles);
                    var count = CheckYamlFile(prefabPath, false, prefabUsedBy[prefabPath]);
                    if (count > 0)
                    {
                        fileCounts.Add((prefabPath, count));
                    }

                    total += count;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                PrefabAssetCache.Clear();
            }

            var breakdownBuilder = new StringBuilder();
            foreach (var (path, count) in fileCounts)
            {
                if (breakdownBuilder.Length > 0)
                {
                    breakdownBuilder.Append(", ");
                }

                breakdownBuilder.Append(Path.GetFileName(path)).Append(": ").Append(count).Append(" 件");
            }

            breakdown = breakdownBuilder.Length > 0 ? breakdownBuilder.ToString() : "なし";

            var summary = $"{LogPrefix} 検査完了: {scenePaths.Count} シーン / {prefabPaths.Count} Prefab / Missing {total} 件";
            if (total > 0)
            {
                summary += $"\n内訳: {breakdown}";
            }

            Debug.Log(summary);
            return total;
        }

        // ------------------------------------------------------------------
        // YAML ドキュメント
        // ------------------------------------------------------------------

        private sealed class MissingEntry
        {
            /// <summary>フィールド名 (配列の場合は "name[index]")。PrefabInstance のオーバーライドの場合は propertyPath。</summary>
            public string Key;

            public string Guid;

            /// <summary>PrefabInstance のオーバーライドが指す元 Prefab 内のコンポーネント fileID。それ以外は 0。</summary>
            public long TargetFileId;
        }

        private sealed class DocumentInfo
        {
            public int ClassId;
            public long FileId;
            public string Name;
            public long GameObjectFileId;
            public bool IsStripped;
            public long CorrespondingSourceFileId;
            public long PrefabInstanceFileId;

            /// <summary>Transform / RectTransform の親 Transform fileID。ルートは 0。</summary>
            public long FatherFileId;

            /// <summary>PrefabInstance の親 Transform fileID。ルートは 0。</summary>
            public long TransformParentFileId;

            /// <summary>PrefabInstance の元 Prefab GUID。</summary>
            public string SourcePrefabGuid;

            /// <summary>MonoBehaviour のスクリプト GUID。</summary>
            public string ScriptGuid;

            /// <summary>GameObject が持つコンポーネント fileID (Inspector 表示順)。</summary>
            public readonly List<long> ComponentFileIds = new List<long>();

            /// <summary>MonoBehaviour に保存されているユーザー定義フィールド名。</summary>
            public readonly List<string> FieldNames = new List<string>();

            /// <summary>PrefabInstance の m_Modifications による m_Name 上書き (元 Prefab 内 fileID → 名前)。</summary>
            public readonly Dictionary<long, string> NameOverrides = new Dictionary<long, string>();

            public readonly List<MissingEntry> Missing = new List<MissingEntry>();

            public bool IsTransform => ClassId == TransformClassId || ClassId == RectTransformClassId;
        }

        /// <summary>シーンまたは Prefab ファイルを 1 つ検査し、Missing の数を返す。</summary>
        private static int CheckYamlFile(string assetPath, bool isScene, List<string> usedByScenes)
        {
            var context = new FileContext(assetPath, isScene, ParseYamlFile(assetPath), usedByScenes);

            var count = 0;
            foreach (var doc in context.Documents)
            {
                foreach (var entry in doc.Missing)
                {
                    count++;
                    Debug.LogError(context.BuildReport(doc, entry), context.GetContextObject(doc, entry));
                }
            }

            return count;
        }

        private static List<DocumentInfo> ParseYamlFile(string assetPath)
        {
            var documents = new List<DocumentInfo>();
            DocumentInfo current = null;
            var pathTracker = new YamlPathTracker();
            long modificationTarget = 0;
            string modificationPropertyPath = null;

            foreach (var line in File.ReadLines(assetPath))
            {
                var header = DocumentHeaderRegex.Match(line);
                if (header.Success)
                {
                    FinalizeDocument(current);
                    current = new DocumentInfo
                    {
                        ClassId = int.Parse(header.Groups[1].Value),
                        FileId = long.Parse(header.Groups[2].Value),
                        IsStripped = line.TrimEnd().EndsWith("stripped"),
                    };
                    documents.Add(current);
                    pathTracker.Reset();
                    modificationTarget = 0;
                    modificationPropertyPath = null;
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                switch (current.ClassId)
                {
                    case GameObjectClassId:
                    {
                        var name = NameRegex.Match(line);
                        if (name.Success)
                        {
                            current.Name = name.Groups[1].Value.Trim();
                        }

                        var component = ComponentListItemRegex.Match(line);
                        if (component.Success)
                        {
                            current.ComponentFileIds.Add(long.Parse(component.Groups[1].Value));
                        }

                        break;
                    }
                    case MonoBehaviourClassId:
                    {
                        var script = ScriptRegex.Match(line);
                        if (script.Success)
                        {
                            current.ScriptGuid = script.Groups[1].Value.ToLowerInvariant();
                        }

                        var topLevelKey = TopLevelKeyRegex.Match(line);
                        if (topLevelKey.Success && !StandardMonoBehaviourKeys.Contains(topLevelKey.Groups[1].Value))
                        {
                            current.FieldNames.Add(topLevelKey.Groups[1].Value);
                        }

                        break;
                    }
                    case PrefabInstanceClassId:
                    {
                        var target = ModificationTargetRegex.Match(line);
                        if (target.Success)
                        {
                            modificationTarget = long.Parse(target.Groups[1].Value);
                            modificationPropertyPath = null;
                        }

                        var propertyPath = ModificationPropertyPathRegex.Match(line);
                        if (propertyPath.Success)
                        {
                            modificationPropertyPath = propertyPath.Groups[1].Value.Trim();
                        }

                        if (modificationPropertyPath == "m_Name")
                        {
                            var value = ModificationValueRegex.Match(line);
                            if (value.Success)
                            {
                                current.NameOverrides[modificationTarget] = value.Groups[1].Value.Trim();
                            }
                        }

                        var transformParent = TransformParentRegex.Match(line);
                        if (transformParent.Success)
                        {
                            current.TransformParentFileId = long.Parse(transformParent.Groups[1].Value);
                        }

                        var sourcePrefab = SourcePrefabRegex.Match(line);
                        if (sourcePrefab.Success)
                        {
                            current.SourcePrefabGuid = sourcePrefab.Groups[1].Value.ToLowerInvariant();
                        }

                        break;
                    }
                }

                if (current.IsTransform)
                {
                    var father = FatherRegex.Match(line);
                    if (father.Success)
                    {
                        current.FatherFileId = long.Parse(father.Groups[1].Value);
                    }
                }

                // 所属 GameObject / Prefab インスタンス情報
                var goRef = GameObjectRefRegex.Match(line);
                if (goRef.Success)
                {
                    current.GameObjectFileId = long.Parse(goRef.Groups[1].Value);
                }

                var sourceRef = CorrespondingSourceRegex.Match(line);
                if (sourceRef.Success)
                {
                    current.CorrespondingSourceFileId = long.Parse(sourceRef.Groups[1].Value);
                }

                var prefabRef = PrefabInstanceRefRegex.Match(line);
                if (prefabRef.Success)
                {
                    current.PrefabInstanceFileId = long.Parse(prefabRef.Groups[1].Value);
                }

                // キー追跡 (インデントから "_entries[1]._material" のような完全パスを組み立てる)
                pathTracker.Feed(line);
                var key = pathTracker.LeafKey;

                if (IgnoredKeys.Contains(key))
                {
                    continue;
                }

                // GUID 参照
                var guidMatch = GuidRegex.Match(line);
                if (!guidMatch.Success)
                {
                    continue;
                }

                var guid = guidMatch.Groups[1].Value.ToLowerInvariant();
                if (BuiltinGuids.Contains(guid))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                {
                    continue;
                }

                var entry = new MissingEntry { Guid = guid };
                if (current.ClassId == PrefabInstanceClassId)
                {
                    if (key == "objectReference" && !string.IsNullOrEmpty(modificationPropertyPath))
                    {
                        // 元 Prefab は存在するが、オーバーライド値が消えたアセットを指している
                        entry.Key = modificationPropertyPath;
                        entry.TargetFileId = modificationTarget;
                    }
                    else
                    {
                        // m_Modifications の各 target と m_SourcePrefab は全て元 Prefab の GUID を指すので 1 件にまとめる
                        entry.Key = SourcePrefabKey;
                    }
                }
                else
                {
                    entry.Key = pathTracker.Path;
                }

                // 1 行 = 1 参照なので原則そのまま全て報告する。
                // 例外として元 Prefab 欠落 (m_SourcePrefab) は同じ GUID が何十行も並ぶため 1 件にまとめる
                if (entry.Key == SourcePrefabKey)
                {
                    var duplicate = false;
                    foreach (var existing in current.Missing)
                    {
                        if (existing.Key == SourcePrefabKey && existing.Guid == entry.Guid)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (duplicate)
                    {
                        continue;
                    }
                }

                current.Missing.Add(entry);
            }

            FinalizeDocument(current);
            return documents;
        }

        /// <summary>元 Prefab が消えている PrefabInstance では、同じ GUID を指すオーバーライド報告を m_SourcePrefab の 1 件に畳む。</summary>
        private static void FinalizeDocument(DocumentInfo doc)
        {
            if (doc == null || doc.ClassId != PrefabInstanceClassId)
            {
                return;
            }

            string missingSourceGuid = null;
            foreach (var entry in doc.Missing)
            {
                if (entry.Key == SourcePrefabKey)
                {
                    missingSourceGuid = entry.Guid;
                    break;
                }
            }

            if (missingSourceGuid == null)
            {
                return;
            }

            doc.Missing.RemoveAll(entry => entry.Key != SourcePrefabKey && entry.Guid == missingSourceGuid);
        }

        // ------------------------------------------------------------------
        // 元 Prefab の読み込みキャッシュ (Prefab インスタンス内オブジェクトの名前・階層解決用)
        // ------------------------------------------------------------------

        private static class PrefabAssetCache
        {
            private sealed class Info
            {
                public string AssetPath;
                public GameObject Root;
                public readonly Dictionary<long, Object> ObjectsById = new Dictionary<long, Object>();
                public readonly Dictionary<long, GameObject> GameObjectsById = new Dictionary<long, GameObject>();
            }

            private static readonly Dictionary<string, Info> Cache = new Dictionary<string, Info>();

            public static void Clear()
            {
                Cache.Clear();
            }

            public static string GetPath(string guid)
            {
                return Get(guid)?.AssetPath;
            }

            /// <summary>Prefab 内の fileID からオブジェクトを取得する。</summary>
            public static Object GetObject(string guid, long localId)
            {
                var info = Get(guid);
                if (info == null)
                {
                    return null;
                }

                return info.ObjectsById.TryGetValue(localId, out var obj) ? obj : null;
            }

            /// <summary>Prefab 内の fileID (GameObject または Transform) から GameObject を取得する。0 はルート。</summary>
            public static GameObject GetGameObject(string guid, long localId)
            {
                var info = Get(guid);
                if (info == null)
                {
                    return null;
                }

                if (localId == 0)
                {
                    return info.Root;
                }

                return info.GameObjectsById.TryGetValue(localId, out var go) ? go : null;
            }

            /// <summary>Prefab ルートから <paramref name="gameObject"/> までの (fileID, 名前) をルート側から順に返す。</summary>
            public static List<(long Id, string Name)> GetPathSegments(GameObject gameObject)
            {
                if (gameObject == null)
                {
                    return null;
                }

                var segments = new List<(long, string)>();
                var current = gameObject.transform;
                var depth = 0;
                while (current != null && depth++ < MaxPathDepth)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(current.gameObject, out _, out long localId);
                    segments.Insert(0, (localId, current.gameObject.name));
                    current = current.parent;
                }

                return segments;
            }

            private static Info Get(string guid)
            {
                if (string.IsNullOrEmpty(guid))
                {
                    return null;
                }

                if (Cache.TryGetValue(guid, out var cached))
                {
                    return cached;
                }

                // 元 Prefab は .prefab のほか、モデル (.fbx) の場合もある
                Info info = null;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    info = new Info
                    {
                        AssetPath = path,
                        Root = AssetDatabase.LoadAssetAtPath<GameObject>(path),
                    };

                    foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        // スクリプトが Missing のコンポーネントは null で返る
                        if (obj == null)
                        {
                            continue;
                        }

                        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long localId))
                        {
                            continue;
                        }

                        info.ObjectsById[localId] = obj;
                        switch (obj)
                        {
                            case GameObject go:
                                info.GameObjectsById[localId] = go;
                                break;
                            case Transform transform:
                                info.GameObjectsById[localId] = transform.gameObject;
                                break;
                        }
                    }
                }

                Cache[guid] = info;
                return info;
            }
        }

        // ------------------------------------------------------------------
        // 1 ファイル分の解決・レポート生成
        // ------------------------------------------------------------------

        private sealed class FileContext
        {
            public readonly List<DocumentInfo> Documents;

            private readonly string _assetPath;
            private readonly bool _isScene;
            private readonly string _fileGuid;
            private readonly string _fileLabel;
            private readonly Object _mainAsset;
            private readonly bool _isSceneLoaded;
            private readonly List<string> _usedByScenes;
            private readonly Dictionary<long, DocumentInfo> _documentsById = new Dictionary<long, DocumentInfo>();
            private readonly Dictionary<long, DocumentInfo> _transformByGameObject = new Dictionary<long, DocumentInfo>();

            public FileContext(string assetPath, bool isScene, List<DocumentInfo> documents, List<string> usedByScenes)
            {
                Documents = documents;
                _assetPath = assetPath;
                _isScene = isScene;
                _fileGuid = AssetDatabase.AssetPathToGUID(assetPath);
                _fileLabel = Path.GetFileName(assetPath);
                _mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                _isSceneLoaded = isScene && SceneManager.GetSceneByPath(assetPath).isLoaded;
                _usedByScenes = usedByScenes;

                foreach (var doc in documents)
                {
                    _documentsById[doc.FileId] = doc;
                    if (doc.IsTransform && doc.GameObjectFileId != 0)
                    {
                        _transformByGameObject[doc.GameObjectFileId] = doc;
                    }
                }
            }

            // ---- レポート ----

            public string BuildReport(DocumentInfo doc, MissingEntry entry)
            {
                if (doc.ClassId == PrefabInstanceClassId)
                {
                    return entry.Key == SourcePrefabKey
                        ? ReportMissingSourcePrefab(doc, entry)
                        : ReportMissingOverride(doc, entry);
                }

                DocumentInfo owner;
                if (doc.ClassId == GameObjectClassId)
                {
                    owner = doc;
                }
                else if (!_documentsById.TryGetValue(doc.GameObjectFileId, out owner))
                {
                    owner = null;
                }

                var ownerPath = owner != null ? GetGameObjectPath(owner, 0) : $"(所属 GameObject 不明) &{doc.FileId}";
                var ordinal = GetComponentOrdinal(doc, owner);

                if (doc.ClassId == MonoBehaviourClassId && entry.Key == ScriptKey)
                {
                    return ReportMissingScript(doc, ownerPath, ordinal, entry);
                }

                return ReportMissingField(doc, ownerPath, ordinal, entry);
            }

            private string ReportMissingField(DocumentInfo doc, string ownerPath, string ordinal, MissingEntry entry)
            {
                var typeName = GetComponentTypeName(doc);
                var componentLabel = ordinal == null ? typeName : $"{typeName} ({ordinal})";
                var fieldTypeName = doc.ClassId == MonoBehaviourClassId ? GetFieldTypeName(doc.ScriptGuid, entry.Key) : null;
                var displayName = NicifyPropertyPath(entry.Key);

                string fix;
                if (doc.IsStripped)
                {
                    fix = $"この参照は Prefab インスタンス内のものです。元の Prefab ({GetSourcePrefabPath(doc)}) を開いて修正してください。";
                }
                else
                {
                    fix = $"{OpenInstruction} 上記 GameObject を選択し、Inspector の {typeName} にある「{displayName}」に" +
                          $"正しい{fieldTypeName ?? "アセット"}を設定し直すか、None に戻してください。";
                }

                return Compose(
                    $"[Missing] {_fileLabel} | {ownerPath} | {componentLabel} | {entry.Key}",
                    "ファイル: " + _assetPath,
                    DescribeUsedBy(),
                    "GameObject: " + ownerPath,
                    "コンポーネント: " + componentLabel,
                    "フィールド: " + DescribeField(entry.Key, displayName, fieldTypeName),
                    DescribeGuid(entry.Guid),
                    "対処: " + fix);
            }

            private string ReportMissingScript(DocumentInfo doc, string ownerPath, string ordinal, MissingEntry entry)
            {
                var position = ordinal ?? "位置不明";
                var fieldHint = doc.FieldNames.Count > 0
                    ? "手掛かり: このコンポーネントに保存されているフィールド名 = " + string.Join(", ", doc.FieldNames) +
                      " (どのスクリプトだったか推測できます)"
                    : "手掛かり: 保存されているフィールドはありません";

                string fix;
                if (doc.IsStripped)
                {
                    fix = $"このコンポーネントは Prefab インスタンス内のものです。元の Prefab ({GetSourcePrefabPath(doc)}) を開いて修正してください。";
                }
                else
                {
                    fix = $"{OpenInstruction} 上記 GameObject を選択し、Inspector で「Missing (Mono Script)」と表示されているコンポーネント ({position}) の" +
                          " Script 欄に正しいスクリプトを設定するか、右クリック → Remove Component で削除してください。";
                }

                return Compose(
                    $"[Missing Script] {_fileLabel} | {ownerPath} | {position} のコンポーネントのスクリプトが見つかりません",
                    "ファイル: " + _assetPath,
                    DescribeUsedBy(),
                    "GameObject: " + ownerPath,
                    $"コンポーネント: Inspector で「Missing (Mono Script)」と表示されるもの ({position})",
                    $"元スクリプト: GUID {entry.Guid} のスクリプトはプロジェクト内に存在しません (削除・移動・meta 再生成の可能性)",
                    fieldHint,
                    "対処: " + fix);
            }

            private string ReportMissingSourcePrefab(DocumentInfo instance, MissingEntry entry)
            {
                var ownerPath = GetPrefabInstanceObjectPath(instance, 0, 0);
                return Compose(
                    $"[Missing Prefab] {_fileLabel} | {ownerPath} | 元の Prefab アセットが存在しません",
                    "ファイル: " + _assetPath,
                    DescribeUsedBy(),
                    $"GameObject: {ownerPath} (Hierarchy で「Missing Prefab」と表示されます)",
                    $"参照先: GUID {entry.Guid} の Prefab はプロジェクト内に存在しません (削除・移動・meta 再生成の可能性)",
                    $"対処: {OpenInstruction} 上記オブジェクトを削除するか、元の Prefab アセットを復元してください。" +
                    " 別の Prefab で置き換える場合は、削除してから新しい Prefab を配置し直してください。");
            }

            private string ReportMissingOverride(DocumentInfo instance, MissingEntry entry)
            {
                var targetComponent = PrefabAssetCache.GetObject(instance.SourcePrefabGuid, entry.TargetFileId) as Component;
                var targetGameObject = targetComponent != null
                    ? targetComponent.gameObject
                    : PrefabAssetCache.GetGameObject(instance.SourcePrefabGuid, entry.TargetFileId);

                string ownerPath;
                if (targetGameObject != null)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(targetGameObject, out _, out long gameObjectLocalId);
                    ownerPath = GetPrefabInstanceObjectPath(instance, gameObjectLocalId, 0);
                }
                else
                {
                    ownerPath = GetPrefabInstanceObjectPath(instance, 0, 0) + $"/(&{entry.TargetFileId})";
                }

                string typeName;
                string componentLabel;
                string fieldTypeName = null;
                if (targetComponent != null)
                {
                    typeName = targetComponent.GetType().Name;
                    var ordinal = GetComponentOrdinal(targetComponent);
                    componentLabel = ordinal == null ? typeName : $"{typeName} ({ordinal})";
                    fieldTypeName = GetSerializedPropertyTypeName(targetComponent, entry.Key);
                }
                else
                {
                    typeName = $"コンポーネント &{entry.TargetFileId}";
                    componentLabel = typeName;
                }

                var displayName = NicifyPropertyPath(entry.Key);
                var sourcePrefabPath = PrefabAssetCache.GetPath(instance.SourcePrefabGuid) ?? instance.SourcePrefabGuid;

                return Compose(
                    $"[Missing] {_fileLabel} | {ownerPath} | {componentLabel} | {entry.Key} (Prefab インスタンスのオーバーライド値)",
                    "ファイル: " + _assetPath,
                    DescribeUsedBy(),
                    $"GameObject: {ownerPath} (Prefab {sourcePrefabPath} のインスタンス)",
                    "コンポーネント: " + componentLabel,
                    "フィールド: " + DescribeField(entry.Key, displayName, fieldTypeName) + " ※インスタンス側で上書きされた値",
                    DescribeGuid(entry.Guid),
                    $"対処: {OpenInstruction} 上記 GameObject を選択し、Inspector の {typeName} にある「{displayName}」に" +
                    $"正しい{fieldTypeName ?? "アセット"}を設定し直すか、フィールドを右クリック → Revert でオーバーライドを解除して Prefab の値に戻してください。");
            }

            private string OpenInstruction => _isScene
                ? $"シーン {_fileLabel} を開いて"
                : $"Prefab {_fileLabel} を Project ウィンドウでダブルクリックして開き、";

            private string DescribeUsedBy()
            {
                if (_usedByScenes == null || _usedByScenes.Count == 0)
                {
                    return null;
                }

                return "使用シーン: " + string.Join(", ", _usedByScenes);
            }

            private static string DescribeGuid(string guid)
            {
                return $"参照先: GUID {guid} のアセットはプロジェクト内に存在しません (削除・移動・meta 再生成の可能性)";
            }

            private static string DescribeField(string key, string displayName, string fieldTypeName)
            {
                var text = key;
                if (!string.Equals(displayName, key, StringComparison.Ordinal))
                {
                    text += $" (Inspector 表示名: {displayName})";
                }

                if (!string.IsNullOrEmpty(fieldTypeName))
                {
                    text += $" / 型: {fieldTypeName}";
                }

                return text;
            }

            private static string Compose(string header, params string[] lines)
            {
                var builder = new StringBuilder(header);
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    builder.Append('\n').Append("  ").Append(line);
                }

                return builder.ToString();
            }

            // ---- コンポーネント情報 ----

            private string GetComponentTypeName(DocumentInfo doc)
            {
                if (doc.ClassId == MonoBehaviourClassId)
                {
                    var scriptPath = string.IsNullOrEmpty(doc.ScriptGuid) ? null : AssetDatabase.GUIDToAssetPath(doc.ScriptGuid);
                    if (string.IsNullOrEmpty(scriptPath))
                    {
                        return "Missing Script";
                    }

                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                    var scriptClass = script != null ? script.GetClass() : null;
                    return scriptClass != null ? scriptClass.Name : Path.GetFileNameWithoutExtension(scriptPath);
                }

                if (ResolveObject(doc) is Component component)
                {
                    return component.GetType().Name;
                }

                return BuiltinClassNames.TryGetValue(doc.ClassId, out var name) ? name : $"ClassID {doc.ClassId}";
            }

            /// <summary>"上から N 番目 / 全 M 個" 形式。GameObject のコンポーネント一覧が取れない場合は null。</summary>
            private static string GetComponentOrdinal(DocumentInfo doc, DocumentInfo owner)
            {
                if (owner == null || owner == doc || owner.ComponentFileIds.Count == 0)
                {
                    return null;
                }

                var index = owner.ComponentFileIds.IndexOf(doc.FileId);
                return index < 0 ? null : $"上から {index + 1} 番目 / 全 {owner.ComponentFileIds.Count} 個";
            }

            private static string GetComponentOrdinal(Component component)
            {
                var components = component.gameObject.GetComponents<Component>();
                var index = Array.IndexOf(components, component);
                return index < 0 ? null : $"上から {index + 1} 番目 / 全 {components.Length} 個";
            }

            private static string GetFieldTypeName(string scriptGuid, string key)
            {
                if (string.IsNullOrEmpty(scriptGuid))
                {
                    return null;
                }

                var scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuid);
                if (string.IsNullOrEmpty(scriptPath))
                {
                    return null;
                }

                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                var scriptClass = script != null ? script.GetClass() : null;
                if (scriptClass == null)
                {
                    return null;
                }

                // "_entries[1]._material" のようなパスをフィールド定義に沿って辿る
                Type currentType = scriptClass;
                foreach (var segment in ArrayDataRegex.Replace(key, "[").Split('.'))
                {
                    var fieldName = ArrayIndexRegex.Replace(segment, string.Empty);
                    var field = FindField(currentType, fieldName);
                    if (field == null)
                    {
                        return null;
                    }

                    currentType = GetElementType(field.FieldType);
                }

                return currentType?.Name;
            }

            private static FieldInfo FindField(Type type, string fieldName)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (; type != null && type != typeof(object); type = type.BaseType)
                {
                    var field = type.GetField(fieldName, flags);
                    if (field != null)
                    {
                        return field;
                    }
                }

                return null;
            }

            private static Type GetElementType(Type type)
            {
                if (type.IsArray)
                {
                    return type.GetElementType();
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    return type.GetGenericArguments()[0];
                }

                return type;
            }

            private static string GetSerializedPropertyTypeName(Component component, string propertyPath)
            {
                try
                {
                    using var serializedObject = new SerializedObject(component);
                    var property = serializedObject.FindProperty(propertyPath);
                    if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        return null;
                    }

                    var typeName = property.type;
                    // "PPtr<$Transform>" のような表記から型名だけを取り出す
                    if (typeName.StartsWith("PPtr<$") && typeName.EndsWith(">"))
                    {
                        typeName = typeName.Substring(6, typeName.Length - 7);
                    }

                    return typeName;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>"_entries[1]._material" → "Entries [1] > Material" のように Inspector の見た目に近づける。</summary>
            private static string NicifyPropertyPath(string propertyPath)
            {
                var builder = new StringBuilder();
                foreach (var segment in ArrayDataRegex.Replace(propertyPath, "[").Split('.'))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(" > ");
                    }

                    var indexMatch = ArrayIndexRegex.Match(segment);
                    var name = ArrayIndexRegex.Replace(segment, string.Empty);
                    builder.Append(ObjectNames.NicifyVariableName(name));
                    if (indexMatch.Success)
                    {
                        builder.Append(' ').Append(indexMatch.Value);
                    }
                }

                return builder.ToString();
            }

            // ---- 階層パス ----

            /// <summary>GameObject ドキュメントの階層パス ("Root/Child/Leaf") を返す。</summary>
            private string GetGameObjectPath(DocumentInfo gameObject, int depth)
            {
                if (gameObject == null || depth > MaxPathDepth)
                {
                    return "?";
                }

                if (gameObject.IsStripped)
                {
                    return GetStrippedObjectPath(gameObject, depth);
                }

                var name = string.IsNullOrEmpty(gameObject.Name) ? $"GameObject &{gameObject.FileId}" : gameObject.Name;
                if (!_transformByGameObject.TryGetValue(gameObject.FileId, out var transform) || transform.FatherFileId == 0)
                {
                    return name;
                }

                var parentPath = GetTransformOwnerPath(transform.FatherFileId, depth + 1);
                return parentPath == null ? name : $"{parentPath}/{name}";
            }

            /// <summary>Transform ドキュメントの fileID から、その Transform を持つ GameObject の階層パスを返す。</summary>
            private string GetTransformOwnerPath(long transformFileId, int depth)
            {
                if (depth > MaxPathDepth || !_documentsById.TryGetValue(transformFileId, out var transform))
                {
                    return null;
                }

                if (transform.IsStripped)
                {
                    return GetStrippedObjectPath(transform, depth);
                }

                return _documentsById.TryGetValue(transform.GameObjectFileId, out var gameObject)
                    ? GetGameObjectPath(gameObject, depth + 1)
                    : null;
            }

            /// <summary>Prefab インスタンス内の (stripped) GameObject / Transform ドキュメントの階層パスを返す。</summary>
            private string GetStrippedObjectPath(DocumentInfo stripped, int depth)
            {
                _documentsById.TryGetValue(stripped.PrefabInstanceFileId, out var instance);
                if (instance == null)
                {
                    return $"(Prefab インスタンス内) &{stripped.FileId}";
                }

                return GetPrefabInstanceObjectPath(instance, stripped.CorrespondingSourceFileId, depth + 1);
            }

            /// <summary>
            /// PrefabInstance の元 Prefab 内 fileID (<paramref name="sourceLocalId"/>, 0 はルート) で示されるオブジェクトの、
            /// このファイル内での階層パスを返す。親側のパスはファイル内の Transform を辿り、Prefab 内のパスは元 Prefab を読んで解決する。
            /// </summary>
            private string GetPrefabInstanceObjectPath(DocumentInfo instance, long sourceLocalId, int depth)
            {
                string parentPath = null;
                if (instance.TransformParentFileId != 0 && depth <= MaxPathDepth)
                {
                    parentPath = GetTransformOwnerPath(instance.TransformParentFileId, depth + 1);
                }

                var sourceObject = PrefabAssetCache.GetGameObject(instance.SourcePrefabGuid, sourceLocalId);
                var segments = PrefabAssetCache.GetPathSegments(sourceObject);

                string innerPath;
                if (segments == null)
                {
                    // 元 Prefab を読めない (Missing など)。分かる範囲で名前を出す
                    var rootName = !string.IsNullOrEmpty(instance.Name) ? instance.Name
                        : instance.NameOverrides.Count > 0 ? GetFirstNameOverride(instance)
                        : $"PrefabInstance &{instance.FileId}";
                    innerPath = sourceLocalId == 0 ? rootName : $"{rootName}/(&{sourceLocalId})";
                }
                else
                {
                    var builder = new StringBuilder();
                    foreach (var (id, name) in segments)
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('/');
                        }

                        builder.Append(instance.NameOverrides.TryGetValue(id, out var overridden) ? overridden : name);
                    }

                    innerPath = builder.ToString();
                }

                return parentPath == null ? innerPath : $"{parentPath}/{innerPath}";
            }

            private static string GetFirstNameOverride(DocumentInfo instance)
            {
                foreach (var pair in instance.NameOverrides)
                {
                    return pair.Value;
                }

                return null;
            }

            private string GetSourcePrefabPath(DocumentInfo stripped)
            {
                if (_documentsById.TryGetValue(stripped.PrefabInstanceFileId, out var instance))
                {
                    var path = PrefabAssetCache.GetPath(instance.SourcePrefabGuid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }

                return "パス不明";
            }

            // ---- 実オブジェクト解決 (Console クリック時のフォーカス用) ----

            /// <summary>ドキュメントに対応する実オブジェクトを返す。シーンは開かれている場合のみ、Prefab は常に解決を試みる。</summary>
            private Object ResolveObject(DocumentInfo doc)
            {
                if (_isScene)
                {
                    if (!_isSceneLoaded)
                    {
                        return null;
                    }

                    return doc.IsStripped
                        ? ResolveSceneObject(doc.CorrespondingSourceFileId, doc.PrefabInstanceFileId)
                        : ResolveSceneObject(doc.FileId, 0);
                }

                return PrefabAssetCache.GetObject(_fileGuid, doc.FileId);
            }

            private Object ResolveSceneObject(long targetId, long prefabInstanceId)
            {
                if (!GlobalObjectId.TryParse($"GlobalObjectId_V1-2-{_fileGuid}-{targetId}-{prefabInstanceId}", out var id))
                {
                    return null;
                }

                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            }

            public Object GetContextObject(DocumentInfo doc, MissingEntry entry)
            {
                Object resolved = null;
                if (doc.ClassId == PrefabInstanceClassId)
                {
                    if (_isScene && _isSceneLoaded && entry.TargetFileId != 0)
                    {
                        resolved = ResolveSceneObject(entry.TargetFileId, doc.FileId);
                    }
                }
                else
                {
                    resolved = ResolveObject(doc);
                    if (resolved == null && doc.GameObjectFileId != 0 && _documentsById.TryGetValue(doc.GameObjectFileId, out var owner))
                    {
                        resolved = ResolveObject(owner);
                    }
                }

                switch (resolved)
                {
                    case GameObject go:
                        return go;
                    case Component component:
                        return component.gameObject;
                    default:
                        return _mainAsset;
                }
            }
        }
    }
}
