using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools
{
    //Which value of the transparency map becomes the alpha
    public enum AlphaChannel
    {
        Luminance,
        Red,
        Green,
        Blue,
        Alpha,
    }

    //Merges a colour map and a separate transparency map into one RGBA texture
    public static class PackAlpha
    {
        //Explicit importer formats with no alpha channel; an output with one of these would lose the packed alpha
        private static readonly HashSet<string> FormatsWithoutAlpha = new HashSet<string>
        {
            "DXT1", "DXT1Crunched", "RGB24", "RGB16", "RGB48", "R8", "R16", "RG16", "RG32", "RHalf", "RGHalf", "RFloat", "RGFloat",
            "BC4", "BC5", "BC6H", "RGB9E5", "EAC_R", "EAC_R_SIGNED", "EAC_RG", "EAC_RG_SIGNED",
            "ETC_RGB4", "ETC_RGB4Crunched", "ETC2_RGB4", "PVRTC_RGB2", "PVRTC_RGB4",
        };

        public static float Value(Color pixel, AlphaChannel channel)
        {
            switch (channel)
            {
                case AlphaChannel.Red: return pixel.r;
                case AlphaChannel.Green: return pixel.g;
                case AlphaChannel.Blue: return pixel.b;
                case AlphaChannel.Alpha: return pixel.a;
                default: return 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
            }
        }

        public static float[] ExtractAlpha(Color[] pixels, AlphaChannel channel, bool invert)
        {
            var alpha = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float value = Value(pixels[i], channel);
                alpha[i] = invert ? 1f - value : value;
            }
            return alpha;
        }

        //Bilinear resize with pixel centres aligned; edges clamp
        public static float[] Resample(float[] source, int sourceWidth, int sourceHeight, int width, int height)
        {
            if (sourceWidth == width && sourceHeight == height) return (float[])source.Clone();

            var result = new float[width * height];
            float scaleX = sourceWidth / (float)width;
            float scaleY = sourceHeight / (float)height;
            for (int y = 0; y < height; y++)
            {
                float sourceY = (y + 0.5f) * scaleY - 0.5f;
                for (int x = 0; x < width; x++)
                {
                    float sourceX = (x + 0.5f) * scaleX - 0.5f;
                    result[y * width + x] = Bilinear(source, sourceWidth, sourceHeight, sourceX, sourceY);
                }
            }
            return result;
        }

        //Sample at a position in source pixel coordinates (pixel centres at whole numbers)
        public static float Bilinear(float[] source, int width, int height, float x, float y)
        {
            x = Mathf.Clamp(x, 0f, width - 1);
            y = Mathf.Clamp(y, 0f, height - 1);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Mathf.Min(x0 + 1, width - 1), y1 = Mathf.Min(y0 + 1, height - 1);
            float tx = x - x0, ty = y - y0;
            float bottom = Mathf.Lerp(source[y0 * width + x0], source[y0 * width + x1], tx);
            float top = Mathf.Lerp(source[y1 * width + x0], source[y1 * width + x1], tx);
            return Mathf.Lerp(bottom, top, ty);
        }

        public static Color[] Pack(Color[] colour, float[] alpha)
        {
            if (colour.Length != alpha.Length) throw new ArgumentException("Colour and alpha must have the same number of pixels.");
            var packed = new Color[colour.Length];
            for (int i = 0; i < colour.Length; i++) packed[i] = new Color(colour[i].r, colour[i].g, colour[i].b, alpha[i]);
            return packed;
        }

        public static string DefaultOutputPath(string colourPath)
        {
            string folder = Path.GetDirectoryName(colourPath).Replace('\\', '/');
            return folder + "/" + Path.GetFileNameWithoutExtension(colourPath) + "_Alpha.png";
        }

        public static bool SameAspect(int widthA, int heightA, int widthB, int heightB)
        {
            return Mathf.Abs(widthA / (float)heightA - widthB / (float)heightB) < 0.01f;
        }

        //Writes the packed texture and returns its path, or null if the user cancelled.
        //Pass colour / transparency if those sources were already read
        public static string Run(string colourPath, string transparencyPath, AlphaChannel channel, bool invert, float cutoff,
            string outputPath, ExistingOutput existing, PixelImage colour = null, PixelImage transparency = null)
        {
            if (!AssetPaths.IsUnderAssets(outputPath)) throw new ArgumentException("The output must be inside the Assets folder: " + outputPath);
            if (colour == null) colour = SourcePixels.Read(colourPath);
            if (transparency == null) transparency = SourcePixels.Read(transparencyPath);

            float[] alpha = ExtractAlpha(transparency.Pixels, channel, invert);
            alpha = Resample(alpha, transparency.Width, transparency.Height, colour.Width, colour.Height);
            Color[] packed = Pack(colour.Pixels, alpha);

            outputPath = TextureWriter.ResolveOutputPath(outputPath, existing);
            if (outputPath == null) return null;
            if (outputPath == colourPath || outputPath == transparencyPath)
                throw new InvalidOperationException("The output can't replace one of its own sources: " + outputPath);

            var encoding = colour.BitDepth > 8 ? OutputEncoding.Png16 : OutputEncoding.Png8;
            TextureWriter.Write(outputPath, colour.Width, colour.Height, packed, encoding, true, colourPath, importer => ApplyAlphaSettings(importer, cutoff));
            Debug.Log("Pack Alpha: " + colourPath + " + " + transparencyPath + " -> " + outputPath);
            return outputPath;
        }

        //Alpha from the file, edge colour spread into transparent texels (no dark fringes), and mips that keep
        //the same alpha-tested coverage as the full-size texture (hair and lashes don't thin out at a distance)
        public static void ApplyAlphaSettings(TextureImporter importer, float cutoff)
        {
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipMapsPreserveCoverage = true;
            importer.alphaTestReferenceValue = cutoff;

            foreach (string platform in TextureWriter.PlatformNames())
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                if (!settings.overridden || !FormatsWithoutAlpha.Contains(settings.format.ToString())) continue;
                Debug.Log("Pack Alpha: the " + platform + " override's format " + settings.format + " has no alpha, so the output uses Automatic instead.");
                settings.format = TextureImporterFormat.Automatic;
                importer.SetPlatformTextureSettings(settings);
            }
        }
    }

    public sealed class PackAlphaWindow : EditorWindow
    {
        [SerializeField] private Texture2D colourTexture;
        [SerializeField] private Texture2D transparencyTexture;
        [SerializeField] private AlphaChannel channel = AlphaChannel.Luminance;
        [SerializeField] private bool invert;
        [SerializeField] private float cutoff = 0.5f;
        [SerializeField] private string outputPath;
        [SerializeField] private bool customOutput;

        private readonly Source colour = new Source();
        private readonly Source transparency = new Source();
        private Texture2D packedPreview;
        private Texture2D alphaPreview;
        private string error;
        private Vector2 scroll;

        //One input texture and the pixels read from it
        private sealed class Source
        {
            public PixelImage Image;
            public string Path;
            public Hash128 Hash;
            public string Error;
            public bool HasAlphaChannel;

            public void Load(Texture2D texture)
            {
                Image = null;
                Path = null;
                Error = null;
                HasAlphaChannel = false;
                if (texture == null) return;

                string path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path))
                {
                    Error = texture.name + " isn't a texture file in the project.";
                    return;
                }
                try
                {
                    Image = SourcePixels.Read(path);
                    Path = path;
                    Hash = AssetDatabase.GetAssetDependencyHash(path);
                    HasAlphaChannel = Image.HasAlpha;
                }
                catch (Exception exception)
                {
                    Error = exception.Message;
                }
            }

            public bool IsStale => Path != null && AssetDatabase.GetAssetDependencyHash(Path) != Hash;
        }

        [MenuItem("Tools/Texture Tools/Pack Alpha")]
        public static void Open()
        {
            GetWindow<PackAlphaWindow>("Pack Alpha");
        }

        [MenuItem("Assets/Texture Tools/Pack Alpha...", true)]
        private static bool CanOpenForSelection()
        {
            return NormalGreenFlip.SelectedTexturePaths().Length == 1;
        }

        [MenuItem("Assets/Texture Tools/Pack Alpha...")]
        private static void OpenForSelection()
        {
            //A newly opened window loads its inputs from OnEnable; an open one needs telling
            bool alreadyOpen = HasOpenInstances<PackAlphaWindow>();
            var window = GetWindow<PackAlphaWindow>("Pack Alpha");
            window.colourTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(NormalGreenFlip.SelectedTexturePaths()[0]);
            window.customOutput = false;
            if (alreadyOpen) EditorApplication.delayCall += window.LoadColour;
        }

        private void OnEnable()
        {
            EditorApplication.delayCall += LoadAll;
        }

        private void OnDisable()
        {
            Previews.Destroy(ref packedPreview);
            Previews.Destroy(ref alphaPreview);
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox(
                "Puts a separate transparency map into the alpha channel of a colour map, written as a new PNG. " +
                "Shaders like Standard read transparency only from the colour texture's alpha.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            colourTexture = (Texture2D)EditorGUILayout.ObjectField("Colour map", colourTexture, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (!customOutput) outputPath = null;
                EditorApplication.delayCall += LoadColour;
            }
            if (colour.Error != null) EditorGUILayout.HelpBox(colour.Error, MessageType.Error);
            if (colour.Image != null)
            {
                EditorGUILayout.LabelField(" ", colour.Image.Width + " x " + colour.Image.Height + ", " + NormalGreenFlipWindow.DepthLabel(colour.Image.BitDepth));
                if (colour.HasAlphaChannel) EditorGUILayout.HelpBox("This texture already has an alpha channel. It will be replaced.", MessageType.Warning);
            }

            EditorGUI.BeginChangeCheck();
            transparencyTexture = (Texture2D)EditorGUILayout.ObjectField("Transparency map", transparencyTexture, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += LoadTransparency;
            if (transparency.Error != null) EditorGUILayout.HelpBox(transparency.Error, MessageType.Error);
            if (transparency.Image != null)
            {
                EditorGUILayout.LabelField(" ", transparency.Image.Width + " x " + transparency.Image.Height);
                DrawSizeMessages();
            }

            EditorGUI.BeginChangeCheck();
            channel = (AlphaChannel)EditorGUILayout.EnumPopup("Alpha from", channel);
            invert = EditorGUILayout.Toggle("Invert", invert);
            if (EditorGUI.EndChangeCheck()) RebuildPreview();
            if (channel == AlphaChannel.Alpha && transparency.Image != null && !transparency.HasAlphaChannel)
                EditorGUILayout.HelpBox("The transparency map has no alpha channel, so the result would be fully opaque.", MessageType.Warning);

            cutoff = EditorGUILayout.Slider(new GUIContent("Cutoff", "The alpha-test cutoff your material will use. Mip maps keep the same coverage at this value, so cut-out detail doesn't thin out at a distance."), cutoff, 0f, 1f);

            DrawOutputField();

            if (packedPreview != null) Previews.DrawRow(200f, ("Result", packedPreview), ("Alpha", alphaPreview));
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(colour.Image == null || transparency.Image == null || string.IsNullOrEmpty(outputPath)))
            {
                if (GUILayout.Button("Pack", GUILayout.Height(28f))) PackNow();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSizeMessages()
        {
            if (colour.Image == null) return;
            var a = colour.Image;
            var b = transparency.Image;
            if (a.Width == b.Width && a.Height == b.Height) return;
            EditorGUILayout.HelpBox("It will be resized to " + a.Width + " x " + a.Height + " to match the colour map.", MessageType.Info);
            if (!PackAlpha.SameAspect(a.Width, a.Height, b.Width, b.Height))
                EditorGUILayout.HelpBox("The two maps have different shapes. They share UVs, so the transparency will be stretched to fit; check they belong together.", MessageType.Warning);
        }

        private void DrawOutputField()
        {
            if (!customOutput && colour.Path != null) outputPath = PackAlpha.DefaultOutputPath(colour.Path);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Output", string.IsNullOrEmpty(outputPath) ? "-" : outputPath);
            using (new EditorGUI.DisabledScope(colour.Path == null))
            {
                if (GUILayout.Button("...", GUILayout.Width(28f)))
                {
                    string start = string.IsNullOrEmpty(outputPath) ? PackAlpha.DefaultOutputPath(colour.Path) : outputPath;
                    string chosen = EditorUtility.SaveFilePanelInProject("Save packed texture", Path.GetFileNameWithoutExtension(start), "png",
                        "Where should the packed texture go?", Path.GetDirectoryName(start).Replace('\\', '/'));
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        outputPath = chosen;
                        customOutput = true;
                    }
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
            if (customOutput && GUILayout.Button("Use the default output name", EditorStyles.miniButton)) customOutput = false;
        }

        private void PackNow()
        {
            error = null;
            try
            {
                if (colour.IsStale) colour.Load(colourTexture);
                if (transparency.IsStale) transparency.Load(transparencyTexture);
                if (colour.Image == null || transparency.Image == null) return;

                string output = PackAlpha.Run(colour.Path, transparency.Path, channel, invert, cutoff, outputPath, ExistingOutput.Ask, colour.Image, transparency.Image);
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

        private void LoadAll()
        {
            if (this == null) return;
            colour.Load(colourTexture);
            transparency.Load(transparencyTexture);
            RebuildPreview();
        }

        //Both loads are scheduled with delayCall (they import a temporary file), so the window may have closed since
        private void LoadColour()
        {
            if (this == null) return;
            colour.Load(colourTexture);
            RebuildPreview();
        }

        private void LoadTransparency()
        {
            if (this == null) return;
            transparency.Load(transparencyTexture);
            RebuildPreview();
        }

        private void RebuildPreview()
        {
            Previews.Destroy(ref packedPreview);
            Previews.Destroy(ref alphaPreview);
            var c = colour.Image;
            var t = transparency.Image;
            if (c != null && t != null)
            {
                float[] alpha = PackAlpha.ExtractAlpha(t.Pixels, channel, invert);
                float AlphaAt(float u, float v) => PackAlpha.Bilinear(alpha, t.Width, t.Height, u * t.Width - 0.5f, v * t.Height - 0.5f);

                packedPreview = Previews.Make(c.Width, c.Height, (u, v) =>
                {
                    Color pixel = Previews.Nearest(c, u, v);
                    Color result = Color.Lerp(Previews.Checker(u, v), pixel, AlphaAt(u, v));
                    result.a = 1f;
                    return result;
                });
                alphaPreview = Previews.Make(c.Width, c.Height, (u, v) => Previews.Grey(AlphaAt(u, v)));
            }
            Repaint();
        }
    }
}
