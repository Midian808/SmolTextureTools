using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools.Tests
{
    //A temporary folder under Assets holding image files generated in code, imported by Unity like any
    //texture a user adds. Deleted on Dispose
    public sealed class TestAssets : IDisposable
    {
        public readonly string Folder;

        public TestAssets()
        {
            string name = "__TextureToolsTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", name);
            Folder = "Assets/" + name;
        }

        public string Path(string fileName)
        {
            return Folder + "/" + fileName;
        }

        public string WritePng8(string fileName, int width, int height, Color32[] pixels, bool withAlpha = true)
        {
            var texture = new Texture2D(width, height, withAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                return WriteAndImport(fileName, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        public string WriteTga(string fileName, int width, int height, Color32[] pixels)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                return WriteAndImport(fileName, texture.EncodeToTGA());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        public string WriteJpg(string fileName, int width, int height, Color32[] pixels)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false, true);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                return WriteAndImport(fileName, texture.EncodeToJPG(100));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        //values: 4 per pixel (RGBA), rows bottom-up
        public string WritePng16(string fileName, int width, int height, ushort[] values)
        {
            return WriteAndImport(fileName, Png16Encoder.Encode(width, height, values, 4));
        }

        public string WriteExr(string fileName, int width, int height, Color[] pixels)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false);
                return WriteAndImport(fileName, texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        //Compresses the pixels (rows bottom-up, like Texture2D) to format (DXT1, DXT5, BC5 or BC7) with Unity's
        //own compressor and writes them as a DDS file, top row first as real DDS files are stored.
        //authored receives the picture after compression, rows bottom-up, as a viewer would show the file
        public string WriteDds(string fileName, int width, int height, Color32[] pixels, TextureFormat format, out Color[] authored)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                //Compressing the upside-down picture puts its top row first in the raw data
                texture.SetPixels32(FlipRows(pixels, width, height));
                texture.Apply(false);
                EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Best);
                if (texture.format != format) throw new InvalidOperationException("Unity compressed to " + texture.format + " instead of " + format);
                authored = FlipRows(texture.GetPixels(), width, height);
                return WriteAndImport(fileName, Dds(width, height, format, texture.GetRawTextureData()));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        //A DDS file: "DDS ", the 124-byte header, a DX10 header for formats with no FourCC, then the data
        private static byte[] Dds(int width, int height, TextureFormat format, byte[] data)
        {
            string fourCc;
            int dxgiFormat = 0;
            switch (format)
            {
                case TextureFormat.DXT1: fourCc = "DXT1"; break;
                case TextureFormat.DXT5: fourCc = "DXT5"; break;
                case TextureFormat.BC5: fourCc = "DX10"; dxgiFormat = 83; break;
                case TextureFormat.BC7: fourCc = "DX10"; dxgiFormat = 98; break;
                default: throw new ArgumentException("No DDS mapping for " + format);
            }

            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("DDS "));
            writer.Write(124);
            writer.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); //caps, height, width, pixel format, linear size
            writer.Write(height);
            writer.Write(width);
            writer.Write(data.Length);
            writer.Write(0); //depth
            writer.Write(0); //mip count
            for (int i = 0; i < 11; i++) writer.Write(0);
            writer.Write(32); //pixel format size
            writer.Write(0x4); //FourCC
            writer.Write(System.Text.Encoding.ASCII.GetBytes(fourCc));
            for (int i = 0; i < 5; i++) writer.Write(0); //bit count, masks
            writer.Write(0x1000); //caps: texture
            for (int i = 0; i < 4; i++) writer.Write(0); //caps2-4, reserved
            if (fourCc == "DX10")
            {
                writer.Write(dxgiFormat);
                writer.Write(3); //Texture2D
                writer.Write(0);
                writer.Write(1); //array size
                writer.Write(0);
            }
            writer.Write(data);
            writer.Flush();
            return stream.ToArray();
        }

        public static T[] FlipRows<T>(T[] pixels, int width, int height)
        {
            var flipped = new T[pixels.Length];
            for (int y = 0; y < height; y++) Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
            return flipped;
        }

        //Flat-colour 4 x 4 blocks, which block compression reproduces closely
        public static Color32[] Blocks(int width, int height, int seed)
        {
            var random = new System.Random(seed);
            var colours = new Color32[(width / 4) * (height / 4)];
            for (int i = 0; i < colours.Length; i++)
                colours[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = colours[(y / 4) * (width / 4) + x / 4];
            return pixels;
        }

        public string WriteAndImport(string fileName, byte[] bytes)
        {
            string assetPath = Path(fileName);
            File.WriteAllBytes(FullPath(assetPath), bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        public static TextureImporter Importer(string assetPath)
        {
            return (TextureImporter)AssetImporter.GetAtPath(assetPath);
        }

        public static string FullPath(string assetPath)
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), assetPath));
        }

        //Bit depth byte of a PNG file's IHDR chunk
        public static int PngBitDepth(string assetPath)
        {
            return File.ReadAllBytes(FullPath(assetPath))[24];
        }

        //Colour type byte of a PNG file's IHDR chunk: 2 = RGB, 6 = RGBA
        public static int PngColourType(string assetPath)
        {
            return File.ReadAllBytes(FullPath(assetPath))[25];
        }

        //Every value different, including 0 and 255 in each channel
        public static Color32[] Pattern8(int width, int height, int seed)
        {
            var random = new System.Random(seed);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            pixels[0] = new Color32(0, 0, 0, 0);
            pixels[pixels.Length - 1] = new Color32(255, 255, 255, 255);
            return pixels;
        }

        public static Color32[] Opaque(Color32[] pixels)
        {
            var result = (Color32[])pixels.Clone();
            for (int i = 0; i < result.Length; i++) result[i].a = 255;
            return result;
        }

        public static byte To8(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }

        public static ushort To16(float value)
        {
            return (ushort)Mathf.Clamp(Mathf.RoundToInt(value * 65535f), 0, 65535);
        }

        public void Dispose()
        {
            AssetDatabase.DeleteAsset(Folder);
        }
    }
}
