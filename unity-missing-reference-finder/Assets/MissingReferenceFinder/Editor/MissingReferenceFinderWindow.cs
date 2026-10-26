using UnityEditor;
using UnityEngine;

namespace Hidano.MissingReferenceFinder.Editor
{
    /// <summary>
    /// <see cref="MissingReferenceScanner"/> を手動実行するためのウィンドウ。
    /// 現在開いている全シーンを対象に Missing / None の参照を Console に出力する。
    /// </summary>
    public sealed class MissingReferenceFinderWindow : EditorWindow
    {
        private const string MenuPath = "Hidano/Tools/Missing Reference Finder";

        [SerializeField] private bool _reportNone = true;
        [SerializeField] private bool _monoBehaviourOnly = true;
        [SerializeField] private bool _includeInactive = true;

        [MenuItem(MenuPath)]
        private static void Open()
        {
            var window = GetWindow<MissingReferenceFinderWindow>();
            window.titleContent = new GUIContent("Missing Ref Finder");
            window.minSize = new Vector2(320f, 200f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "現在開いている全シーンのコンポーネントを走査し、Missing / None の参照を Console に出力します。\n" +
                "Console のログをクリックすると該当 GameObject が Hierarchy 上でハイライトされます。",
                MessageType.Info);

            EditorGUILayout.Space();

            _reportNone = EditorGUILayout.ToggleLeft(
                new GUIContent("None も報告する", "OFF の場合は Missing (参照先が消失したもの) のみ報告します。"),
                _reportNone);
            _monoBehaviourOnly = EditorGUILayout.ToggleLeft(
                new GUIContent("MonoBehaviour のみ", "OFF の場合は Renderer や Camera などの組み込みコンポーネントも検査します。"),
                _monoBehaviourOnly);
            _includeInactive = EditorGUILayout.ToggleLeft(
                new GUIContent("非アクティブな GameObject も含める"),
                _includeInactive);

            EditorGUILayout.Space();

            if (GUILayout.Button("検索してConsoleに出力", GUILayout.Height(32f)))
            {
                Scan();
            }

            EditorGUILayout.Space();

            var buildCheckEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "ビルド時に Missing をチェックしてビルドを中止する",
                    "ビルド開始前に Build Settings の各シーンファイルを読み、存在しないアセットへの参照 (Missing) があればビルドを失敗させます。"),
                MissingReferenceBuildCheck.Enabled);
            if (buildCheckEnabled != MissingReferenceBuildCheck.Enabled)
            {
                MissingReferenceBuildCheck.Enabled = buildCheckEnabled;
            }
        }

        private void Scan()
        {
            var options = new MissingReferenceScanner.Options
            {
                ReportNone = _reportNone,
                MonoBehaviourOnly = _monoBehaviourOnly,
                IncludeInactive = _includeInactive,
                ShowProgressBar = true,
            };

            var result = MissingReferenceScanner.ScanLoadedScenes(options);
            Debug.Log(MissingReferenceScanner.FormatSummary("[MissingReferenceFinder]", result, _reportNone));
        }
    }
}
