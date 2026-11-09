// PackageImporterDllConfig.cs
// 焦糖工具箱 · 静默导入 —— 把 SharpCompress 相关 DLL 标记为 Editor-only。
// 这些 DLL 只用于编辑器里解压压缩包（zip/rar/7z），不应打进 Avatar 上传构建，
// 因此统一设为「仅编辑器平台」并关闭在其它平台上的引用。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>编辑器初始化时，把 PackageImporter/Plugins 下的 DLL 限定为 Editor 平台。</summary>
    [InitializeOnLoad]
    public static class PackageImporterDllConfig
    {
        private static readonly string[] DllNames =
        {
            "SharpCompress.dll",
        };

        static PackageImporterDllConfig()
        {
            EditorApplication.delayCall += Configure;
        }

        private static void Configure()
        {
            foreach (string dllName in DllNames)
            {
                string[] guids = AssetDatabase.FindAssets("t:Plugin " + dllName);
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!path.Contains("PackageImporter")) continue;

                    PluginImporter importer = AssetImporter.GetAtPath(path) as PluginImporter;
                    if (importer == null) continue;

                    bool changed = false;
                    if (importer.GetCompatibleWithEditor() != true)
                    {
                        importer.SetCompatibleWithEditor(true);
                        changed = true;
                    }

                    // 关闭所有非编辑器平台，确保不打进运行时/上传构建。
                    if (importer.GetCompatibleWithAnyPlatform() != false)
                    {
                        importer.SetCompatibleWithAnyPlatform(false);
                        changed = true;
                    }

                    if (changed)
                    {
                        importer.SaveAndReimport();
                    }
                }
            }
        }
    }
}
#endif
