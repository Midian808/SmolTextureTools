using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Midian.TextureTools
{
    //A texture's pixels exactly as stored in its source file. Rows run bottom-up, like Texture2D.GetPixels
    public sealed class PixelImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly Color[] Pixels;
        //Bits per channel in the source file: 8, 16, or 32 for floating point (EXR / HDR)
        public readonly int BitDepth;
        public readonly bool HasAlpha;

        public PixelImage(int width, int height, Color[] pixels, int bitDepth, bool hasAlpha)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
            BitDepth = bitDepth;
            HasAlpha = hasAlpha;
        }
    }

    //Reads a texture's source file through a temporary raw import, so the result is free of the
    //original asset's compression, Max Size, sRGB decoding and normal-map swizzling. DDS files keep
    //the data stored in them (often block-compressed), which is decoded on the CPU
    public static class SourcePixels
    {
        public const string TempFolderName = "__TextureToolsTemp";
        public const string TempFolder = "Assets/" + TempFolderName;

        public static bool IsTempPath(string assetPath)
        {
            return assetPath.StartsWith(TempFolder + "/", StringComparison.Ordinal);
        }

        public static PixelImage Read(string assetPath)
        {
            string sourceFile = AssetPaths.ToFullPath(assetPath);
            if (!File.Exists(sourceFile)) throw new FileNotFoundException("Texture source file not found: " + assetPath, sourceFile);

            var sourceImporter = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            bool hasAlpha = sourceImporter == null || sourceImporter.DoesSourceTextureHaveAlpha();
            int bitDepth = SourceBitDepth.Detect(sourceFile);

            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", TempFolderName);
            string tempPath = TempFolder + "/" + Guid.NewGuid().ToString("N") + Path.GetExtension(assetPath).ToLowerInvariant();
            try
            {
                File.Copy(sourceFile, AssetPaths.ToFullPath(tempPath));
                AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(tempPath);
                var importer = AssetImporter.GetAtPath(tempPath);
                if (texture != null && importer is IHVImageFormatImporter) return ReadStoredData(texture);
                var tempImporter = importer as TextureImporter;
                if (texture == null || tempImporter == null) throw new InvalidOperationException("Unity could not import " + assetPath + " as a 2D texture.");

                tempImporter.GetSourceTextureWidthAndHeight(out int width, out int height);
                if (texture.width != width || texture.height != height)
                    throw new InvalidOperationException(assetPath + " is " + width + " x " + height + ", larger than Unity's 16384 limit.");

                return new PixelImage(width, height, texture.GetPixels(), bitDepth, hasAlpha);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        //A DDS (or other IHV format) texture: its pixels are the data stored in the file, so the
        //texture's own format gives the bit depth and whether there is alpha
        private static PixelImage ReadStoredData(Texture2D texture)
        {
            Color[] pixels = texture.GetPixels(0);
            bool hasAlpha = GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat);
            return new PixelImage(texture.width, texture.height, pixels, BitDepthOf(texture.format), hasAlpha);
        }

        //A texture's size and bit depth without reading its pixels
        public static void Describe(string assetPath, out int width, out int height, out int bitDepth)
        {
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.GetSourceTextureWidthAndHeight(out width, out height);
                bitDepth = SourceBitDepth.Detect(AssetPaths.ToFullPath(assetPath));
                return;
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null) throw new InvalidOperationException(assetPath + " isn't a 2D texture.");
            width = texture.width;
            height = texture.height;
            bitDepth = BitDepthOf(texture.format);
        }

        //Bits per channel of data stored in a texture format: 8, 16, or 32 for floating point
        private static int BitDepthOf(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RHalf:
                case TextureFormat.RGHalf:
                case TextureFormat.RFloat:
                case TextureFormat.RGFloat:
                case TextureFormat.BC6H:
                case TextureFormat.RGB9e5Float:
                    return 32;
                case TextureFormat.R16:
                case TextureFormat.RG32:
                case TextureFormat.RGB48:
                case TextureFormat.RGBA64:
                    return 16;
                default:
                    return 8;
            }
        }

        //Called by ImportHooks for DDS files in the temp folder: readable, and at full size whatever the quality settings' mipmap limit
        internal static void ConfigureTempImport(IHVImageFormatImporter importer)
        {
            importer.isReadable = true;
            importer.ignoreMipmapLimit = true;
        }

        //Called by ImportHooks for files in the temp folder: no colour-space decode, no compression,
        //no resizing, no mips; 8-bit sources as RGBA32, deeper ones as RGBAFloat so no precision is lost
        internal static void ConfigureTempImport(TextureImporter importer, string assetPath)
        {
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.ignorePngGamma = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = true;
            importer.maxTextureSize = 16384;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            bool wide = SourceBitDepth.Detect(AssetPaths.ToFullPath(assetPath)) > 8;
            var platform = importer.GetPlatformTextureSettings(AssetPaths.ActivePlatformName());
            platform.overridden = true;
            platform.maxTextureSize = 16384;
            platform.textureCompression = TextureImporterCompression.Uncompressed;
            platform.format = wide ? TextureImporterFormat.RGBAFloat : TextureImporterFormat.RGBA32;
            importer.SetPlatformTextureSettings(platform);
        }

        //A crash mid-read can leave the temp folder behind; remove it on the next Editor load
        [InitializeOnLoadMethod]
        private static void RemoveLeftoverTempFolder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            };
        }
    }

    //Bits per channel of an image file, read from its header. Formats that are always 8-bit, and
    //files whose header can't be read, report 8
    public static class SourceBitDepth
    {
        public static int Detect(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                return FromStream(stream, Path.GetExtension(file));
            }
        }

        public static int FromStream(Stream stream, string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".exr":
                case ".hdr":
                    return 32;
                case ".png":
                    return Png(stream);
                case ".psd":
                    return Psd(stream);
                case ".tif":
                case ".tiff":
                    return Tiff(stream);
                default:
                    return 8;
            }
        }

        //Signature (8), IHDR length (4), "IHDR" (4), width (4), height (4), then the bit depth byte
        private static int Png(Stream stream)
        {
            var header = new byte[25];
            if (ReadFully(stream, header) < header.Length) return 8;
            if (header[1] != 'P' || header[2] != 'N' || header[3] != 'G') return 8;
            if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R') return 8;
            return header[24] == 16 ? 16 : 8;
        }

        //"8BPS", version (2), reserved (6), channels (2), height (4), width (4), then depth (2, big-endian)
        private static int Psd(Stream stream)
        {
            var header = new byte[24];
            if (ReadFully(stream, header) < header.Length) return 8;
            if (header[0] != '8' || header[1] != 'B' || header[2] != 'P' || header[3] != 'S') return 8;
            return Depth((header[22] << 8) | header[23], false);
        }

        //Reads BitsPerSample (258) and SampleFormat (339) from the first image directory
        private static int Tiff(Stream stream)
        {
            var header = new byte[8];
            if (ReadFully(stream, header) < header.Length) return 8;
            bool little = header[0] == 'I' && header[1] == 'I';
            bool big = header[0] == 'M' && header[1] == 'M';
            if (!little && !big) return 8;
            if (U16(header, 2, little) != 42) return 8;

            long directory = U32(header, 4, little);
            if (directory + 2 > stream.Length) return 8;
            stream.Position = directory;
            var countBytes = new byte[2];
            if (ReadFully(stream, countBytes) < 2) return 8;
            int count = U16(countBytes, 0, little);
            var entries = new byte[count * 12];
            if (ReadFully(stream, entries) < entries.Length) return 8;

            int bits = 8;
            int sampleFormat = 1;
            for (int i = 0; i < count; i++)
            {
                int offset = i * 12;
                int tag = U16(entries, offset, little);
                if (tag != 258 && tag != 339) continue;
                int value = FirstShort(stream, entries, offset, little);
                if (tag == 258) bits = value;
                else sampleFormat = value;
            }
            return Depth(bits, sampleFormat == 3);
        }

        //The first SHORT of a TIFF entry: stored in the entry itself when the values fit in 4 bytes, otherwise at an offset
        private static int FirstShort(Stream stream, byte[] entries, int offset, bool little)
        {
            int type = U16(entries, offset + 2, little);
            long valueCount = U32(entries, offset + 4, little);
            if (type != 3) return (int)U32(entries, offset + 8, little);
            if (valueCount <= 2) return U16(entries, offset + 8, little);

            long position = U32(entries, offset + 8, little);
            if (position + 2 > stream.Length) return 8;
            stream.Position = position;
            var value = new byte[2];
            return ReadFully(stream, value) < 2 ? 8 : U16(value, 0, little);
        }

        private static int Depth(int bits, bool isFloat)
        {
            if (isFloat || bits >= 32) return 32;
            return bits >= 16 ? 16 : 8;
        }

        private static int U16(byte[] bytes, int offset, bool little)
        {
            return little ? bytes[offset] | (bytes[offset + 1] << 8) : (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static long U32(byte[] bytes, int offset, bool little)
        {
            return little
                ? (long)(uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24))
                : (long)(uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]);
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }

    internal static class AssetPaths
    {
        public static string ToFullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath));
        }

        public static bool IsUnderAssets(string assetPath)
        {
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal);
        }

        //The importer's name for the platform currently selected in Build Settings ("Standalone", "Android", "iPhone"...)
        public static string ActivePlatformName()
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return NamedBuildTarget.FromBuildTargetGroup(group).TargetName;
        }
    }
}
