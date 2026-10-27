using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Keep each original enemy material bound to its exported native diffuse map.</summary>
    public sealed class EnemyMaterialImport : AssetPostprocessor
    {
        /// <summary>Keep model materials embedded so names shared by other enemies cannot redirect them.</summary>
        private void OnPreprocessModel()
        {
            if (!assetPath.Contains("/LegacyEnemies/")) return;
            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        /// <summary>Apply the exact MTL diffuse references after Unity creates the model's materials.</summary>
        private void OnPostprocessModel(GameObject model)
        {
            if (!assetPath.Contains("/LegacyEnemies/")) return;
            string mtl = Path.ChangeExtension(assetPath, ".mtl");
            if (!File.Exists(mtl)) return;
            var textures = new Dictionary<string, string>();
            string name = "";
            foreach (string raw in File.ReadLines(mtl))
            {
                string line = raw.Trim();
                if (line.StartsWith("newmtl ")) name = line.Substring(7);
                else if (line.StartsWith("map_Kd ") && name.Length > 0)
                    textures[name] = line.Substring(7).Trim();
            }
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || !textures.TryGetValue(material.name, out string texture)) continue;
                    string path = (Path.GetDirectoryName(assetPath) + "/" + texture).Replace('\\', '/');
                    material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    material.color = Color.white;
                }
        }
    }
}
