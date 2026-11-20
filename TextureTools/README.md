# Texture Tools

Four Editor-only texture and material tools. Copy this whole folder (with its `.meta` files) anywhere under a project's `Assets` folder; it needs nothing else. It works in any render pipeline (Built-in, URP, HDRP) and adds nothing to builds. HDRP to Better Lit converts only when Better Lit Shader is in the project.

The texture tools leave their sources untouched and write a **new file next to the source**. Normal Green Flip and Pack Alpha give the new file the source's import settings, including platform overrides.

## Normal Green Flip

Converts a normal map between the **OpenGL** and **DirectX** conventions by inverting its green channel. The same operation converts in either direction.

**Unity uses OpenGL** (green = +Y, "up"). DirectX maps come from Unreal, from Substance's default export and from many store assets, and they need converting before Unity can use them. Signs of a map in the wrong convention: bumps look lit from below, and dents look like bumps.

- **Several at once:** select normal maps in the Project window, then right-click → **Texture Tools → Flip Normal Green** and pick the convention you want.
- **One at a time, with a preview:** **Tools → Texture Tools → Normal Green Flip**.

Output: `<name>_GL.png` or `<name>_DX.png`. An existing `_GL` / `_DX` suffix is swapped, not stacked. 16-bit sources give 16-bit PNGs, EXR/HDR sources give EXR, and JPGs become PNGs, so the file doesn't go through a second lossy save.

## Pack Alpha

Puts a separate **transparency map** into the **alpha channel** of a colour (albedo/diffuse) map. Shaders such as Built-in's Standard read transparency only from the colour texture's alpha. Exporters like DAZ Studio supply transparency as a separate greyscale map, often a JPG, which can't hold alpha at all.

Open it from **Tools → Texture Tools → Pack Alpha**, or right-click a colour texture → **Texture Tools → Pack Alpha...**.

| Setting | |
|---|---|
| Colour map | The texture whose RGB is kept. Any alpha it already has is replaced. |
| Transparency map | Resized to the colour map's size if it's different. |
| Alpha from | Luminance (default), Red, Green, Blue or Alpha of the transparency map. |
| Invert | Off by default. White = opaque, black = see-through is already Unity's convention. |
| Cutoff | Set this to the alpha-test cutoff your material will use. Mipmaps keep the same coverage at this value, so hair and lashes don't thin out at a distance. |
| Output | `<colour name>_Alpha.png` next to the colour map, or pick another location with **...**. |

The output is imported with **Alpha Is Transparency** on, which stops dark fringes at cut-out edges, and with **Mip Maps Preserve Coverage** on. The tool doesn't change materials: point your material at the new texture and set it to a cutout or transparent mode.

## Mask Texture Maker

Builds one texture from channels of other textures and constant values: mask maps and the other packed textures used by Better Lit Shader and HDRP Lit. Open it from **Tools → Texture Tools → Mask Texture Maker**, or select textures and right-click → **Texture Tools → Mask Texture Maker**. Opening it from the right-click menu fills the rows from the textures' names.

| Preset | R | G | B | A |
|---|---|---|---|---|
| Mask Map (Better Lit / HDRP) | Metallic | Occlusion | Detail Mask | Smoothness |
| Packed Normal (Better Lit Fastest) | Smoothness | Normal Y | Occlusion | Normal X |
| Packed Normal, Fast Metal | Smoothness | Normal Y | Metallic | Normal X |
| Detail Map (Better Lit / HDRP) | Albedo brightness | Normal Y | Smoothness | Normal X |
| Albedo + Height | Colour | Colour | Colour | Height (or alpha) |
| Custom | you name them | | | |

The window has one row per output channel, labelled with what it's for:
- **Texture or value:** each row takes either a texture (with the channel to take: R, G, B, A or Luminance, plus **Invert**) or a constant **Value**.
- **Defaults:** the values already filled in leave that part of the material unchanged: metallic 0, occlusion 1, detail mask 1, smoothness 0.5, flat normal 0.5.
- **Result line:** spells out where every channel comes from before you press **Make**, for example `A <- 1 - Rock_Roughness.png (R)`.
- **Preview:** shows the whole result or one channel at a time.

**Fill from names** assigns the selected textures by their name suffix:

| Suffix | Fills |
|---|---|
| `_Metallic`, `_AO`, `_Smoothness`, `_Height`, `_Albedo`, `_Normal` (and common variants) | the matching row |
| `_Roughness` | Smoothness, inverted |
| `_MetallicSmoothness` | Metallic from R and Smoothness from A |
| `_Normal_DX` | the normal rows, with Normal Y inverted (DirectX → OpenGL) |

Rows with no match keep their value.

The result is a PNG next to the first texture, named `<name>_MaskMap.png` (or `_PackedNormal`, `_Detail`, `_AlbedoHeight`, `_Packed`). It is the size of the largest input, and 16-bit if any input is. It's imported as a plain texture (packed normals must not be imported as Normal maps), with sRGB off except for Albedo + Height, and at a Max Size that keeps its full resolution.

## HDRP to Better Lit

Converts materials made for **HDRP's Lit shader**, which show pink in a Built-in project, into **Better Lit Shader** materials. Open it from **Tools → Texture Tools → HDRP to Better Lit**, or select materials and/or folders and right-click → **Texture Tools → HDRP to Better Lit...**. Folders are searched recursively.

The window lists each HDRP material with what it becomes and anything that doesn't carry over. Click a row for the full details. Materials that already have a working shader aren't listed. **Convert** then changes them **in place**, keeping their GUIDs, so prefabs and scenes using them update. It's one Undo step, and HDRP's values stay saved in each material file. The result is logged to the Console, and **Copy log** copies it.

- **Converted:** HDRP Lit and Lit Tessellation. Opaque materials become Better Lit Lit, alpha-clipped ones become Lit with alpha clip, transparent ones become Lit Alpha, and displaced ones become Lit Tessellation or use parallax. Textures, tint, mask-map remaps, detail maps, emission, double-sided, planar and triplanar mapping, specular colour and subsurface scattering (as Better Lit's approximate SSS) carry over.
- **Listed but not converted:** LayeredLit, Unlit, AxF, TerrainLit, Decal, URP Lit and unknown missing shaders.
- **Won't look identical:** HDRP's lighting and exposure differ from Built-in's. Notes flag what to check, such as parallax height, SSS tint and blend modes Better Lit doesn't have.
- **HDRP exposure (EV100):** sets how bright converted emission is. 0 keeps dim emission visible. Raise it for materials made for bright scenes.
- **New textures:** Better Lit keeps height in the albedo's alpha and SSS thickness in its own texture, so the tool writes `<albedo>_AlbedoHeight.png` or `<thickness map>_SSS.png` next to the sources when needed. Specular materials without a specular map get a shared `Generated/White.png` in this folder. Undo doesn't remove these files.

## How the pixels are read

All the texture tools read the **source file**, not the imported texture, because the imported texture may be compressed, shrunk by Max Size, or (for normal maps) stored in a rearranged form. To do this, they import a temporary copy raw in `Assets/__TextureToolsTemp` and delete it immediately. If the Editor crashes partway through, the folder is removed the next time the Editor loads. Every format Unity's TextureImporter handles works: PNG, JPG, TGA, PSD, TIF, EXR, HDR, BMP, GIF and PICT. **DDS** files work too. The tools read the data stored in the DDS, which is usually already compressed, and write the result as a PNG, because Unity can't write DDS. Point your material at the new PNG. The PNG looks exactly as the DDS did in Unity, including orientation. Unity shows BC7 DDS files upside down, and the PNG keeps that.

## Tests

`Tests/` holds EditMode tests. Run them from **Window → General → Test Runner**. They need the Test Framework package. Without it, the test assembly is skipped and the tools still compile.
