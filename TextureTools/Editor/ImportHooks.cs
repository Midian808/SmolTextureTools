using System;
using System.Collections.Generic;
using UnityEditor;

namespace Midian.TextureTools
{
    //Applies import settings as a texture is imported, so each file the tools create is imported once, with the right settings
    internal sealed class ImportHooks : AssetPostprocessor
    {
        //Output files about to be imported, and how to configure each one's importer
        internal static readonly Dictionary<string, Action<TextureImporter>> PendingOutputs = new Dictionary<string, Action<TextureImporter>>(StringComparer.Ordinal);

        //DDS files go through IHVImageFormatImporter, which doesn't trigger OnPreprocessTexture
        private void OnPreprocessAsset()
        {
            if (assetImporter is IHVImageFormatImporter importer && SourcePixels.IsTempPath(assetPath)) SourcePixels.ConfigureTempImport(importer);
        }

        private void OnPreprocessTexture()
        {
            if (!(assetImporter is TextureImporter importer)) return;

            if (SourcePixels.IsTempPath(assetPath)) SourcePixels.ConfigureTempImport(importer, assetPath);
            else if (PendingOutputs.TryGetValue(assetPath, out var configure)) configure(importer);
        }
    }
}
