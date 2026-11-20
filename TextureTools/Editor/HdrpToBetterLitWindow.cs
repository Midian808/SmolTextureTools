using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools
{
    //Lists HDRP materials, shows what each will become and what won't carry over, and converts them in one step (§9.5)
    public sealed class HdrpToBetterLitWindow : EditorWindow
    {
        private const string Title = "HDRP to Better Lit";
        //Above this many materials, planning shows a progress bar
        private const int ProgressThreshold = 50;

        [SerializeField] private List<string> inputs = new List<string>();
        [SerializeField] private float ev100 = HdrpToBetterLit.DefaultEv100;
        [SerializeField] private string selectedPath;
        [SerializeField] private string lastLog;

        private List<ConversionPlan> plans = new List<ConversionPlan>();
        private bool betterLitInstalled;
        private Vector2 listScroll, detailScroll;

        [MenuItem("Tools/Texture Tools/HDRP to Better Lit")]
        public static void Open()
        {
            GetWindow<HdrpToBetterLitWindow>(Title);
        }

        [MenuItem("Assets/Texture Tools/HDRP to Better Lit...", true)]
        private static bool CanOpenForSelection()
        {
            return SelectedPaths().Any(p => AssetDatabase.IsValidFolder(p) || AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(Material));
        }

        //Opens with the selected materials and folders listed
        [MenuItem("Assets/Texture Tools/HDRP to Better Lit...")]
        private static void OpenForSelection()
        {
            var window = GetWindow<HdrpToBetterLitWindow>(Title);
            window.inputs.Clear();
            window.lastLog = null;
            window.Add(SelectedPaths());
        }

        private static string[] SelectedPaths()
        {
            return Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }

        private void OnEnable()
        {
            Replan();
        }

        private void Add(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (!inputs.Contains(path)) inputs.Add(path);
            }
            Replan();
        }

        private void Replan()
        {
            betterLitInstalled = HdrpToBetterLit.BetterLitInstalled();
            inputs.RemoveAll(p => !AssetDatabase.IsValidFolder(p) && AssetDatabase.GetMainAssetTypeAtPath(p) != typeof(Material));
            var paths = HdrpToBetterLit.CollectMaterials(inputs);
            plans = new List<ConversionPlan>();
            bool progress = paths.Count > ProgressThreshold;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (progress && EditorUtility.DisplayCancelableProgressBar(Title, "Reading " + paths[i], (float)i / paths.Count)) break;
                    var material = AssetDatabase.LoadAssetAtPath<Material>(paths[i]);
                    if (material == null) continue;
                    var plan = HdrpToBetterLit.Plan(material, paths[i], ev100);
                    if (plan.Listed) plans.Add(plan);
                }
            }
            finally
            {
                if (progress) EditorUtility.ClearProgressBar();
            }
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Converts materials made for HDRP's Lit shader into Better Lit Shader materials, in place: prefabs and scenes using them update. " +
                "HDRP's values stay saved in each material. Materials that already have a working shader aren't listed.",
                MessageType.Info);

            if (!betterLitInstalled)
                EditorGUILayout.HelpBox("Better Lit Shader isn't in this project, so nothing can be converted. The list still shows what would happen.", MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Add selection", "Add the materials and folders selected in the Project window. Folders are searched recursively.")))
            {
                Add(SelectedPaths());
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Add folder..."))
            {
                string folder = EditorUtility.OpenFolderPanel("Add folder", Application.dataPath, "");
                string assetPath = ToAssetPath(folder);
                if (assetPath != null) Add(new[] { assetPath });
                else if (!string.IsNullOrEmpty(folder)) EditorUtility.DisplayDialog(Title, "Pick a folder inside this project's Assets folder.", "OK");
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button(new GUIContent("Refresh", "Read the materials again, after changing them elsewhere.")))
            {
                Replan();
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Clear"))
            {
                inputs.Clear();
                selectedPath = null;
                lastLog = null;
                Replan();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (inputs.Count > 0)
                EditorGUILayout.LabelField("Searching: " + string.Join(", ", inputs), EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();
            ev100 = EditorGUILayout.FloatField(new GUIContent("HDRP exposure (EV100)",
                "Stands in for the camera exposure of the HDRP scene the materials came from; it sets how bright converted emission is. " +
                "0 keeps dim emission visible; raise it for materials made for bright scenes."), ev100);
            if (EditorGUI.EndChangeCheck())
            {
                Replan();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space();
            DrawList();
            DrawDetails();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(plans.Count == 0 ? "No HDRP materials listed" : HdrpToBetterLit.Totals(plans), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!betterLitInstalled || !plans.Any(p => p.Converts)))
            {
                if (GUILayout.Button("Convert", GUILayout.Height(28f)))
                {
                    ConvertNow();
                    GUIUtility.ExitGUI();
                }
            }

            if (!string.IsNullOrEmpty(lastLog))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Last conversion logged to the Console.", EditorStyles.miniLabel);
                if (GUILayout.Button("Copy log", GUILayout.Width(80f))) EditorGUIUtility.systemCopyBuffer = lastLog;
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawList()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Material", EditorStyles.miniBoldLabel, GUILayout.Width(200f));
            GUILayout.Label("Source", EditorStyles.miniBoldLabel, GUILayout.Width(150f));
            GUILayout.Label("Becomes", EditorStyles.miniBoldLabel, GUILayout.Width(170f));
            GUILayout.Label("Notes", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(360f));
            foreach (var plan in plans)
            {
                bool selected = plan.MaterialPath == selectedPath;
                Rect row = EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(Path.GetFileNameWithoutExtension(plan.MaterialPath), plan.MaterialPath),
                    selected ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(200f));
                GUILayout.Label(HdrpToBetterLit.Describe(plan.Source), GUILayout.Width(150f));
                GUILayout.Label(HdrpToBetterLit.Becomes(plan), GUILayout.Width(170f));
                string notes = !plan.Converts ? plan.SkipReason
                    : plan.Notes.Count == 0 ? "—"
                    : plan.Notes.Count == 1 ? plan.Notes[0]
                    : plan.Notes[0] + " (+" + (plan.Notes.Count - 1) + " more)";
                GUILayout.Label(new GUIContent(notes, string.Join("\n", plan.Notes)), EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    selectedPath = plan.MaterialPath;
                    EditorGUIUtility.PingObject(plan.Material);
                    Selection.activeObject = plan.Material;
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        //Everything about the clicked material, under the list
        private void DrawDetails()
        {
            var plan = plans.FirstOrDefault(p => p.MaterialPath == selectedPath);
            if (plan == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(plan.MaterialPath, EditorStyles.boldLabel);
            detailScroll = EditorGUILayout.BeginScrollView(detailScroll, GUILayout.MaxHeight(160f));
            if (!plan.Converts)
            {
                EditorGUILayout.LabelField("Skipped: " + plan.SkipReason, EditorStyles.wordWrappedLabel);
            }
            else
            {
                EditorGUILayout.LabelField(HdrpToBetterLit.Describe(plan.Source) + " → Better Lit " + HdrpToBetterLit.Becomes(plan), EditorStyles.wordWrappedLabel);
                foreach (var pack in plan.Packs)
                    EditorGUILayout.LabelField("Writes " + pack.Value.OutputPath + " for " + pack.Key, EditorStyles.wordWrappedMiniLabel);
                if (plan.Notes.Count == 0) EditorGUILayout.LabelField("Everything carries over.", EditorStyles.wordWrappedMiniLabel);
                foreach (string note in plan.Notes) EditorGUILayout.LabelField("• " + note, EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void ConvertNow()
        {
            //Plan again from the files as they are now, so nothing changed since the list was made is overwritten
            Replan();
            var converting = plans.Where(p => p.Converts).ToList();
            if (converting.Count == 0) return;
            if (!EditorUtility.DisplayDialog(Title,
                    "Convert " + converting.Count + " material(s) to Better Lit Shader?\n\n" +
                    "They're changed in place, as one Undo step. Packed textures are written as new files next to their sources, and Undo doesn't remove them.",
                    "Convert", "Cancel"))
                return;

            int converted;
            try
            {
                converted = HdrpToBetterLit.Apply(plans, ExistingOutput.Ask);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(Title, "Conversion stopped: " + e.Message, "OK");
                return;
            }

            lastLog = HdrpToBetterLit.Log(plans, ev100);
            Debug.Log(lastLog);
            if (converted < converting.Count)
                EditorUtility.DisplayDialog(Title, converted + " of " + converting.Count + " converted. The others were skipped; the Console log says why.", "OK");
            Replan();
        }

        //An absolute folder path inside this project's Assets folder as "Assets/...", or null
        private static string ToAssetPath(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string full = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
            if (string.Equals(full, assets, System.StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!full.StartsWith(assets + "/", System.StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + full.Substring(assets.Length);
        }
    }
}
