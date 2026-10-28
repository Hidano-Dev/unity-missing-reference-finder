using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Hidano.MissingReferenceFinder.Editor.TimelineBindings
{
    /// <summary>
    /// Timeline (.playable) の保存時に、その Timeline を再生する PlayableDirector の m_SceneBindings へ
    /// 未登録トラックの空 Binding (value: None) を追加する。
    ///
    /// PlayableDirector の Inspector は Bindings 欄を表示した時点で、未登録の出力に空の Binding を作る
    /// (UnityEditor.DirectorEditor.UpdatePlayableBindingsIfRequired)。トラックを追加した人がその Prefab の
    /// Inspector を表示していないと、後で別の人が表示したときに意図しない差分として現れる。その差分をコミットせずに
    /// 抱えたまま pull すると、コンフリクト解決時に他の人が設定した Binding を上書きして消す事故につながる。
    /// トラック追加と同じタイミングで空 Binding を作っておき、同じコミットに含めることでこれを防ぐ。
    ///
    /// 対象は Inspector と同じく、playableAsset.outputs のうち出力先の型を持つもの。Inspector は出力先の型を持たない
    /// 出力の Binding を削除もするが、要素の位置がずれて Prefab インスタンスのオーバーライド (配列番号で記録される) を
    /// 崩しうるため、ここでは末尾への追加のみを行う。Prefab インスタンス上の PlayableDirector は
    /// 元 Prefab 側で処理されるので対象外 (インスタンス側に追加すると不要なオーバーライドになる)。
    ///
    /// 有効/無効はメニュー (Tools/Hidano/Fill Timeline Binding Entries On Save) で切り替えられる。
    /// </summary>
    public sealed class TimelineBindingEntryFiller : AssetPostprocessor
    {
        private const string ToggleMenuPath = "Tools/Hidano/Fill Timeline Binding Entries On Save";
        private const string RunMenuPath = "Tools/Hidano/Fill Timeline Binding Entries In All Prefabs";
        private const string EditorPrefsKey = "Hidano.TimelineBindingEntryFiller.Enabled";
        private const string LogPrefix = "[TimelineBindingEntryFiller]";

        private const string PlayableExtension = ".playable";
        private const string AssetsFolder = "Assets";
        private const string SceneBindingsProperty = "m_SceneBindings";
        private const string BindingKeyProperty = "key";
        private const string BindingValueProperty = "value";

        /// <summary>delayCall 待ちの間に保存された Timeline のパス。</summary>
        private static readonly HashSet<string> PendingTimelinePaths = new HashSet<string>();

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EditorPrefsKey, true);
            set => EditorPrefs.SetBool(EditorPrefsKey, value);
        }

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

        /// <summary>保存を待たずに、Timeline を参照する全 Prefab と開いているシーンに同じ処理を行う。</summary>
        [MenuItem(RunMenuPath)]
        private static void RunManually()
        {
            var addedCount = Fill(_ => true);
            if (addedCount == 0)
            {
                Debug.Log($"{LogPrefix} 追加が必要な Binding はありませんでした。");
            }
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (!Enabled)
            {
                return;
            }

            var added = false;
            foreach (var path in importedAssets)
            {
                if (IsProjectTimelinePath(path))
                {
                    added |= PendingTimelinePaths.Add(path);
                }
            }

            if (added)
            {
                // インポート処理中に他のアセットを書き換えないよう、インポート完了後に回す
                EditorApplication.delayCall -= ProcessPending;
                EditorApplication.delayCall += ProcessPending;
            }
        }

        private static bool IsProjectTimelinePath(string path)
        {
            return path.StartsWith(AssetsFolder + "/", StringComparison.Ordinal) &&
                   path.EndsWith(PlayableExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static void ProcessPending()
        {
            if (PendingTimelinePaths.Count == 0)
            {
                return;
            }

            var timelinePaths = new HashSet<string>(PendingTimelinePaths);
            PendingTimelinePaths.Clear();
            Fill(timelinePaths.Contains);
        }

        /// <summary>
        /// <paramref name="isTargetTimeline"/> に該当する Timeline を参照する Prefab と、開いているシーンを処理する。
        /// </summary>
        /// <returns>追加した Binding の総数。</returns>
        private static int Fill(Func<string, bool> isTargetTimeline)
        {
            var addedCount = 0;
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { AssetsFolder }))
            {
                var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var referencesTarget = AssetDatabase.GetDependencies(prefabPath, false)
                    .Any(path => IsProjectTimelinePath(path) && isTargetTimeline(path));
                if (!referencesTarget)
                {
                    continue;
                }

                // Prefab モードで開いている Prefab をファイル側から書き換えると編集中の内容と食い違うので、ステージ上で処理する
                if (prefabStage != null && prefabStage.assetPath == prefabPath)
                {
                    var stageAdded = FillDirectors(prefabStage.prefabContentsRoot, isTargetTimeline, prefabPath);
                    if (stageAdded > 0)
                    {
                        EditorSceneManager.MarkSceneDirty(prefabStage.scene);
                        addedCount += stageAdded;
                    }

                    continue;
                }

                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    var prefabAdded = FillDirectors(root, isTargetTimeline, prefabPath);
                    if (prefabAdded > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                        addedCount += prefabAdded;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                var sceneAdded = scene.GetRootGameObjects()
                    .Sum(root => FillDirectors(root, isTargetTimeline, scene.path));
                if (sceneAdded > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    addedCount += sceneAdded;
                }
            }

            return addedCount;
        }

        private static int FillDirectors(GameObject root, Func<string, bool> isTargetTimeline, string context)
        {
            var addedCount = 0;
            foreach (var director in root.GetComponentsInChildren<PlayableDirector>(true))
            {
                if (PrefabUtility.IsPartOfPrefabInstance(director))
                {
                    continue;
                }

                var playableAsset = director.playableAsset;
                if (playableAsset == null || !isTargetTimeline(AssetDatabase.GetAssetPath(playableAsset)))
                {
                    continue;
                }

                var addedKeys = FillDirector(director, playableAsset);
                if (addedKeys.Count == 0)
                {
                    continue;
                }

                addedCount += addedKeys.Count;
                Debug.Log(
                    $"{LogPrefix} {context} の {GetHierarchyPath(director.transform)} に空の Binding を {addedKeys.Count} 件追加しました " +
                    $"({playableAsset.name}: {string.Join(", ", addedKeys.Select(key => key.name))})。" +
                    "Timeline の変更と一緒にコミットしてください。");
            }

            return addedCount;
        }

        /// <summary>
        /// 出力先の型を持つ出力のうち m_SceneBindings に無いものを、value が None の要素として末尾に追加する。
        /// UnityEditor.DirectorEditor.UpdatePlayableBindingsIfRequired と同じ対象・同じ順序になる。
        /// </summary>
        private static List<Object> FillDirector(PlayableDirector director, PlayableAsset playableAsset)
        {
            var serializedDirector = new SerializedObject(director);
            var bindings = serializedDirector.FindProperty(SceneBindingsProperty);

            var boundKeys = new HashSet<Object>();
            for (var i = 0; i < bindings.arraySize; i++)
            {
                boundKeys.Add(bindings.GetArrayElementAtIndex(i).FindPropertyRelative(BindingKeyProperty).objectReferenceValue);
            }

            var addedKeys = new List<Object>();
            foreach (var output in playableAsset.outputs)
            {
                var key = output.sourceObject;
                if (key == null || output.outputTargetType == null || !boundKeys.Add(key))
                {
                    continue;
                }

                var index = bindings.arraySize;
                bindings.InsertArrayElementAtIndex(index);
                var element = bindings.GetArrayElementAtIndex(index);
                element.FindPropertyRelative(BindingKeyProperty).objectReferenceValue = key;
                element.FindPropertyRelative(BindingValueProperty).objectReferenceValue = null;
                addedKeys.Add(key);
            }

            if (addedKeys.Count > 0)
            {
                serializedDirector.ApplyModifiedPropertiesWithoutUndo();
            }

            return addedKeys;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var builder = new StringBuilder(transform.name);
            for (var parent = transform.parent; parent != null; parent = parent.parent)
            {
                builder.Insert(0, parent.name + "/");
            }

            return builder.ToString();
        }
    }
}
