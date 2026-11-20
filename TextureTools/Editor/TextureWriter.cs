using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools
{
    public enum OutputEncoding
    {
        Png8,
        Png16,
        Exr,
    }

    //What to do when the output file already exists
    public enum ExistingOutput
    {
        Ask,
        Replace,
        NewName,
        Cancel,
    }

    //Encodes pixels to a file inside the project and imports it with settings copied from a source texture
    public static class TextureWriter
    {
        //The path to write to, or null to cancel. Replace writes over the existing file, keeping its GUID
        public static string ResolveOutputPath(string outputPath, ExistingOutput existing)
        {
            if (!File.Exists(AssetPaths.ToFullPath(outputPath))) return outputPath;

            if (existing == ExistingOutput.Ask)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Output already exists",
                    outputPath + " already exists.\n\nReplace keeps the file's GUID, so materials already using it pick up the new pixels.",
                    "Replace", "Cancel", "Save as new name");
                existing = choice == 0 ? ExistingOutput.Replace : choice == 2 ? ExistingOutput.NewName : ExistingOutput.Cancel;
            }

            switch (existing)
            {
                case ExistingOutput.Replace: return outputPath;
                case ExistingOutput.NewName: return AssetDatabase.GenerateUniqueAssetPath(outputPath);
                default: return null;
            }
        }

        //Writes the pixels (rows bottom-up) to outputPath and imports it. The importer gets settingsSourcePath's
        //texture and platform settings (or, for a DDS source, the few settings its importer has; none if it's null),
        //then adjust (if any) runs on it
        public static void Write(string outputPath, int width, int height, Color[] pixels, OutputEncoding encoding, bool withAlpha,
            string settingsSourcePath, Action<TextureImporter> adjust)
        {
            if (!AssetPaths.IsUnderAssets(outputPath)) throw new ArgumentException("The output must be inside the project's Assets folder: " + outputPath);

            byte[] bytes = Encode(width, height, pixels, encoding, withAlpha);
            File.WriteAllBytes(AssetPaths.ToFullPath(outputPath), bytes);

            var settingsSource = string.IsNullOrEmpty(settingsSourcePath) ? null : AssetImporter.GetAtPath(settingsSourcePath);
            ImportHooks.PendingOutputs[outputPath] = importer =>
            {
                if (settingsSource is TextureImporter textureSource) CopySettings(textureSource, importer);
                else if (settingsSource is IHVImageFormatImporter ddsSource) CopySettings(ddsSource, importer);
                adjust?.Invoke(importer);
            };
            try
            {
                AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                ImportHooks.PendingOutputs.Remove(outputPath);
            }
        }

        public static byte[] Encode(int width, int height, Color[] pixels, OutputEncoding encoding, bool withAlpha)
        {
            if (pixels.Length != width * height) throw new ArgumentException("Expected " + width * height + " pixels, got " + pixels.Length);

            Texture2D texture = null;
            try
            {
                switch (encoding)
                {
                    case OutputEncoding.Png8:
                    {
                        int channels = withAlpha ? 4 : 3;
                        var data = new byte[pixels.Length * channels];
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            int o = i * channels;
                            data[o] = To8(pixels[i].r);
                            data[o + 1] = To8(pixels[i].g);
                            data[o + 2] = To8(pixels[i].b);
                            if (withAlpha) data[o + 3] = To8(pixels[i].a);
                        }
                        texture = new Texture2D(width, height, withAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
                        texture.SetPixelData(data, 0);
                        texture.Apply(false);
                        return Checked(texture.EncodeToPNG(), encoding);
                    }
                    case OutputEncoding.Png16:
                    {
                        int channels = withAlpha ? 4 : 3;
                        var data = new ushort[pixels.Length * channels];
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            int o = i * channels;
                            data[o] = To16(pixels[i].r);
                            data[o + 1] = To16(pixels[i].g);
                            data[o + 2] = To16(pixels[i].b);
                            if (withAlpha) data[o + 3] = To16(pixels[i].a);
                        }
                        return Png16Encoder.Encode(width, height, data, channels);
                    }
                    default:
                    {
                        texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                        texture.SetPixels(pixels);
                        texture.Apply(false);
                        return Checked(texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP), encoding);
                    }
                }
            }
            finally
            {
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        //Texture settings (type, wrap, filter, mips, alpha...) and every platform override
        public static void CopySettings(TextureImporter from, TextureImporter to)
        {
            var settings = new TextureImporterSettings();
            from.ReadTextureSettings(settings);
            to.SetTextureSettings(settings);

            to.SetPlatformTextureSettings(from.GetDefaultPlatformTextureSettings());
            foreach (string platform in PlatformNames())
            {
                var platformSettings = from.GetPlatformTextureSettings(platform);
                if (platformSettings.overridden) to.SetPlatformTextureSettings(platformSettings);
                else to.ClearPlatformTextureSettings(platform);
            }
        }

        //A DDS importer only has wrap, filter, readability and mip streaming settings
        public static void CopySettings(IHVImageFormatImporter from, TextureImporter to)
        {
            to.wrapModeU = from.wrapModeU;
            to.wrapModeV = from.wrapModeV;
            to.wrapModeW = from.wrapModeW;
            to.filterMode = from.filterMode;
            to.isReadable = from.isReadable;
            to.streamingMipmaps = from.streamingMipmaps;
            to.streamingMipmapsPriority = from.streamingMipmapsPriority;
        }

        //The importer's name for every platform this Editor knows ("Standalone", "Android", "iPhone"...)
        public static IEnumerable<string> PlatformNames()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            var fromFields = typeof(NamedBuildTarget).GetFields(flags)
                .Where(field => field.FieldType == typeof(NamedBuildTarget))
                .Select(field => (NamedBuildTarget)field.GetValue(null));
            var fromProperties = typeof(NamedBuildTarget).GetProperties(flags)
                .Where(property => property.PropertyType == typeof(NamedBuildTarget) && property.GetIndexParameters().Length == 0)
                .Select(property => (NamedBuildTarget)property.GetValue(null));
            return fromFields.Concat(fromProperties)
                .Select(target => target.TargetName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .ToArray();
        }

        private static byte To8(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }

        private static ushort To16(float value)
        {
            return (ushort)Mathf.Clamp(Mathf.RoundToInt(value * 65535f), 0, 65535);
        }

        private static byte[] Checked(byte[] bytes, OutputEncoding encoding)
        {
            if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("Unity could not encode the texture as " + encoding + ".");
            return bytes;
        }
    }
}
