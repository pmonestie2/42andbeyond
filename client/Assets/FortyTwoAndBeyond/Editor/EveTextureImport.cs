using UnityEditor;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Keep packed EVE material data in linear color space.</summary>
    public sealed class EveTextureImport : AssetPostprocessor
    {
        /// <summary>Import tangent normals and roughness as numeric data.</summary>
        private void OnPreprocessTexture()
        {
            if (!assetPath.Contains("/Resources/EveShips/")) return;
            if (!assetPath.EndsWith("_normal.png") &&
                !assetPath.EndsWith("_roughness.png")) return;
            var texture = (TextureImporter)assetImporter;
            texture.sRGBTexture = false;
            texture.mipmapEnabled = true;
        }
    }
}
