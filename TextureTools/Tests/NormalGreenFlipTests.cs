using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools.Tests
{
    public class NormalGreenFlipTests
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

        //A normal map as a user would have it: imported as Normal map, with some non-default settings
        private string NormalMap(string fileName, Color32[] pixels, int size, bool withAlpha = true)
        {
            string path = assets.WritePng8(fileName, size, size, pixels, withAlpha);
            var importer = TestAssets.Importer(path);
            importer.textureType = TextureImporterType.NormalMap;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 512;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = 256,
                format = TextureImporterFormat.ASTC_6x6,
            });
            importer.SaveAndReimport();
            return path;
        }

        [Test]
        public void FlipGreen_InvertsOnlyGreen()
        {
            var pixels = new[] { new Color(0.1f, 0.2f, 0.3f, 0.4f), new Color(1f, 0f, 0.5f, 1f) };

            NormalGreenFlip.FlipGreen(pixels);

            Assert.AreEqual(new Color(0.1f, 0.8f, 0.3f, 0.4f), pixels[0]);
            Assert.AreEqual(new Color(1f, 1f, 0.5f, 1f), pixels[1]);
        }

        [Test]
        public void DefaultOutputPath_NamesByTargetConvention()
        {
            Assert.AreEqual("Assets/A/rock_n_GL.png", NormalGreenFlip.DefaultOutputPath("Assets/A/rock_n.png", NormalConvention.OpenGL, 8));
            Assert.AreEqual("Assets/A/rock_n_DX.png", NormalGreenFlip.DefaultOutputPath("Assets/A/rock_n.jpg", NormalConvention.DirectX, 8));
            Assert.AreEqual("Assets/A/rock_GL.png", NormalGreenFlip.DefaultOutputPath("Assets/A/rock_DX.tga", NormalConvention.OpenGL, 8), "a _DX suffix is swapped, not stacked");
            Assert.AreEqual("Assets/A/rock_DX.png", NormalGreenFlip.DefaultOutputPath("Assets/A/rock_gl.png", NormalConvention.DirectX, 16));
            Assert.AreEqual("Assets/A/hdr_GL.exr", NormalGreenFlip.DefaultOutputPath("Assets/A/hdr.exr", NormalConvention.OpenGL, 32));
        }

        [Test]
        public void Convert_WritesFlippedFileWithTheSourcesImportSettings()
        {
            var pixels = TestAssets.Pattern8(32, 32, 10);
            string source = NormalMap("bumps.png", pixels, 32);

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("bumps_GL.png"), output);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color pixel = result.Pixels[i];
                Assert.AreEqual(pixels[i].r, TestAssets.To8(pixel.r), "red at " + i);
                Assert.AreEqual(255 - pixels[i].g, TestAssets.To8(pixel.g), "green at " + i);
                Assert.AreEqual(pixels[i].b, TestAssets.To8(pixel.b), "blue at " + i);
                Assert.AreEqual(pixels[i].a, TestAssets.To8(pixel.a), "alpha at " + i);
            }

            var importer = TestAssets.Importer(output);
            Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType);
            Assert.AreEqual(TextureWrapMode.Clamp, importer.wrapMode);
            Assert.AreEqual(4, importer.anisoLevel);
            Assert.AreEqual(512, importer.maxTextureSize);
            var android = importer.GetPlatformTextureSettings("Android");
            Assert.IsTrue(android.overridden);
            Assert.AreEqual(256, android.maxTextureSize);
            Assert.AreEqual(TextureImporterFormat.ASTC_6x6, android.format);
        }

        [Test]
        public void Convert_ImportSettingsSurviveAReimport()
        {
            string source = NormalMap("persist.png", TestAssets.Pattern8(8, 8, 11), 8);
            string output = NormalGreenFlip.Convert(source, NormalConvention.DirectX, ExistingOutput.Replace);

            AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            Assert.AreEqual(TextureImporterType.NormalMap, TestAssets.Importer(output).textureType);
            StringAssert.Contains("textureType: 1", File.ReadAllText(TestAssets.FullPath(output) + ".meta"), "settings must be saved in the .meta file");
        }

        [Test]
        public void Convert_SourceWithoutAlpha_WritesRgb()
        {
            string source = NormalMap("rgb.png", TestAssets.Opaque(TestAssets.Pattern8(8, 8, 12)), 8, false);

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(2, TestAssets.PngColourType(output));
        }

        [Test]
        public void FlippingTwice_GivesBackTheSource()
        {
            var pixels = TestAssets.Pattern8(16, 16, 13);
            string source = NormalMap("twice.png", pixels, 16);

            string once = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);
            string twice = NormalGreenFlip.Convert(once, NormalConvention.DirectX, ExistingOutput.Replace);

            var result = SourcePixels.Read(twice);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.AreEqual(pixels[i].g, TestAssets.To8(result.Pixels[i].g), "green at " + i);
                Assert.AreEqual(pixels[i].r, TestAssets.To8(result.Pixels[i].r), "red at " + i);
            }
        }

        [Test]
        public void Convert_SixteenBitSource_GivesSixteenBitOutput()
        {
            const int size = 6;
            var random = new System.Random(14);
            var values = new ushort[size * size * 4];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)random.Next(65536);
            string source = assets.WritePng16("deep.png", size, size, values);

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(16, TestAssets.PngBitDepth(output));
            var result = SourcePixels.Read(output);
            for (int i = 0; i < size * size; i++)
            {
                Assert.AreEqual(values[i * 4], TestAssets.To16(result.Pixels[i].r), "red at " + i);
                Assert.AreEqual(65535 - values[i * 4 + 1], TestAssets.To16(result.Pixels[i].g), "green at " + i);
                Assert.AreEqual(values[i * 4 + 2], TestAssets.To16(result.Pixels[i].b), "blue at " + i);
            }
        }

        [Test]
        public void Convert_ExrSource_GivesExrOutput()
        {
            var pixels = new[] { new Color(0.5f, 0.25f, 1f, 1f), new Color(0.1f, 0.9f, 0.7f, 1f), new Color(0f, 1f, 0f, 1f), new Color(1f, 0f, 1f, 1f) };
            string source = assets.WriteExr("float.exr", 2, 2, pixels);

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("float_GL.exr"), output);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < pixels.Length; i++) Assert.AreEqual(1f - pixels[i].g, result.Pixels[i].g, 1e-6f);
        }

        [Test]
        public void Convert_NeverOverwritesTheSource()
        {
            //Already named for the target convention, so the default output name is the source's own
            string source = NormalMap("wall_GL.png", TestAssets.Pattern8(8, 8, 15), 8);
            byte[] before = File.ReadAllBytes(TestAssets.FullPath(source));

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreNotEqual(source, output);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(TestAssets.FullPath(source)));
        }

        [Test]
        public void Convert_DdsSource_WritesAPngNormalMap()
        {
            //DXT5, which Unity shows the right way up
            string source = assets.WriteDds("body_n.dds", 16, 16, TestAssets.Blocks(16, 16, 16), TextureFormat.DXT5, out Color[] decoded);
            var ddsImporter = (IHVImageFormatImporter)AssetImporter.GetAtPath(source);
            ddsImporter.wrapModeU = TextureWrapMode.Clamp;
            ddsImporter.wrapModeV = TextureWrapMode.Mirror;
            ddsImporter.filterMode = FilterMode.Trilinear;
            ddsImporter.SaveAndReimport();

            string output = NormalGreenFlip.Convert(source, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("body_n_GL.png"), output);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < decoded.Length; i++)
            {
                Assert.AreEqual(TestAssets.To8(decoded[i].r), TestAssets.To8(result.Pixels[i].r), "red at " + i);
                Assert.AreEqual(255 - TestAssets.To8(decoded[i].g), TestAssets.To8(result.Pixels[i].g), "green at " + i);
                Assert.AreEqual(TestAssets.To8(decoded[i].b), TestAssets.To8(result.Pixels[i].b), "blue at " + i);
            }
            var importer = TestAssets.Importer(output);
            Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType);
            Assert.AreEqual(TextureWrapMode.Clamp, importer.wrapModeU);
            Assert.AreEqual(TextureWrapMode.Mirror, importer.wrapModeV);
            Assert.AreEqual(FilterMode.Trilinear, importer.filterMode);
        }

        [Test]
        public void Warnings_FlagNonNormalMapsAndFlatGreen()
        {
            var flat = new Color32[16];
            for (int i = 0; i < flat.Length; i++) flat[i] = new Color32(128, 128, 255, 255);
            string path = assets.WritePng8("flat.png", 4, 4, flat);

            var warnings = NormalGreenFlip.Warnings(path, SourcePixels.Read(path));

            Assert.AreEqual(2, warnings.Count);
        }
    }
}
