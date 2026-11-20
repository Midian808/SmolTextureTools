using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools
{
    //What a material's shader reference points at (§9.1)
    public enum MaterialSource
    {
        ShaderPresent,
        HdrpLit,
        HdrpLitTessellation,
        HdrpLayeredLit,
        HdrpLayeredLitTessellation,
        HdrpUnlit,
        HdrpAxF,
        HdrpTerrainLit,
        HdrpDecal,
        UrpLit,
        UnknownMissing,
        Unreadable,
    }

    public enum BetterLitShader
    {
        Lit,
        LitAlpha,
        LitTessellation,
        LitTessellationAlpha,
    }

    public struct SavedTexture
    {
        public Texture Texture;
        //A reference is saved but the texture isn't in the project
        public bool Missing;
        public Vector2 Scale;
        public Vector2 Offset;

        public Vector4 ScaleOffset => new Vector4(Scale.x, Scale.y, Offset.x, Offset.y);
    }

    //A material's saved properties, read from the file's m_SavedProperties so they're available while its shader is missing (H1)
    public sealed class SavedProperties
    {
        public readonly Dictionary<string, SavedTexture> Textures = new Dictionary<string, SavedTexture>();
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();

        public static SavedProperties Read(Material material)
        {
            var result = new SavedProperties();
            var serialized = new SerializedObject(material);
            var saved = serialized.FindProperty("m_SavedProperties");

            var texEnvs = saved.FindPropertyRelative("m_TexEnvs");
            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                var pair = texEnvs.GetArrayElementAtIndex(i);
                var second = pair.FindPropertyRelative("second");
                var reference = second.FindPropertyRelative("m_Texture");
                var texture = reference.objectReferenceValue as Texture;
                result.Textures[pair.FindPropertyRelative("first").stringValue] = new SavedTexture
                {
                    Texture = texture,
                    Missing = texture == null && reference.objectReferenceInstanceIDValue != 0,
                    Scale = second.FindPropertyRelative("m_Scale").vector2Value,
                    Offset = second.FindPropertyRelative("m_Offset").vector2Value,
                };
            }

            var floats = saved.FindPropertyRelative("m_Floats");
            for (int i = 0; i < floats.arraySize; i++)
            {
                var pair = floats.GetArrayElementAtIndex(i);
                result.Floats[pair.FindPropertyRelative("first").stringValue] = pair.FindPropertyRelative("second").floatValue;
            }

            var colors = saved.FindPropertyRelative("m_Colors");
            for (int i = 0; i < colors.arraySize; i++)
            {
                var pair = colors.GetArrayElementAtIndex(i);
                result.Colors[pair.FindPropertyRelative("first").stringValue] = pair.FindPropertyRelative("second").colorValue;
            }

            return result;
        }

        //A property that isn't saved takes HDRP Lit's default (H1, §9.3)
        public float Float(string name)
        {
            return Floats.TryGetValue(name, out float value) ? value : HdrpToBetterLit.HdrpDefaultFloat(name);
        }

        public Color Color(string name)
        {
            return Colors.TryGetValue(name, out Color value) ? value : HdrpToBetterLit.HdrpDefaultColor(name);
        }

        public SavedTexture Texture(string name)
        {
            return Textures.TryGetValue(name, out SavedTexture value) ? value : new SavedTexture { Scale = Vector2.one };
        }
    }

    public enum PackKind
    {
        //Albedo RGB + height in A (H9)
        AlbedoHeight,
        //White RGB + Better Lit thickness in A, from HDRP's thickness map (H13)
        SssThickness,
        //The shared 4 x 4 white texture (H12)
        White,
    }

    //A texture the conversion writes before it changes the material (H9, H12, H13). Jobs with the same Key are written once
    //per batch and shared, so materials using the same source textures get the same packed file
    public sealed class PackJob
    {
        public PackKind Kind;
        public string OutputPath;
        public string AlbedoPath;
        public string HeightPath;
        public string ThicknessPath;
        //HDRP's _ThicknessRemap: the shader uses x + y × thickness map
        public Vector2 ThicknessRemap;

        public string Key
        {
            get
            {
                switch (Kind)
                {
                    case PackKind.AlbedoHeight: return "AlbedoHeight|" + AlbedoPath + "|" + HeightPath;
                    case PackKind.SssThickness:
                        return "SssThickness|" + ThicknessPath + "|" + ThicknessRemap.x.ToString("R") + "|" + ThicknessRemap.y.ToString("R");
                    default: return "White";
                }
            }
        }
    }

    //Everything the conversion of one material will do, worked out before anything is written (H3)
    public sealed class ConversionPlan
    {
        public Material Material;
        public string MaterialPath;
        public MaterialSource Source;
        public string ShaderGuid;

        public bool Converts;
        //Why a recognised material isn't converted (H17), empty when it converts
        public string SkipReason = "";

        public BetterLitShader Target;
        public readonly Dictionary<string, SavedTexture> Textures = new Dictionary<string, SavedTexture>();
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
        public readonly Dictionary<string, Vector4> Vectors = new Dictionary<string, Vector4>();
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
        //Material texture property → the texture written for it; its tiling comes from Textures
        public readonly Dictionary<string, PackJob> Packs = new Dictionary<string, PackJob>();
        public readonly List<string> Keywords = new List<string>();
        public int RenderQueue = -1;
        public string RenderType = "";
        //What differs from HDRP or can't carry over, one line each
        public readonly List<string> Notes = new List<string>();

        //Materials whose shader exists and isn't an HDRP one are left alone and not listed (§9.1)
        public bool Listed => Source != MaterialSource.ShaderPresent;
    }

    public static class HdrpToBetterLit
    {
        //From the .meta files in Unity's Graphics repository (Packages/com.unity.render-pipelines.high-definition/Runtime/Material)
        public const string HdrpLitGuid = "6e4ae4064600d784cac1e41a9e6f2e59";
        public const string HdrpLitTessellationGuid = "756bac9090102564582875f4c7e30202";
        public const string HdrpLayeredLitGuid = "81d02e8644315b742b154842a3a2f98c";
        public const string HdrpLayeredLitTessellationGuid = "eab0538d9d5746246806a9611c04ac4d";
        public const string HdrpUnlitGuid = "c4edd00ff2db5b24391a4fcb1762e459";
        public const string HdrpAxFGuid = "6510c57cccce5674092bdc0dd1ae98c1";
        public const string HdrpTerrainLitGuid = "eb607f5e8fc16e140b732f33d26c8c31";
        public const string HdrpDecalGuid = "1d64af84bdc970c4fae0c1e06dd95b73";
        public const string UrpLitGuid = "933532a4fcc9baf4fa0491de14d08ed7";

        //Better Lit's .betterlitpack shader assets (H4)
        public const string BetterLitLitGuid = "8d7180d86aab44f15bb161c4ab3113a9";
        public const string BetterLitLitAlphaGuid = "bea851d2911324d81adc2f7471fdcb8b";
        public const string BetterLitLitTessellationGuid = "cf5d184c3d9304a7baae9d5f7a3a3cd5";
        public const string BetterLitLitTessellationAlphaGuid = "610d27da9020049b4b6180390a02d185";

        public const float DefaultEv100 = 0f;

        private const string ErrorShaderName = "Hidden/InternalErrorShader";

        private static readonly Regex ShaderReference = new Regex(@"m_Shader:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32})");

        private static readonly Dictionary<string, MaterialSource> KnownGuids = new Dictionary<string, MaterialSource>
        {
            { HdrpLitGuid, MaterialSource.HdrpLit },
            { HdrpLitTessellationGuid, MaterialSource.HdrpLitTessellation },
            { HdrpLayeredLitGuid, MaterialSource.HdrpLayeredLit },
            { HdrpLayeredLitTessellationGuid, MaterialSource.HdrpLayeredLitTessellation },
            { HdrpUnlitGuid, MaterialSource.HdrpUnlit },
            { HdrpAxFGuid, MaterialSource.HdrpAxF },
            { HdrpTerrainLitGuid, MaterialSource.HdrpTerrainLit },
            { HdrpDecalGuid, MaterialSource.HdrpDecal },
            { UrpLitGuid, MaterialSource.UrpLit },
        };

        //HDRP Lit.shader defaults for the properties the conversion reads (§9.3)
        private static readonly Dictionary<string, float> HdrpFloatDefaults = new Dictionary<string, float>
        {
            { "_Metallic", 0f }, { "_Smoothness", 0.5f },
            { "_MetallicRemapMin", 0f }, { "_MetallicRemapMax", 1f },
            { "_SmoothnessRemapMin", 0f }, { "_SmoothnessRemapMax", 1f },
            { "_AORemapMin", 0f }, { "_AORemapMax", 1f },
            { "_AlphaRemapMin", 0f }, { "_AlphaRemapMax", 1f },
            { "_NormalScale", 1f }, { "_NormalMapSpace", 0f },
            { "_DetailAlbedoScale", 1f }, { "_DetailNormalScale", 1f }, { "_DetailSmoothnessScale", 1f },
            { "_LinkDetailsWithBase", 1f },
            { "_AlphaCutoffEnable", 0f }, { "_AlphaCutoff", 0.5f },
            { "_SurfaceType", 0f }, { "_BlendMode", 0f },
            { "_DoubleSidedEnable", 0f }, { "_DoubleSidedNormalMode", 1f },
            { "_UVBase", 0f }, { "_UVDetail", 0f }, { "_UVEmissive", 0f }, { "_TexWorldScale", 1f },
            { "_MaterialID", 1f }, { "_DisplacementMode", 0f },
            { "_EmissiveExposureWeight", 1f }, { "_AlbedoAffectEmissive", 0f },
            { "_CoatMask", 0f }, { "_EnableGeometricSpecularAA", 0f },
            { "_RefractionModel", 0f }, { "_DistortionEnable", 0f },
            { "_HeightAmplitude", 0.02f }, { "_HeightCenter", 0.5f },
            { "_HeightTessAmplitude", 2f }, { "_HeightTessCenter", 0.5f },
            { "_Thickness", 1f }, { "_ObjectSpaceUVMapping", 0f },
        };

        //Colours and vectors (a material file keeps both under m_Colors)
        private static readonly Dictionary<string, Color> HdrpColorDefaults = new Dictionary<string, Color>
        {
            { "_BaseColor", Color.white },
            { "_EmissiveColor", Color.black },
            { "_SpecularColor", Color.white },
            { "_ThicknessRemap", new Color(0f, 1f, 0f, 0f) },
        };

        //Better Lit's _ParallaxHeight given to converted pixel-displacement materials (H10)
        public const float ParallaxHeight = 0.02f;

        private static string generatedFolder;

        //Where the shared white texture lives: <TextureTools>/Generated, found from this script's location so the folder
        //can sit anywhere (D15b). Settable so tests can point it at their own folder
        public static string GeneratedFolder
        {
            get
            {
                if (generatedFolder != null) return generatedFolder;
                string script = AssetDatabase.FindAssets("HdrpToBetterLit t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => Path.GetFileName(p) == "HdrpToBetterLit.cs");
                if (script == null) throw new InvalidOperationException("Can't find HdrpToBetterLit.cs to locate the TextureTools folder");
                string editorFolder = Path.GetDirectoryName(script).Replace('\\', '/');
                return Path.GetDirectoryName(editorFolder).Replace('\\', '/') + "/Generated";
            }
            set => generatedFolder = value;
        }

        public static string WhiteTexturePath => GeneratedFolder + "/White.png";

        public static float HdrpDefaultFloat(string name)
        {
            return HdrpFloatDefaults.TryGetValue(name, out float value) ? value : 0f;
        }

        public static Color HdrpDefaultColor(string name)
        {
            return HdrpColorDefaults.TryGetValue(name, out Color value) ? value : Color.black;
        }

        public static string GuidOf(BetterLitShader shader)
        {
            switch (shader)
            {
                case BetterLitShader.Lit: return BetterLitLitGuid;
                case BetterLitShader.LitAlpha: return BetterLitLitAlphaGuid;
                case BetterLitShader.LitTessellation: return BetterLitLitTessellationGuid;
                default: return BetterLitLitTessellationAlphaGuid;
            }
        }

        public static Shader LoadShader(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null) return shader;
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Shader>().FirstOrDefault();
        }

        //Convert is only possible when every Better Lit shader the tool may target is in the project (H4)
        public static bool BetterLitInstalled()
        {
            return BetterLitInstalled(new[] { BetterLitLitGuid, BetterLitLitAlphaGuid, BetterLitLitTessellationGuid, BetterLitLitTessellationAlphaGuid });
        }

        public static bool BetterLitInstalled(IEnumerable<string> guids)
        {
            return guids.All(g => LoadShader(g) != null);
        }

        //The shader GUID as written in the material file. Read from the text because Unity doesn't expose the GUID of a missing reference
        public static string ReadShaderGuid(string materialPath)
        {
            string fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), materialPath));
            if (!File.Exists(fullPath)) return null;
            string text;
            using (var reader = new StreamReader(fullPath))
            {
                var buffer = new char[4096];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                text = new string(buffer, 0, read);
            }
            if (!text.StartsWith("%YAML")) return null;
            var match = ShaderReference.Match(text);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static bool ShaderIsMissing(Material material)
        {
            return material.shader == null || material.shader.name == ErrorShaderName;
        }

        public static MaterialSource Classify(Material material, string materialPath, out string guid)
        {
            guid = ReadShaderGuid(materialPath);
            bool missing = ShaderIsMissing(material);
            if (guid == null) return missing ? MaterialSource.Unreadable : MaterialSource.ShaderPresent;
            if (KnownGuids.TryGetValue(guid, out MaterialSource source)) return source;
            return missing ? MaterialSource.UnknownMissing : MaterialSource.ShaderPresent;
        }

        public static string Describe(MaterialSource source)
        {
            switch (source)
            {
                case MaterialSource.ShaderPresent: return "Shader present";
                case MaterialSource.HdrpLit: return "HDRP Lit";
                case MaterialSource.HdrpLitTessellation: return "HDRP Lit Tessellation";
                case MaterialSource.HdrpLayeredLit: return "HDRP LayeredLit";
                case MaterialSource.HdrpLayeredLitTessellation: return "HDRP LayeredLit Tessellation";
                case MaterialSource.HdrpUnlit: return "HDRP Unlit";
                case MaterialSource.HdrpAxF: return "HDRP AxF";
                case MaterialSource.HdrpTerrainLit: return "HDRP TerrainLit";
                case MaterialSource.HdrpDecal: return "HDRP Decal";
                case MaterialSource.UrpLit: return "URP Lit";
                case MaterialSource.UnknownMissing: return "Unrecognised missing shader";
                default: return "Unreadable material file";
            }
        }

        private static string SkipReasonFor(MaterialSource source, string guid)
        {
            switch (source)
            {
                case MaterialSource.HdrpLayeredLit:
                case MaterialSource.HdrpLayeredLitTessellation:
                    return "LayeredLit isn't converted: its layers would need Better Lit's texture layers, a separate mapping";
                case MaterialSource.HdrpUnlit: return "HDRP Unlit isn't converted";
                case MaterialSource.HdrpAxF: return "AxF has no Better Lit equivalent";
                case MaterialSource.HdrpTerrainLit: return "TerrainLit is a terrain material, not a mesh material";
                case MaterialSource.HdrpDecal: return "Decals have no Built-in equivalent";
                case MaterialSource.UrpLit: return "URP Lit — not an HDRP material";
                case MaterialSource.UnknownMissing: return "Unrecognised missing shader (GUID " + guid + ")";
                case MaterialSource.Unreadable: return "Shader is missing and the file isn't text-serialized, so its shader can't be identified";
                default: return "";
            }
        }

        //HDRP's camera exposure multiplier for an EV100 value: 1 / (1.2 × 2^EV100)
        public static float ExposureMultiplier(float ev100)
        {
            return 1f / (1.2f * Mathf.Pow(2f, ev100));
        }

        //HDRP shows emission E as E × lerp(1, exposure, weight) (LitBuiltinData.hlsl): the emission is divided by the exposure,
        //blended back to E by _EmissiveExposureWeight, and then the camera applies the exposure
        public static float EmissionScale(float ev100, float exposureWeight)
        {
            return Mathf.Lerp(1f, ExposureMultiplier(ev100), exposureWeight);
        }

        //Better Lit's emission colour and multiplier for a linear emission value (H11, H21).
        //HDRP's _EmissiveColor is linear: HDRP's UI saves LDR colour (linear) × intensity, and it reaches the shader unchanged.
        //Better Lit's _EmissionColor is a plain colour property, which Unity treats as sRGB and converts to linear for the shader
        //in a linear project, so the colour is stored as gamma with its brightest channel at 1, and the brightness goes in
        //_EmissionMultiplier, a float that isn't converted
        public static void EmissionForBetterLit(Color linear, out Color colour, out float multiplier)
        {
            multiplier = Mathf.Max(linear.r, Mathf.Max(linear.g, linear.b));
            if (multiplier <= 0f)
            {
                colour = Color.black;
                multiplier = 1f;
                return;
            }
            var normalised = new Color(linear.r / multiplier, linear.g / multiplier, linear.b / multiplier, 1f);
            colour = normalised.gamma;
            colour.a = 1f;
        }

        public static List<ConversionPlan> Plan(IEnumerable<string> materialPaths, float ev100)
        {
            var plans = new List<ConversionPlan>();
            foreach (string path in materialPaths)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null) plans.Add(Plan(material, path, ev100));
            }
            return plans;
        }

        //Expands a selection of materials and folders into material paths, folders searched recursively (H19)
        public static List<string> CollectMaterials(IEnumerable<string> assetPaths)
        {
            var result = new List<string>();
            foreach (string path in assetPaths)
            {
                if (AssetDatabase.IsValidFolder(path))
                    result.AddRange(AssetDatabase.FindAssets("t:Material", new[] { path }).Select(AssetDatabase.GUIDToAssetPath));
                else if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(Material))
                    result.Add(path);
            }
            return result.Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static ConversionPlan Plan(Material material, string materialPath, float ev100)
        {
            var plan = new ConversionPlan { Material = material, MaterialPath = materialPath };
            plan.Source = Classify(material, materialPath, out plan.ShaderGuid);
            if (plan.Source == MaterialSource.ShaderPresent) return plan;
            if (plan.Source != MaterialSource.HdrpLit && plan.Source != MaterialSource.HdrpLitTessellation)
            {
                plan.SkipReason = SkipReasonFor(plan.Source, plan.ShaderGuid);
                return plan;
            }

            plan.Converts = true;
            var p = SavedProperties.Read(material);
            PlanSurface(plan, p);
            PlanBaseMaps(plan, p);
            PlanDetail(plan, p);
            PlanProjection(plan, p);
            PlanEmission(plan, p, ev100);
            PlanDoubleSided(plan, p);
            PlanMaterialType(plan, p);
            PlanDisplacement(plan, p);
            PlanDropped(plan, p);
            foreach (var entry in p.Textures.Where(t => t.Value.Missing && UsedTextures.Contains(t.Key)))
                plan.Notes.Add(entry.Key + " references a texture that isn't in the project");

            plan.Floats["_IsConverted"] = 1f;
            return plan;
        }

        //HDRP texture slots the conversion reads, for the missing-texture note
        private static readonly HashSet<string> UsedTextures = new HashSet<string>
        {
            "_BaseColorMap", "_NormalMap", "_MaskMap", "_DetailMap", "_EmissiveColorMap",
            "_HeightMap", "_SpecularColorMap", "_ThicknessMap",
        };

        //Shader choice, alpha clipping, transparency (H5, H6, H7)
        private static void PlanSurface(ConversionPlan plan, SavedProperties p)
        {
            bool transparent = p.Float("_SurfaceType") >= 0.5f;
            plan.Target = transparent ? BetterLitShader.LitAlpha : BetterLitShader.Lit;

            if (transparent)
            {
                int blend = Mathf.RoundToInt(p.Float("_BlendMode"));
                if (blend == 1) plan.Notes.Add("Additive blending becomes alpha blending");
                else if (blend == 4) plan.Notes.Add("Premultiplied blending becomes alpha blending");
            }

            float cutoff = p.Float("_AlphaCutoff");
            bool clip = p.Float("_AlphaCutoffEnable") >= 0.5f && cutoff > 0f;
            if (clip)
            {
                plan.Keywords.Add("_ALPHACUT");
                plan.Floats["_AlphaThreshold"] = cutoff;
                plan.Floats["_Cutoff"] = cutoff;
                if (!transparent)
                {
                    plan.RenderQueue = 2450;
                    plan.RenderType = "TransparentCutout";
                }
            }
            else
            {
                plan.Floats["_AlphaThreshold"] = 0f;
            }

            //Better Lit remaps the albedo alpha with _HeightRemap; HDRP's alpha remap only matters when the alpha is used
            bool alphaUsed = transparent || clip;
            plan.Vectors["_HeightRemap"] = alphaUsed
                ? new Vector4(p.Float("_AlphaRemapMin"), p.Float("_AlphaRemapMax"), 0f, 0f)
                : new Vector4(0f, 1f, 0f, 0f);
        }

        //Albedo, tint, normal, mask, metallic/smoothness, base UV set (H8, §9.3)
        private static void PlanBaseMaps(ConversionPlan plan, SavedProperties p)
        {
            var baseMap = p.Texture("_BaseColorMap");
            plan.Textures["_AlbedoMap"] = baseMap;
            plan.Colors["_Tint"] = p.Color("_BaseColor");

            //Planar and triplanar (4, 5) are set by PlanProjection
            int uvBase = Mathf.RoundToInt(p.Float("_UVBase"));
            if (uvBase == 0 || uvBase == 1) plan.Floats["_UVSource"] = uvBase;
            else if (uvBase == 2 || uvBase == 3)
            {
                plan.Floats["_UVSource"] = 0f;
                plan.Notes.Add("Uses UV" + uvBase + ", which Better Lit doesn't read; it uses UV0");
            }

            var normal = p.Texture("_NormalMap");
            if (p.Float("_NormalMapSpace") >= 0.5f)
            {
                if (p.Texture("_NormalMapOS").Texture != null) plan.Notes.Add("Object-space normal map isn't supported by Better Lit; left out");
            }
            else if (normal.Texture != null)
            {
                plan.Textures["_NormalMap"] = normal;
                plan.Keywords.Add("_NORMALMAP");
            }
            float normalScale = p.Float("_NormalScale");
            plan.Floats["_NormalStrength"] = Mathf.Clamp(normalScale, 0f, 2f);
            if (normalScale > 2f && plan.Keywords.Contains("_NORMALMAP"))
                plan.Notes.Add("Normal strength " + normalScale + " is above Better Lit's maximum of 2; set to 2");

            plan.Floats["_Metallic"] = p.Float("_Metallic");
            plan.Floats["_Smoothness"] = p.Float("_Smoothness");

            var mask = p.Texture("_MaskMap");
            if (mask.Texture != null)
            {
                plan.Textures["_MaskMap"] = mask;
                plan.Keywords.Add("_MASKMAP");
                plan.Vectors["_MetallicRemap"] = new Vector4(p.Float("_MetallicRemapMin"), p.Float("_MetallicRemapMax"), 0f, 0f);
                plan.Vectors["_SmoothnessRemap"] = new Vector4(p.Float("_SmoothnessRemapMin"), p.Float("_SmoothnessRemapMax"), 0f, 0f);
                plan.Vectors["_AORemap"] = new Vector4(p.Float("_AORemapMin"), p.Float("_AORemapMax"), 0f, 0f);
            }
            else
            {
                //Better Lit runs the constants through the remaps when a normal map is on, so they must pass through unchanged (H8)
                plan.Vectors["_MetallicRemap"] = new Vector4(0f, 1f, 0f, 0f);
                plan.Vectors["_SmoothnessRemap"] = new Vector4(0f, 1f, 0f, 0f);
                plan.Vectors["_AORemap"] = new Vector4(0f, 1f, 0f, 0f);
            }
        }

        //Detail map, strengths, UV set, linked tiling (H16)
        private static void PlanDetail(ConversionPlan plan, SavedProperties p)
        {
            var detail = p.Texture("_DetailMap");
            if (detail.Texture == null) return;

            plan.Keywords.Add("_DETAIL");
            plan.Floats["_DetailAlbedoStrength"] = p.Float("_DetailAlbedoScale");
            plan.Floats["_DetailNormalStrength"] = p.Float("_DetailNormalScale");
            plan.Floats["_DetailSmoothnessStrength"] = p.Float("_DetailSmoothnessScale");

            if (p.Float("_LinkDetailsWithBase") >= 0.5f)
            {
                //HDRP applies the base tiling on top of the detail tiling (ComputeLayerTexCoord); Better Lit doesn't, so it's baked in
                var baseMap = p.Texture("_BaseColorMap");
                detail.Offset = Vector2.Scale(detail.Offset, baseMap.Scale) + baseMap.Offset;
                detail.Scale = Vector2.Scale(detail.Scale, baseMap.Scale);
            }
            plan.Textures["_DetailMap"] = detail;

            //With a planar or triplanar base, HDRP projects the detail map the same way; PlanProjection sets that up
            if (Mathf.RoundToInt(p.Float("_UVBase")) >= 4) return;
            int uvDetail = Mathf.RoundToInt(p.Float("_UVDetail"));
            plan.Floats["_DetailUVSource"] = uvDetail == 1 ? 1f : 0f;
            if (uvDetail >= 2) plan.Notes.Add("Detail map uses UV" + uvDetail + ", which Better Lit doesn't read; it uses UV0");
        }

        //Planar and triplanar mapping (H15). HDRP projects world (or object) position × _TexWorldScale and then applies the
        //tiling: planar uses (x, z), triplanar (z, y), (x, z) and (x, y). Better Lit applies its tiling straight to the position,
        //and its Project Y source uses (x, −z), so the world scale goes into the tiling and planar's V is negated
        private static void PlanProjection(ConversionPlan plan, SavedProperties p)
        {
            int uvBase = Mathf.RoundToInt(p.Float("_UVBase"));
            if (uvBase != 4 && uvBase != 5) return;

            bool planar = uvBase == 4;
            float worldScale = p.Float("_TexWorldScale");
            float space = p.Float("_ObjectSpaceUVMapping") >= 0.5f ? 1f : 0f;
            Vector2 projection = planar ? new Vector2(worldScale, -worldScale) : new Vector2(worldScale, worldScale);

            ProjectTiling(plan, "_AlbedoMap", projection);
            plan.Floats["_TriplanarSpace"] = space;
            if (planar) plan.Floats["_UVSource"] = 3f;
            else
            {
                plan.Floats["_UVSource"] = 0f;
                plan.Keywords.Add("_TRIPLANAR");
            }

            if (plan.Textures.ContainsKey("_DetailMap"))
            {
                ProjectTiling(plan, "_DetailMap", projection);
                plan.Floats["_DetailTriplanarSpace"] = space;
                if (planar) plan.Floats["_DetailUVSource"] = 3f;
                else
                {
                    plan.Floats["_DetailUVSource"] = 0f;
                    plan.Keywords.Add("_DETAILTRIPLANAR");
                }
            }

            plan.Notes.Add(planar
                ? "Planar mapping: check the texture's alignment, as the two shaders' projections are set up differently"
                : "Triplanar mapping: check the blend between sides; Better Lit blends differently and shifts two of the three projections");
        }

        private static void ProjectTiling(ConversionPlan plan, string property, Vector2 projection)
        {
            var texture = plan.Textures[property];
            texture.Scale = Vector2.Scale(texture.Scale, projection);
            plan.Textures[property] = texture;
        }

        //Emission (H11, H21)
        private static void PlanEmission(ConversionPlan plan, SavedProperties p, float ev100)
        {
            Color emissive = p.Color("_EmissiveColor");
            var linear = new Color(emissive.r, emissive.g, emissive.b, 1f) * EmissionScale(ev100, p.Float("_EmissiveExposureWeight"));
            linear.a = 1f;

            var map = p.Texture("_EmissiveColorMap");
            if (p.Float("_AlbedoAffectEmissive") >= 0.5f)
            {
                //HDRP multiplies by the base colour; _BaseColor is a plain colour, so the shader sees it as linear
                Color tint = p.Color("_BaseColor").linear;
                linear = new Color(linear.r * tint.r, linear.g * tint.g, linear.b * tint.b, 1f);
                if (p.Texture("_BaseColorMap").Texture != null)
                    plan.Notes.Add("Emission is multiplied by the base colour in HDRP; Better Lit can't multiply it by the albedo texture");
            }

            EmissionForBetterLit(linear, out Color colour, out float multiplier);
            plan.Colors["_EmissionColor"] = colour;
            plan.Floats["_EmissionMultiplier"] = multiplier;
            if (map.Texture != null) plan.Textures["_EmissionMap"] = map;

            if (colour.maxColorComponent <= 0f) return;
            plan.Keywords.Add("_EMISSION");

            if (map.Texture != null)
            {
                int uvEmissive = Mathf.RoundToInt(p.Float("_UVEmissive"));
                int uvBase = Mathf.RoundToInt(p.Float("_UVBase"));
                var baseMap = p.Texture("_BaseColorMap");
                if (uvEmissive != 6 && uvEmissive != uvBase)
                    plan.Notes.Add("Emission map uses a different UV set from the base map; Better Lit uses the base map's");
                else if (uvEmissive != 6 && map.ScaleOffset != baseMap.ScaleOffset)
                    plan.Notes.Add("Emission map tiling (" + Format(map) + ") differs from the base map's (" + Format(baseMap) + "); Better Lit uses the base map's");
            }
        }

        private static string Format(SavedTexture texture)
        {
            string tiling = texture.Scale.x + " x " + texture.Scale.y;
            return texture.Offset == Vector2.zero ? tiling : tiling + ", offset " + texture.Offset.x + ", " + texture.Offset.y;
        }

        //Culling and back-face normals (H18)
        private static void PlanDoubleSided(ConversionPlan plan, SavedProperties p)
        {
            plan.Floats["_CullMode"] = p.Float("_DoubleSidedEnable") >= 0.5f ? 0f : 2f;
            plan.Floats["_DoubleSidedNormalMode"] = p.Float("_DoubleSidedNormalMode");
        }

        //Material types (H12, H13)
        private static void PlanMaterialType(ConversionPlan plan, SavedProperties p)
        {
            switch (Mathf.RoundToInt(p.Float("_MaterialID")))
            {
                case 0: PlanSss(plan, p, "Subsurface Scattering"); break;
                case 2: plan.Notes.Add("Anisotropy has no Better Lit equivalent; converted as Standard"); break;
                case 3: plan.Notes.Add("Iridescence has no Better Lit equivalent; converted as Standard"); break;
                case 4: PlanSpecular(plan, p); break;
                case 5: PlanSss(plan, p, "Translucent"); break;
            }
        }

        //Specular Colour → Better Lit's specular workflow. Its specular map defaults to black, so a material with no map
        //gets the shared white texture (H12)
        private static void PlanSpecular(ConversionPlan plan, SavedProperties p)
        {
            plan.Keywords.Add("_SPECULAR");
            plan.Colors["_SpecularTint"] = p.Color("_SpecularColor");
            var map = p.Texture("_SpecularColorMap");
            var baseMap = p.Texture("_BaseColorMap");
            if (map.Texture != null)
            {
                plan.Textures["_SpecularMap"] = new SavedTexture { Texture = map.Texture, Scale = map.Scale, Offset = map.Offset };
                if (map.ScaleOffset != baseMap.ScaleOffset)
                    plan.Notes.Add("Specular map tiling differs from the base map's; Better Lit uses the base map's");
            }
            else
            {
                plan.Textures["_SpecularMap"] = new SavedTexture { Scale = Vector2.one };
                plan.Packs["_SpecularMap"] = new PackJob { Kind = PackKind.White, OutputPath = WhiteTexturePath };
            }
        }

        //Subsurface Scattering and Translucent → Better Lit's Approximate SSS (H13). HDRP's thickness is x + y × map when it
        //has a thickness map (LitDataIndividualLayer.hlsl), _Thickness otherwise; 1 is thick. Better Lit's value is how much
        //light gets through, so it's 1 − HDRP's
        private static void PlanSss(ConversionPlan plan, SavedProperties p, string type)
        {
            plan.Keywords.Add("_CHEAPSSS");
            plan.Colors["_CheapSSSTint"] = Color.white;
            var map = p.Texture("_ThicknessMap");
            if (map.Texture != null)
            {
                Color remap = p.Color("_ThicknessRemap");
                string source = AssetDatabase.GetAssetPath(map.Texture);
                plan.Keywords.Add("_CHEAPSSSTEXTURE");
                plan.Textures["_CheapSSSTexture"] = new SavedTexture { Scale = Vector2.one };
                plan.Packs["_CheapSSSTexture"] = new PackJob
                {
                    Kind = PackKind.SssThickness,
                    ThicknessPath = source,
                    ThicknessRemap = new Vector2(remap.r, remap.g),
                    OutputPath = Path.GetDirectoryName(source).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(source) + "_SSS.png",
                };
            }
            else
            {
                plan.Floats["_CheapSSSThickness"] = Mathf.Clamp01(1f - p.Float("_Thickness"));
            }
            plan.Notes.Add(type + " becomes Better Lit's Approximate SSS with a white tint (the diffusion profile's colour isn't in the material): tune it by eye");
        }

        //Height and displacement (H9, H10). HDRP only uses the height map for displacement, so it's packed into the albedo's
        //alpha only when a displacement mode is on
        private static void PlanDisplacement(ConversionPlan plan, SavedProperties p)
        {
            int mode = Mathf.RoundToInt(p.Float("_DisplacementMode"));
            var height = p.Texture("_HeightMap");
            if (mode == 0 || height.Texture == null) return;

            if (plan.Target == BetterLitShader.LitAlpha || plan.Keywords.Contains("_ALPHACUT"))
            {
                plan.Notes.Add("Height map dropped: Better Lit keeps height in the albedo's alpha, which this material uses for transparency");
                return;
            }

            var baseMap = p.Texture("_BaseColorMap");
            var rows = new[]
            {
                baseMap.Texture != null ? RowSource.FromTexture(AssetDatabase.GetAssetPath(baseMap.Texture), AlphaChannel.Red) : RowSource.Constant(1f),
                baseMap.Texture != null ? RowSource.FromTexture(AssetDatabase.GetAssetPath(baseMap.Texture), AlphaChannel.Green) : RowSource.Constant(1f),
                baseMap.Texture != null ? RowSource.FromTexture(AssetDatabase.GetAssetPath(baseMap.Texture), AlphaChannel.Blue) : RowSource.Constant(1f),
                RowSource.FromTexture(AssetDatabase.GetAssetPath(height.Texture), AlphaChannel.Red),
            };
            plan.Packs["_AlbedoMap"] = new PackJob
            {
                Kind = PackKind.AlbedoHeight,
                AlbedoPath = rows[0].TexturePath,
                HeightPath = rows[3].TexturePath,
                OutputPath = MaskTextureMaker.DefaultOutputPath(rows, MaskTextureMaker.Info(MaskPreset.AlbedoHeight).Suffix),
            };
            if (height.ScaleOffset != baseMap.ScaleOffset)
                plan.Notes.Add("Height map tiling differs from the base map's; the packed texture uses the base map's");

            if (mode == 2)
            {
                plan.Keywords.Add("_PARALLAX");
                plan.Floats["_ParallaxHeight"] = ParallaxHeight;
                plan.Notes.Add("Pixel displacement becomes Better Lit parallax at height " + ParallaxHeight + ": tune it by eye (HDRP's amplitude is in centimetres, Better Lit's in UV units)");
                return;
            }

            plan.Target = BetterLitShader.LitTessellation;
            if (mode == 1)
            {
                //HDRP's UI saves the vertex displacement in world units as _HeightAmplitude and _HeightCenter
                plan.Floats["_TessellationDisplacement"] = p.Float("_HeightAmplitude");
                plan.Floats["_TessellationOffset"] = p.Float("_HeightCenter");
                plan.Notes.Add("Vertex displacement becomes Better Lit tessellation, which also subdivides the mesh");
            }
            else
            {
                //Tessellation amplitude is in centimetres; Better Lit displaces in object units (metres)
                plan.Floats["_TessellationDisplacement"] = p.Float("_HeightTessAmplitude") / 100f;
                plan.Floats["_TessellationOffset"] = p.Float("_HeightTessCenter");
            }
        }

        //Features with no Better Lit equivalent on Built-in, listed where they were in use (H14)
        private static void PlanDropped(ConversionPlan plan, SavedProperties p)
        {
            if (p.Float("_CoatMask") > 0f || p.Texture("_CoatMaskMap").Texture != null)
                plan.Notes.Add("Clear coat dropped: Better Lit's clear coat only exists in URP and HDRP");
            if (p.Texture("_BentNormalMap").Texture != null || p.Texture("_BentNormalMapOS").Texture != null)
                plan.Notes.Add("Bent normal map dropped");
            if (p.Texture("_SpecularOcclusionMap").Texture != null)
                plan.Notes.Add("Specular occlusion map dropped");
            if (p.Float("_EnableGeometricSpecularAA") >= 0.5f)
                plan.Notes.Add("Geometric specular AA dropped (HDRP-only in Better Lit)");
            if (p.Float("_SurfaceType") >= 0.5f && p.Float("_RefractionModel") >= 0.5f)
                plan.Notes.Add("Refraction dropped");
            if (p.Float("_DistortionEnable") >= 0.5f)
                plan.Notes.Add("Distortion dropped");
        }

        //Writes the packed textures, then every converting plan's material in one Undo step (§9.4 step 5). A material whose
        //packed texture can't be written (cancelled, or a source that can't be read) is skipped, with the reason in SkipReason.
        //existing decides what happens when a packed texture's file already exists (D12); the shared white texture is reused
        public static int Apply(IList<ConversionPlan> plans, ExistingOutput existing = ExistingOutput.Ask)
        {
            WritePacks(plans.Where(p => p.Converts && p.Packs.Count > 0).ToList(), existing);

            var converting = plans.Where(p => p.Converts).ToList();
            if (converting.Count == 0) return 0;

            var shaders = new Dictionary<BetterLitShader, Shader>();
            foreach (var target in converting.Select(p => p.Target).Distinct())
            {
                var shader = LoadShader(GuidOf(target));
                if (shader == null) throw new InvalidOperationException("Better Lit's " + target + " shader isn't in the project");
                shaders[target] = shader;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RecordObjects(converting.Select(p => (Object)p.Material).ToArray(), "HDRP to Better Lit");

            foreach (var plan in converting) ApplyOne(plan, shaders[plan.Target]);

            Undo.SetCurrentGroupName("HDRP to Better Lit");
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return converting.Count;
        }

        private static void WritePacks(List<ConversionPlan> plans, ExistingOutput existing)
        {
            //Key → written path, or null when it couldn't be written; failures keep their reason
            var written = new Dictionary<string, string>();
            var failures = new Dictionary<string, string>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var plan in plans)
            {
                foreach (var entry in plan.Packs)
                {
                    var job = entry.Value;
                    if (!written.ContainsKey(job.Key))
                    {
                        try
                        {
                            //Two different packs that would share a file name within one batch: the later one gets a new name
                            string outputPath = job.OutputPath;
                            if (job.Kind != PackKind.White && claimed.Contains(outputPath)) outputPath = AssetDatabase.GenerateUniqueAssetPath(outputPath);
                            string path = WritePack(job, outputPath, existing);
                            written[job.Key] = path;
                            if (path == null) failures[job.Key] = "writing " + outputPath + " was cancelled";
                            else claimed.Add(path);
                        }
                        catch (Exception e)
                        {
                            written[job.Key] = null;
                            failures[job.Key] = "couldn't write " + job.OutputPath + ": " + e.Message;
                        }
                    }

                    string result = written[job.Key];
                    if (result == null)
                    {
                        plan.Converts = false;
                        plan.SkipReason = "Packed texture not written (" + failures[job.Key] + ")";
                        break;
                    }
                    var texture = plan.Textures[entry.Key];
                    texture.Texture = AssetDatabase.LoadAssetAtPath<Texture>(result);
                    plan.Textures[entry.Key] = texture;
                }
            }
        }

        //Writes one packed texture and returns its path, or null if the user cancelled
        public static string WritePack(PackJob job, string outputPath, ExistingOutput existing)
        {
            switch (job.Kind)
            {
                case PackKind.AlbedoHeight:
                {
                    RowSource Albedo(AlphaChannel channel) => job.AlbedoPath != null ? RowSource.FromTexture(job.AlbedoPath, channel) : RowSource.Constant(1f);
                    var rows = new[] { Albedo(AlphaChannel.Red), Albedo(AlphaChannel.Green), Albedo(AlphaChannel.Blue), RowSource.FromTexture(job.HeightPath, AlphaChannel.Red) };
                    return MaskTextureMaker.Make(rows, MaskTextureMaker.Info(MaskPreset.AlbedoHeight).Srgb, false, 1, 1, outputPath, existing);
                }
                case PackKind.SssThickness:
                {
                    outputPath = TextureWriter.ResolveOutputPath(outputPath, existing);
                    if (outputPath == null) return null;
                    var image = SourcePixels.Read(job.ThicknessPath);
                    var pixels = new Color[image.Pixels.Length];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        float thickness = job.ThicknessRemap.x + job.ThicknessRemap.y * image.Pixels[i].r;
                        pixels[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - thickness));
                    }
                    var encoding = image.BitDepth > 8 ? OutputEncoding.Png16 : OutputEncoding.Png8;
                    TextureWriter.Write(outputPath, image.Width, image.Height, pixels, encoding, true, null,
                        importer => MaskTextureMaker.ApplyImportSettings(importer, false, false, Mathf.Max(image.Width, image.Height)));
                    return outputPath;
                }
                default:
                {
                    if (AssetDatabase.LoadAssetAtPath<Texture>(outputPath) != null) return outputPath;
                    string folder = Path.GetDirectoryName(outputPath).Replace('\\', '/');
                    if (!AssetDatabase.IsValidFolder(folder))
                        AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
                    var white = Enumerable.Repeat(Color.white, 16).ToArray();
                    TextureWriter.Write(outputPath, 4, 4, white, OutputEncoding.Png8, false, null,
                        importer => MaskTextureMaker.ApplyImportSettings(importer, true, false, 4));
                    return outputPath;
                }
            }
        }

        private static void ApplyOne(ConversionPlan plan, Shader shader)
        {
            var material = plan.Material;
            material.shader = shader;
            material.shaderKeywords = plan.Keywords.ToArray();

            foreach (var entry in plan.Textures)
            {
                material.SetTexture(entry.Key, entry.Value.Texture);
                material.SetTextureScale(entry.Key, entry.Value.Scale);
                material.SetTextureOffset(entry.Key, entry.Value.Offset);
            }
            foreach (var entry in plan.Floats) material.SetFloat(entry.Key, entry.Value);
            foreach (var entry in plan.Vectors) material.SetVector(entry.Key, entry.Value);
            foreach (var entry in plan.Colors) material.SetColor(entry.Key, entry.Value);

            material.renderQueue = plan.RenderQueue;
            material.SetOverrideTag("RenderType", plan.RenderType);
            EditorUtility.SetDirty(material);
        }

        //One line per material for the Console log (§9.4 step 6)
        public static string Summary(ConversionPlan plan)
        {
            if (!plan.Converts) return plan.MaterialPath + ": skipped — " + plan.SkipReason;
            string line = plan.MaterialPath + ": " + Describe(plan.Source) + " → Better Lit " + plan.Target;
            return plan.Notes.Count == 0 ? line : line + "\n    " + string.Join("\n    ", plan.Notes);
        }

        //The window's "Becomes" column (§9.5)
        public static string Becomes(ConversionPlan plan)
        {
            if (!plan.Converts) return "(skipped)";
            string name;
            switch (plan.Target)
            {
                case BetterLitShader.LitAlpha: name = "Lit Alpha"; break;
                case BetterLitShader.LitTessellation: name = "Lit Tessellation"; break;
                case BetterLitShader.LitTessellationAlpha: name = "Lit Tessellation Alpha"; break;
                default: name = "Lit"; break;
            }
            return plan.Keywords.Contains("_ALPHACUT") ? name + " (alpha clip)" : name;
        }

        //Packed texture files a batch will write: one per distinct job, the shared white texture only if it doesn't exist yet
        public static int PackedTextureCount(IEnumerable<ConversionPlan> plans)
        {
            return plans.Where(p => p.Converts).SelectMany(p => p.Packs.Values)
                .Where(j => j.Kind != PackKind.White || AssetDatabase.LoadAssetAtPath<Texture>(j.OutputPath) == null)
                .Select(j => j.Key).Distinct().Count();
        }

        //"13 to convert · 8 skipped · 2 packed textures will be written"
        public static string Totals(IList<ConversionPlan> plans)
        {
            var listed = plans.Where(p => p.Listed).ToList();
            int converting = listed.Count(p => p.Converts);
            int packed = PackedTextureCount(listed);
            string text = converting + " to convert · " + (listed.Count - converting) + " skipped";
            return packed == 0 ? text : text + " · " + packed + " packed texture" + (packed == 1 ? "" : "s") + " will be written";
        }

        //The whole batch's log: a heading, then every listed material's Summary
        public static string Log(IList<ConversionPlan> plans, float ev100)
        {
            var listed = plans.Where(p => p.Listed).ToList();
            return "HDRP to Better Lit (EV100 " + ev100.ToString("0.##") + "): " + listed.Count(p => p.Converts) + " converted, " +
                   listed.Count(p => !p.Converts) + " skipped\n" + string.Join("\n", listed.Select(Summary));
        }
    }
}
