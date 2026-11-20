using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools
{
    //Normal map conventions. Unity uses OpenGL (green = +Y, "up"); DirectX has green pointing down
    public enum NormalConvention
    {
        OpenGL,
        DirectX,
    }

    //Converts a normal map between OpenGL and DirectX by inverting its green channel. The same
    //operation converts either way; the target convention only names the output file
    public static class NormalGreenFlip
    {
        public static readonly string[] ConventionLabels = { "OpenGL (Unity)", "DirectX" };

        public static void FlipGreen(Color[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i].g = 1f - pixels[i].g;
        }

        //<folder>/<name>_GL.png or _DX.png (an existing _GL / _DX suffix is swapped, not stacked); .exr for floating-point sources
        public static string DefaultOutputPath(string sourcePath, NormalConvention target, int bitDepth)
        {
            string folder = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            foreach (string suffix in new[] { "_GL", "_DX" })
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }
            string extension = bitDepth > 16 ? ".exr" : ".png";
            return folder + "/" + name + (target == NormalConvention.OpenGL ? "_GL" : "_DX") + extension;
        }

        public static OutputEncoding EncodingFor(int bitDepth)
        {
            if (bitDepth > 16) return OutputEncoding.Exr;
            return bitDepth > 8 ? OutputEncoding.Png16 : OutputEncoding.Png8;
        }

        //Things worth telling the user about a source before converting it
        public static List<string> Warnings(string sourcePath, PixelImage image)
        {
            var warnings = new List<string>();
            if (AssetImporter.GetAtPath(sourcePath) is TextureImporter importer && importer.textureType != TextureImporterType.NormalMap)
                warnings.Add(Path.GetFileName(sourcePath) + " isn't imported as a Normal map. It will still be converted, but check it really is one.");

            float min = 1f, max = 0f;
            foreach (var pixel in image.Pixels)
            {
                min = Mathf.Min(min, pixel.g);
                max = Mathf.Max(max, pixel.g);
            }
            if (max - min < 1f / 255f)
                warnings.Add(Path.GetFileName(sourcePath) + " has the same green value everywhere, so it is flat or isn't a normal map.");
            return warnings;
        }

        //Writes the flipped copy and returns its path, or null if the user cancelled. Pass image if the source was already read
        public static string Convert(string sourcePath, NormalConvention target, ExistingOutput existing, PixelImage image = null)
        {
            if (!AssetPaths.IsUnderAssets(sourcePath)) throw new ArgumentException(sourcePath + " is outside the Assets folder, so there's nowhere to write the result next to it.");
            if (image == null) image = SourcePixels.Read(sourcePath);

            string outputPath = DefaultOutputPath(sourcePath, target, image.BitDepth);
            //A source already named for the target convention would otherwise be its own output
            if (outputPath == sourcePath) outputPath = AssetDatabase.GenerateUniqueAssetPath(outputPath);
            outputPath = TextureWriter.ResolveOutputPath(outputPath, existing);
            if (outputPath == null) return null;

            var pixels = (Color[])image.Pixels.Clone();
            FlipGreen(pixels);
            //A DDS source has no texture type to copy, so the output is made a Normal map here
            bool typeFromSource = AssetImporter.GetAtPath(sourcePath) is TextureImporter;
            TextureWriter.Write(outputPath, image.Width, image.Height, pixels, EncodingFor(image.BitDepth), image.HasAlpha, sourcePath,
                importer => { if (!typeFromSource) importer.textureType = TextureImporterType.NormalMap; });
            Debug.Log("Normal Green Flip: " + sourcePath + " -> " + outputPath);
            return outputPath;
        }

        [MenuItem("Assets/Texture Tools/Flip Normal Green", true)]
        private static bool CanFlipSelected()
        {
            return SelectedTexturePaths().Length > 0;
        }

        [MenuItem("Assets/Texture Tools/Flip Normal Green")]
        private static void FlipSelected()
        {
            string[] paths = SelectedTexturePaths();
            int choice = EditorUtility.DisplayDialogComplex(
                "Flip Normal Green",
                "Invert the green channel of " + paths.Length + " texture(s), writing each result as a new file next to its source.\n\n" +
                "Which convention should the results be in? Unity uses OpenGL; DirectX maps (Unreal, Substance's default export, many store assets) need converting to it.",
                ConventionLabels[0], "Cancel", ConventionLabels[1]);
            if (choice == 1) return;
            var target = choice == 0 ? NormalConvention.OpenGL : NormalConvention.DirectX;

            var outputs = new List<Object>();
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    string path = paths[i];
                    EditorUtility.DisplayProgressBar("Flip Normal Green", path, i / (float)paths.Length);
                    try
                    {
                        var image = SourcePixels.Read(path);
                        foreach (string warning in Warnings(path, image)) Debug.LogWarning("Normal Green Flip: " + warning);
                        string output = Convert(path, target, ExistingOutput.Ask, image);
                        if (output != null) outputs.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(output));
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError("Normal Green Flip failed for " + path + ": " + exception.Message);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (outputs.Count > 0) Selection.objects = outputs.ToArray();
        }

        internal static string[] SelectedTexturePaths()
        {
            return Selection.GetFiltered<Texture2D>(SelectionMode.Assets)
                .Select(AssetDatabase.GetAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct()
                .ToArray();
        }
    }

    public sealed class NormalGreenFlipWindow : EditorWindow
    {
        [SerializeField] private Texture2D source;
        [SerializeField] private NormalConvention target = NormalConvention.OpenGL;

        private PixelImage image;
        private string imagePath;
        private Hash128 imageHash;
        private List<string> warnings = new List<string>();
        private string error;
        private Texture2D greenBefore;
        private Texture2D greenAfter;

        [MenuItem("Tools/Texture Tools/Normal Green Flip")]
        public static void Open()
        {
            GetWindow<NormalGreenFlipWindow>("Normal Green Flip");
        }

        private void OnEnable()
        {
            if (source != null) EditorApplication.delayCall += Load;
        }

        private void OnDisable()
        {
            Preview(null);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Inverts the green channel, converting a normal map between OpenGL and DirectX. Unity uses OpenGL (green = up). " +
                "DirectX maps (Unreal, Substance's default export, many store assets) need converting to it. The result is a new file next to the source.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            source = (Texture2D)EditorGUILayout.ObjectField("Normal map", source, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += Load;

            target = (NormalConvention)EditorGUILayout.Popup("Convert to", (int)target, NormalGreenFlip.ConventionLabels);

            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (string warning in warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);

            if (image != null)
            {
                EditorGUILayout.LabelField("Source", image.Width + " x " + image.Height + ", " + DepthLabel(image.BitDepth));
                EditorGUILayout.LabelField("Output", NormalGreenFlip.DefaultOutputPath(imagePath, target, image.BitDepth));
                Previews.DrawRow(180f, ("Green now", greenBefore), ("Green after", greenAfter));
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(image == null))
            {
                if (GUILayout.Button("Convert", GUILayout.Height(28f))) ConvertNow();
            }
        }

        private void ConvertNow()
        {
            try
            {
                if (AssetDatabase.GetAssetDependencyHash(imagePath) != imageHash) Load();
                if (image == null) return;
                string output = NormalGreenFlip.Convert(imagePath, target, ExistingOutput.Ask, image);
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

        private void Load()
        {
            //Scheduled with delayCall, so the window may have closed since
            if (this == null) return;
            image = null;
            imagePath = null;
            error = null;
            warnings = new List<string>();
            Preview(null);

            if (source != null)
            {
                string path = AssetDatabase.GetAssetPath(source);
                if (!AssetPaths.IsUnderAssets(path))
                {
                    error = "Pick a texture inside the Assets folder; the result is written next to it.";
                }
                else
                {
                    try
                    {
                        image = SourcePixels.Read(path);
                        imagePath = path;
                        imageHash = AssetDatabase.GetAssetDependencyHash(path);
                        warnings = NormalGreenFlip.Warnings(path, image);
                        Preview(image);
                    }
                    catch (Exception exception)
                    {
                        error = exception.Message;
                    }
                }
            }
            Repaint();
        }

        private void Preview(PixelImage from)
        {
            Previews.Destroy(ref greenBefore);
            Previews.Destroy(ref greenAfter);
            if (from == null) return;
            greenBefore = Previews.Make(from.Width, from.Height, (u, v) => Previews.Grey(Previews.Nearest(from, u, v).g));
            greenAfter = Previews.Make(from.Width, from.Height, (u, v) => Previews.Grey(1f - Previews.Nearest(from, u, v).g));
        }

        internal static string DepthLabel(int bitDepth)
        {
            return bitDepth > 16 ? "floating point" : bitDepth + "-bit";
        }
    }
}
