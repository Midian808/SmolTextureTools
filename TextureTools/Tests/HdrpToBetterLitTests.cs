using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools.Tests
{
    public class HdrpToBetterLitTests
    {
        private TestAssets assets;
        private string baseMap, maskMap, normalMap, detailMap, emissionMap;

        [SetUp]
        public void SetUp()
        {
            assets = new TestAssets();
            var pixels = TestAssets.Pattern8(4, 4, 1);
            baseMap = assets.WritePng8("base.png", 4, 4, pixels);
            maskMap = assets.WritePng8("mask.png", 4, 4, pixels);
            normalMap = assets.WritePng8("normal.png", 4, 4, pixels);
            detailMap = assets.WritePng8("detail.png", 4, 4, pixels);
            emissionMap = assets.WritePng8("emission.png", 4, 4, pixels);
        }

        [TearDown]
        public void TearDown()
        {
            assets.Dispose();
        }

        //A material file as HDRP saves it: serializedVersion 6, the shader referenced by GUID, keywords in m_ShaderKeywords.
        //The HDRP shaders aren't in the project, so the shader is missing exactly as it is for an imported asset
        private sealed class HdrpMaterial
        {
            public string ShaderGuid = HdrpToBetterLit.HdrpLitGuid;
            public string Keywords = "_NORMALMAP_TANGENT_SPACE";
            public readonly Dictionary<string, (string guid, Vector2 scale, Vector2 offset)> Textures = new Dictionary<string, (string, Vector2, Vector2)>();
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();

            public HdrpMaterial Texture(string name, string assetPath, Vector2? scale = null, Vector2? offset = null)
            {
                Textures[name] = (AssetDatabase.AssetPathToGUID(assetPath), scale ?? Vector2.one, offset ?? Vector2.zero);
                return this;
            }

            public HdrpMaterial MissingTexture(string name)
            {
                Textures[name] = ("0123456789abcdef0123456789abcdef", Vector2.one, Vector2.zero);
                return this;
            }

            public HdrpMaterial Float(string name, float value)
            {
                Floats[name] = value;
                return this;
            }

            public HdrpMaterial Colour(string name, Color value)
            {
                Colors[name] = value;
                return this;
            }

            public string Yaml(string name)
            {
                string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
                var text = new StringBuilder();
                text.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n");
                text.Append("  serializedVersion: 6\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInternal: {fileID: 0}\n");
                text.Append("  m_Name: ").Append(name).Append('\n');
                text.Append("  m_Shader: {fileID: 4800000, guid: ").Append(ShaderGuid).Append(", type: 3}\n");
                text.Append("  m_ShaderKeywords: ").Append(Keywords).Append('\n');
                text.Append("  m_LightmapFlags: 4\n  m_EnableInstancingVariants: 0\n  m_DoubleSidedGI: 0\n  m_CustomRenderQueue: 2000\n");
                text.Append("  stringTagMap: {}\n  disabledShaderPasses: []\n  m_SavedProperties:\n    serializedVersion: 3\n");
                text.Append("    m_TexEnvs:").Append(Textures.Count == 0 ? " []\n" : "\n");
                foreach (var t in Textures)
                {
                    text.Append("    - ").Append(t.Key).Append(":\n");
                    text.Append("        m_Texture: {fileID: 2800000, guid: ").Append(t.Value.guid).Append(", type: 3}\n");
                    text.Append("        m_Scale: {x: ").Append(F(t.Value.scale.x)).Append(", y: ").Append(F(t.Value.scale.y)).Append("}\n");
                    text.Append("        m_Offset: {x: ").Append(F(t.Value.offset.x)).Append(", y: ").Append(F(t.Value.offset.y)).Append("}\n");
                }
                text.Append("    m_Floats:").Append(Floats.Count == 0 ? " []\n" : "\n");
                foreach (var f in Floats) text.Append("    - ").Append(f.Key).Append(": ").Append(F(f.Value)).Append('\n');
                text.Append("    m_Colors:").Append(Colors.Count == 0 ? " []\n" : "\n");
                foreach (var c in Colors)
                    text.Append("    - ").Append(c.Key).Append(": {r: ").Append(F(c.Value.r)).Append(", g: ").Append(F(c.Value.g))
                        .Append(", b: ").Append(F(c.Value.b)).Append(", a: ").Append(F(c.Value.a)).Append("}\n");
                return text.ToString();
            }
        }

        private string Write(string name, HdrpMaterial material)
        {
            return assets.WriteAndImport(name + ".mat", Encoding.UTF8.GetBytes(material.Yaml(name)));
        }

        private HdrpMaterial StandardLit()
        {
            return new HdrpMaterial { Keywords = "_MASKMAP _NORMALMAP _NORMALMAP_TANGENT_SPACE" }
                .Texture("_BaseColorMap", baseMap, new Vector2(2, 3), new Vector2(0.25f, 0.5f))
                .Texture("_MaskMap", maskMap)
                .Texture("_NormalMap", normalMap)
                .Colour("_BaseColor", new Color(0.6911765f, 0.63229334f, 0.56920415f, 1f))
                .Float("_Metallic", 0.447f)
                .Float("_Smoothness", 0.023f)
                .Float("_MetallicRemapMin", 0.1f).Float("_MetallicRemapMax", 0.9f)
                .Float("_SmoothnessRemapMin", 0.055796657f).Float("_SmoothnessRemapMax", 0.58875805f)
                .Float("_AORemapMin", 0.2f).Float("_AORemapMax", 0.8f)
                .Float("_NormalScale", 1.5f)
                .Float("_CullMode", 2f)
                .Float("_MaterialID", 1f);
        }

        private static ConversionPlan Plan(string path, float ev100 = HdrpToBetterLit.DefaultEv100)
        {
            return HdrpToBetterLit.Plan(AssetDatabase.LoadAssetAtPath<Material>(path), path, ev100);
        }

        private static Material Convert(string path, float ev100 = HdrpToBetterLit.DefaultEv100)
        {
            var plan = Plan(path, ev100);
            Assert.IsTrue(plan.Converts, plan.SkipReason);
            Assert.AreEqual(1, HdrpToBetterLit.Apply(new[] { plan }, ExistingOutput.Replace), plan.SkipReason);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static string FileText(string assetPath)
        {
            return File.ReadAllText(TestAssets.FullPath(assetPath));
        }

        private static void AssertVector(Vector4 expected, Vector4 actual)
        {
            Assert.That(Vector4.Distance(expected, actual), Is.LessThan(1e-5f), "expected " + expected + ", got " + actual);
        }

        private static void AssertColour(Color expected, Color actual, float tolerance = 1e-5f)
        {
            Assert.That(Mathf.Abs(expected.r - actual.r) + Mathf.Abs(expected.g - actual.g) + Mathf.Abs(expected.b - actual.b) + Mathf.Abs(expected.a - actual.a),
                Is.LessThan(tolerance), "expected " + expected + ", got " + actual);
        }

        private static bool NoteMentions(ConversionPlan plan, string text)
        {
            return plan.Notes.Any(n => n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Test]
        public void BetterLit_IsInstalledInThisProject_AndAbsentGuidsAreDetected()
        {
            Assert.IsTrue(HdrpToBetterLit.BetterLitInstalled());
            Assert.IsFalse(HdrpToBetterLit.BetterLitInstalled(new[] { HdrpToBetterLit.BetterLitLitGuid, "00000000000000000000000000000abc" }));
        }

        [Test]
        public void Classify_RecognisesEachShaderAndSkipsAllButLit()
        {
            var cases = new Dictionary<string, MaterialSource>
            {
                { HdrpToBetterLit.HdrpLitGuid, MaterialSource.HdrpLit },
                { HdrpToBetterLit.HdrpLitTessellationGuid, MaterialSource.HdrpLitTessellation },
                { HdrpToBetterLit.HdrpLayeredLitGuid, MaterialSource.HdrpLayeredLit },
                { HdrpToBetterLit.HdrpLayeredLitTessellationGuid, MaterialSource.HdrpLayeredLitTessellation },
                { HdrpToBetterLit.HdrpUnlitGuid, MaterialSource.HdrpUnlit },
                { HdrpToBetterLit.HdrpAxFGuid, MaterialSource.HdrpAxF },
                { HdrpToBetterLit.HdrpTerrainLitGuid, MaterialSource.HdrpTerrainLit },
                { HdrpToBetterLit.HdrpDecalGuid, MaterialSource.HdrpDecal },
                { HdrpToBetterLit.UrpLitGuid, MaterialSource.UrpLit },
                { "fedcba9876543210fedcba9876543210", MaterialSource.UnknownMissing },
            };
            int i = 0;
            foreach (var c in cases)
            {
                string path = Write("m" + i++, new HdrpMaterial { ShaderGuid = c.Key }.Texture("_BaseColorMap", baseMap));
                string before = FileText(path);
                var plan = Plan(path);
                Assert.AreEqual(c.Value, plan.Source, c.Key);
                Assert.IsTrue(plan.Listed);
                bool lit = c.Value == MaterialSource.HdrpLit || c.Value == MaterialSource.HdrpLitTessellation;
                Assert.AreEqual(lit, plan.Converts, c.Value.ToString());
                if (!lit)
                {
                    Assert.IsNotEmpty(plan.SkipReason);
                    Assert.AreEqual(0, HdrpToBetterLit.Apply(new[] { plan }));
                    Assert.AreEqual(before, FileText(path), "a skipped material is left unchanged");
                }
            }
            StringAssert.Contains("fedcba9876543210fedcba9876543210", Plan(Write("unknown", new HdrpMaterial { ShaderGuid = "fedcba9876543210fedcba9876543210" })).SkipReason);
            StringAssert.Contains("URP", Plan(Write("urp", new HdrpMaterial { ShaderGuid = HdrpToBetterLit.UrpLitGuid })).SkipReason);
        }

        [Test]
        public void Classify_LeavesMaterialsWithAShaderAloneAndUnlisted()
        {
            string path = assets.Path("standard.mat");
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var plan = Plan(path);
            Assert.AreEqual(MaterialSource.ShaderPresent, plan.Source);
            Assert.IsFalse(plan.Listed);
            Assert.IsFalse(plan.Converts);
            Assert.AreEqual("Standard", AssetDatabase.LoadAssetAtPath<Material>(path).shader.name);
        }

        [Test]
        public void Convert_StandardLit_SetsShaderTexturesValuesAndKeywords()
        {
            string path = Write("standardLit", StandardLit());
            string guidBefore = AssetDatabase.AssetPathToGUID(path);
            var material = Convert(path);

            Assert.AreEqual(HdrpToBetterLit.BetterLitLitGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            Assert.AreEqual(guidBefore, AssetDatabase.AssetPathToGUID(path), "converted in place");

            Assert.AreEqual(baseMap, AssetDatabase.GetAssetPath(material.GetTexture("_AlbedoMap")));
            Assert.AreEqual(new Vector2(2, 3), material.GetTextureScale("_AlbedoMap"));
            Assert.AreEqual(new Vector2(0.25f, 0.5f), material.GetTextureOffset("_AlbedoMap"));
            Assert.AreEqual(maskMap, AssetDatabase.GetAssetPath(material.GetTexture("_MaskMap")));
            Assert.AreEqual(normalMap, AssetDatabase.GetAssetPath(material.GetTexture("_NormalMap")));
            AssertColour(new Color(0.6911765f, 0.63229334f, 0.56920415f, 1f), material.GetColor("_Tint"));
            Assert.AreEqual(1.5f, material.GetFloat("_NormalStrength"), 1e-6f);

            AssertVector(new Vector4(0.1f, 0.9f, 0, 0), material.GetVector("_MetallicRemap"));
            AssertVector(new Vector4(0.055796657f, 0.58875805f, 0, 0), material.GetVector("_SmoothnessRemap"));
            AssertVector(new Vector4(0.2f, 0.8f, 0, 0), material.GetVector("_AORemap"));
            AssertVector(new Vector4(0, 1, 0, 0), material.GetVector("_HeightRemap"));

            CollectionAssert.AreEquivalent(new[] { "_MASKMAP", "_NORMALMAP" }, material.shaderKeywords);
            var serialized = new SerializedObject(material);
            var invalid = serialized.FindProperty("m_InvalidKeywords");
            if (invalid != null) Assert.AreEqual(0, invalid.arraySize, "HDRP keywords removed");
            Assert.AreEqual(material.shader.renderQueue, material.renderQueue, "the shader's own queue");
            Assert.AreEqual("Opaque", material.GetTag("RenderType", false, ""), "no override: the shader's own tag");
            Assert.AreEqual(1f, material.GetFloat("_IsConverted"));
            Assert.AreEqual(2f, material.GetFloat("_CullMode"));
            Assert.AreEqual(0f, material.GetFloat("_AlphaThreshold"));
            CollectionAssert.DoesNotContain(material.shaderKeywords, "_EMISSION");

            //Saved to disk: the new shader reference, and the HDRP values still kept in the file (H2)
            string text = FileText(path);
            StringAssert.Contains("guid: " + HdrpToBetterLit.BetterLitLitGuid, text);
            StringAssert.Contains("_BaseColorMap:", text);
            StringAssert.Contains("_SmoothnessRemapMax: 0.58875805", text);
            var kept = SavedProperties.Read(material);
            Assert.AreEqual(baseMap, AssetDatabase.GetAssetPath(kept.Texture("_BaseColorMap").Texture));
        }

        [Test]
        public void Convert_WithoutMaskMap_KeepsRemapsNeutralAndCopiesConstants()
        {
            var source = StandardLit();
            source.Textures.Remove("_MaskMap");
            var material = Convert(Write("noMask", source));

            CollectionAssert.AreEquivalent(new[] { "_NORMALMAP" }, material.shaderKeywords);
            Assert.AreEqual(0.447f, material.GetFloat("_Metallic"), 1e-6f);
            Assert.AreEqual(0.023f, material.GetFloat("_Smoothness"), 1e-6f);
            AssertVector(new Vector4(0, 1, 0, 0), material.GetVector("_MetallicRemap"));
            AssertVector(new Vector4(0, 1, 0, 0), material.GetVector("_SmoothnessRemap"));
            AssertVector(new Vector4(0, 1, 0, 0), material.GetVector("_AORemap"));
        }

        [Test]
        public void Plan_UnsavedPropertiesTakeHdrpDefaults()
        {
            var source = new HdrpMaterial().Texture("_MaskMap", maskMap).Float("_MetallicRemapMin", 0.3f);
            var plan = Plan(Write("defaults", source));
            AssertVector(new Vector4(0.3f, 1f, 0, 0), plan.Vectors["_MetallicRemap"]);
            Assert.AreEqual(0.5f, plan.Floats["_Smoothness"]);
            Assert.AreEqual(0f, plan.Floats["_Metallic"]);
            AssertColour(Color.white, plan.Colors["_Tint"]);
            Assert.AreEqual(1f, plan.Floats["_DoubleSidedNormalMode"]);
            Assert.IsEmpty(plan.Notes);
        }

        [Test]
        public void Convert_AlphaClipped_SetsUpCutoutLikeBetterLitsInspector()
        {
            var source = StandardLit().Float("_AlphaCutoffEnable", 1f).Float("_AlphaCutoff", 0.4f)
                .Float("_AlphaRemapMin", 0.1f).Float("_AlphaRemapMax", 0.9f);
            var material = Convert(Write("clipped", source));

            Assert.AreEqual(HdrpToBetterLit.BetterLitLitGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            CollectionAssert.Contains(material.shaderKeywords, "_ALPHACUT");
            Assert.AreEqual(0.4f, material.GetFloat("_AlphaThreshold"), 1e-6f);
            Assert.AreEqual(0.4f, material.GetFloat("_Cutoff"), 1e-6f);
            Assert.AreEqual(2450, material.renderQueue);
            Assert.AreEqual("TransparentCutout", material.GetTag("RenderType", false, ""));
            AssertVector(new Vector4(0.1f, 0.9f, 0, 0), material.GetVector("_HeightRemap"));
        }

        [Test]
        public void Convert_ClipWithZeroCutoff_LeavesClippingOff()
        {
            var plan = Plan(Write("zeroCut", StandardLit().Float("_AlphaCutoffEnable", 1f).Float("_AlphaCutoff", 0f)));
            CollectionAssert.DoesNotContain(plan.Keywords, "_ALPHACUT");
            Assert.AreEqual(-1, plan.RenderQueue);
        }

        [Test]
        public void Convert_Transparent_UsesLitAlpha_AndFlagsOtherBlendModes()
        {
            var source = new HdrpMaterial { Keywords = "_BLENDMODE_ALPHA _SURFACE_TYPE_TRANSPARENT" }
                .Float("_SurfaceType", 1f).Float("_BlendMode", 0f)
                .Colour("_BaseColor", new Color(0.27058825f, 0.6156863f, 0.7058824f, 0.2f));
            string path = Write("transparent", source);
            Assert.IsEmpty(Plan(path).Notes);
            var material = Convert(path);
            Assert.AreEqual(HdrpToBetterLit.BetterLitLitAlphaGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            AssertColour(new Color(0.27058825f, 0.6156863f, 0.7058824f, 0.2f), material.GetColor("_Tint"));
            Assert.AreEqual(3000, material.renderQueue, "the shader's own Transparent queue");

            Assert.IsTrue(NoteMentions(Plan(Write("additive", new HdrpMaterial().Float("_SurfaceType", 1f).Float("_BlendMode", 1f))), "Additive"));
            Assert.IsTrue(NoteMentions(Plan(Write("premultiply", new HdrpMaterial().Float("_SurfaceType", 1f).Float("_BlendMode", 4f))), "Premultiplied"));
            Assert.IsTrue(NoteMentions(Plan(Write("refraction", new HdrpMaterial().Float("_SurfaceType", 1f).Float("_RefractionModel", 1f))), "Refraction"));
        }

        [Test]
        public void EmissionScale_FollowsHdrpExposureFormula()
        {
            Assert.AreEqual(1f / 1.2f, HdrpToBetterLit.ExposureMultiplier(0f), 1e-6f);
            Assert.AreEqual(1f / (1.2f * 1024f), HdrpToBetterLit.ExposureMultiplier(10f), 1e-9f);
            Assert.AreEqual(1f, HdrpToBetterLit.EmissionScale(5f, 0f), 1e-6f, "weight 0: exposure ignored");
            Assert.AreEqual(HdrpToBetterLit.ExposureMultiplier(5f), HdrpToBetterLit.EmissionScale(5f, 1f), 1e-9f);
            Assert.AreEqual(Mathf.Lerp(1f, 1f / 1.2f, 0.958f), HdrpToBetterLit.EmissionScale(0f, 0.958f), 1e-6f);
        }

        [Test]
        public void Convert_Emission_ScalesByExposureAndSplitsColourAndMultiplier()
        {
            //Values from this project's Fortress of Hell Lava-Ground material
            var source = StandardLit()
                .Texture("_EmissiveColorMap", emissionMap, new Vector2(2, 3), new Vector2(0.25f, 0.5f))
                .Colour("_EmissiveColor", new Color(1000f, 147.64418f, 0f, 1000f))
                .Float("_EmissiveExposureWeight", 0.958f);
            var material = Convert(Write("emissive", source), 0f);

            float scale = HdrpToBetterLit.EmissionScale(0f, 0.958f);
            CollectionAssert.Contains(material.shaderKeywords, "_EMISSION");
            Assert.AreEqual(emissionMap, AssetDatabase.GetAssetPath(material.GetTexture("_EmissionMap")));
            Assert.AreEqual(1000f * scale, material.GetFloat("_EmissionMultiplier"), 1e-2f);
            Color stored = material.GetColor("_EmissionColor");
            AssertColour(new Color(1f, 0.14764418f, 0f, 1f), stored.linear, 1e-4f);
        }

        [Test]
        public void Plan_Emission_BlackIsOff_AndMismatchedTilingIsFlagged()
        {
            var off = Plan(Write("black", StandardLit().Texture("_EmissiveColorMap", emissionMap)));
            CollectionAssert.DoesNotContain(off.Keywords, "_EMISSION");

            var tiled = Plan(Write("tiled", StandardLit().Texture("_EmissiveColorMap", emissionMap, new Vector2(5, 5))
                .Colour("_EmissiveColor", new Color(2f, 0.29539832f, 0f, 2f))));
            Assert.IsTrue(NoteMentions(tiled, "Emission map tiling"));

            var sameAsBase = Plan(Write("sameAsBase", StandardLit().Texture("_EmissiveColorMap", emissionMap, new Vector2(5, 5))
                .Colour("_EmissiveColor", Color.white).Float("_UVEmissive", 6f)));
            Assert.IsFalse(NoteMentions(sameAsBase, "Emission map"));

            var otherUv = Plan(Write("otherUv", StandardLit().Texture("_EmissiveColorMap", emissionMap, new Vector2(2, 3), new Vector2(0.25f, 0.5f))
                .Colour("_EmissiveColor", Color.white).Float("_UVEmissive", 1f)));
            Assert.IsTrue(NoteMentions(otherUv, "different UV set"));
        }

        [Test]
        public void Plan_Emission_AlbedoAffectsEmissive_MultipliesByLinearBaseColour()
        {
            var plan = Plan(Write("albedoEmissive", new HdrpMaterial()
                .Colour("_BaseColor", new Color(0.5f, 1f, 1f, 1f))
                .Colour("_EmissiveColor", new Color(2f, 2f, 2f, 1f))
                .Float("_EmissiveExposureWeight", 0f)
                .Float("_AlbedoAffectEmissive", 1f)));
            Assert.AreEqual(2f, plan.Floats["_EmissionMultiplier"], 1e-5f);
            AssertColour(new Color(Mathf.GammaToLinearSpace(0.5f), 1f, 1f, 1f), plan.Colors["_EmissionColor"].linear, 1e-4f);
            Assert.IsEmpty(plan.Notes, "no base map, so nothing is lost");
        }

        //EmissionForBetterLit stores _EmissionColor as gamma because Unity converts a plain colour property to linear for the
        //shader in a linear project. This renders a property declared like Better Lit's _EmissionColor and reads back what
        //the shader received
        [Test]
        public void PlainColourProperty_ReachesTheShaderAsLinear()
        {
            const string shaderSource =
                "Shader \"Hidden/TextureToolsColourProbe\" {\n" +
                "  Properties { _Probe(\"Probe\", Color) = (0,0,0,0) }\n" +
                "  SubShader { Pass { ZTest Always Cull Off ZWrite Off\n" +
                "    CGPROGRAM\n    #pragma vertex vert_img\n    #pragma fragment frag\n    #include \"UnityCG.cginc\"\n" +
                "    float4 _Probe;\n    float4 frag(v2f_img i) : SV_Target { return _Probe; }\n    ENDCG\n  } }\n}\n";
            string shaderPath = assets.WriteAndImport("probe.shader", Encoding.UTF8.GetBytes(shaderSource));
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            Assert.IsTrue(shader.isSupported);

            bool asyncWas = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            var material = new Material(shader);
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readBack = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                var stored = new Color(0.5f, 0.25f, 0.8f, 1f);
                material.SetColor("_Probe", stored);
                Graphics.Blit(Texture2D.whiteTexture, target, material, 0);
                RenderTexture.active = target;
                readBack.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                readBack.Apply(false);
                Color received = readBack.GetPixel(0, 0);
                Color expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? stored.linear : stored;
                AssertColour(expected, received, 2e-3f);
            }
            finally
            {
                RenderTexture.active = previous;
                ShaderUtil.allowAsyncCompilation = asyncWas;
                Object.DestroyImmediate(readBack);
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Plan_DetailTiling_BakesInBaseTilingWhenLinked()
        {
            var linked = StandardLit().Texture("_DetailMap", detailMap, new Vector2(4, 5), new Vector2(0.1f, 0.2f))
                .Float("_DetailAlbedoScale", 0.5f).Float("_DetailNormalScale", 1.25f).Float("_DetailSmoothnessScale", 0.75f);
            var material = Convert(Write("linked", linked));
            CollectionAssert.Contains(material.shaderKeywords, "_DETAIL");
            Assert.AreEqual(detailMap, AssetDatabase.GetAssetPath(material.GetTexture("_DetailMap")));
            //base scale (2, 3), offset (0.25, 0.5)
            Assert.That(Vector2.Distance(new Vector2(8, 15), material.GetTextureScale("_DetailMap")), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(new Vector2(0.1f * 2 + 0.25f, 0.2f * 3 + 0.5f), material.GetTextureOffset("_DetailMap")), Is.LessThan(1e-5f));
            Assert.AreEqual(0.5f, material.GetFloat("_DetailAlbedoStrength"), 1e-6f);
            Assert.AreEqual(1.25f, material.GetFloat("_DetailNormalStrength"), 1e-6f);
            Assert.AreEqual(0.75f, material.GetFloat("_DetailSmoothnessStrength"), 1e-6f);

            var unlinked = Plan(Write("unlinked", StandardLit().Texture("_DetailMap", detailMap, new Vector2(4, 5), new Vector2(0.1f, 0.2f))
                .Float("_LinkDetailsWithBase", 0f).Float("_UVDetail", 1f)));
            Assert.AreEqual(new Vector2(4, 5), unlinked.Textures["_DetailMap"].Scale);
            Assert.AreEqual(new Vector2(0.1f, 0.2f), unlinked.Textures["_DetailMap"].Offset);
            Assert.AreEqual(1f, unlinked.Floats["_DetailUVSource"]);

            var uv2 = Plan(Write("detailUv2", StandardLit().Texture("_DetailMap", detailMap).Float("_UVDetail", 2f)));
            Assert.AreEqual(0f, uv2.Floats["_DetailUVSource"]);
            Assert.IsTrue(NoteMentions(uv2, "Detail map uses UV2"));
        }

        [Test]
        public void Convert_DoubleSided_IgnoresSavedCullMode()
        {
            var material = Convert(Write("doubleSided", StandardLit().Float("_DoubleSidedEnable", 1f).Float("_DoubleSidedNormalMode", 0f).Float("_CullMode", 2f)));
            Assert.AreEqual(0f, material.GetFloat("_CullMode"));
            Assert.AreEqual(0f, material.GetFloat("_DoubleSidedNormalMode"));
        }

        [Test]
        public void Plan_UvSets_AndProjections()
        {
            var uv1 = Plan(Write("uv1", StandardLit().Float("_UVBase", 1f)));
            Assert.AreEqual(1f, uv1.Floats["_UVSource"]);
            Assert.IsEmpty(uv1.Notes);

            var uv3 = Plan(Write("uv3", StandardLit().Float("_UVBase", 3f)));
            Assert.AreEqual(0f, uv3.Floats["_UVSource"]);
            Assert.IsTrue(NoteMentions(uv3, "UV3"));

        }

        [Test]
        public void Convert_Planar_ProjectsAlongYWithWorldScaleAndFlippedV()
        {
            //Base tiling (2, 3), offset (0.25, 0.5)
            var source = StandardLit().Float("_UVBase", 4f).Float("_TexWorldScale", 0.5f)
                .Texture("_DetailMap", detailMap, new Vector2(4, 5), new Vector2(0.1f, 0.2f)).Float("_UVDetail", 1f);
            var plan = Plan(Write("planar", source));
            Assert.IsTrue(NoteMentions(plan, "Planar mapping"));
            var material = Convert(AssetDatabase.GetAssetPath(plan.Material));

            Assert.AreEqual(3f, material.GetFloat("_UVSource"));
            Assert.AreEqual(0f, material.GetFloat("_TriplanarSpace"));
            Assert.That(Vector2.Distance(new Vector2(1f, -1.5f), material.GetTextureScale("_AlbedoMap")), Is.LessThan(1e-5f));
            Assert.AreEqual(new Vector2(0.25f, 0.5f), material.GetTextureOffset("_AlbedoMap"));
            CollectionAssert.DoesNotContain(material.shaderKeywords, "_TRIPLANAR");

            //Detail: linked tiling (8, 15), offset (0.45, 1.1), then the projection; HDRP projects it like the base, whatever _UVDetail says
            Assert.AreEqual(3f, material.GetFloat("_DetailUVSource"));
            Assert.That(Vector2.Distance(new Vector2(4f, -7.5f), material.GetTextureScale("_DetailMap")), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(new Vector2(0.45f, 1.1f), material.GetTextureOffset("_DetailMap")), Is.LessThan(1e-5f));
        }

        [Test]
        public void Convert_Triplanar_UsesTriplanarKeywordsAndObjectSpace()
        {
            var source = StandardLit().Float("_UVBase", 5f).Float("_TexWorldScale", 2f).Float("_ObjectSpaceUVMapping", 1f)
                .Texture("_DetailMap", detailMap).Float("_LinkDetailsWithBase", 0f);
            var plan = Plan(Write("triplanar", source));
            Assert.IsTrue(NoteMentions(plan, "Triplanar mapping"));
            var material = Convert(AssetDatabase.GetAssetPath(plan.Material));

            CollectionAssert.IsSupersetOf(material.shaderKeywords, new[] { "_TRIPLANAR", "_DETAILTRIPLANAR" });
            Assert.AreEqual(0f, material.GetFloat("_UVSource"));
            Assert.AreEqual(1f, material.GetFloat("_TriplanarSpace"));
            Assert.AreEqual(1f, material.GetFloat("_DetailTriplanarSpace"));
            Assert.That(Vector2.Distance(new Vector2(4f, 6f), material.GetTextureScale("_AlbedoMap")), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(new Vector2(2f, 2f), material.GetTextureScale("_DetailMap")), Is.LessThan(1e-5f));
        }

        private string Grey(string fileName, int seed, out Color32[] pixels)
        {
            var random = new System.Random(seed);
            pixels = new Color32[16];
            for (int i = 0; i < 16; i++)
            {
                byte v = (byte)random.Next(256);
                pixels[i] = new Color32(v, v, v, 255);
            }
            return assets.WritePng8(fileName, 4, 4, pixels, false);
        }

        [Test]
        public void Convert_PixelDisplacement_PacksHeightIntoAlbedoAlphaAndTurnsOnParallax()
        {
            string heightMap = Grey("height.png", 7, out var heightPixels);
            string baseBefore = FileText(baseMap);
            var plan = Plan(Write("pixelDisp", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 2f)));
            Assert.AreEqual(PackKind.AlbedoHeight, plan.Packs["_AlbedoMap"].Kind);
            Assert.IsTrue(NoteMentions(plan, "parallax"));
            var material = Convert(AssetDatabase.GetAssetPath(plan.Material));

            Assert.AreEqual(HdrpToBetterLit.BetterLitLitGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            CollectionAssert.Contains(material.shaderKeywords, "_PARALLAX");
            Assert.AreEqual(HdrpToBetterLit.ParallaxHeight, material.GetFloat("_ParallaxHeight"), 1e-6f);
            string packed = AssetDatabase.GetAssetPath(material.GetTexture("_AlbedoMap"));
            Assert.AreEqual(assets.Path("base_AlbedoHeight.png"), packed);
            Assert.AreEqual(new Vector2(2, 3), material.GetTextureScale("_AlbedoMap"), "keeps the base map's tiling");

            var basePixels = TestAssets.Pattern8(4, 4, 1);
            var result = SourcePixels.Read(packed).Pixels;
            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(basePixels[i].r, TestAssets.To8(result[i].r));
                Assert.AreEqual(basePixels[i].g, TestAssets.To8(result[i].g));
                Assert.AreEqual(basePixels[i].b, TestAssets.To8(result[i].b));
                Assert.AreEqual(heightPixels[i].r, TestAssets.To8(result[i].a));
            }
            Assert.AreEqual(baseBefore, FileText(baseMap), "source untouched");
        }

        [Test]
        public void Convert_VertexDisplacement_UsesTessellationWithHdrpsWorldAmplitude()
        {
            string heightMap = Grey("height.png", 8, out _);
            var material = Convert(Write("vertexDisp", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 1f)
                .Float("_HeightAmplitude", 0.05f).Float("_HeightCenter", 0.4f)));
            Assert.AreEqual(HdrpToBetterLit.BetterLitLitTessellationGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            Assert.AreEqual(0.05f, material.GetFloat("_TessellationDisplacement"), 1e-6f);
            Assert.AreEqual(0.4f, material.GetFloat("_TessellationOffset"), 1e-6f);
        }

        [Test]
        public void Convert_Tessellation_ConvertsCentimetresToMetres()
        {
            string heightMap = Grey("height.png", 9, out _);
            var source = StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 3f)
                .Float("_HeightTessAmplitude", 5f).Float("_HeightTessCenter", 0.25f);
            source.ShaderGuid = HdrpToBetterLit.HdrpLitTessellationGuid;
            var material = Convert(Write("tess", source));
            Assert.AreEqual(HdrpToBetterLit.BetterLitLitTessellationGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material.shader)));
            Assert.AreEqual(0.05f, material.GetFloat("_TessellationDisplacement"), 1e-6f);
            Assert.AreEqual(0.25f, material.GetFloat("_TessellationOffset"), 1e-6f);
        }

        [Test]
        public void Plan_Height_OnlyWithDisplacement_AndDroppedWhenAlphaIsUsed()
        {
            string heightMap = Grey("height.png", 10, out _);
            var unused = Plan(Write("noDisp", StandardLit().Texture("_HeightMap", heightMap)));
            Assert.IsEmpty(unused.Packs);
            Assert.IsEmpty(unused.Notes);

            var clipped = Plan(Write("clipDisp", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 2f).Float("_AlphaCutoffEnable", 1f)));
            Assert.IsEmpty(clipped.Packs);
            Assert.IsTrue(NoteMentions(clipped, "Height map dropped"));
            CollectionAssert.DoesNotContain(clipped.Keywords, "_PARALLAX");
            Assert.AreEqual(BetterLitShader.Lit, clipped.Target);
        }

        [Test]
        public void Apply_SharedSources_WriteOnePackedTexture()
        {
            string heightMap = Grey("height.png", 11, out _);
            var plans = HdrpToBetterLit.Plan(new[]
            {
                Write("shareA", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 2f)),
                Write("shareB", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 2f)),
            }, 0f);
            Assert.AreEqual(2, HdrpToBetterLit.Apply(plans, ExistingOutput.Replace));
            var a = AssetDatabase.LoadAssetAtPath<Material>(plans[0].MaterialPath).GetTexture("_AlbedoMap");
            var b = AssetDatabase.LoadAssetAtPath<Material>(plans[1].MaterialPath).GetTexture("_AlbedoMap");
            Assert.AreSame(a, b);
            Assert.AreEqual(1, Directory.GetFiles(TestAssets.FullPath(assets.Folder), "*_AlbedoHeight*.png").Length);
        }

        [Test]
        public void Apply_CancelledPack_SkipsTheMaterialAndLeavesItUnchanged()
        {
            string heightMap = Grey("height.png", 12, out _);
            assets.WritePng8("base_AlbedoHeight.png", 4, 4, TestAssets.Pattern8(4, 4, 3));
            string path = Write("cancelled", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 2f));
            string before = FileText(path);
            var plans = HdrpToBetterLit.Plan(new[] { path }, 0f);
            Assert.AreEqual(0, HdrpToBetterLit.Apply(plans, ExistingOutput.Cancel));
            Assert.IsFalse(plans[0].Converts);
            StringAssert.Contains("cancelled", plans[0].SkipReason);
            Assert.AreEqual(before, FileText(path));
        }

        [Test]
        public void Convert_SpecularColour_UsesMapOrTheSharedWhiteTexture()
        {
            string specMap = assets.WritePng8("spec.png", 4, 4, TestAssets.Pattern8(4, 4, 4));
            var withMap = Convert(Write("specMap", StandardLit().Float("_MaterialID", 4f).Texture("_SpecularColorMap", specMap, new Vector2(2, 3), new Vector2(0.25f, 0.5f))
                .Colour("_SpecularColor", new Color(0.2f, 0.3f, 0.4f, 1f))));
            CollectionAssert.Contains(withMap.shaderKeywords, "_SPECULAR");
            Assert.AreEqual(specMap, AssetDatabase.GetAssetPath(withMap.GetTexture("_SpecularMap")));
            AssertColour(new Color(0.2f, 0.3f, 0.4f, 1f), withMap.GetColor("_SpecularTint"));

            string previous = HdrpToBetterLit.GeneratedFolder;
            HdrpToBetterLit.GeneratedFolder = assets.Folder + "/Generated";
            try
            {
                var noMap = Convert(Write("specWhite", StandardLit().Float("_MaterialID", 4f)));
                string white = AssetDatabase.GetAssetPath(noMap.GetTexture("_SpecularMap"));
                Assert.AreEqual(assets.Folder + "/Generated/White.png", white);
                Assert.IsTrue(SourcePixels.Read(white).Pixels.All(c => c.r == 1f && c.g == 1f && c.b == 1f));
                var again = Convert(Write("specWhite2", StandardLit().Float("_MaterialID", 4f)));
                Assert.AreEqual(white, AssetDatabase.GetAssetPath(again.GetTexture("_SpecularMap")), "reused");
            }
            finally
            {
                HdrpToBetterLit.GeneratedFolder = previous;
            }
        }

        [Test]
        public void Convert_Sss_InvertsThickness_FromConstantOrRemappedMap()
        {
            var constant = Convert(Write("sssConst", StandardLit().Float("_MaterialID", 0f).Float("_Thickness", 0.3f)));
            CollectionAssert.Contains(constant.shaderKeywords, "_CHEAPSSS");
            CollectionAssert.DoesNotContain(constant.shaderKeywords, "_CHEAPSSSTEXTURE");
            Assert.AreEqual(0.7f, constant.GetFloat("_CheapSSSThickness"), 1e-6f);
            AssertColour(Color.white, constant.GetColor("_CheapSSSTint"));

            string thicknessMap = Grey("thickness.png", 13, out var thicknessPixels);
            var mapped = Convert(Write("sssMap", StandardLit().Float("_MaterialID", 5f).Texture("_ThicknessMap", thicknessMap)
                .Colour("_ThicknessRemap", new Color(0.1f, 0.5f, 0f, 0f))));
            CollectionAssert.IsSupersetOf(mapped.shaderKeywords, new[] { "_CHEAPSSS", "_CHEAPSSSTEXTURE" });
            string packed = AssetDatabase.GetAssetPath(mapped.GetTexture("_CheapSSSTexture"));
            Assert.AreEqual(assets.Path("thickness_SSS.png"), packed);
            var result = SourcePixels.Read(packed).Pixels;
            for (int i = 0; i < 16; i++)
            {
                float expected = 1f - (0.1f + 0.5f * thicknessPixels[i].r / 255f);
                Assert.AreEqual(expected, result[i].a, 1f / 255f);
                Assert.AreEqual(1f, result[i].r);
            }
        }

        [Test]
        public void Plan_FlagsMaterialTypesAndDroppedFeatures()
        {
            Assert.IsTrue(NoteMentions(Plan(Write("aniso", StandardLit().Float("_MaterialID", 2f))), "Anisotropy"));
            Assert.IsTrue(NoteMentions(Plan(Write("irid", StandardLit().Float("_MaterialID", 3f))), "Iridescence"));
            Assert.IsTrue(NoteMentions(Plan(Write("sss", StandardLit().Float("_MaterialID", 0f))), "Subsurface Scattering becomes"));
            Assert.IsTrue(NoteMentions(Plan(Write("translucent", StandardLit().Float("_MaterialID", 5f))), "Translucent becomes"));
            Assert.IsTrue(NoteMentions(Plan(Write("coat", StandardLit().Float("_CoatMask", 0.5f))), "Clear coat"));
            Assert.IsTrue(NoteMentions(Plan(Write("bent", StandardLit().Texture("_BentNormalMap", normalMap))), "Bent normal"));
            Assert.IsTrue(NoteMentions(Plan(Write("specAA", StandardLit().Float("_EnableGeometricSpecularAA", 1f))), "specular AA"));
            Assert.IsTrue(NoteMentions(Plan(Write("strongNormal", StandardLit().Float("_NormalScale", 4f))), "above Better Lit's maximum"));

            var objectSpace = Plan(Write("objectSpace", StandardLit().Float("_NormalMapSpace", 1f).Texture("_NormalMapOS", normalMap)));
            Assert.IsTrue(NoteMentions(objectSpace, "Object-space"));
            CollectionAssert.DoesNotContain(objectSpace.Keywords, "_NORMALMAP");

            Assert.IsEmpty(Plan(Write("plain", StandardLit())).Notes);
            Assert.IsEmpty(Plan(Write("spec", StandardLit().Float("_MaterialID", 4f))).Notes, "Specular Colour converts fully");
        }

        [Test]
        public void Plan_FlagsTextureReferencesMissingFromTheProject()
        {
            var plan = Plan(Write("missingTexture", StandardLit().MissingTexture("_MaskMap")));
            Assert.IsTrue(NoteMentions(plan, "_MaskMap references a texture that isn't in the project"));
            CollectionAssert.DoesNotContain(plan.Keywords, "_MASKMAP");
        }

        [Test]
        public void CollectMaterials_SearchesFoldersRecursively()
        {
            AssetDatabase.CreateFolder(assets.Folder, "Sub");
            string top = Write("top", new HdrpMaterial());
            string nested = assets.Path("Sub/nested.mat");
            File.WriteAllText(TestAssets.FullPath(nested), new HdrpMaterial().Yaml("nested"));
            AssetDatabase.ImportAsset(nested, ImportAssetOptions.ForceSynchronousImport);

            CollectionAssert.AreEquivalent(new[] { top, nested }, HdrpToBetterLit.CollectMaterials(new[] { assets.Folder }));
            CollectionAssert.AreEquivalent(new[] { top }, HdrpToBetterLit.CollectMaterials(new[] { top, baseMap }));
        }

        [Test]
        public void Apply_IsOneUndoStep_RestoringTheHdrpMaterial()
        {
            var plans = HdrpToBetterLit.Plan(new[] { Write("undoA", StandardLit()), Write("undoB", StandardLit().Float("_AlphaCutoffEnable", 1f)) }, 0f);
            Assert.AreEqual(2, HdrpToBetterLit.Apply(plans, ExistingOutput.Replace));
            Assert.IsFalse(HdrpToBetterLit.ShaderIsMissing(plans[0].Material));

            Undo.PerformUndo();

            foreach (var plan in plans)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(plan.MaterialPath);
                Assert.IsTrue(HdrpToBetterLit.ShaderIsMissing(material), plan.MaterialPath);
                Assert.AreEqual(baseMap, AssetDatabase.GetAssetPath(SavedProperties.Read(material).Texture("_BaseColorMap").Texture));
                EditorUtility.SetDirty(material);
            }
            AssetDatabase.SaveAssets();
            foreach (var plan in plans)
            {
                StringAssert.Contains("guid: " + HdrpToBetterLit.HdrpLitGuid, FileText(plan.MaterialPath));
                Assert.AreEqual(MaterialSource.HdrpLit, Plan(plan.MaterialPath).Source, "convertible again");
            }
        }

        [Test]
        public void WindowText_BecomesTotalsAndLog()
        {
            string previous = HdrpToBetterLit.GeneratedFolder;
            HdrpToBetterLit.GeneratedFolder = assets.Folder + "/Generated";
            try
            {
                string heightMap = Grey("height.png", 14, out _);
                var plans = HdrpToBetterLit.Plan(new[]
                {
                    Write("w1", StandardLit()),
                    Write("w2", StandardLit().Float("_AlphaCutoffEnable", 1f)),
                    Write("w3", new HdrpMaterial().Float("_SurfaceType", 1f)),
                    Write("w4", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 1f)),
                    Write("w5", StandardLit().Texture("_HeightMap", heightMap).Float("_DisplacementMode", 1f)),
                    Write("w6", StandardLit().Float("_MaterialID", 4f)),
                    Write("w7", new HdrpMaterial { ShaderGuid = HdrpToBetterLit.HdrpDecalGuid }),
                }, 0f);
                string standard = assets.Path("standard.mat");
                AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), standard);
                plans.AddRange(HdrpToBetterLit.Plan(new[] { standard }, 0f));

                CollectionAssert.AreEqual(
                    new[] { "Lit", "Lit (alpha clip)", "Lit Alpha", "Lit Tessellation", "Lit Tessellation", "Lit", "(skipped)" },
                    plans.Where(p => p.Listed).Select(HdrpToBetterLit.Becomes));

                //w4 and w5 share one albedo + height file; w6 needs the white texture, which doesn't exist yet
                Assert.AreEqual(2, HdrpToBetterLit.PackedTextureCount(plans));
                Assert.AreEqual("6 to convert · 1 skipped · 2 packed textures will be written", HdrpToBetterLit.Totals(plans));

                string log = HdrpToBetterLit.Log(plans, 0f);
                StringAssert.StartsWith("HDRP to Better Lit (EV100 0): 6 converted, 1 skipped", log);
                StringAssert.Contains("w7.mat: skipped", log);
                StringAssert.DoesNotContain("standard.mat", log);

                HdrpToBetterLit.Apply(plans, ExistingOutput.Replace);
                var after = HdrpToBetterLit.Plan(new[] { Write("w8", StandardLit().Float("_MaterialID", 4f)) }, 0f);
                Assert.AreEqual(0, HdrpToBetterLit.PackedTextureCount(after), "the white texture now exists");
            }
            finally
            {
                HdrpToBetterLit.GeneratedFolder = previous;
            }
        }

        [Test]
        public void Summary_ListsNotesOrSkipReason()
        {
            var skipped = Plan(Write("decal", new HdrpMaterial { ShaderGuid = HdrpToBetterLit.HdrpDecalGuid }));
            StringAssert.Contains("skipped", HdrpToBetterLit.Summary(skipped));
            var flagged = Plan(Write("flagged", StandardLit().Float("_MaterialID", 2f)));
            StringAssert.Contains("Anisotropy", HdrpToBetterLit.Summary(flagged));
            StringAssert.Contains("Better Lit Lit", HdrpToBetterLit.Summary(flagged));
        }
    }
}
