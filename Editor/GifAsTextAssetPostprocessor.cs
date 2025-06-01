// Assets/Editor/GifDuplicatePostprocessor.cs
using UnityEditor;
using UnityEngine;
using System.IO;

public class GifDuplicatePostprocessor : AssetPostprocessor
{
    // Called after any assets are imported, deleted, or moved.
    static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromPaths)
    {
        // Determine the project root (parent of "Assets")
        string projectRoot = Path.GetDirectoryName(Application.dataPath);

        foreach (string assetPath in importedAssets)
        {
            // If a .gif was just imported (or reimported)
            if (assetPath.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase))
            {
                // Full filesystem path of the original .gif
                string fullGifPath = Path.Combine(projectRoot, assetPath);

                // Target path for the duplicated ".gif.bytes" file
                string fullBytesPath = fullGifPath + ".bytes";
                // Corresponding Unity AssetDatabase path: "Assets/…/filename.gif.bytes"
                string bytesAssetPath = assetPath + ".bytes";

                // If the ".gif.bytes" copy does not already exist on disk, create it:
                if (!File.Exists(fullBytesPath))
                {
                    // Copy the raw .gif data to "…/filename.gif.bytes"
                    File.Copy(fullGifPath, fullBytesPath);

                    // Tell Unity to import that new .bytes file as an asset
                    AssetDatabase.ImportAsset(bytesAssetPath);
                }
            }
        }
    }
}
