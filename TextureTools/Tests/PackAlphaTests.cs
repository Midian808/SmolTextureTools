using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools.Tests
{
    public class PackAlphaTests
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

        private static Color32[] Greys(byte[] values)
        {
            var pixels = new Color32[values.Length];
            for (int i = 0; i < values.Length; i++) pixels[i] = new Color32(values[i], values[i], values[i], 255);
            return pixels;
        }

        [Test]
        public void ExtractAlpha_EachChannelAndInvert()
        {
            var pixels = new[] { new Color(0.2f, 0.4f, 0.6f, 0.8f) };

            Assert.AreEqual(0.2f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Red, false)[0], 1e-6f);
            Assert.AreEqual(0.4f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Green, false)[0], 1e-6f);
            Assert.AreEqual(0.6f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Blue, false)[0], 1e-6f);
            Assert.AreEqual(0.8f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Alpha, false)[0], 1e-6f);
            Assert.AreEqual(0.2126f * 0.2f + 0.7152f * 0.4f + 0.0722f * 0.6f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Luminance, false)[0], 1e-6f);
            Assert.AreEqual(0.6f, PackAlpha.ExtractAlpha(pixels, AlphaChannel.Green, true)[0], 1e-6f);
        }

        [Test]
        public void Resample_UpscalesWithCentredBilinear()
        {
            var result = PackAlpha.Resample(new[] { 0f, 1f }, 2, 1, 4, 1);

            //Destination centres map to source x = -0.25, 0.25, 0.75, 1.25, clamped at the edges
            Assert.AreEqual(0f, result[0], 1e-6f);
            Assert.AreEqual(0.25f, result[1], 1e-6f);
            Assert.AreEqual(0.75f, result[2], 1e-6f);
            Assert.AreEqual(1f, result[3], 1e-6f);
        }

        [Test]
        public void Resample_SameSizeIsUnchanged()
        {
            var source = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };

            CollectionAssert.AreEqual(source, PackAlpha.Resample(source, 3, 2, 3, 2));
        }

        [Test]
        public void Run_PacksTransparencyIntoTheColourMap()
        {
            var colour = TestAssets.Opaque(TestAssets.Pattern8(8, 8, 20));
            string colourPath = assets.WriteJpg("diffuse.jpg", 8, 8, colour);
            var colourImporter = TestAssets.Importer(colourPath);
            colourImporter.wrapMode = TextureWrapMode.Mirror;
            colourImporter.SaveAndReimport();
            var colourStored = SourcePixels.Read(colourPath);

            //A smaller transparency map, as DAZ often exports
            var greys = new byte[] { 0, 255, 255, 0, 64, 128, 192, 255, 255, 0, 0, 255, 10, 20, 30, 40 };
            string transparencyPath = assets.WritePng8("trans.png", 4, 4, Greys(greys), false);

            string output = PackAlpha.Run(colourPath, transparencyPath, AlphaChannel.Luminance, false, 0.35f,
                PackAlpha.DefaultOutputPath(colourPath), ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("diffuse_Alpha.png"), output);
            Assert.AreEqual(6, TestAssets.PngColourType(output), "the output must be RGBA");
            var result = SourcePixels.Read(output);
            Assert.AreEqual(8, result.Width);
            Assert.AreEqual(8, result.Height);

            var expectedAlpha = PackAlpha.Resample(PackAlpha.ExtractAlpha(SourcePixels.Read(transparencyPath).Pixels, AlphaChannel.Luminance, false), 4, 4, 8, 8);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(TestAssets.To8(colourStored.Pixels[i].r), TestAssets.To8(result.Pixels[i].r), "red at " + i);
                Assert.AreEqual(TestAssets.To8(colourStored.Pixels[i].g), TestAssets.To8(result.Pixels[i].g), "green at " + i);
                Assert.AreEqual(TestAssets.To8(colourStored.Pixels[i].b), TestAssets.To8(result.Pixels[i].b), "blue at " + i);
                Assert.AreEqual(TestAssets.To8(expectedAlpha[i]), TestAssets.To8(result.Pixels[i].a), "alpha at " + i);
            }

            var importer = TestAssets.Importer(output);
            Assert.AreEqual(TextureImporterAlphaSource.FromInput, importer.alphaSource);
            Assert.IsTrue(importer.alphaIsTransparency);
            Assert.IsTrue(importer.mipMapsPreserveCoverage);
            Assert.AreEqual(0.35f, importer.alphaTestReferenceValue, 1e-6f);
            Assert.AreEqual(TextureWrapMode.Mirror, importer.wrapMode, "the colour map's settings carry over");
            Assert.IsTrue(importer.DoesSourceTextureHaveAlpha());
        }

        [Test]
        public void Run_DdsColourMap()
        {
            //DXT1, which Unity shows the right way up
            string colourPath = assets.WriteDds("diffuse.dds", 8, 8, TestAssets.Blocks(8, 8, 32), TextureFormat.DXT1, out Color[] decoded);
            var greys = new byte[64];
            for (int i = 0; i < 64; i++) greys[i] = (byte)(i * 4);
            string transparencyPath = assets.WritePng8("trans.png", 8, 8, Greys(greys), false);

            string output = PackAlpha.Run(colourPath, transparencyPath, AlphaChannel.Luminance, false, 0.5f,
                PackAlpha.DefaultOutputPath(colourPath), ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("diffuse_Alpha.png"), output);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(TestAssets.To8(decoded[i].r), TestAssets.To8(result.Pixels[i].r), "red at " + i);
                Assert.AreEqual(greys[i], TestAssets.To8(result.Pixels[i].a), "alpha at " + i);
            }
            Assert.IsTrue(TestAssets.Importer(output).alphaIsTransparency);
        }

        [Test]
        public void Run_InvertAndChannelAreApplied()
        {
            string colourPath = assets.WritePng8("c.png", 2, 2, TestAssets.Opaque(TestAssets.Pattern8(2, 2, 21)), false);
            var mask = new[] { new Color32(10, 200, 0, 255), new Color32(20, 100, 0, 255), new Color32(30, 0, 0, 255), new Color32(40, 255, 0, 255) };
            string maskPath = assets.WritePng8("m.png", 2, 2, mask, false);

            string output = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Green, true, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);

            var result = SourcePixels.Read(output);
            for (int i = 0; i < 4; i++) Assert.AreEqual(255 - mask[i].g, TestAssets.To8(result.Pixels[i].a), "alpha at " + i);
        }

        [Test]
        public void Run_ExistingOutput_ReplaceKeepsTheGuid()
        {
            string colourPath = assets.WritePng8("c.png", 4, 4, TestAssets.Pattern8(4, 4, 22));
            string maskPath = assets.WritePng8("m.png", 4, 4, TestAssets.Pattern8(4, 4, 23));
            string output = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Red, false, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);
            string guid = AssetDatabase.AssetPathToGUID(output);

            string again = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Blue, false, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);

            Assert.AreEqual(output, again);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(again));
        }

        [Test]
        public void Run_ExistingOutput_NewNameLeavesTheOldFile()
        {
            string colourPath = assets.WritePng8("c.png", 4, 4, TestAssets.Pattern8(4, 4, 24));
            string maskPath = assets.WritePng8("m.png", 4, 4, TestAssets.Pattern8(4, 4, 25));
            string first = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Red, false, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);
            byte[] firstBytes = File.ReadAllBytes(TestAssets.FullPath(first));

            string second = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Blue, false, 0.5f, assets.Path("out.png"), ExistingOutput.NewName);

            Assert.AreNotEqual(first, second);
            CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(TestAssets.FullPath(first)));
        }

        [Test]
        public void Run_ExistingOutput_CancelWritesNothing()
        {
            string colourPath = assets.WritePng8("c.png", 4, 4, TestAssets.Pattern8(4, 4, 26));
            string maskPath = assets.WritePng8("m.png", 4, 4, TestAssets.Pattern8(4, 4, 27));
            string first = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Red, false, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);
            byte[] firstBytes = File.ReadAllBytes(TestAssets.FullPath(first));

            Assert.IsNull(PackAlpha.Run(colourPath, maskPath, AlphaChannel.Blue, false, 0.5f, assets.Path("out.png"), ExistingOutput.Cancel));
            CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(TestAssets.FullPath(first)));
        }

        [Test]
        public void Run_RefusesToReplaceASource()
        {
            string colourPath = assets.WritePng8("c.png", 4, 4, TestAssets.Pattern8(4, 4, 28));
            string maskPath = assets.WritePng8("m.png", 4, 4, TestAssets.Pattern8(4, 4, 29));
            byte[] before = File.ReadAllBytes(TestAssets.FullPath(colourPath));

            Assert.Throws<InvalidOperationException>(() =>
                PackAlpha.Run(colourPath, maskPath, AlphaChannel.Red, false, 0.5f, colourPath, ExistingOutput.Replace));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(TestAssets.FullPath(colourPath)));
        }

        [Test]
        public void Run_PlatformFormatWithoutAlpha_BecomesAutomatic()
        {
            string colourPath = assets.WriteJpg("c.jpg", 4, 4, TestAssets.Pattern8(4, 4, 30));
            var importer = TestAssets.Importer(colourPath);
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.DXT1 });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.ASTC_4x4 });
            importer.SaveAndReimport();
            string maskPath = assets.WritePng8("m.png", 4, 4, TestAssets.Pattern8(4, 4, 31));

            string output = PackAlpha.Run(colourPath, maskPath, AlphaChannel.Red, false, 0.5f, assets.Path("out.png"), ExistingOutput.Replace);

            var outputImporter = TestAssets.Importer(output);
            var standalone = outputImporter.GetPlatformTextureSettings("Standalone");
            Assert.IsTrue(standalone.overridden);
            Assert.AreEqual(TextureImporterFormat.Automatic, standalone.format);
            Assert.AreEqual(1024, standalone.maxTextureSize);
            Assert.AreEqual(TextureImporterFormat.ASTC_4x4, outputImporter.GetPlatformTextureSettings("Android").format, "formats with alpha are kept");
        }
    }
}
