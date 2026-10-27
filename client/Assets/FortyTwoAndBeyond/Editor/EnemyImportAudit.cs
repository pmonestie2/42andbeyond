using System;
using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Verify the server's original enemy catalog before building the player.</summary>
    public static class EnemyImportAudit
    {
        /// <summary>Reject missing meshes and report imported material coverage, then build macOS.</summary>
        public static void VerifyAndBuildMac()
        {
            foreach (string path in Directory.GetFiles("Assets/FortyTwoAndBeyond/Resources/LegacyEnemies", "*.obj"))
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TextAsset catalog = Resources.Load<TextAsset>("LegacyEnemies/Catalog");
            if (catalog == null) throw new InvalidOperationException("Original enemy catalog is missing.");
            int mapped = 0, textured = 0;
            foreach (string row in catalog.text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                string[] fields = row.Trim().Split('\t');
                GameObject asset = Resources.Load<GameObject>("LegacyEnemies/" + fields[1]);
                if (asset == null || !asset.GetComponentsInChildren<MeshFilter>(true)
                    .Any(filter => filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0))
                    throw new InvalidOperationException("Enemy model has no imported mesh: " + row);
                mapped++;
                if (asset.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => renderer.sharedMaterials)
                    .Any(material => material != null && material.mainTexture != null)) textured++;
                else Debug.LogWarning("Original enemy model has no diffuse texture binding: " + row);
            }
            if (mapped != 139) throw new InvalidOperationException("Expected 139 native enemy model mappings, got " + mapped);
            Debug.Log("ENEMY_IMPORT_PASS: " + mapped + " model IDs, " + textured + " with original diffuse textures.");
            BuildClient.BuildMac();
        }
    }
}
