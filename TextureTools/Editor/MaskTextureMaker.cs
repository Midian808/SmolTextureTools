using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.TextureTools
{
    public enum MaskPreset
    {
        MaskMap,
        PackedNormal,
        PackedNormalFastMetal,
        DetailMap,
        AlbedoHeight,
        Custom,
    }

    //What an output channel holds, for name matching. A texture whose name matches a role fills the row with that role
    public enum ChannelRole
    {
        None,
        Metallic,
        Occlusion,
        DetailMask,
        Smoothness,
        NormalX,
        NormalY,
        DetailAlbedo,
        ColourR,
        ColourG,
        ColourB,
        Height,
    }

    //One output channel of a preset: its label, what it holds, and the constant that leaves that part of the material unchanged
    public sealed class ChannelSlot
    {
        public readonly string Label;
        public readonly ChannelRole Role;
        public readonly float Default;
        public readonly string DefaultNote;

        public ChannelSlot(string label, ChannelRole role, float defaultValue, string defaultNote)
        {
            Label = label;
            Role = role;
            Default = defaultValue;
            DefaultNote = defaultNote;
        }
    }

    public sealed class PresetInfo
    {
        public readonly string Name;
        public readonly ChannelSlot[] Slots;
        public readonly bool Srgb;
        public readonly string Suffix;
        //Albedo + Height and Custom: the alpha can be transparency instead of data (M8)
        public readonly bool OffersTransparency;

        public PresetInfo(string name, ChannelSlot[] slots, bool srgb, string suffix, bool offersTransparency)
        {
            Name = name;
            Slots = slots;
            Srgb = srgb;
            Suffix = suffix;
            OffersTransparency = offersTransparency;
        }
    }

    //Where one output channel comes from: a channel of a texture (optionally inverted), or a constant
    public sealed class RowSource
    {
        public string TexturePath;
        public AlphaChannel Channel;
        public bool Invert;
        public float Value;

        public bool UsesTexture => !string.IsNullOrEmpty(TexturePath);

        public static RowSource Constant(float value)
        {
            return new RowSource { Value = value };
        }

        public static RowSource FromTexture(string path, AlphaChannel channel, bool invert = false)
        {
            return new RowSource { TexturePath = path, Channel = channel, Invert = invert };
        }
    }

    //A name suffix's meaning: which role it fills, from which channel of the texture, and whether to invert it
    public struct NameMatch
    {
        public ChannelRole Role;
        public AlphaChannel Channel;
        public bool Invert;

        public NameMatch(ChannelRole role, AlphaChannel channel, bool invert = false)
        {
            Role = role;
            Channel = channel;
            Invert = invert;
        }
    }

    //Builds a packed texture (Better Lit / HDRP mask map and Better Lit's other packed layouts) from
    //channels of input textures and constant values
    public static class MaskTextureMaker
    {
        public static readonly string[] ChannelNames = { "R", "G", "B", "A" };

        public static PresetInfo Info(MaskPreset preset)
        {
            switch (preset)
            {
                case MaskPreset.PackedNormal:
                    return new PresetInfo("Packed Normal (Better Lit Fastest)", new[]
                    {
                        new ChannelSlot("Smoothness", ChannelRole.Smoothness, 0.5f, "medium"),
                        new ChannelSlot("Normal Y", ChannelRole.NormalY, 0.5f, "flat"),
                        new ChannelSlot("Occlusion", ChannelRole.Occlusion, 1f, "no occlusion"),
                        new ChannelSlot("Normal X", ChannelRole.NormalX, 0.5f, "flat"),
                    }, false, "_PackedNormal", false);
                case MaskPreset.PackedNormalFastMetal:
                    return new PresetInfo("Packed Normal, Fast Metal (Better Lit)", new[]
                    {
                        new ChannelSlot("Smoothness", ChannelRole.Smoothness, 0.5f, "medium"),
                        new ChannelSlot("Normal Y", ChannelRole.NormalY, 0.5f, "flat"),
                        new ChannelSlot("Metallic", ChannelRole.Metallic, 0f, "no metal"),
                        new ChannelSlot("Normal X", ChannelRole.NormalX, 0.5f, "flat"),
                    }, false, "_PackedNormal", false);
                case MaskPreset.DetailMap:
                    return new PresetInfo("Detail Map (Better Lit / HDRP)", new[]
                    {
                        new ChannelSlot("Albedo brightness", ChannelRole.DetailAlbedo, 0.5f, "no change"),
                        new ChannelSlot("Normal Y", ChannelRole.NormalY, 0.5f, "flat"),
                        new ChannelSlot("Smoothness", ChannelRole.Smoothness, 0.5f, "no change"),
                        new ChannelSlot("Normal X", ChannelRole.NormalX, 0.5f, "flat"),
                    }, false, "_Detail", false);
                case MaskPreset.AlbedoHeight:
                    return new PresetInfo("Albedo + Height", new[]
                    {
                        new ChannelSlot("Colour red", ChannelRole.ColourR, 1f, "white"),
                        new ChannelSlot("Colour green", ChannelRole.ColourG, 1f, "white"),
                        new ChannelSlot("Colour blue", ChannelRole.ColourB, 1f, "white"),
                        new ChannelSlot("Height (or alpha)", ChannelRole.Height, 1f, "flat / opaque"),
                    }, true, "_AlbedoHeight", true);
                case MaskPreset.Custom:
                    return new PresetInfo("Custom", new[]
                    {
                        new ChannelSlot("Red", ChannelRole.None, 0f, ""),
                        new ChannelSlot("Green", ChannelRole.None, 0f, ""),
                        new ChannelSlot("Blue", ChannelRole.None, 0f, ""),
                        new ChannelSlot("Alpha", ChannelRole.None, 1f, ""),
                    }, false, "_Packed", true);
                default:
                    return new PresetInfo("Mask Map (Better Lit / HDRP)", new[]
                    {
                        new ChannelSlot("Metallic", ChannelRole.Metallic, 0f, "no metal"),
                        new ChannelSlot("Occlusion", ChannelRole.Occlusion, 1f, "no occlusion"),
                        new ChannelSlot("Detail Mask", ChannelRole.DetailMask, 1f, "detail everywhere"),
                        new ChannelSlot("Smoothness", ChannelRole.Smoothness, 0.5f, "medium"),
                    }, false, "_MaskMap", false);
            }
        }

        //Fills one output channel (0-3) from a channel of image, resized to the output if needed
        public static void FillChannel(Color[] output, int width, int height, int outputChannel, PixelImage image, AlphaChannel channel, bool invert)
        {
            float[] values = PackAlpha.ExtractAlpha(image.Pixels, channel, invert);
            values = PackAlpha.Resample(values, image.Width, image.Height, width, height);
            for (int i = 0; i < output.Length; i++) output[i][outputChannel] = values[i];
        }

        public static void FillChannel(Color[] output, int outputChannel, float value)
        {
            for (int i = 0; i < output.Length; i++) output[i][outputChannel] = value;
        }

        //The output size: the largest texture input, or the given size when every row is a constant
        public static Vector2Int OutputSize(RowSource[] rows, int constantWidth, int constantHeight)
        {
            var size = new Vector2Int(0, 0);
            foreach (var row in rows.Where(r => r.UsesTexture))
            {
                SourcePixels.Describe(row.TexturePath, out int width, out int height, out _);
                if ((long)width * height > (long)size.x * size.y) size = new Vector2Int(width, height);
            }
            return size.x > 0 ? size : new Vector2Int(Mathf.Max(1, constantWidth), Mathf.Max(1, constantHeight));
        }

        //16-bit when any texture-sourced row comes from a 16-bit or floating-point source (M7)
        public static int OutputBitDepth(RowSource[] rows)
        {
            foreach (var row in rows.Where(r => r.UsesTexture))
            {
                SourcePixels.Describe(row.TexturePath, out _, out _, out int bitDepth);
                if (bitDepth > 8) return 16;
            }
            return 8;
        }

        //Reads each source texture once, one at a time, filling every row that uses it, so only one full-size source is in memory at a time
        public static Color[] Build(RowSource[] rows, int width, int height)
        {
            if (rows.Length != 4) throw new ArgumentException("Expected 4 rows (R, G, B, A).");
            var output = new Color[width * height];
            for (int c = 0; c < 4; c++)
            {
                if (!rows[c].UsesTexture) FillChannel(output, c, rows[c].Value);
            }
            foreach (string path in rows.Where(r => r.UsesTexture).Select(r => r.TexturePath).Distinct())
            {
                var image = SourcePixels.Read(path);
                for (int c = 0; c < 4; c++)
                {
                    if (rows[c].TexturePath == path) FillChannel(output, width, height, c, image, rows[c].Channel, rows[c].Invert);
                }
            }
            return output;
        }

        //Writes the packed texture and returns its path, or null if the user cancelled
        public static string Make(RowSource[] rows, bool srgb, bool alphaIsTransparency, int constantWidth, int constantHeight,
            string outputPath, ExistingOutput existing)
        {
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("Choose where to save the texture.");
            if (!AssetPaths.IsUnderAssets(outputPath)) throw new ArgumentException("The output must be inside the Assets folder: " + outputPath);

            var size = OutputSize(rows, constantWidth, constantHeight);
            int bitDepth = OutputBitDepth(rows);

            outputPath = TextureWriter.ResolveOutputPath(outputPath, existing);
            if (outputPath == null) return null;
            if (rows.Any(r => r.TexturePath == outputPath))
                throw new InvalidOperationException("The output can't replace one of its own sources: " + outputPath);

            Color[] pixels = Build(rows, size.x, size.y);
            var encoding = bitDepth > 8 ? OutputEncoding.Png16 : OutputEncoding.Png8;
            TextureWriter.Write(outputPath, size.x, size.y, pixels, encoding, true, null,
                importer => ApplyImportSettings(importer, srgb, alphaIsTransparency, Mathf.Max(size.x, size.y)));
            Debug.Log("Mask Texture Maker: " + outputPath + "  " + Describe(rows));
            return outputPath;
        }

        //M8: a plain texture (packed normals must not be re-encoded as a Normal map), colour space per preset,
        //alpha as data unless it's transparency, and a Max Size that keeps the full resolution
        public static void ApplyImportSettings(TextureImporter importer, bool srgb, bool alphaIsTransparency, int largestSide)
        {
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = srgb;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = alphaIsTransparency;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = MaxSizeFor(largestSide);
        }

        //The smallest Max Size setting (a power of two from 32 to 16384) that doesn't shrink the texture
        public static int MaxSizeFor(int largestSide)
        {
            int size = 32;
            while (size < largestSide && size < 16384) size *= 2;
            return size;
        }

        //The Result line: "R <- metal.png (R)   B = 1.00   A <- 1 - rough.png (R)"
        public static string Describe(RowSource[] rows)
        {
            var parts = new List<string>();
            for (int c = 0; c < rows.Length; c++)
            {
                var row = rows[c];
                if (!row.UsesTexture)
                {
                    parts.Add(ChannelNames[c] + " = " + row.Value.ToString("0.00"));
                    continue;
                }
                string channel = row.Channel == AlphaChannel.Luminance ? "luminance" : ChannelNames[(int)row.Channel - 1];
                parts.Add(ChannelNames[c] + " <- " + (row.Invert ? "1 - " : "") + Path.GetFileName(row.TexturePath) + " (" + channel + ")");
            }
            return string.Join("   ", parts);
        }

        //<folder of the first texture>/<its name without the matched suffix><preset suffix>.png, or null with no texture inputs
        public static string DefaultOutputPath(RowSource[] rows, string presetSuffix)
        {
            var first = rows.FirstOrDefault(r => r.UsesTexture);
            if (first == null) return null;
            string folder = Path.GetDirectoryName(first.TexturePath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(first.TexturePath);
            if (TryMatchName(name, out _, out string baseName) && baseName.Length > 0) name = baseName;
            return folder + "/" + name + presetSuffix + ".png";
        }

        private static readonly char[] NameSeparators = { '_', '-', '.', ' ' };

        private static readonly Dictionary<string, NameMatch[]> Suffixes = BuildSuffixes();

        private static Dictionary<string, NameMatch[]> BuildSuffixes()
        {
            var table = new Dictionary<string, NameMatch[]>(StringComparer.OrdinalIgnoreCase);
            void Add(NameMatch[] matches, params string[] names)
            {
                foreach (string name in names) table[name] = matches;
            }

            Add(new[] { new NameMatch(ChannelRole.Metallic, AlphaChannel.Red) }, "metallic", "metalness", "metal", "m");
            Add(new[] { new NameMatch(ChannelRole.Occlusion, AlphaChannel.Red) }, "ao", "occlusion", "ambientocclusion");
            Add(new[] { new NameMatch(ChannelRole.Smoothness, AlphaChannel.Red) }, "smoothness", "smooth", "gloss", "glossiness");
            Add(new[] { new NameMatch(ChannelRole.Smoothness, AlphaChannel.Red, true) }, "roughness", "rough", "r");
            Add(new[] { new NameMatch(ChannelRole.Metallic, AlphaChannel.Red), new NameMatch(ChannelRole.Smoothness, AlphaChannel.Alpha) },
                "metallicsmoothness", "metallicgloss");
            Add(new[] { new NameMatch(ChannelRole.DetailMask, AlphaChannel.Red) }, "detailmask", "detail");
            Add(new[] { new NameMatch(ChannelRole.Height, AlphaChannel.Red) }, "height", "displacement", "disp", "h");
            Add(Normal(false), "normal", "nrm", "n", "normalgl");
            Add(Normal(true), "normaldx");
            Add(new[]
            {
                new NameMatch(ChannelRole.ColourR, AlphaChannel.Red),
                new NameMatch(ChannelRole.ColourG, AlphaChannel.Green),
                new NameMatch(ChannelRole.ColourB, AlphaChannel.Blue),
                new NameMatch(ChannelRole.DetailAlbedo, AlphaChannel.Luminance),
            }, "albedo", "basecolor", "basecolour", "diffuse", "color", "colour", "col", "d", "bc");
            return table;
        }

        //Normal X from red, Normal Y from green; a DirectX map's Y is inverted to make it OpenGL
        private static NameMatch[] Normal(bool directX)
        {
            return new[] { new NameMatch(ChannelRole.NormalX, AlphaChannel.Red), new NameMatch(ChannelRole.NormalY, AlphaChannel.Green, directX) };
        }

        //Matches the name's whole last part (or last two parts for "normal"/"nrm" + "gl"/"opengl"/"dx"/"directx") against §8.3's table.
        //baseName is the name with the matched parts removed
        public static bool TryMatchName(string fileName, out NameMatch[] matches, out string baseName)
        {
            matches = null;
            baseName = fileName;
            string[] parts = fileName.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            int used = 0;
            if (parts.Length >= 2)
            {
                string normal = parts[parts.Length - 2].ToLowerInvariant();
                string convention = parts[parts.Length - 1].ToLowerInvariant();
                if (normal == "normal" || normal == "nrm")
                {
                    if (convention == "gl" || convention == "opengl") { matches = Normal(false); used = 2; }
                    else if (convention == "dx" || convention == "directx") { matches = Normal(true); used = 2; }
                }
            }
            if (matches == null && Suffixes.TryGetValue(parts[parts.Length - 1], out var found))
            {
                matches = found;
                used = 1;
            }
            if (matches == null) return false;

            baseName = StripLastParts(fileName, used);
            return true;
        }

        //The name up to (not including) the separator before its last count parts
        private static string StripLastParts(string fileName, int count)
        {
            for (int i = 0; i < count; i++)
            {
                string trimmed = fileName.TrimEnd(NameSeparators);
                int separator = trimmed.LastIndexOfAny(NameSeparators);
                if (separator < 0) return "";
                fileName = trimmed.Substring(0, separator);
            }
            return fileName.TrimEnd(NameSeparators);
        }

        //Assigns textures to the preset's rows by name. Rows with no match keep their current source (M4).
        //notes lists what went where, and what didn't match
        public static RowSource[] FillFromNames(MaskPreset preset, RowSource[] current, IEnumerable<string> texturePaths, out List<string> notes)
        {
            var info = Info(preset);
            var rows = current.Select(r => new RowSource { TexturePath = r.TexturePath, Channel = r.Channel, Invert = r.Invert, Value = r.Value }).ToArray();
            var filled = new bool[4];
            notes = new List<string>();

            foreach (string path in texturePaths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!TryMatchName(name, out var matches, out _))
                {
                    notes.Add(Path.GetFileName(path) + ": name not recognised");
                    continue;
                }

                bool usedHere = false;
                foreach (var match in matches)
                {
                    int slot = Array.FindIndex(info.Slots, s => s.Role == match.Role);
                    if (slot < 0) continue;
                    if (filled[slot])
                    {
                        notes.Add(Path.GetFileName(path) + ": " + info.Slots[slot].Label + " already filled from " + Path.GetFileName(rows[slot].TexturePath));
                        continue;
                    }
                    rows[slot] = RowSource.FromTexture(path, match.Channel, match.Invert);
                    filled[slot] = true;
                    usedHere = true;
                }
                if (!usedHere && !notes.Any(n => n.StartsWith(Path.GetFileName(path) + ":", StringComparison.Ordinal)))
                    notes.Add(Path.GetFileName(path) + ": not used by the " + info.Name + " preset");
            }
            return rows;
        }
    }
}
