using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools.Tests
{
    public class SourcePixelsTests
    {
        private TestAssets assets;

        [SetUp]
        public void SetUp()
        {
            assets = new TestAssets();
        }

        [TearDown]
        public void TearDown()
        {
            assets.Dispose();
        }

        private static void AssertExact8(Color32[] expected, PixelImage image, bool compareAlpha = true)
        {
            Assert.AreEqual(expected.Length, image.Pixels.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Color pixel = image.Pixels[i];
                Assert.AreEqual(expected[i].r, TestAssets.To8(pixel.r), "red at " + i);
                Assert.AreEqual(expected[i].g, TestAssets.To8(pixel.g), "green at " + i);
                Assert.AreEqual(expected[i].b, TestAssets.To8(pixel.b), "blue at " + i);
                if (compareAlpha) Assert.AreEqual(expected[i].a, TestAssets.To8(pixel.a), "alpha at " + i);
            }
        }

        [Test]
        public void Png8_ReadsExactStoredValues()
        {
            //Odd, non-power-of-two size: must not be rescaled
            var pixels = TestAssets.Pattern8(37, 23, 1);
            string path = assets.WritePng8("pattern.png", 37, 23, pixels);

            var image = SourcePixels.Read(path);

            Assert.AreEqual(37, image.Width);
            Assert.AreEqual(23, image.Height);
            Assert.AreEqual(8, image.BitDepth);
            Assert.IsTrue(image.HasAlpha);
            AssertExact8(pixels, image);
        }

        [Test]
        public void Png8_WithoutAlpha_ReportsNoAlpha()
        {
            var pixels = TestAssets.Opaque(TestAssets.Pattern8(16, 16, 2));
            string path = assets.WritePng8("rgb.png", 16, 16, pixels, false);

            var image = SourcePixels.Read(path);

            Assert.IsFalse(image.HasAlpha);
            AssertExact8(pixels, image);
        }

        [Test]
        public void Tga_ReadsExactStoredValues()
        {
            var pixels = TestAssets.Pattern8(20, 12, 3);
            string path = assets.WriteTga("pattern.tga", 20, 12, pixels);

            AssertExact8(pixels, SourcePixels.Read(path));
        }

        [Test]
        public void Jpg_ReadsTheStoredColours()
        {
            //JPEG averages colour over 16 x 16 blocks, so flat 16 x 16 blocks survive almost unchanged
            var colours = new[] { new Color32(200, 30, 60, 255), new Color32(10, 220, 90, 255), new Color32(128, 128, 255, 255), new Color32(250, 250, 250, 255) };
            var pixels = new Color32[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    pixels[y * 32 + x] = colours[(y / 16) * 2 + x / 16];
            string path = assets.WriteJpg("blocks.jpg", 32, 32, pixels);

            var image = SourcePixels.Read(path);

            Assert.AreEqual(8, image.BitDepth);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.AreEqual(pixels[i].r, TestAssets.To8(image.Pixels[i].r), 4, "red at " + i);
                Assert.AreEqual(pixels[i].g, TestAssets.To8(image.Pixels[i].g), 4, "green at " + i);
                Assert.AreEqual(pixels[i].b, TestAssets.To8(image.Pixels[i].b), 4, "blue at " + i);
            }
        }

        [Test]
        public void Png16_ReadsSixteenBitValues()
        {
            const int width = 9, height = 7;
            var random = new System.Random(4);
            var values = new ushort[width * height * 4];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)random.Next(65536);
            values[0] = 0;
            values[1] = 65535;
            values[2] = 0x1234;
            string path = assets.WritePng16("deep.png", width, height, values);
            Assert.AreEqual(16, TestAssets.PngBitDepth(path), "the test file itself should be a 16-bit PNG");

            var image = SourcePixels.Read(path);

            Assert.AreEqual(16, image.BitDepth);
            for (int i = 0; i < width * height; i++)
            {
                Color pixel = image.Pixels[i];
                Assert.AreEqual(values[i * 4], TestAssets.To16(pixel.r), "red at " + i);
                Assert.AreEqual(values[i * 4 + 1], TestAssets.To16(pixel.g), "green at " + i);
                Assert.AreEqual(values[i * 4 + 2], TestAssets.To16(pixel.b), "blue at " + i);
                Assert.AreEqual(values[i * 4 + 3], TestAssets.To16(pixel.a), "alpha at " + i);
            }
        }

        [Test]
        public void Exr_ReadsFloatValues()
        {
            var pixels = new[] { new Color(2.5f, 0.125f, 0f, 1f), new Color(0.333f, 10f, 0.5f, 0.25f), new Color(0f, 0f, 1f, 1f), new Color(1f, 1f, 1f, 0f) };
            string path = assets.WriteExr("hdr.exr", 2, 2, pixels);

            var image = SourcePixels.Read(path);

            Assert.AreEqual(32, image.BitDepth);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.AreEqual(pixels[i].r, image.Pixels[i].r, 1e-6f);
                Assert.AreEqual(pixels[i].g, image.Pixels[i].g, 1e-6f);
                Assert.AreEqual(pixels[i].b, image.Pixels[i].b, 1e-6f);
                Assert.AreEqual(pixels[i].a, image.Pixels[i].a, 1e-6f);
            }
        }

        //The read gives the texture Unity shows for the DDS, so a PNG made from it is a drop-in replacement.
        //Unity turns DXT1/DXT5/BC5 files the right way up as it imports them, but not BC7, which it shows upside down
        [TestCase(TextureFormat.DXT1, false, true)]
        [TestCase(TextureFormat.DXT5, true, true)]
        [TestCase(TextureFormat.BC5, false, true)]
        [TestCase(TextureFormat.BC7, true, false)]
        public void Dds_ReadsWhatUnityShows(TextureFormat format, bool alphaKept, bool unityTurnsUpright)
        {
            //Non-square, so a row/column mix-up would show
            const int width = 32, height = 16;
            var pixels = TestAssets.Blocks(width, height, 7);
            string path = assets.WriteDds("stored.dds", width, height, pixels, format, out Color[] authored);
            Assert.IsInstanceOf<IHVImageFormatImporter>(AssetImporter.GetAtPath(path), "Unity should import the test file as DDS");

            var image = SourcePixels.Read(path);

            Assert.AreEqual(width, image.Width);
            Assert.AreEqual(height, image.Height);
            Assert.AreEqual(8, image.BitDepth);
            var expected = unityTurnsUpright ? authored : TestAssets.FlipRows(authored, width, height);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].r, image.Pixels[i].r, 1e-6f, "red at " + i);
                Assert.AreEqual(expected[i].g, image.Pixels[i].g, 1e-6f, "green at " + i);
                Assert.AreEqual(expected[i].b, image.Pixels[i].b, 1e-6f, "blue at " + i);
                if (alphaKept) Assert.AreEqual(expected[i].a, image.Pixels[i].a, 1e-6f, "alpha at " + i);
            }
            //And the decode is the picture that was compressed, not just self-consistent
            var original = unityTurnsUpright ? pixels : TestAssets.FlipRows(pixels, width, height);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.AreEqual(original[i].r, TestAssets.To8(image.Pixels[i].r), 10, "red vs original at " + i);
                Assert.AreEqual(original[i].g, TestAssets.To8(image.Pixels[i].g), 10, "green vs original at " + i);
            }
            Assert.IsFalse(AssetDatabase.IsValidFolder(SourcePixels.TempFolder));
        }

        [Test]
        public void Dds_ReadsFullSizeWhateverTheQualityMipmapLimit()
        {
            string path = assets.WriteDds("limited.dds", 32, 32, TestAssets.Blocks(32, 32, 8), TextureFormat.DXT5, out _);
            int limit = QualitySettings.globalTextureMipmapLimit;
            try
            {
                QualitySettings.globalTextureMipmapLimit = 1;

                var image = SourcePixels.Read(path);

                Assert.AreEqual(32, image.Width);
                Assert.AreEqual(32 * 32, image.Pixels.Length);
            }
            finally
            {
                QualitySettings.globalTextureMipmapLimit = limit;
            }
        }

        [Test]
        public void IgnoresTheSourceTexturesImportSettings()
        {
            var pixels = TestAssets.Pattern8(512, 512, 5);
            string path = assets.WritePng8("normal.png", 512, 512, pixels);
            var importer = TestAssets.Importer(path);
            importer.textureType = TextureImporterType.NormalMap;
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
            Assert.AreEqual(256, AssetDatabase.LoadAssetAtPath<Texture2D>(path).width, "the source asset itself should be downsized");

            var image = SourcePixels.Read(path);

            Assert.AreEqual(512, image.Width);
            Assert.AreEqual(512, image.Height);
            AssertExact8(pixels, image);
            Assert.AreEqual(TextureImporterType.NormalMap, TestAssets.Importer(path).textureType, "the source importer must be left alone");
            Assert.AreEqual(256, TestAssets.Importer(path).maxTextureSize);
        }

        [Test]
        public void TempFolderIsRemovedAfterReading()
        {
            string path = assets.WritePng8("small.png", 4, 4, TestAssets.Pattern8(4, 4, 6));

            SourcePixels.Read(path);

            Assert.IsFalse(AssetDatabase.IsValidFolder(SourcePixels.TempFolder));
        }

        [Test]
        public void TempFolderIsRemovedWhenTheFileIsNotATexture()
        {
            string path = assets.WriteAndImport("notes.txt", System.Text.Encoding.UTF8.GetBytes("not an image"));

            Assert.Throws<InvalidOperationException>(() => SourcePixels.Read(path));
            Assert.IsFalse(AssetDatabase.IsValidFolder(SourcePixels.TempFolder));
        }

        [Test]
        public void BitDepth_Png()
        {
            var header = new byte[33];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(header, 0);
            header[24] = 16;
            Assert.AreEqual(16, SourceBitDepth.FromStream(new MemoryStream(header), ".png"));
            header[24] = 8;
            Assert.AreEqual(8, SourceBitDepth.FromStream(new MemoryStream(header), ".PNG"));
        }

        [Test]
        public void BitDepth_Psd()
        {
            var header = new byte[26];
            new byte[] { (byte)'8', (byte)'B', (byte)'P', (byte)'S' }.CopyTo(header, 0);
            header[23] = 16;
            Assert.AreEqual(16, SourceBitDepth.FromStream(new MemoryStream(header), ".psd"));
            header[23] = 8;
            Assert.AreEqual(8, SourceBitDepth.FromStream(new MemoryStream(header), ".psd"));
            header[23] = 32;
            Assert.AreEqual(32, SourceBitDepth.FromStream(new MemoryStream(header), ".psd"));
        }

        //A TIFF whose first directory holds BitsPerSample (3 values, stored at an offset) and optionally SampleFormat
        private static byte[] Tiff(bool little, ushort bits, ushort sampleFormat)
        {
            var data = new byte[64];
            void U16(int at, int value)
            {
                if (little) { data[at] = (byte)value; data[at + 1] = (byte)(value >> 8); }
                else { data[at] = (byte)(value >> 8); data[at + 1] = (byte)value; }
            }
            void U32(int at, int value)
            {
                if (little) { U16(at, value & 0xFFFF); U16(at + 2, value >> 16); }
                else { U16(at, value >> 16); U16(at + 2, value & 0xFFFF); }
            }

            data[0] = data[1] = (byte)(little ? 'I' : 'M');
            U16(2, 42);
            U32(4, 8);
            U16(8, 2);
            //BitsPerSample: SHORT x 3 at offset 50
            U16(10, 258); U16(12, 3); U32(14, 3); U32(18, 50);
            //SampleFormat: SHORT x 1, inline
            U16(22, 339); U16(24, 3); U32(26, 1); U16(30, sampleFormat);
            U16(50, bits); U16(52, bits); U16(54, bits);
            return data;
        }

        [Test]
        public void BitDepth_Tiff()
        {
            Assert.AreEqual(16, SourceBitDepth.FromStream(new MemoryStream(Tiff(true, 16, 1)), ".tif"));
            Assert.AreEqual(16, SourceBitDepth.FromStream(new MemoryStream(Tiff(false, 16, 1)), ".tiff"));
            Assert.AreEqual(8, SourceBitDepth.FromStream(new MemoryStream(Tiff(true, 8, 1)), ".tif"));
            Assert.AreEqual(32, SourceBitDepth.FromStream(new MemoryStream(Tiff(false, 32, 3)), ".tif"));
        }

        [Test]
        public void BitDepth_OtherFormats()
        {
            Assert.AreEqual(32, SourceBitDepth.FromStream(new MemoryStream(new byte[4]), ".exr"));
            Assert.AreEqual(32, SourceBitDepth.FromStream(new MemoryStream(new byte[4]), ".hdr"));
            Assert.AreEqual(8, SourceBitDepth.FromStream(new MemoryStream(new byte[4]), ".jpg"));
            Assert.AreEqual(8, SourceBitDepth.FromStream(new MemoryStream(new byte[4]), ".png"), "a truncated header reads as 8-bit");
        }
    }
}
