using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hidano.MissingReferenceFinder.Editor
{
    /// <summary>
    /// シーン内のコンポーネントを走査し、Serialize されたオブジェクト参照の Missing / None を Console に出力する。
    /// ログは 1 パラメータ 1 行で出力し、context に GameObject を渡すので Console でクリックすると Hierarchy 上でハイライトされる。
    /// <see cref="MissingReferenceFinderWindow"/> から呼び出す。
    /// ビルド時のチェックはシーンファイルを直接読む <see cref="MissingReferenceBuildCheck"/> が担当する。
    /// </summary>
    public static class MissingReferenceScanner
    {
        public struct Options
        {
            /// <summary>None (未設定) も報告するか。</summary>
            public bool ReportNone;

            /// <summary>MonoBehaviour のみを対象にするか。false の場合は組み込みコンポーネントも検査する。</summary>
            public bool MonoBehaviourOnly;

            /// <summary>非アクティブな GameObject も対象にするか。</summary>
            public bool IncludeInactive;

            /// <summary>進捗バーを表示するか。</summary>
            public bool ShowProgressBar;
        }

        public struct Result
        {
            public int ComponentCount;
            public int MissingCount;
            public int MissingScriptCount;
            public int NoneCount;

            /// <summary>Missing 参照または Missing Script が 1 件でもあれば true。</summary>
            public bool HasMissing => MissingCount > 0 || MissingScriptCount > 0;

            public void Add(in Result other)
            {
                ComponentCount += other.ComponentCount;
                MissingCount += other.MissingCount;
                MissingScriptCount += other.MissingScriptCount;
                NoneCount += other.NoneCount;
            }
        }

        /// <summary>
        /// Unity が内部管理用に持つ参照フィールド。ユーザーが設定するものではないので検査対象から除外する。
        /// </summary>
        private static readonly HashSet<string> IgnoredPropertyPaths = new HashSet<string>
        {
            "m_Script",
            "m_GameObject",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_EditorClassIdentifier",
        };

        /// <summary>現在ロードされている全シーンを走査する。</summary>
        public static Result ScanLoadedScenes(in Options options)
        {
            var total = new Result();
            try
            {
                for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                {
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!scene.isLoaded)
                    {
                        continue;
                    }

                    total.Add(ScanScene(scene, options));
                }
            }
            finally
            {
                if (options.ShowProgressBar)
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            return total;
        }

        /// <summary>指定したシーンを走査する。</summary>
        public static Result ScanScene(Scene scene, in Options options)
        {
            var result = new Result();
            var roots = scene.GetRootGameObjects();

            try
            {
                for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    var root = roots[rootIndex];
                    if (options.ShowProgressBar)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Missing Reference Finder",
                            $"{scene.name} : {root.name}",
                            (float)rootIndex / roots.Length);
                    }

                    var components = root.GetComponentsInChildren<Component>(true);
                    foreach (var component in components)
                    {
                        // component が null なのはスクリプト自体が Missing になっているケース。
                        // GetComponentsInChildren では所属 GameObject を辿れないため、GameObject 単位で別途検出する。
                        if (component == null)
                        {
                            continue;
                        }

                        if (!options.IncludeInactive && !component.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        if (options.MonoBehaviourOnly && !(component is MonoBehaviour))
                        {
                            continue;
                        }

                        result.ComponentCount++;
                        ScanComponent(component, options.ReportNone, ref result);
                    }

                    result.MissingScriptCount += ReportMissingScripts(root);
                }
            }
            finally
            {
                if (options.ShowProgressBar)
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            return result;
        }

        public static string FormatSummary(string prefix, in Result result, bool includeNone)
        {
            var summary = new StringBuilder();
            summary.Append(prefix).Append(" 検査完了: ");
            summary.Append(result.ComponentCount).Append(" コンポーネント / Missing ");
            summary.Append(result.MissingCount).Append(" 件 / Missing Script ");
            summary.Append(result.MissingScriptCount).Append(" 件");
            if (includeNone)
            {
                summary.Append(" / None ").Append(result.NoneCount).Append(" 件");
            }

            return summary.ToString();
        }

        private static void ScanComponent(Component component, bool reportNone, ref Result result)
        {
            var serializedObject = new SerializedObject(component);
            var property = serializedObject.GetIterator();

            var enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;

                // 文字列の子要素へは潜らない
                if (property.propertyType == SerializedPropertyType.String)
                {
                    enterChildren = false;
                }

                if (property.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                if (IgnoredPropertyPaths.Contains(property.propertyPath))
                {
                    continue;
                }

                if (property.objectReferenceValue != null)
                {
                    continue;
                }

                // 参照先の instanceID が残っているのに解決できない = Missing
                var isMissing = property.objectReferenceInstanceIDValue != 0;
                if (isMissing)
                {
                    result.MissingCount++;
                    Debug.LogError(FormatMessage("Missing", component, property), component.gameObject);
                }
                else if (reportNone)
                {
                    result.NoneCount++;
                    Debug.LogWarning(FormatMessage("None", component, property), component.gameObject);
                }
            }
        }

        private static int ReportMissingScripts(GameObject root)
        {
            var count = 0;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var transform in transforms)
            {
                var gameObject = transform.gameObject;
                var missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                for (var i = 0; i < missing; i++)
                {
                    count++;
                    Debug.LogError(
                        $"[Missing Script] {GetHierarchyPath(gameObject)} にスクリプトが見つからないコンポーネントがあります。",
                        gameObject);
                }
            }

            return count;
        }

        private static string FormatMessage(string kind, Component component, SerializedProperty property)
        {
            var typeName = property.type;
            // "PPtr<$Transform>" のような表記から型名だけを取り出す
            if (typeName.StartsWith("PPtr<$") && typeName.EndsWith(">"))
            {
                typeName = typeName.Substring(6, typeName.Length - 7);
            }

            return $"[{kind}] {GetHierarchyPath(component.gameObject)} <{component.GetType().Name}> " +
                   $"{property.propertyPath} ({typeName})";
        }

        private static string GetHierarchyPath(GameObject gameObject)
        {
            var path = new StringBuilder(gameObject.name);
            var parent = gameObject.transform.parent;
            while (parent != null)
            {
                path.Insert(0, '/').Insert(0, parent.name);
                parent = parent.parent;
            }

            return $"{gameObject.scene.name}:{path}";
        }
    }
}
