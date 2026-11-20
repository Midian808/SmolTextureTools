using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools.Tests
{
    public class MaskTextureMakerTests
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

        private static Color32[] Greys(int count, int seed)
        {
            var random = new System.Random(seed);
            var pixels = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                byte value = (byte)random.Next(256);
                pixels[i] = new Color32(value, value, value, 255);
            }
            return pixels;
        }

        private string Grey(string fileName, int size, int seed, out Color32[] pixels)
        {
            pixels = Greys(size * size, seed);
            return assets.WritePng8(fileName, size, size, pixels, false);
        }

        private static RowSource[] Defaults(MaskPreset preset)
        {
            return MaskTextureMaker.Info(preset).Slots.Select(s => RowSource.Constant(s.Default)).ToArray();
        }

        [Test]
        public void Presets_MatchBetterLitsLayouts()
        {
            string Labels(MaskPreset p) => string.Join(",", MaskTextureMaker.Info(p).Slots.Select(s => s.Label));

            Assert.AreEqual("Metallic,Occlusion,Detail Mask,Smoothness", Labels(MaskPreset.MaskMap));
            Assert.AreEqual("Smoothness,Normal Y,Occlusion,Normal X", Labels(MaskPreset.PackedNormal));
            Assert.AreEqual("Smoothness,Normal Y,Metallic,Normal X", Labels(MaskPreset.PackedNormalFastMetal));
            Assert.AreEqual("Albedo brightness,Normal Y,Smoothness,Normal X", Labels(MaskPreset.DetailMap));
            CollectionAssert.AreEqual(new[] { 0f, 1f, 1f, 0.5f }, MaskTextureMaker.Info(MaskPreset.MaskMap).Slots.Select(s => s.Default));
            CollectionAssert.AreEqual(new[] { 0.5f, 0.5f, 0.5f, 0.5f }, MaskTextureMaker.Info(MaskPreset.DetailMap).Slots.Select(s => s.Default));
            Assert.IsFalse(MaskTextureMaker.Info(MaskPreset.MaskMap).Srgb);
            Assert.IsTrue(MaskTextureMaker.Info(MaskPreset.AlbedoHeight).Srgb);
        }

        [Test]
        public void Make_MaskMap_TexturesAndConstantsLandInTheirChannels()
        {
            string metal = Grey("Rock_Metallic.png", 8, 1, out var metalPixels);
            string ao = Grey("Rock_AO.png", 8, 2, out var aoPixels);
            string rough = Grey("Rock_Roughness.png", 8, 3, out var roughPixels);
            var rows = new[]
            {
                RowSource.FromTexture(metal, AlphaChannel.Red),
                RowSource.FromTexture(ao, AlphaChannel.Red),
                RowSource.Constant(1f),
                RowSource.FromTexture(rough, AlphaChannel.Red, true),
            };

            string output = MaskTextureMaker.Make(rows, false, false, 0, 0, MaskTextureMaker.DefaultOutputPath(rows, "_MaskMap"), ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("Rock_MaskMap.png"), output);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(metalPixels[i].r, TestAssets.To8(result.Pixels[i].r), "metallic at " + i);
                Assert.AreEqual(aoPixels[i].r, TestAssets.To8(result.Pixels[i].g), "occlusion at " + i);
                Assert.AreEqual(255, TestAssets.To8(result.Pixels[i].b), "detail mask at " + i);
                Assert.AreEqual(255 - roughPixels[i].r, TestAssets.To8(result.Pixels[i].a), "smoothness at " + i);
            }
        }

        [Test]
        public void Make_EachChannelChoiceAndLuminance()
        {
            var colour = TestAssets.Pattern8(4, 4, 4);
            string path = assets.WritePng8("colour.png", 4, 4, colour);
            var rows = new[]
            {
                RowSource.FromTexture(path, AlphaChannel.Alpha),
                RowSource.FromTexture(path, AlphaChannel.Blue),
                RowSource.FromTexture(path, AlphaChannel.Green, true),
                RowSource.FromTexture(path, AlphaChannel.Luminance),
            };

            string output = MaskTextureMaker.Make(rows, false, false, 0, 0, assets.Path("out.png"), ExistingOutput.Replace);

            var stored = SourcePixels.Read(path);
            var result = SourcePixels.Read(output);
            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(colour[i].a, TestAssets.To8(result.Pixels[i].r), "R from A at " + i);
                Assert.AreEqual(colour[i].b, TestAssets.To8(result.Pixels[i].g), "G from B at " + i);
                Assert.AreEqual(255 - colour[i].g, TestAssets.To8(result.Pixels[i].b), "B from inverted G at " + i);
                Assert.AreEqual(TestAssets.To8(PackAlpha.Value(stored.Pixels[i], AlphaChannel.Luminance)), TestAssets.To8(result.Pixels[i].a), "A from luminance at " + i);
            }
        }

        [Test]
        public void Make_MixedSizes_ResampleToTheLargest()
        {
            string big = Grey("big.png", 8, 5, out var bigPixels);
            string small = Grey("small.png", 4, 6, out _);
            var rows = new[] { RowSource.FromTexture(small, AlphaChannel.Red), RowSource.FromTexture(big, AlphaChannel.Red), RowSource.Constant(0f), RowSource.Constant(1f) };

            string output = MaskTextureMaker.Make(rows, false, false, 0, 0, assets.Path("mixed.png"), ExistingOutput.Replace);

            var result = SourcePixels.Read(output);
            Assert.AreEqual(8, result.Width);
            Assert.AreEqual(8, result.Height);
            var expectedSmall = PackAlpha.Resample(PackAlpha.ExtractAlpha(SourcePixels.Read(small).Pixels, AlphaChannel.Red, false), 4, 4, 8, 8);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(TestAssets.To8(expectedSmall[i]), TestAssets.To8(result.Pixels[i].r), "resampled at " + i);
                Assert.AreEqual(bigPixels[i].r, TestAssets.To8(result.Pixels[i].g), "full size at " + i);
            }
        }

        [Test]
        public void Make_ConstantsOnly_UsesTheGivenSize()
        {
            var rows = new[] { RowSource.Constant(0f), RowSource.Constant(1f), RowSource.Constant(0.5f), RowSource.Constant(0.25f) };

            string output = MaskTextureMaker.Make(rows, false, false, 6, 3, assets.Path("flat.png"), ExistingOutput.Replace);

            var result = SourcePixels.Read(output);
            Assert.AreEqual(6, result.Width);
            Assert.AreEqual(3, result.Height);
            Assert.AreEqual(new Color32(0, 255, 128, 64), new Color32(TestAssets.To8(result.Pixels[0].r), TestAssets.To8(result.Pixels[0].g), TestAssets.To8(result.Pixels[0].b), TestAssets.To8(result.Pixels[0].a)));
        }

        [Test]
        public void Make_SixteenBitSource_GivesSixteenBitOutput()
        {
            var values = new ushort[4 * 4 * 4];
            for (int i = 0; i < values.Length; i++) values[i] = (ushort)(i * 997);
            string deep = assets.WritePng16("deep_Height.png", 4, 4, values);
            var rows = new[] { RowSource.Constant(1f), RowSource.Constant(1f), RowSource.Constant(1f), RowSource.FromTexture(deep, AlphaChannel.Red) };

            string output = MaskTextureMaker.Make(rows, true, false, 0, 0, assets.Path("deep_AlbedoHeight.png"), ExistingOutput.Replace);

            Assert.AreEqual(16, TestAssets.PngBitDepth(output));
            var result = SourcePixels.Read(output);
            for (int i = 0; i < 16; i++) Assert.AreEqual(values[i * 4], TestAssets.To16(result.Pixels[i].a), "height at " + i);
        }

        [Test]
        public void Make_ImportSettingsFollowThePreset()
        {
            string metal = Grey("m.png", 4, 7, out _);
            var importer = TestAssets.Importer(metal);
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
            var rows = new[] { RowSource.FromTexture(metal, AlphaChannel.Red), RowSource.Constant(1f), RowSource.Constant(1f), RowSource.Constant(0.5f) };

            string linear = MaskTextureMaker.Make(rows, false, false, 0, 0, assets.Path("linear.png"), ExistingOutput.Replace);
            string colour = MaskTextureMaker.Make(rows, true, true, 0, 0, assets.Path("colour.png"), ExistingOutput.Replace);

            var linearImporter = TestAssets.Importer(linear);
            Assert.AreEqual(TextureImporterType.Default, linearImporter.textureType, "never a Normal map, whatever the inputs are");
            Assert.IsFalse(linearImporter.sRGBTexture);
            Assert.IsFalse(linearImporter.alphaIsTransparency);
            Assert.IsTrue(linearImporter.mipmapEnabled);
            Assert.AreEqual(TextureImporterAlphaSource.FromInput, linearImporter.alphaSource);
            var colourImporter = TestAssets.Importer(colour);
            Assert.IsTrue(colourImporter.sRGBTexture);
            Assert.IsTrue(colourImporter.alphaIsTransparency);
        }

        [Test]
        public void MaxSizeFor_KeepsTheFullResolution()
        {
            Assert.AreEqual(32, MaskTextureMaker.MaxSizeFor(1));
            Assert.AreEqual(64, MaskTextureMaker.MaxSizeFor(37));
            Assert.AreEqual(2048, MaskTextureMaker.MaxSizeFor(2048));
            Assert.AreEqual(4096, MaskTextureMaker.MaxSizeFor(2049));
            Assert.AreEqual(16384, MaskTextureMaker.MaxSizeFor(20000));
        }

        [Test]
        public void Make_ExistingOutput_ReplaceKeepsTheGuidAndCancelWritesNothing()
        {
            var rows = new[] { RowSource.Constant(0f), RowSource.Constant(1f), RowSource.Constant(1f), RowSource.Constant(0.5f) };
            string first = MaskTextureMaker.Make(rows, false, false, 4, 4, assets.Path("same.png"), ExistingOutput.Replace);
            string guid = AssetDatabase.AssetPathToGUID(first);

            rows[0] = RowSource.Constant(1f);
            string second = MaskTextureMaker.Make(rows, false, false, 4, 4, assets.Path("same.png"), ExistingOutput.Replace);

            Assert.AreEqual(first, second);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(second));
            Assert.AreEqual(255, TestAssets.To8(SourcePixels.Read(second).Pixels[0].r));
            Assert.IsNull(MaskTextureMaker.Make(rows, false, false, 4, 4, assets.Path("same.png"), ExistingOutput.Cancel));
            Assert.AreNotEqual(first, MaskTextureMaker.Make(rows, false, false, 4, 4, assets.Path("same.png"), ExistingOutput.NewName));
        }

        [Test]
        public void Make_RefusesToReplaceASource()
        {
            string metal = Grey("source.png", 4, 8, out _);
            var rows = new[] { RowSource.FromTexture(metal, AlphaChannel.Red), RowSource.Constant(1f), RowSource.Constant(1f), RowSource.Constant(0.5f) };

            Assert.Throws<InvalidOperationException>(() => MaskTextureMaker.Make(rows, false, false, 0, 0, metal, ExistingOutput.Replace));
        }

        [Test]
        public void DefaultOutputPath_PerPreset()
        {
            var rows = new[] { RowSource.FromTexture("Assets/Rocks/Rock_Normal_DX.png", AlphaChannel.Red), RowSource.Constant(0f), RowSource.Constant(0f), RowSource.Constant(0f) };

            Assert.AreEqual("Assets/Rocks/Rock_MaskMap.png", MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.MaskMap).Suffix));
            Assert.AreEqual("Assets/Rocks/Rock_PackedNormal.png", MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.PackedNormal).Suffix));
            Assert.AreEqual("Assets/Rocks/Rock_Detail.png", MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.DetailMap).Suffix));
            Assert.AreEqual("Assets/Rocks/Rock_AlbedoHeight.png", MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.AlbedoHeight).Suffix));
            Assert.AreEqual("Assets/Rocks/Rock_Packed.png", MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.Custom).Suffix));
            rows[0] = RowSource.FromTexture("Assets/Rocks/Pebbles.png", AlphaChannel.Red);
            Assert.AreEqual("Assets/Rocks/Pebbles_MaskMap.png", MaskTextureMaker.DefaultOutputPath(rows, "_MaskMap"), "an unrecognised name is kept whole");
            Assert.IsNull(MaskTextureMaker.DefaultOutputPath(Defaults(MaskPreset.MaskMap), "_MaskMap"), "no texture, no default path");
        }

        [TestCase("Rock_Metallic", ChannelRole.Metallic, false, "Rock")]
        [TestCase("Rock_metalness", ChannelRole.Metallic, false, "Rock")]
        [TestCase("Rock_Metal", ChannelRole.Metallic, false, "Rock")]
        [TestCase("Rock_M", ChannelRole.Metallic, false, "Rock")]
        [TestCase("Rock_AO", ChannelRole.Occlusion, false, "Rock")]
        [TestCase("Rock-Occlusion", ChannelRole.Occlusion, false, "Rock")]
        [TestCase("Rock AmbientOcclusion", ChannelRole.Occlusion, false, "Rock")]
        [TestCase("Rock_Smoothness", ChannelRole.Smoothness, false, "Rock")]
        [TestCase("Rock_Smooth", ChannelRole.Smoothness, false, "Rock")]
        [TestCase("Rock_Gloss", ChannelRole.Smoothness, false, "Rock")]
        [TestCase("Rock_Glossiness", ChannelRole.Smoothness, false, "Rock")]
        [TestCase("Rock_Roughness", ChannelRole.Smoothness, true, "Rock")]
        [TestCase("Rock_Rough", ChannelRole.Smoothness, true, "Rock")]
        [TestCase("Rock_R", ChannelRole.Smoothness, true, "Rock")]
        [TestCase("Rock_DetailMask", ChannelRole.DetailMask, false, "Rock")]
        [TestCase("Rock_Detail", ChannelRole.DetailMask, false, "Rock")]
        [TestCase("Rock_Height", ChannelRole.Height, false, "Rock")]
        [TestCase("Rock_Displacement", ChannelRole.Height, false, "Rock")]
        [TestCase("Rock_Disp", ChannelRole.Height, false, "Rock")]
        [TestCase("Rock_H", ChannelRole.Height, false, "Rock")]
        [TestCase("Big.Rock_Albedo", ChannelRole.ColourR, false, "Big.Rock")]
        [TestCase("Rock_BaseColor", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_BaseColour", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_Diffuse", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_Color", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_Colour", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_Col", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_D", ChannelRole.ColourR, false, "Rock")]
        [TestCase("Rock_BC", ChannelRole.ColourR, false, "Rock")]
        public void TryMatchName_SingleRoleSuffixes(string name, ChannelRole role, bool invert, string baseName)
        {
            Assert.IsTrue(MaskTextureMaker.TryMatchName(name, out var matches, out string found));
            Assert.AreEqual(role, matches[0].Role);
            Assert.AreEqual(invert, matches[0].Invert);
            Assert.AreEqual(baseName, found);
        }

        [TestCase("Rock_Normal", false, "Rock")]
        [TestCase("Rock_NRM", false, "Rock")]
        [TestCase("Rock_N", false, "Rock")]
        [TestCase("Rock_NormalGL", false, "Rock")]
        [TestCase("Rock_Normal_GL", false, "Rock")]
        [TestCase("Rock_Normal_OpenGL", false, "Rock")]
        [TestCase("Rock_nrm-gl", false, "Rock")]
        [TestCase("Rock_NormalDX", true, "Rock")]
        [TestCase("Rock_Normal_DX", true, "Rock")]
        [TestCase("Rock_Normal_DirectX", true, "Rock")]
        public void TryMatchName_NormalMaps(string name, bool directX, string baseName)
        {
            Assert.IsTrue(MaskTextureMaker.TryMatchName(name, out var matches, out string found));
            var x = matches.Single(m => m.Role == ChannelRole.NormalX);
            var y = matches.Single(m => m.Role == ChannelRole.NormalY);
            Assert.AreEqual(AlphaChannel.Red, x.Channel);
            Assert.AreEqual(AlphaChannel.Green, y.Channel);
            Assert.IsFalse(x.Invert);
            Assert.AreEqual(directX, y.Invert);
            Assert.AreEqual(baseName, found);
        }

        [Test]
        public void TryMatchName_MetallicSmoothnessFillsTwoRoles()
        {
            Assert.IsTrue(MaskTextureMaker.TryMatchName("Rock_MetallicSmoothness", out var matches, out string baseName));
            Assert.AreEqual("Rock", baseName);
            Assert.AreEqual(AlphaChannel.Red, matches.Single(m => m.Role == ChannelRole.Metallic).Channel);
            Assert.AreEqual(AlphaChannel.Alpha, matches.Single(m => m.Role == ChannelRole.Smoothness).Channel);
            Assert.IsTrue(MaskTextureMaker.TryMatchName("Rock_MetallicGloss", out _, out _));
        }

        [TestCase("Rock_Mud")]
        [TestCase("Rock")]
        [TestCase("Rock_Metallicness")]
        [TestCase("GL")]
        public void TryMatchName_WholePartsOnly(string name)
        {
            Assert.IsFalse(MaskTextureMaker.TryMatchName(name, out _, out string baseName));
            Assert.AreEqual(name, baseName);
        }

        [Test]
        public void FillFromNames_AssignsByRoleAndKeepsUnmatchedRows()
        {
            var current = Defaults(MaskPreset.MaskMap);
            var paths = new[] { "Assets/T/Rock_Roughness.png", "Assets/T/Rock_AO.png", "Assets/T/Rock_Normal.png", "Assets/T/Rock_Mud.png" };

            var rows = MaskTextureMaker.FillFromNames(MaskPreset.MaskMap, current, paths, out var notes);

            Assert.IsFalse(rows[0].UsesTexture, "no metallic texture: Metallic keeps its constant");
            Assert.AreEqual(0f, rows[0].Value);
            Assert.AreEqual("Assets/T/Rock_AO.png", rows[1].TexturePath);
            Assert.IsFalse(rows[2].UsesTexture);
            Assert.AreEqual(1f, rows[2].Value);
            Assert.AreEqual("Assets/T/Rock_Roughness.png", rows[3].TexturePath);
            Assert.IsTrue(rows[3].Invert);
            Assert.IsTrue(notes.Any(n => n.StartsWith("Rock_Mud.png") && n.Contains("not recognised")));
            Assert.IsTrue(notes.Any(n => n.StartsWith("Rock_Normal.png") && n.Contains("not used")));
        }

        [Test]
        public void FillFromNames_MetallicSmoothnessFillsTwoRows()
        {
            var rows = MaskTextureMaker.FillFromNames(MaskPreset.MaskMap, Defaults(MaskPreset.MaskMap), new[] { "Assets/T/Rock_MetallicSmoothness.png" }, out _);

            Assert.AreEqual(AlphaChannel.Red, rows[0].Channel);
            Assert.AreEqual("Assets/T/Rock_MetallicSmoothness.png", rows[0].TexturePath);
            Assert.AreEqual(AlphaChannel.Alpha, rows[3].Channel);
            Assert.AreEqual("Assets/T/Rock_MetallicSmoothness.png", rows[3].TexturePath);
        }

        [Test]
        public void FillFromNames_FirstMatchWinsAndTheRestAreReported()
        {
            var rows = MaskTextureMaker.FillFromNames(MaskPreset.MaskMap, Defaults(MaskPreset.MaskMap),
                new[] { "Assets/T/Rock_Smoothness.png", "Assets/T/Rock_Roughness.png" }, out var notes);

            Assert.AreEqual("Assets/T/Rock_Smoothness.png", rows[3].TexturePath);
            Assert.IsFalse(rows[3].Invert);
            Assert.IsTrue(notes.Any(n => n.StartsWith("Rock_Roughness.png") && n.Contains("already filled")));
        }

        [Test]
        public void FillFromNames_ColourFillsDetailAlbedoFromLuminance()
        {
            var rows = MaskTextureMaker.FillFromNames(MaskPreset.DetailMap, Defaults(MaskPreset.DetailMap),
                new[] { "Assets/T/Moss_Albedo.png", "Assets/T/Moss_Normal_DX.png" }, out _);

            Assert.AreEqual(AlphaChannel.Luminance, rows[0].Channel);
            Assert.AreEqual("Assets/T/Moss_Normal_DX.png", rows[1].TexturePath);
            Assert.AreEqual(AlphaChannel.Green, rows[1].Channel);
            Assert.IsTrue(rows[1].Invert, "a DirectX normal's Y is flipped");
            Assert.AreEqual(AlphaChannel.Red, rows[3].Channel);
            Assert.IsFalse(rows[2].UsesTexture);
        }

        [Test]
        public void PackedNormal_FromADirectXMap_MatchesNormalGreenFlip()
        {
            var normalPixels = TestAssets.Pattern8(8, 8, 9);
            string normal = assets.WritePng8("Wall_Normal_DX.png", 8, 8, normalPixels);
            string rough = Grey("Wall_Roughness.png", 8, 10, out _);
            var rows = MaskTextureMaker.FillFromNames(MaskPreset.PackedNormal, Defaults(MaskPreset.PackedNormal), new[] { normal, rough }, out _);

            string packed = MaskTextureMaker.Make(rows, false, false, 0, 0, MaskTextureMaker.DefaultOutputPath(rows, "_PackedNormal"), ExistingOutput.Replace);
            string flipped = NormalGreenFlip.Convert(normal, NormalConvention.OpenGL, ExistingOutput.Replace);

            Assert.AreEqual(assets.Path("Wall_PackedNormal.png"), packed);
            var packedPixels = SourcePixels.Read(packed).Pixels;
            var flippedPixels = SourcePixels.Read(flipped).Pixels;
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(TestAssets.To8(flippedPixels[i].g), TestAssets.To8(packedPixels[i].g), "normal Y at " + i);
                Assert.AreEqual(TestAssets.To8(flippedPixels[i].r), TestAssets.To8(packedPixels[i].a), "normal X at " + i);
                Assert.AreEqual(255, TestAssets.To8(packedPixels[i].b), "occlusion default at " + i);
            }
        }

        [Test]
        public void Describe_SpellsOutTheMapping()
        {
            var rows = new[]
            {
                RowSource.FromTexture("Assets/T/metal.png", AlphaChannel.Red),
                RowSource.Constant(1f),
                RowSource.FromTexture("Assets/T/colour.png", AlphaChannel.Luminance),
                RowSource.FromTexture("Assets/T/rough.png", AlphaChannel.Red, true),
            };

            Assert.AreEqual("R <- metal.png (R)   G = 1.00   B <- colour.png (luminance)   A <- 1 - rough.png (R)", MaskTextureMaker.Describe(rows));
        }
    }
}
