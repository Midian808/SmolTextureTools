using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools
{
    public sealed class MaskTextureMakerWindow : EditorWindow
    {
        private const string PresetPref = "Midian.TextureTools.MaskTextureMaker.Preset";
        private static readonly string[] PreviewModes = { "RGB", "R", "G", "B", "A" };
        private static readonly string[] ChannelChoices = { "Luminance", "R", "G", "B", "A" };

        //One output channel's settings in the window
        [Serializable]
        private sealed class RowState
        {
            public bool UseTexture;
            public Texture2D Texture;
            public AlphaChannel Channel = AlphaChannel.Red;
            public bool Invert;
            public float Value;
        }

        //A small copy of an input texture for the preview, plus its full size and depth
        private sealed class Thumbnail
        {
            public Hash128 Hash;
            public int Width, Height, BitDepth;
            public bool HasAlpha;
            public Color[] Small;
            public int SmallWidth, SmallHeight;
            public string Error;
        }

        [SerializeField] private MaskPreset preset;
        [SerializeField] private RowState[] rows = NewRows();
        [SerializeField] private string[] customLabels = { "Red", "Green", "Blue", "Alpha" };
        [SerializeField] private bool customSrgb;
        [SerializeField] private bool alphaIsTransparency;
        [SerializeField] private int constantWidth = 1024;
        [SerializeField] private int constantHeight = 1024;
        [SerializeField] private string outputPath;
        [SerializeField] private bool customOutput;
        [SerializeField] private int previewMode;
        [SerializeField] private bool initialised;

        private readonly Dictionary<string, Thumbnail> thumbnails = new Dictionary<string, Thumbnail>();
        private List<string> fillNotes = new List<string>();
        private string error;
        private Texture2D preview;
        private Vector2 scroll;

        [MenuItem("Tools/Texture Tools/Mask Texture Maker")]
        public static void Open()
        {
            GetWindow<MaskTextureMakerWindow>("Mask Texture Maker");
        }

        [MenuItem("Assets/Texture Tools/Mask Texture Maker", true)]
        private static bool CanOpenForSelection()
        {
            return NormalGreenFlip.SelectedTexturePaths().Length > 0;
        }

        //Opens with the selected textures filled into the rows by name
        [MenuItem("Assets/Texture Tools/Mask Texture Maker")]
        private static void OpenForSelection()
        {
            var window = GetWindow<MaskTextureMakerWindow>("Mask Texture Maker");
            window.FillFromSelection();
        }

        private static RowState[] NewRows()
        {
            return new[] { new RowState(), new RowState(), new RowState(), new RowState() };
        }

        private PresetInfo Info => MaskTextureMaker.Info(preset);
        private bool Srgb => preset == MaskPreset.Custom ? customSrgb : Info.Srgb;

        private void OnEnable()
        {
            //A new window starts on the last preset used; one restored after a script reload keeps its rows
            if (!initialised || rows == null || rows.Length != 4)
            {
                preset = (MaskPreset)EditorPrefs.GetInt(PresetPref, (int)MaskPreset.MaskMap);
                ResetRows();
                initialised = true;
            }
            EditorApplication.delayCall += LoadThumbnails;
        }

        private void OnDisable()
        {
            Previews.Destroy(ref preview);
        }

        private void ResetRows()
        {
            rows = NewRows();
            var slots = Info.Slots;
            for (int c = 0; c < 4; c++) rows[c].Value = slots[c].Default;
            fillNotes = new List<string>();
            if (!customOutput) outputPath = null;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox(
                "Builds one texture from channels of other textures. Each row below is one channel of the result: give it a channel of a texture, or a value. " +
                "The values already filled in leave that part of the material unchanged.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var chosen = (MaskPreset)EditorGUILayout.Popup("Preset", (int)preset,
                Enum.GetValues(typeof(MaskPreset)).Cast<MaskPreset>().Select(p => MaskTextureMaker.Info(p).Name).ToArray());
            if (EditorGUI.EndChangeCheck() && chosen != preset)
            {
                preset = chosen;
                EditorPrefs.SetInt(PresetPref, (int)preset);
                ResetRows();
                RebuildPreview();
            }
            if (GUILayout.Button(new GUIContent("Fill from names", "Fill the rows from the textures selected in the Project window, matched by name (Metallic, AO, Roughness, Normal...)."), GUILayout.Width(110f)))
                FillFromSelection();
            EditorGUILayout.EndHorizontal();

            foreach (string note in fillNotes) EditorGUILayout.LabelField(note, EditorStyles.miniLabel);

            if (preset == MaskPreset.Custom) customSrgb = EditorGUILayout.Toggle(new GUIContent("sRGB (colour data)", "On for colour, off for data such as masks and normals."), customSrgb);
            if (Info.OffersTransparency) alphaIsTransparency = EditorGUILayout.Toggle(new GUIContent("A is transparency", "Tick if the alpha is transparency rather than data such as height."), alphaIsTransparency);

            EditorGUILayout.Space();
            var size = CurrentSize();
            for (int c = 0; c < 4; c++) DrawRow(c, size);

            EditorGUILayout.Space();
            var sources = Sources();
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(MaskTextureMaker.Describe(sources), EditorStyles.wordWrappedLabel);
            if (sources.Any(s => s.UsesTexture))
            {
                EditorGUILayout.LabelField("Size", size.x + " x " + size.y + " (largest input), " + (CurrentBitDepth() > 8 ? "16-bit" : "8-bit") + ", " + (Srgb ? "sRGB" : "linear"));
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                constantWidth = Mathf.Clamp(EditorGUILayout.IntField("Size", constantWidth), 1, 16384);
                constantHeight = Mathf.Clamp(EditorGUILayout.IntField(constantHeight, GUILayout.Width(80f)), 1, 16384);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.BeginChangeCheck();
            previewMode = GUILayout.Toolbar(previewMode, PreviewModes);
            if (EditorGUI.EndChangeCheck()) RebuildPreview();
            if (preview != null)
            {
                Rect rect = GUILayoutUtility.GetRect(220f, 220f, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit, false);
            }

            DrawOutputField(sources);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(outputPath)))
            {
                if (GUILayout.Button("Make", GUILayout.Height(28f))) MakeNow();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawRow(int c, Vector2Int outputSize)
        {
            var row = rows[c];
            var slot = Info.Slots[c];

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(MaskTextureMaker.ChannelNames[c], EditorStyles.boldLabel, GUILayout.Width(18f));
            if (preset == MaskPreset.Custom) customLabels[c] = EditorGUILayout.TextField(customLabels[c]);
            else EditorGUILayout.LabelField(slot.Label, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(row.UseTexture, "Texture", EditorStyles.radioButton, GUILayout.Width(70f))) row.UseTexture = true;
            using (new EditorGUI.DisabledScope(!row.UseTexture))
            {
                row.Texture = (Texture2D)EditorGUILayout.ObjectField(row.Texture, typeof(Texture2D), false);
                EditorGUILayout.LabelField("channel", GUILayout.Width(50f));
                row.Channel = (AlphaChannel)EditorGUILayout.Popup((int)row.Channel, ChannelChoices, GUILayout.Width(80f));
                row.Invert = GUILayout.Toggle(row.Invert, "Invert", GUILayout.Width(60f));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!row.UseTexture, "Value", EditorStyles.radioButton, GUILayout.Width(70f))) row.UseTexture = false;
            using (new EditorGUI.DisabledScope(row.UseTexture))
            {
                row.Value = EditorGUILayout.Slider(row.Value, 0f, 1f);
                string note = preset == MaskPreset.Custom ? "" : slot.Default.ToString("0.0") + " = " + slot.DefaultNote;
                EditorGUILayout.LabelField(note, EditorStyles.miniLabel, GUILayout.Width(140f));
            }
            EditorGUILayout.EndHorizontal();
            //Reads any newly picked texture, then rebuilds the preview
            if (EditorGUI.EndChangeCheck())
            {
                if (!customOutput) outputPath = null;
                EditorApplication.delayCall += LoadThumbnails;
            }

            if (row.UseTexture && row.Texture != null)
            {
                string path = AssetDatabase.GetAssetPath(row.Texture);
                if (thumbnails.TryGetValue(path, out var thumbnail))
                {
                    if (thumbnail.Error != null)
                    {
                        EditorGUILayout.HelpBox(thumbnail.Error, MessageType.Error);
                    }
                    else
                    {
                        if (row.Channel == AlphaChannel.Alpha && !thumbnail.HasAlpha)
                            EditorGUILayout.HelpBox(row.Texture.name + " has no alpha channel, so this row would be solid white.", MessageType.Warning);
                        if (!PackAlpha.SameAspect(thumbnail.Width, thumbnail.Height, outputSize.x, outputSize.y))
                            EditorGUILayout.HelpBox(row.Texture.name + " is a different shape from the result (" + thumbnail.Width + " x " + thumbnail.Height + "), so it will be stretched to fit.", MessageType.Warning);
                    }
                }
            }
            else if (row.UseTexture)
            {
                EditorGUILayout.HelpBox("No texture: the row will be 0. Pick a texture, or switch to Value.", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawOutputField(RowSource[] sources)
        {
            if (!customOutput) outputPath = MaskTextureMaker.DefaultOutputPath(sources, Info.Suffix);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Output", string.IsNullOrEmpty(outputPath) ? "choose with ..." : outputPath);
            if (GUILayout.Button("...", GUILayout.Width(28f)))
            {
                string start = outputPath ?? "Assets/New" + Info.Suffix + ".png";
                string chosen = EditorUtility.SaveFilePanelInProject("Save packed texture", Path.GetFileNameWithoutExtension(start), "png",
                    "Where should the texture go?", Path.GetDirectoryName(start).Replace('\\', '/'));
                if (!string.IsNullOrEmpty(chosen))
                {
                    outputPath = chosen;
                    customOutput = true;
                }
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
            if (customOutput && GUILayout.Button("Use the default output name", EditorStyles.miniButton)) customOutput = false;
        }

        //Rows with no texture picked count as their value; a texture row with no texture counts as 0
        private RowSource[] Sources()
        {
            return rows.Select(r =>
            {
                if (!r.UseTexture) return RowSource.Constant(r.Value);
                if (r.Texture == null) return RowSource.Constant(0f);
                return RowSource.FromTexture(AssetDatabase.GetAssetPath(r.Texture), r.Channel, r.Invert);
            }).ToArray();
        }

        private Vector2Int CurrentSize()
        {
            var size = new Vector2Int(0, 0);
            foreach (var thumbnail in ActiveThumbnails())
            {
                if ((long)thumbnail.Width * thumbnail.Height > (long)size.x * size.y) size = new Vector2Int(thumbnail.Width, thumbnail.Height);
            }
            return size.x > 0 ? size : new Vector2Int(constantWidth, constantHeight);
        }

        private int CurrentBitDepth()
        {
            return ActiveThumbnails().Any(t => t.BitDepth > 8) ? 16 : 8;
        }

        private IEnumerable<Thumbnail> ActiveThumbnails()
        {
            foreach (var row in rows)
            {
                if (!row.UseTexture || row.Texture == null) continue;
                if (thumbnails.TryGetValue(AssetDatabase.GetAssetPath(row.Texture), out var thumbnail) && thumbnail.Error == null) yield return thumbnail;
            }
        }

        private void FillFromSelection()
        {
            string[] paths = NormalGreenFlip.SelectedTexturePaths();
            if (paths.Length == 0)
            {
                fillNotes = new List<string> { "Select the textures in the Project window first." };
                return;
            }

            var current = Sources();
            var filled = MaskTextureMaker.FillFromNames(preset, current, paths, out fillNotes);
            for (int c = 0; c < 4; c++)
            {
                if (filled[c].TexturePath == current[c].TexturePath || !filled[c].UsesTexture) continue;
                rows[c].UseTexture = true;
                rows[c].Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(filled[c].TexturePath);
                rows[c].Channel = filled[c].Channel;
                rows[c].Invert = filled[c].Invert;
                fillNotes.Insert(0, MaskTextureMaker.ChannelNames[c] + " " + Info.Slots[c].Label + " <- " + Path.GetFileName(filled[c].TexturePath) + (filled[c].Invert ? " (inverted)" : ""));
            }
            if (!customOutput) outputPath = null;
            EditorApplication.delayCall += LoadThumbnails;
            Repaint();
        }

        //Reads any input not read yet (or changed since) into a small preview copy. Scheduled with delayCall
        //because reading imports a temporary file, so the window may have closed since
        private void LoadThumbnails()
        {
            if (this == null) return;
            foreach (var row in rows)
            {
                if (!row.UseTexture || row.Texture == null) continue;
                string path = AssetDatabase.GetAssetPath(row.Texture);
                var hash = AssetDatabase.GetAssetDependencyHash(path);
                if (thumbnails.TryGetValue(path, out var existing) && existing.Hash == hash) continue;
                thumbnails[path] = MakeThumbnail(path, hash);
            }
            RebuildPreview();
        }

        private static Thumbnail MakeThumbnail(string path, Hash128 hash)
        {
            var thumbnail = new Thumbnail { Hash = hash };
            try
            {
                var image = SourcePixels.Read(path);
                thumbnail.Width = image.Width;
                thumbnail.Height = image.Height;
                thumbnail.BitDepth = image.BitDepth;
                thumbnail.HasAlpha = image.HasAlpha;
                float scale = Mathf.Min(1f, Previews.MaxSize / (float)Mathf.Max(image.Width, image.Height));
                thumbnail.SmallWidth = Mathf.Max(1, Mathf.RoundToInt(image.Width * scale));
                thumbnail.SmallHeight = Mathf.Max(1, Mathf.RoundToInt(image.Height * scale));
                thumbnail.Small = new Color[thumbnail.SmallWidth * thumbnail.SmallHeight];
                for (int y = 0; y < thumbnail.SmallHeight; y++)
                    for (int x = 0; x < thumbnail.SmallWidth; x++)
                        thumbnail.Small[y * thumbnail.SmallWidth + x] = Previews.Nearest(image, (x + 0.5f) / thumbnail.SmallWidth, (y + 0.5f) / thumbnail.SmallHeight);
            }
            catch (Exception exception)
            {
                thumbnail.Error = exception.Message;
            }
            return thumbnail;
        }

        private void RebuildPreview()
        {
            Previews.Destroy(ref preview);
            var size = CurrentSize();
            var samplers = new Func<float, float, float>[4];
            for (int c = 0; c < 4; c++)
            {
                var row = rows[c];
                Thumbnail thumbnail = null;
                if (row.UseTexture && row.Texture != null) thumbnails.TryGetValue(AssetDatabase.GetAssetPath(row.Texture), out thumbnail);
                if (thumbnail != null && thumbnail.Error == null)
                {
                    var t = thumbnail;
                    var channel = row.Channel;
                    bool invert = row.Invert;
                    samplers[c] = (u, v) =>
                    {
                        int x = Mathf.Clamp((int)(u * t.SmallWidth), 0, t.SmallWidth - 1);
                        int y = Mathf.Clamp((int)(v * t.SmallHeight), 0, t.SmallHeight - 1);
                        float value = PackAlpha.Value(t.Small[y * t.SmallWidth + x], channel);
                        return invert ? 1f - value : value;
                    };
                }
                else
                {
                    float value = row.UseTexture ? 0f : row.Value;
                    samplers[c] = (u, v) => value;
                }
            }

            int mode = previewMode;
            preview = Previews.Make(size.x, size.y, (u, v) =>
            {
                if (mode == 0) return new Color(samplers[0](u, v), samplers[1](u, v), samplers[2](u, v), 1f);
                return Previews.Grey(samplers[mode - 1](u, v));
            });
            Repaint();
        }

        private void MakeNow()
        {
            error = null;
            try
            {
                string output = MaskTextureMaker.Make(Sources(), Srgb, Info.OffersTransparency && alphaIsTransparency,
                    constantWidth, constantHeight, outputPath, ExistingOutput.Ask);
                if (output == null) return;
                var result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
                Selection.activeObject = result;
                EditorGUIUtility.PingObject(result);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }
    }
}
