# Texture Tools Design — Normal Green Flip, Pack Alpha, Mask Texture Maker and HDRP to Better Lit

A design for four Editor-only texture and material tools:
- **Normal Green Flip** — converts a normal map between the OpenGL and DirectX conventions by inverting its green channel.
- **Pack Alpha** — merges a colour (albedo/diffuse) map and a separate transparency map into one RGBA texture, so the Standard shader can use the transparency. This covers a gap in the DAZ Bridge (§4).
- **Mask Texture Maker** — builds a packed texture (Better Lit Shader / HDRP mask map and Better Lit's other packed layouts) from input textures and constant values (§8).
- **HDRP to Better Lit** — batch-converts materials made for HDRP's Lit shader into Better Lit Shader materials for the Built-in pipeline (§9).

**Status:** Normal Green Flip and Pack Alpha are built, and the real-asset and portability checks (§7, Phase 4) are still to do. Mask Texture Maker is built (§8, decisions M1–M9). Its real-asset check (§8.6, M-3) is still to do. HDRP to Better Lit (§9, decisions H1–H21, all settled): Built and checked: all four phases are done, its 33 tests pass, and the real-asset check on this project's HDRP Lit materials passed.

---

## 1. Decisions

| # | Decision |
|---|---|
| D1 | Both tools are **Editor-only** and **portable**: one self-contained folder, with its own assemblies, that can be copied into any Unity project and works there unchanged (D15). Nothing ships in a build. |
| D2 | **Sources are never modified.** Each tool writes a **new file next to its source** and leaves the source texture and its import settings untouched. |
| D3 | **Pixels are read from the source file, not from the imported texture.** The imported texture may be compressed, downsized by Max Size, sRGB-decoded, or (for normal maps) stored swizzled as DXT5nm, so reading it would bake those losses into the output. Instead the tool copies the source file into a temporary folder, imports the copy raw (Default type, sRGB off, PNG gamma ignored, no mips, no NPOT scaling, Max Size 16384, uncompressed: RGBA32 for 8-bit sources, RGBAFloat for 16-bit and floating-point ones, so nothing is rounded), reads the pixels, and deletes the copy. Import settings for the temporary copy and for each output file are applied by an `AssetPostprocessor` as the file imports, so each is imported once. This works for every format Unity's TextureImporter handles (PNG, JPG, TGA, PSD, TIF, EXR, HDR, BMP, GIF, PICT) and keeps values exact. **DDS** goes through a different importer (`IHVImageFormatImporter`) and keeps the data stored in the file, usually block-compressed (DXT1/DXT5/BC5/BC7…): the temporary copy is imported readable and ignoring the quality settings' mipmap limit, and mip 0 is decoded on the CPU. The bit depth and alpha come from the stored format. The pixels are exactly the texture Unity shows for the DDS, so the PNG made from them is a drop-in replacement. That includes orientation: DDS files store the top row first, and Unity turns DXT1/DXT5/BC5 files the right way up as it imports them, but not BC7, which Unity shows upside down (and so does the PNG). |
| D4 | **No colour-space conversion anywhere.** Both tools move raw stored values: colour channels are copied as stored, the alpha comes from the transparency map's stored values, and the green flip is done on stored values. |
| D5 | **Output import settings are copied from the source** (texture type, wrap, filter, aniso, Max Size, compression, mip settings and every platform override), then the tool changes only what it must (D9 for Pack Alpha). |
| D6 | **Normal Green Flip is one operation, G′ = 1 − G**, which converts in either direction. The window asks which convention you want the result in — **OpenGL (Unity) selected by default** — only to name the file (`_GL` / `_DX`). **Unity uses the OpenGL convention (green = +Y, "up")**, so a map going *into* Unity should end up OpenGL; DirectX maps (Unreal, Substance's default export, many store assets) are the ones that need flipping. |
| D7 | **Bit depth is kept.** Normal Green Flip writes a 16-bit PNG when the source is 16-bit (PNG, PSD or TIFF header says so), EXR when the source is EXR/HDR, otherwise an 8-bit PNG. JPG sources are written as PNG (no second lossy pass). Pack Alpha writes an 8-bit RGBA PNG, or 16-bit if the colour source is deeper than 8-bit. Red, blue and (for normal maps) alpha are copied unchanged. |
| D8 | **Pack Alpha's output takes the colour map's resolution.** If the transparency map is a different size (DAZ transparency maps are often smaller), it is resampled bilinearly to fit. Aspect-ratio mismatches are allowed, with a warning, since UVs are shared. |
| D9 | **Pack Alpha's import settings:** copied from the colour map (D5), then *Alpha Source = Input Texture Alpha*, *Alpha Is Transparency = on* (spreads edge colour into transparent texels so cutout edges don't get dark fringes), and *Mip Maps Preserve Coverage = on* with *Alpha Cutoff Value* set to the window's **Cutoff** value (0.5 default) — this stops hair and lashes thinning out at a distance as the mips shrink. |
| D10 | **The alpha comes from one chosen channel of the transparency map**: Luminance (default), R, G, B or A, with an **Invert** toggle (off by default — DAZ maps are white = opaque, black = see-through, which is already Unity's convention). If the colour map already has an alpha channel, it is replaced and the window says so. |
| D11 | **Batch from the Project window.** Select one or more textures → right-click → **Texture Tools → Flip Normal Green**. Pack Alpha works on one pair at a time (its two inputs can't be inferred safely from a selection), opened from **Tools → Texture Tools → Pack Alpha**, or the same context menu with the selected texture pre-filled as the colour map. |
| D12 | **Existing output files are never silently overwritten.** If `<name>_GL.png` (etc.) already exists the tool asks: Replace (keeps the file's GUID, so materials already pointing at it update) / Save as new name / Cancel. |
| D13 | **Pack Alpha makes the texture only; it never touches materials.** The DAZ materials get replaced with project materials anyway, so setting up Standard's Rendering Mode would be thrown away. |
| D14 | **Both Pack Alpha inputs are picked by hand.** Finding the transparency map automatically from a figure's `.dtu` file (it lists each material's "Cutout Opacity" texture) is left out of the first build; it is a separate convenience feature to add if packing many materials per figure proves tedious. |
| D15 | **Portability rules.** (a) The code references only `UnityEngine` and `UnityEditor` — no project code, no DAZ Bridge, no packages (so no `Unity.Mathematics`; pixels are handled as `Color[]`). (b) Nothing depends on where the folder sits: no hard-coded paths; the temporary import folder (D3) is created at `Assets/__TextureToolsTemp`, deleted after every read, and also deleted on Editor load if a crash left it behind. (c) Works in any render pipeline (Built-in, URP, HDRP) because it never touches shaders or materials (D13). (d) The test assembly has the define constraint `UNITY_INCLUDE_TESTS`, so in a project without the Test Framework package it is skipped instead of breaking compilation; tests generate their own images in code (no fixture files). (e) Assembly names and the root namespace carry the prefix **`Midian.TextureTools`** (assemblies `Midian.TextureTools.Editor` / `Midian.TextureTools.Tests`), so they can't clash with another asset's. (f) Uses no Unity API newer than 2021.3 LTS. (g) The folder carries its own `README.md` (what each tool does, where the menus are, the OpenGL/DirectX note), so a copy doesn't depend on this project's `docs/`. |

---

## 2. Files

In this project the folder lives at `Assets/_MyFiles/Tools/TextureTools/`; to use it elsewhere, copy the whole folder (with its `.meta` files) anywhere under that project's `Assets`.

```
TextureTools/
  README.md                         usage, menus, OpenGL/DirectX note (D15g)
  Editor/
    Midian.TextureTools.Editor.asmdef   Editor-only, rootNamespace Midian.TextureTools; references nothing
    SourcePixels.cs                 D3 temp-copy raw read; header bit-depth sniff (PNG IHDR, PSD, TIFF BitsPerSample / SampleFormat)
    ImportHooks.cs                  AssetPostprocessor: raw settings for temp copies, copied settings for outputs
    Png16Encoder.cs                 16-bit PNG writer (Unity's EncodeToPNG writes 8 bits per channel even from RGBA64)
    TextureWriter.cs                encode PNG 8/16-bit or EXR, write, import, copy importer settings (D5), D12 overwrite choice
    Previews.cs                     small preview textures for the windows
    NormalGreenFlip.cs              pure pixel op, batch menu item, NormalGreenFlipWindow (target convention, green before/after)
    PackAlpha.cs                    pure pixel op (channel pick, invert, bilinear resample, pack), D9 settings, PackAlphaWindow (§5.2)
  Tests/
    Midian.TextureTools.Tests.asmdef    EditMode, define constraint UNITY_INCLUDE_TESTS (D15d)
    TestAssets.cs                   generates test images in a temporary Assets folder
    Png16EncoderTests.cs            decodes the 16-bit writer's output independently of Unity
    NormalGreenFlipTests.cs
    PackAlphaTests.cs
    SourcePixelsTests.cs
```

The pixel operations are pure functions over `Color[]` so they can be unit-tested; the tests in §7 also run the **real path** — a real PNG/JPG/TGA file written into a test folder under `Assets`, imported by Unity, run through the menu command's code path, and the output file read back.

---

## 3. Normal Green Flip

### 3.1 Using it
- **Batch:** select normal maps in the Project window → right-click → **Texture Tools → Flip Normal Green**. A small dialog asks for the target convention (OpenGL (Unity), the default / DirectX), then writes `<name>_GL.png` or `<name>_DX.png` next to each source and selects the results.
- **Window:** **Tools → Texture Tools → Normal Green Flip** — a texture field, the target-convention choice, a before/after preview of the green channel, and a **Convert** button.

### 3.2 What it does
1. Read raw pixels (D3).
2. For every texel: `G = 1 − G`. R, B, A unchanged.
3. Write the file (D7), import it, copy importer settings from the source (D5) — so it comes out as a Normal Map texture with the same Max Size and compression.
4. Log one line per file: source → output.

### 3.3 Checks
- A source whose importer isn't *Normal map* type gets a warning (still converts — some packs ship normal maps imported as Default).
- A source whose green channel is constant (e.g. a flat or packed map) gets a warning.

---

## 4. The DAZ Bridge gap that Pack Alpha covers

Measured on this project (`Assets/Daz3D/Scripts/Editor/DTUConverter.cs`, this project's copy, which starts with `#define USING_BUILTIN`):

- DAZ stores transparency as a **separate "Cutout Opacity" map** (e.g. `aprisel_trans.jpg` for the BarbariAnne hair), usually a JPG, so it has no alpha of its own.
- On Built-in RP the bridge picks the **Standard** shader for every material (`DTU_Constants`, the `USING_BUILTIN` block). Standard only reads transparency from **`_MainTex`'s alpha** — it has no separate opacity slot.
- The bridge hands the transparency map to `_AlphaMap` (a property of its own uDTU shaders, which Standard doesn't have), and in the Standard-shader hair branch (`#if USING_STANDARD_SHADER` inside the `isHair` block of `ConvertToUnityIrayUber`) it sets `_MainTex` to the cutout map and then immediately overwrites `_MainTex` with the diffuse map. Either way **the transparency never reaches the material**.
- Result on disk: the four BarbariAnne hair materials (`hair1`, `hair2`, `scalp`, `ties`) are Standard, Opaque, render queue 2000.

Pack Alpha supplies the missing piece: a texture whose alpha *is* the transparency, ready for whatever material replaces the bridge's (D13).

---

## 5. Pack Alpha

### 5.1 Using it
**Tools → Texture Tools → Pack Alpha**, or right-click a colour texture → **Texture Tools → Pack Alpha…** (pre-fills Colour).

### 5.2 Window

| Field | Notes |
|---|---|
| **Colour map** | Texture field (object picker "browse" button, or drag from the Project window). Shows size and whether it already has alpha. |
| **Transparency map** | Texture field. Shows size; a warning if it differs from the colour map (D8). |
| **Alpha from** | Luminance (default) / R / G / B / A (D10). |
| **Invert** | Off by default. |
| **Preview** | Colour map with the packed alpha shown over a checkerboard, plus the alpha alone, so you can check the invert/channel choice before writing. |
| **Output** | Path, default `<colour name>_Alpha.png` next to the colour map; a **…** button opens a Save dialog restricted to the project. |
| **Cutoff** | 0.5 default — the alpha-test value the mip coverage setting preserves (D9); set it to the cutoff your material will use. |
| **Pack** | Writes the file, imports it (D9), selects it. |

### 5.3 What it does
1. Read raw pixels of both maps (D3).
2. Take the chosen channel of the transparency map (Luminance = 0.2126 R + 0.7152 G + 0.0722 B on stored values), invert if asked, resample to the colour map's size if needed (D8).
3. Output RGB = colour map RGB as stored; A = that value.
4. Write the PNG (D7), import it with D9's settings.

---

## 6. Code changes outside the new files
None, by design (D15a). The DAZ Bridge code is third-party and is not changed by these tools.

---

## 7. Build phases

| Phase | Content | Status |
|---|---|---|
| 1 | Folder, assemblies, `SourcePixels` (temp-copy read, bit-depth sniff), `TextureWriter` (8/16-bit PNG, EXR, importer copy, overwrite prompt) + tests | Done |
| 2 | Normal Green Flip: op, batch menu, window + tests | Done |
| 3 | Pack Alpha: op, window, preview, D9 import settings + tests | Done |
| 4 | Try it on real assets, and the portability check (below) | Not started: the BarbariAnne hair, `body_n1.dds` and a store DirectX map in the Editor (user); copying into fresh projects (to run with the project's Editor closed) |
| 5 | DDS sources: read through `IHVImageFormatImporter` (D3); output is a PNG, since Unity can't write DDS. The DDS importer's wrap, filter, readability and mip-streaming settings are copied, and Flip makes the output a Normal map, since a DDS has no texture type to copy. Real-file check: `Assets/_MyFiles/Meshes/Rim/Drauger/DraugerTex/body_n1.dds` | Done (tests pass); the real-file check is part of Phase 4 |

**Tests (EditMode, through the real import path):**
- Round trip: a known PNG / JPG / TGA / 16-bit PNG / EXR written under a test folder in `Assets`, read through `SourcePixels`, values match exactly (8-bit) or within half precision (16-bit / EXR); the temporary copy is gone afterwards.
- Reading ignores the source importer: a source imported as compressed, Max Size 256, Normal map type still reads at full size with exact values.
- Flip: green inverted, R/B/A identical; flipping twice gives back the source; output importer matches the source importer's settings; 16-bit source → 16-bit output.
- Pack: each channel choice and Invert; mismatched sizes resample to the colour map's size; colour RGB unchanged; output importer has D9's settings; an existing output file triggers the overwrite path and Replace keeps its GUID.

**Real-asset check:**
- Pack the BarbariAnne hair (`aprisel_txbrownblonde.jpg` + `aprisel_trans.jpg`), put it on a Cutout material on the hair: strands cut out correctly in the Scene view, no dark fringes, hair doesn't thin at a distance.
- Flip a DirectX normal map from a store asset and compare lighting on a lit sphere against the original: bumps that looked inverted (lit from below) now read correctly.

**Portability check:**
- Copy the `TextureTools` folder into a new empty Unity project (Built-in) and into a new URP project: both compile with no errors and no missing references, both menus appear, Flip and Pack produce correct files.
- In one of those projects without the Test Framework package, the folder still compiles (the test assembly is skipped).

---

## 8. Mask Texture Maker

Replaces Rambo's Studios **Texture Channel Packer** (`Assets/Asset Packs/Texture Channel Packer`), which works but doesn't say what each output channel is for, doesn't say what to use when there's no suitable texture, and hides which input channel goes to which output channel (you drag input channel buttons onto output buttons). Lives in the same portable folder and follows D15; reads inputs through `SourcePixels` (so 16-bit, DDS and the source-file guarantees of D3 apply) and writes through `TextureWriter` (new file, sources untouched).

### 8.1 Target layouts
Verified in Better Lit's shader source (`Packages/com.jbooth.better-lit-shader/Shaders/BetterShadersSource/LitBase-Dev.surfshader`, `FunctionLibrary.surfshader`) and its manual:

| Preset | R | G | B | A | Colour space |
|---|---|---|---|---|---|
| Mask Map (Better Lit / HDRP Lit) | Metallic | Occlusion | Detail Mask | Smoothness | Linear |
| Packed Normal (Better Lit "Fastest"; "Fast Metal" puts Metallic in B) | Smoothness | Normal Y | Occlusion | Normal X | Linear |
| Detail Map (Better Lit / HDRP Lit) | Albedo brightness (0.5 = no change) | Normal Y | Smoothness | Normal X | Linear |
| Albedo + Height | Colour R | Colour G | Colour B | Height (or alpha) | sRGB |
| Custom | user-labelled | user-labelled | user-labelled | user-labelled | user's choice |

### 8.2 Decisions

| # | Decision |
|---|---|
| M1 | **The window is built around the output.** One row per output channel, labelled with what the preset's shader uses it for. Each row is *either* a texture (with which of its channels to take, and Invert) *or* a constant value. A **Result** line spells out the whole mapping (`R ← metal.png (R)`, `B = 1.0`, `A ← 1 − rough.png (R)`) before anything is written, and the preview shows each output channel on its own as well as RGB. |
| M2 | **Every row's constant is pre-filled with the value that leaves that part of the material unchanged**, with a note saying so: Metallic 0 (no metal), Occlusion 1 (no occlusion), Detail Mask 1 (detail everywhere), Smoothness 0.5, Detail albedo 0.5 (no change). |
| M3 | **Presets: Mask Map, Packed Normal (with a Fast Metal variant), Detail Map, Albedo + Height, and Custom.** A preset sets the row labels, constants, colour space and output name; Custom lets you name the four channels and pick sRGB on or off. |
| M4 | **Auto-fill from texture names.** Select a set of textures (or right-click one → **Texture Tools → Mask Texture Maker**) and press **Fill from names**: each texture goes to the row its name suffix matches (§8.3), with Invert switched on where the name says the data is the other way round (roughness). Every assignment shows in the rows and the Result line for checking before **Make**; rows with no match keep their constant. |
| M5 | **Every row's channel choice is R, G, B, A or Luminance**, plus Invert. So a Unity Standard *MetallicSmoothness* map fills two rows (Metallic ← R, Smoothness ← A), and a DirectX normal map goes into a packed layout by ticking Invert on the Normal Y row (the same green flip as Normal Green Flip). |
| M6 | **Output size = the largest texture input**; smaller inputs are resampled bilinearly (as Pack Alpha, D8), with a warning if their shapes differ. Constant-only output needs a size, entered in the window. |
| M7 | **Output file:** a PNG next to the first assigned texture, named `<base>_MaskMap` / `_PackedNormal` / `_Detail` / `_AlbedoHeight` / `_Packed` (Custom), where `<base>` is the inputs' shared name with the matched suffix removed; changeable with **...**. 8-bit, or 16-bit when any texture-sourced row comes from a 16-bit or floating-point source. Existing files follow D12. |
| M8 | **Output import settings:** texture type Default (never Normal map: Better Lit reads packed normals' channels directly, and Unity's Normal map type would re-encode them), sRGB off for the linear layouts and on for Albedo + Height, mipmaps on, alpha from the file. *Alpha Is Transparency* stays off because the alpha is data, except when the Albedo + Height or Custom preset's **A is transparency** option is ticked. |
| M9 | **A one-off window**, like the other two tools: fill it in, press **Make**, get a PNG. Nothing is saved besides the PNG; to change it, run it again (auto-fill makes that quick). The window remembers only the last preset used. |

### 8.3 Name matching for auto-fill
The name is split into parts at `_`, `-`, `.` and spaces, and matched case-insensitively on its whole last part, or on its last two parts for the normal-map conventions (`Rock_Normal_DX`). Whole parts only: `Rock_M` fills Metallic, `Rock_Mud` matches nothing.

| Row | Suffixes | Invert |
|---|---|---|
| Metallic | `metallic`, `metalness`, `metal`, `m` | off |
| Occlusion | `ao`, `occlusion`, `ambientocclusion` | off |
| Smoothness | `smoothness`, `smooth`, `gloss`, `glossiness` | off |
| Smoothness | `roughness`, `rough`, `r` | **on** |
| Metallic + Smoothness | `metallicsmoothness`, `metallicgloss` (Unity Standard: Metallic ← R, Smoothness ← A) | off |
| Detail Mask | `detailmask`, `detail` (Mask Map preset only) | off |
| Height | `height`, `displacement`, `disp`, `h` | off |
| Normal X / Y | `normal`, `nrm`, `n`, `normalgl`, or `normal`/`nrm` followed by `gl`/`opengl` → X ← R, Y ← G | off |
| Normal X / Y | `normaldx`, or `normal`/`nrm` followed by `dx`/`directx` | Y **on** |
| Colour / Detail albedo | `albedo`, `basecolor`, `basecolour`, `diffuse`, `color`, `colour`, `col`, `d`, `bc` | off |

### 8.4 Window
**Tools → Texture Tools → Mask Texture Maker**, or right-click textures → **Texture Tools → Mask Texture Maker**, which opens the window and fills the rows from the selection by name.

```
Preset: [ Mask Map (Better Lit / HDRP) ▾ ]                    [ Fill from names ]

R  Metallic     (•) Texture [ rock_metal.png ] channel [R ▾] [ ] Invert
                ( ) Value   0.0   no metal
G  Occlusion    (•) Texture [ rock_ao.png    ] channel [R ▾] [ ] Invert
                ( ) Value   1.0   no occlusion
B  Detail Mask  ( ) Texture [ none ]
                (•) Value   1.0   detail everywhere
A  Smoothness   (•) Texture [ rock_rough.png ] channel [R ▾] [x] Invert
                ( ) Value   0.5

Result:  R <- rock_metal.png (R)   G <- rock_ao.png (R)   B = 1.0   A <- 1 - rock_rough.png (R)
Size 2048 x 2048 (largest input)   8-bit   linear
[ RGB ][ R ][ G ][ B ][ A ]  preview
Output: Assets/.../rock_MaskMap.png  [...]                         [ Make ]
```

Custom adds a name field per row and an sRGB toggle; Albedo + Height and Custom add **A is transparency** (M8). Warnings appear under the row they concern (a texture that isn't in the project, shapes that differ, a row reading A from a texture with no alpha).

### 8.5 Files
Added to the folder in §2:

```
Editor/
  MaskTextureMaker.cs          presets (labels, constants, colour space, suffix), the pack op, name matching (§8.3), output naming and import settings
  MaskTextureMakerWindow.cs    the window (§8.4), context-menu entry; previews from small copies of the inputs
Tests/
  MaskTextureMakerTests.cs
```

The pack op reuses `PackAlpha.ExtractAlpha` (channel / Luminance / Invert) and `PackAlpha.Resample`; files go through `SourcePixels` and `TextureWriter`. It reads one source at a time and fills every row that uses it, so only one full-size source is in memory at once; output size and bit depth come from `SourcePixels.Describe`, which reads headers and import data, not pixels.

### 8.6 Build phases and tests

| Phase | Content | Status |
|---|---|---|
| M-1 | Presets, pack op, name matching, output naming and import settings + tests | Done (tests pass) |
| M-2 | Window, preview, Result line, context menu | Code written; checked in the real-asset pass (M-3) |
| M-3 | Real-asset check: a Better Lit material on a rock from an asset pack (auto-filled), and a Packed Normal from a DirectX normal map | Not started |

**Tests (EditMode, through the real import path):**
- Each preset: rows from textures and from constants land in the right output channels; Invert and each channel choice (including Luminance) apply; a MetallicSmoothness source fills two rows.
- Mixed sizes resample to the largest; constant-only output uses the entered size.
- Output: name per preset, next to the first texture; 16-bit when a 16-bit source is used; import settings per M8 (Default type, sRGB per preset, Alpha Is Transparency only when ticked); D12 overwrite handling.
- Name matching: every suffix in §8.3, two-part normal suffixes, whole-part only (`Rock_Mud` matches nothing), roughness / DirectX set Invert, unmatched rows keep their constants.
- Packed Normal from a DirectX normal map with Invert on Normal Y equals the Normal Green Flip result's G and R in G and A.

---

## 9. HDRP to Better Lit

Converts materials made for HDRP's **Lit** shader (as shipped in asset packs with HDRP versions) into **Better Lit Shader** materials for the Built-in pipeline. Unity's own converters go the other way (Built-in → URP/HDRP); nothing ships for HDRP → Built-in. Better Lit was written to sit close to HDRP: its mask map and detail map use HDRP's channel layouts, and several properties share HDRP's names. Most of the work is copying values; the rest is listed below, with what can't carry over reported per material.

The result is not meant to look identical to HDRP. HDRP's lighting, physical light units, exposure and diffusion profiles have no Built-in equivalent. The aim is the closest Better Lit material, with everything that differs listed.

### 9.1 Sources: what the tool recognises
In a Built-in project the HDRP shaders aren't installed, so these materials show as pink with a missing shader. The material file still holds every HDRP value, and still references the HDRP shader by GUID. The GUIDs below come from the `.meta` files in Unity's Graphics repository (`Packages/com.unity.render-pipelines.high-definition/Runtime/Material/…`). The Lit GUID matches the 13 HDRP Lit materials already in this project.

| HDRP shader | GUID | Handling |
|---|---|---|
| `HDRP/Lit` | `6e4ae4064600d784cac1e41a9e6f2e59` | Converted |
| `HDRP/LitTessellation` | `756bac9090102564582875f4c7e30202` | Converted, to Better Lit's tessellation variants |
| `HDRP/LayeredLit` | `81d02e8644315b742b154842a3a2f98c` | Reported, not converted (H17) |
| `HDRP/LayeredLitTessellation` | `eab0538d9d5746246806a9611c04ac4d` | Reported, not converted (H17) |
| `HDRP/Unlit` | `c4edd00ff2db5b24391a4fcb1762e459` | Reported, not converted (H17) |
| `HDRP/AxF` | `6510c57cccce5674092bdc0dd1ae98c1` | Reported, not converted (H17) |
| `HDRP/TerrainLit` | `eb607f5e8fc16e140b732f33d26c8c31` | Reported, not converted (H17) |
| `HDRP/Decal` | `1d64af84bdc970c4fae0c1e06dd95b73` | Reported, not converted (H17) |
| `Universal Render Pipeline/Lit` (URP, not HDRP) | `933532a4fcc9baf4fa0491de14d08ed7` | Reported as "URP Lit — not an HDRP material", not converted. This project has 13, e.g. `Assets/Procedural Worlds/GeNa/Resources/Materials/GeNa_URP_Road_Material.mat` |
| Anything else with a missing shader (Shader Graph, StackLit, Hair, Fabric, Eye, third-party HDRP shaders) | — | Reported as "unrecognised missing shader", with its GUID |

Materials whose shader exists (Standard, Better Lit, anything else) are left alone and not listed, so a whole folder can be selected without risk.

### 9.2 Decisions

| # | Decision |
|---|---|
| H1 | **Values are read from the material file's saved properties**, through `SerializedObject` on `m_SavedProperties` (`m_TexEnvs`, `m_Floats`, `m_Colors`) and `m_Shader`. Asking the `Material` API by name isn't reliable when the shader is missing. A property that isn't saved takes HDRP Lit's default value (§9.3 lists the ones used), which is what HDRP itself would do. |
| H2 | **Materials are converted in place**, keeping their GUID, so every prefab, scene and renderer that uses them updates without being re-pointed. This differs from D2 (sources never modified) on purpose: an HDRP material is unusable in a Built-in project, and writing copies would leave every reference pointing at the broken original. Nothing is lost: Unity keeps properties the new shader doesn't use in the file's saved properties, so the HDRP values stay in the material. New **textures** the conversion needs (H9, H13) follow D2: new files next to their sources, sources untouched. |
| H3 | **Report before convert.** The window lists every selected material with what will happen to it (§9.5) before anything is written. **Convert** then applies it as one Undo step. A log of what was converted and what was flagged is written to the Console and can be copied as text. |
| H4 | **Better Lit shaders are found by asset GUID** (the package's `.betterlitpack` files), not by name: Lit `8d7180d86aab44f15bb161c4ab3113a9`, Lit Alpha `bea851d2911324d81adc2f7471fdcb8b`, Lit Tessellation `cf5d184c3d9304a7baae9d5f7a3a3cd5`, Lit Tessellation Alpha `610d27da9020049b4b6180390a02d185`. The code references Better Lit only through these GUIDs and property-name strings, so the folder still compiles in a project without Better Lit (D15). The window then says Better Lit isn't installed, and Convert is disabled. |
| H5 | **Which Better Lit shader:** HDRP Opaque, with or without alpha clipping → **Lit**. HDRP Transparent (`_SurfaceType` 1) → **Lit Alpha**. Source LitTessellation with `_DisplacementMode` Tessellation → **Lit Tessellation** / **Lit Tessellation Alpha**. |
| H6 | **Alpha clipping** (`_AlphaCutoffEnable` 1) is set up the way Better Lit's own Inspector does it: `_AlphaThreshold` = `_AlphaCutoff`, keyword `_ALPHACUT`, render queue 2450, `RenderType` tag `TransparentCutout`, and `_Cutoff` set to the same value (Unity's lightmapper reads `_Cutoff`). |
| H7 | **Transparent blend modes:** Lit Alpha does ordinary alpha blending. HDRP Alpha (`_BlendMode` 0) converts as is. Additive (1) and Premultiply (4) convert to alpha blending and are flagged. |
| H8 | **Keywords and remaps follow Better Lit's own rules**, as checked in `LitBase-Dev.surfshader` and `EditorStub.cs`: `_NORMALMAP` when a normal map is set, `_MASKMAP` when a mask map is set, `_DETAIL`, `_EMISSION`, `_SPECULAR` and `_ALPHACUT` as each feature is used. All HDRP keywords are removed. **Remaps are copied only when there's a mask map.** Without one, HDRP uses `_Smoothness` / `_Metallic` directly. Better Lit, though, runs those constants through the remaps whenever a normal map is on, so the remaps must stay at (0, 1) for them to pass through unchanged. |
| H9 | **Height map → albedo alpha.** HDRP keeps height in its own `_HeightMap`; Better Lit reads it from the albedo's alpha. The tool writes `<albedo>_AlbedoHeight.png` with Mask Texture Maker's Albedo + Height preset (RGB ← albedo, A ← height's R) and uses it as the albedo. HDRP only uses the height map for displacement, so it's packed only when `_DisplacementMode` isn't None. The packed texture keeps the base map's tiling (a height map tiled differently is flagged). Materials sharing the same albedo and height get one packed file. If the material also uses albedo alpha for transparency or clipping, both can't share one channel: transparency wins, and the height is dropped and flagged. |
| H10 | **Displacement.** HDRP Pixel displacement (`_DisplacementMode` 2) → Better Lit `_PARALLAX` with `_ParallaxHeight` 0.02, flagged to tune. HDRP gives the amplitude in centimetres, while Better Lit's offset is in UV units, and how many centimetres one UV tile covers depends on the mesh. HDRP Tessellation → Lit Tessellation with `_TessellationDisplacement` = `_HeightTessAmplitude` / 100 (centimetres → metres; Better Lit displaces in object units) and `_TessellationOffset` = `_HeightTessCenter`. HDRP Vertex displacement (1) → Lit Tessellation too, flagged because it also subdivides the mesh. Its values are the ones HDRP's UI saves for vertex displacement in world units: `_TessellationDisplacement` = `_HeightAmplitude`, `_TessellationOffset` = `_HeightCenter`. |
| H11 | **Emission** = `_EmissiveColor` × the exposure factor in H21, with the map copied to `_EmissionMap`. HDRP's `_EmissiveColor` is linear: its UI saves the LDR colour converted to linear × the intensity, which is exactly what this project's Lava-Ground files hold. Better Lit's `_EmissionColor` is a plain colour property, which Unity converts from sRGB to linear for the shader in a linear project. So the colour goes in with its brightest channel scaled to 1 and stored as gamma, and the brightness goes in `_EmissionMultiplier`, a float that isn't converted. A test renders a property declared the same way to confirm the conversion. Black emission leaves `_EMISSION` off. `_AlbedoAffectEmissive` 1 multiplies the emission colour by `_BaseColor` (the texture part can't carry over, so it's flagged). Better Lit samples emission with the main UVs and tiling, so the material is flagged if `_EmissiveColorMap_ST` differs from `_BaseColorMap_ST`, or if `_UVEmissive` differs from the base UV set and isn't "same as base". This project's Lava-Ground materials (base tiling 20, emission 5) are an example. |
| H12 | **Material types** (`_MaterialID`): **Standard** (1) converts fully. **Specular Colour** (4) → `_SPECULAR` with `_SpecularTint` = `_SpecularColor` and `_SpecularMap` = `_SpecularColorMap`. With no map, a shared 4×4 white texture (`TextureTools/Generated/White.png`, made once) is assigned, because Better Lit's specular map defaults to black. **Subsurface Scattering** (0) and **Translucent** (5) → H13. **Anisotropy** (2) and **Iridescence** (3) convert as Standard and are flagged, since Better Lit has neither. |
| H13 | **SSS / Translucent → Better Lit's Approximate SSS** (`_CHEAPSSS`). Better Lit's value is the amount of light let through (0 = none); HDRP's thickness is the opposite (1 = thick), so Better Lit thickness = 1 − HDRP thickness. HDRP's thickness is `_Thickness`, or with a thickness map `_ThicknessRemap.x + _ThicknessRemap.y × map R`, as `LitDataIndividualLayer.hlsl` computes it. With a `_ThicknessMap`, the tool packs `<thickness map name>_SSS.png` (RGB = white tint, A = 1 − that value) and turns on `_CHEAPSSSTEXTURE`; otherwise it sets `_CheapSSSThickness`. The tint is white, because the diffusion-profile colour lives in a separate HDRP asset, not in the material. The material is always flagged to tune by eye. |
| H14 | **Dropped, and listed for each material where they were in use:** clear coat (`_CoatMask`; Better Lit's clear coat only exists in URP/HDRP), bent normal map, specular occlusion, geometric specular AA (HDRP-only in Better Lit), object-space normal maps (`_NormalMapSpace` 1, where the normal map isn't copied), refraction, distortion, transmission settings beyond H13, and HDRP's decal/SSR/motion-vector toggles. |
| H15 | **UV mapping.** `_UVBase` UV0/UV1 → `_UVSource` UV0/UV1. UV2/UV3 → UV0, flagged. **Planar** (4) → `_UVSource` Project Y, world space, with tiling = base tiling × `_TexWorldScale` and V negated, because Better Lit projects with `pos.xz × (1, −1)` and HDRP with `pos.xz`. **Triplanar** (5) → `_TRIPLANAR`, same tiling without the V flip; both shaders project (z, y), (x, z) and (x, y). `_ObjectSpaceUVMapping` 1 → `_TriplanarSpace` Local, otherwise World. HDRP projects the detail map the same way as the base whatever `_UVDetail` says, so the detail map gets the same projection (`_DetailUVSource` Project Y or `_DETAILTRIPLANAR`) on top of H16's tiling. Projected materials are flagged to check: Better Lit shifts two of the three triplanar projections and blends them differently. |
| H16 | **Tiling.** `_BaseColorMap_ST` → `_AlbedoMap_ST`, used for every main map, as in HDRP. Detail: `_DetailMap_ST` → `_DetailMap_ST`, and `_UVDetail` UV0/UV1 → `_DetailUVSource` (UV2/UV3 → UV0, flagged). When `_LinkDetailsWithBase` is 1 (HDRP's default), HDRP applies the base tiling on top of the detail tiling (`LitDataIndividualLayer.hlsl`, `ComputeLayerTexCoord`). Better Lit doesn't, so the tool bakes it in: detail scale = detail scale × base scale, and detail offset = detail offset × base scale + base offset. |
| H17 | **LayeredLit, Unlit, AxF, TerrainLit and Decal are reported, not converted**, each with the reason. Unlit could map to Better Lit Unlit, and LayeredLit to Better Lit's texture layers, but each is its own mapping job. This project has 1 LayeredLitTessellation, 1 Unlit, 2 TerrainLit and 4 Decal materials. Decals have no Built-in equivalent, and TerrainLit belongs to terrains, not meshes. Extending the tool to Unlit or LayeredLit is a separate decision. |
| H18 | **Double-sided:** `_DoubleSidedEnable` 1 → `_CullMode` 0 (Off), otherwise 2 (Back). The saved HDRP `_CullMode` isn't trusted, since HDRP derives it. `_DoubleSidedNormalMode` is copied as is: both shaders use the same property name and the same Flip 0 / Mirror 1 / None 2 values. `m_DoubleSidedGI` is kept. |
| H19 | **Batch from the Project window**: select materials and/or folders → right-click → **Texture Tools → HDRP to Better Lit…** opens the window with them listed. Folders are searched recursively. Also **Tools → Texture Tools → HDRP to Better Lit**, with a field for adding a folder. |
| H20 | **Lives in the portable TextureTools folder** and follows D15 (H4 covers Better Lit being absent). The packed textures go through `MaskTextureMaker.Make` / `TextureWriter`, so D3, D7 and D12 apply to them. |
| H21 | **Emission exposure: EV100 0 by default.** HDRP displays emission as `E × lerp(1, X, w)`, from `LitBuiltinData.hlsl`: the emission is multiplied by the inverse of the camera exposure and blended back by `_EmissiveExposureWeight` w. X is the camera exposure, `1 / (1.2 × 2^EV100)`. Built-in has no physical exposure, so a fixed EV100 stands in for the HDRP scene's camera. The window's **HDRP exposure (EV100)** field defaults to **0** (X ≈ 0.83), which keeps low-intensity emission visible (the 2-nit lava comes out at about 1.7). It can be changed for each batch. |

### 9.3 Property map (HDRP Lit → Better Lit)
HDRP defaults (used when a property isn't saved, H1) are from `Lit.shader` in Unity's Graphics repository.

| HDRP | HDRP default | Better Lit | Notes |
|---|---|---|---|
| `_BaseColorMap` (+ `_ST`) | white | `_AlbedoMap` (+ `_ST`) | Or the packed albedo + height (H9) |
| `_BaseColor` | (1,1,1,1) | `_Tint` | Both multiply RGBA |
| `_AlphaRemapMin` / `Max` | 0 / 1 | `_HeightRemap` (x, y) | Only when alpha is used for transparency or clipping; Better Lit remaps albedo alpha with `_HeightRemap` |
| `_AlphaCutoffEnable`, `_AlphaCutoff` | 0, 0.5 | `_ALPHACUT`, `_AlphaThreshold`, `_Cutoff` | H6 |
| `_NormalMap` | bump | `_NormalMap`, `_NORMALMAP` | Same name |
| `_NormalScale` | 1 | `_NormalStrength` | HDRP allows up to 8, Better Lit up to 2: clamped and flagged |
| `_MaskMap` | — | `_MaskMap`, `_MASKMAP` | Same name and layout |
| `_Metallic`, `_Smoothness` | 0, 0.5 | `_Metallic`, `_Smoothness` | Same names; used when there's no mask map |
| `_MetallicRemapMin`/`Max`, `_SmoothnessRemapMin`/`Max`, `_AORemapMin`/`Max` | 0 / 1 each | `_MetallicRemap`, `_SmoothnessRemap`, `_AORemap` (x = min, y = max) | Only with a mask map (H8) |
| `_DetailMap` (+ `_ST`) | linearGrey | `_DetailMap` (+ `_ST`), `_DETAIL` | Same layout; tiling per H16 |
| `_DetailAlbedoScale`, `_DetailNormalScale`, `_DetailSmoothnessScale` | 1, 1, 1 | `_DetailAlbedoStrength`, `_DetailNormalStrength`, `_DetailSmoothnessStrength` | |
| `_UVBase`, `_TexWorldScale` | 0, 1 | `_UVSource`, `_TRIPLANAR`, `_TriplanarSpace` | H15 |
| `_UVDetail`, `_LinkDetailsWithBase` | 0, 1 | `_DetailUVSource` | H16 |
| `_EmissiveColor`, `_EmissiveColorMap`, `_EmissiveExposureWeight`, `_AlbedoAffectEmissive` | black, white, 1, 0 | `_EmissionColor` (hue), `_EmissionMultiplier` (brightness), `_EmissionMap`, `_EMISSION` | H11, H21 |
| `_SpecularColor`, `_SpecularColorMap` | white, white | `_SpecularTint`, `_SpecularMap`, `_SPECULAR` | `_MaterialID` 4 only (H12) |
| `_Thickness`, `_ThicknessMap`, `_ThicknessRemap` | 1, white, (0,1) | `_CheapSSSThickness` or `_CheapSSSTexture`, `_CHEAPSSS` | `_MaterialID` 0 / 5 only (H13) |
| `_HeightMap`, `_DisplacementMode`, `_HeightTessAmplitude`, `_HeightTessCenter` | black, 0, 2, 0.5 | albedo alpha, `_PARALLAX`, `_TessellationDisplacement`, `_TessellationOffset` | H9, H10 |
| `_DoubleSidedEnable`, `_DoubleSidedNormalMode` | 0, 1 | `_CullMode`, `_DoubleSidedNormalMode` | H18 |
| `_SurfaceType`, `_BlendMode` | 0, 0 | Lit / Lit Alpha | H5, H7 |

### 9.4 What it does, per material
1. Read `m_Shader`, and classify by GUID (§9.1). Skip it if the shader exists, or report it if it isn't Lit or LitTessellation.
2. Read the saved properties (H1), filling in HDRP defaults.
3. Work out the Better Lit shader (H5), the property values (§9.3), the textures to pack (H9, H13) and the flags (H7, H10–H15, H17).
4. **Report only, until Convert is pressed** (H3).
5. On Convert: write any packed textures first; if one fails, that material is skipped and reported. Then, in one Undo group, set the shader, clear all keywords, set the values and keywords, set the render queue and the `RenderType` tag, mark the material dirty, and save assets.
6. Log one line per material: converted / converted with flags (each listed) / skipped (reason).

### 9.5 Window

```
HDRP to Better Lit                                    Better Lit: installed ✓
[ Add selection ]  [ Add folder… ]  [ Refresh ]  [ Clear ]
HDRP exposure (EV100): [ 0 ]   (H21)

Material                 Source          Becomes              Notes
pinecones                HDRP Lit        Better Lit / Lit     —
sticks_02_phys           HDRP Lit        Lit (alpha clip)     double-sided
Lava-Ground (×3)         HDRP Lit        Lit                  emission tiling differs (20 vs 5)
ReticleHDRP              HDRP Lit        Lit Alpha            —
Grass_Layered            HDRP LayeredLit (skipped)            LayeredLit not converted
…
13 to convert · 8 skipped · 2 packed textures will be written
                                                                    [ Convert ]
```

Clicking a row selects and pings the material. Its flags, and any packed texture it will write, show in full in a details panel under the list. The list is read when materials are added, when EV100 changes and on **Refresh**. **Convert** reads the materials again first, so nothing changed since is overwritten, then asks for confirmation. Afterwards the log is in the Console, and **Copy log** copies it.

### 9.6 Files
Added to the folder in §2:

```
Editor/
  HdrpToBetterLit.cs           GUID tables (§9.1, H4), saved-property reader (H1), plan (H5–H18) and apply
  HdrpToBetterLitWindow.cs     the window (§9.5), context menu (H19)
Tests/
  HdrpToBetterLitTests.cs
```

### 9.7 Build phases and tests

| Phase | Content | Status |
|---|---|---|
| H-1 | Classification, saved-property reader, plan for Standard / opaque / clipped / transparent, keywords, remaps, tiling, double-sided, emission + tests | Done |
| H-2 | Height packing, displacement, Specular Colour, SSS/Translucent packing, UV projection modes + tests | Done. 31 HDRP to Better Lit tests in all |
| H-3 | Window, report, context menu, Undo, log, and the converter's section in the folder's `README.md` (D15g) | Done: 33 HDRP to Better Lit tests pass, including one Undo step that restores the HDRP material. The window itself is checked in H-4 |
| H-4 | Real-asset check: the 13 HDRP Lit materials in this project (below), side by side with their textures in the Scene view | Done: converted through the window, and every check below passed |

**Tests (EditMode, through the real path):** each test writes a `.mat` file as YAML text, in the form HDRP saves it, with `m_Shader` pointing at the real HDRP Lit or LitTessellation GUID. Those shaders aren't in the project, so the shader is missing exactly as it is for a real imported asset. The test imports the file, runs the tool's code path, and reads the result back.
- Opaque Standard with base/mask/normal: shader is Better Lit Lit; textures, tint and remaps land per §9.3; `_NORMALMAP` and `_MASKMAP` on; no HDRP keywords left; the GUID is unchanged; HDRP properties are still saved.
- No mask map with a normal map: the remaps stay at (0, 1) and `_Smoothness` / `_Metallic` are copied (H8).
- Clipped: `_ALPHACUT`, queue 2450, tag, `_Cutoff`. Transparent: Lit Alpha. Additive is flagged.
- A missing property takes the HDRP default (a file with `_MetallicRemapMax` removed reads as 1).
- Emission: the H11/H21 formula with a known EV100 and weight; black emission leaves `_EMISSION` off; mismatched emission tiling is flagged.
- Detail tiling with the link on and off (H16).
- Height packing writes `_AlbedoHeight.png` with the height in A; height plus clipping drops the height with a flag.
- Specular Colour without a map gets the white texture; SSS thickness is inverted; a thickness map packs `_SSS.png`.
- Planar UV gives Project Y with V negated and `_TexWorldScale` applied.
- Double-sided sets `_CullMode` 0, whatever the saved `_CullMode` says.
- LayeredLit, Unlit, Decal, URP Lit and an unknown missing GUID are each reported and left unchanged; a Standard material is left unchanged and not listed.
- Better Lit absent: covered by H4's GUID lookup returning nothing → Convert disabled. This is tested by pointing the lookup at a GUID that doesn't exist.

**Real-asset check (this project's HDRP Lit materials):**
- `BOTD_Standard Shader/_BOTD Original Assets/…/pinecones.mat` (base + mask + normal, mask remaps), `sticks_02_phys.mat` (clipped, double-sided, tinted), `Art/Terrain/Grey_01.mat` (no textures).
- `Deadly Dungeons/Infernal/_Demo Scenes/*/Lava-Ground.mat` ×3 and `ddi_hellgate-handle 1.mat` (emission map; 2 to 1000 nits; emission tiling differs), `Hell's Gate/Eyes.mat` (emission colour only, 400 nits), `Hell's Gate/Darkness.mat` (clipped, black).
- `Procedural Worlds/GeNa/Resources/Materials/GeNa_HDRP_Road_Material.mat` and `…Road_Intersection_Material.mat` (base + mask + normal), `GeNa_HDRP_River_Material.mat` (transparent, alpha 0.2).
- `Digger/Misc/HDRP/ReticleHDRP.mat` (transparent, colour only).
- Check: no pink materials; textures match their sources; clipped edges are correct; the transparent materials blend; the emission brightness reads sensibly at the chosen EV100.

