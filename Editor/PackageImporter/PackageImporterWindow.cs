// PackageImporterWindow.cs
// 焦糖工具箱 · 静默导入 —— 把 .unitypackage 拖入窗口即后台静默导入，无需 Unity 默认的导入对话框。
// 放置路径：Assets/CaramelToolbox/Editor/PackageImporter/PackageImporterWindow.cs
//
// 行为：
//   1. 顶部拖入框：把 .unitypackage 文件拖入后，自动在后台解包并导入到 Assets；
//   2. 下方日志：列出每次导入的时间、导入到了哪些文件夹（多个文件夹逐个列出）；
//   3. 日志同时追加写入 CaramelToolbox 下的 log 文件（Assets/CaramelToolbox/log/package_import.log）；
//   4. 全程静默：不弹出 Unity 默认的 Import Unity Package 对话框，导入完成仅在窗口日志里体现。
//
// 静默导入原理：AssetDatabase.ImportPackage(path, interactive=false) —— interactive 传 false
// 时 Unity 直接按包内记录的路径导入，跳过选择/确认对话框，也不会弹出进度窗。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>静默导入 unitypackage 的窗口：拖入即导入，日志列出导入时间与目标文件夹。</summary>
    public class PackageImporterWindow : EditorWindow
    {
        private const string MenuPath = "焦糖工具箱/静默导入";

        /// <summary>一条导入日志：标题文本 + 可点击跳转的文件夹列表。</summary>
        private class LogEntry
        {
            public string Line;              // 完整文本（落盘用，含时间与包名）
            public List<string> Directories; // 可点击的文件夹（相对工程路径，如 Assets/xxx）
        }

        /// <summary>日志文件路径（相对工程，落盘在 CaramelToolbox/log 下）。</summary>
        private static readonly string LogRelativeDir = "Assets/CaramelToolbox/log";
        private static readonly string LogRelativePath = LogRelativeDir + "/package_import.log";

        // ---- 日志内存缓存（窗口显示用，倒序：最新在上） ----
        private readonly List<LogEntry> _logEntries = new List<LogEntry>();

        private Vector2 _scroll = Vector2.zero;
        private string _dropPath = string.Empty;
        private bool _importing;

        // ---- 本次拖入伴随的图片（待导入后复制到对应文件夹） ----
        private readonly List<string> _pendingImages = new List<string>();

        [MenuItem(MenuPath, false, 101)]
        private static void Open()
        {
            PackageImporterWindow window = GetWindow<PackageImporterWindow>(false, "静默导入", true);
            window.minSize = new Vector2(420f, 360f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("静默导入");
            LoadExistingLog();
        }

        private void OnGUI()
        {
            CaramelUI.DrawTitle("静默导入", null);
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawDropArea();
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("导入日志", EditorStyles.boldLabel);
                DrawLog();
            }
        }

        // --------------------------------------------------------------------
        // 拖入区
        // --------------------------------------------------------------------

        private void DrawDropArea()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0, 72, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, string.Empty, EditorStyles.helpBox);

            string hint = _importing
                ? "正在导入 " + Path.GetFileName(_dropPath) + " …"
                : "拖入 .unitypackage / zip / rar / 7z 压缩包；可同时拖入图片，导入后一并放入对应文件夹";

            GUIStyle labelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
            };
            GUI.Label(dropRect, hint, labelStyle);

            // 处理拖入事件。
            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropRect.Contains(evt.mousePosition)) break;

                    if (TryGetDroppedFiles(out string directPackage, out List<string> archives, out List<string> images))
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        if (evt.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            HandleDrop(directPackage, archives, images);
                        }
                    }
                    break;
            }
        }

        /// <summary>处理一次拖入：直接 .unitypackage 立即导入；压缩包则列包内包让用户勾选。</summary>
        private void HandleDrop(string directPackage, List<string> archives, List<string> images)
        {
            // 记录本次拖入伴随的图片，导入完成后复制到对应文件夹。
            _pendingImages.Clear();
            if (images != null) _pendingImages.AddRange(images);

            // 优先处理直接的 .unitypackage。
            if (!string.IsNullOrEmpty(directPackage))
            {
                _dropPath = directPackage;
                List<string> dirs = new List<string>();
                ImportPackage(directPackage, dirs);
                CopyPendingImages(dirs);
                _pendingImages.Clear();
                return;
            }

            // 压缩包：解压收集里面的所有 .unitypackage。
            List<string> packages = new List<string>();
            foreach (string archive in archives)
            {
                try
                {
                    packages.AddRange(ExtractUnitypackagesFromArchive(archive));
                }
                catch (Exception e)
                {
                    AppendLog("解压 " + Path.GetFileName(archive) + " 失败：" + e.Message, true);
                }
            }

            if (packages.Count == 0)
            {
                AppendLog("未在压缩包中找到任何 .unitypackage 文件", true);
                return;
            }

            // 只有一个包：直接导入，不弹选择窗；多个包才弹窗让用户勾选。
            if (packages.Count == 1)
            {
                OnPackagesSelected(packages);
                return;
            }

            // 弹窗让用户勾选导入哪些。
            PackageSelectWindow.Show(packages, OnPackagesSelected);
        }

        /// <summary>用户在弹窗里确认后回调：逐个导入勾选的包，最后统一复制图片并刷一次资源库。</summary>
        private void OnPackagesSelected(List<string> packages)
        {
            if (packages == null || packages.Count == 0) return;

            // 收集本次导入涉及的所有目标目录，最后统一复制图片。
            List<string> allDirs = new List<string>();

            // 批量导入提速的关键：
            //   1) LockReloadAssemblies 阻止每导入一个含脚本的包就重新编译脚本（这是最慢的部分）；
            //   2) StartAssetEditing 抑制中间多次刷新，最后统一刷一次。
            bool locked = false;
            try
            {
                EditorApplication.LockReloadAssemblies();
                locked = true;
                AssetDatabase.StartAssetEditing();
            }
            catch
            {
                // 若环境不支持，忽略，退回普通导入。
            }

            try
            {
                foreach (string pkg in packages)
                {
                    if (!_importing)
                    {
                        _dropPath = pkg;
                        ImportPackage(pkg, allDirs);
                    }
                }
            }
            finally
            {
                try
                {
                    AssetDatabase.StopAssetEditing();
                }
                catch
                {
                }

                if (locked)
                {
                    try
                    {
                        EditorApplication.UnlockReloadAssemblies();
                    }
                    catch
                    {
                    }
                }
            }

            // 全部导入完成后，统一复制伴随图片（只刷一次资源库）。
            CopyPendingImages(allDirs);
            _pendingImages.Clear();
        }

        /// <summary>
        /// 从当前拖入对象里拆出三类结果：
        /// directPackage —— 直接拖入的单个 .unitypackage；archives —— 需要解压的压缩包路径；
        /// images —— 伴随拖入的图片文件路径。
        /// </summary>
        private static bool TryGetDroppedFiles(out string directPackage, out List<string> archives, out List<string> images)
        {
            directPackage = null;
            archives = new List<string>();
            images = new List<string>();

            string[] paths = DragAndDrop.paths;
            if (paths == null || paths.Length == 0) return false;

            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;

                string ext = Path.GetExtension(p);
                if (string.Equals(ext, ".unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    directPackage = p;
                }
                else if (IsArchiveExtension(ext))
                {
                    archives.Add(p);
                }
                else if (IsImageExtension(ext))
                {
                    images.Add(p);
                }
            }

            return directPackage != null || archives.Count > 0 || images.Count > 0;
        }

        /// <summary>判断扩展名是否是可解压的压缩包（zip / rar / 7z / tar / gz）。</summary>
        private static bool IsArchiveExtension(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".zip":
                case ".rar":
                case ".7z":
                case ".tar":
                case ".gz":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>判断扩展名是否是常见图片格式。</summary>
        private static bool IsImageExtension(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".gif":
                case ".tga":
                case ".psd":
                case ".exr":
                case ".tif":
                case ".tiff":
                    return true;
                default:
                    return false;
            }
        }

        // --------------------------------------------------------------------
        // 导入
        // --------------------------------------------------------------------

        private void ImportPackage(string packagePath, List<string> allDirs = null)
        {
            if (_importing) return;
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
            {
                AppendLog("导入失败：找不到文件 " + packagePath, true);
                return;
            }

            _importing = true;
            Repaint();

            try
            {
                // interactive=false：跳过 Unity 默认的导入选择/确认对话框，静默按包内路径导入。
                AssetDatabase.ImportPackage(packagePath, false);

                // 解析包内资源路径，统计导入到了哪些文件夹（忽略 Packages/ 与 .meta 条目）。
                List<string> dirs = CollectImportedDirectories(packagePath);

                // 目录累积到 allDirs，供批量导入后统一复制图片。
                if (allDirs != null)
                {
                    foreach (string d in dirs)
                    {
                        if (!allDirs.Contains(d)) allDirs.Add(d);
                    }
                }

                AppendImportLog(packagePath, dirs);
            }
            catch (Exception e)
            {
                AppendLog("导入 " + Path.GetFileName(packagePath) + " 失败：" + e.Message, true);
                Debug.LogException(e);
            }
            finally
            {
                _importing = false;
                _dropPath = string.Empty;
                Repaint();
            }
        }

        /// <summary>
        /// 把伴随拖入的图片复制到导入的「最上层目录」下。
        /// 规则：图片放到 Assets/<导入的最上层目录>/ 里；多个顶层目录则每处放一份。
        /// </summary>
        private void CopyPendingImages(List<string> dirs)
        {
            if (_pendingImages.Count == 0) return;
            if (dirs == null || dirs.Count == 0) return;

            // 把一堆深层目录归约成「最上层目录」（如 Assets/A/B、Assets/A/C → Assets/A）。
            List<string> topDirs = CollectTopLevelDirectories(dirs);

            foreach (string imagePath in _pendingImages)
            {
                if (!File.Exists(imagePath)) continue;

                string fileName = Path.GetFileName(imagePath);
                foreach (string topDir in topDirs)
                {
                    try
                    {
                        // topDir 形如 "Assets/xxx"，去掉开头的 "Assets/" 后拼到 dataPath（工程 Assets 根）。
                        string rel = topDir;
                        if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        {
                            rel = rel.Substring("Assets/".Length);
                        }
                        else if (rel.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                        {
                            rel = string.Empty;
                        }

                        string absDir = Path.Combine(Application.dataPath, rel);
                        Directory.CreateDirectory(absDir);
                        string dest = Path.Combine(absDir, fileName);
                        File.Copy(imagePath, dest, true);
                        Debug.Log("[静默导入] 已复制图片 " + fileName + " → " + topDir + "/");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[静默导入] 复制图片 " + fileName + " 到 " + topDir + " 失败：" + e.Message);
                    }
                }
            }

            // 最后统一刷一次资源库，让新图片进入 Unity（避免每包都刷）。
            AssetDatabase.Refresh();
        }

        /// <summary>把目录列表归约成「最上层目录」，去重。</summary>
        private static List<string> CollectTopLevelDirectories(List<string> dirs)
        {
            SortedSet<string> tops = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;

                string d = dir.Replace('\\', '/').TrimEnd('/');

                // 取 "Assets" 后的第一段作为最上层目录。
                // 例如 Assets/A/B → Assets/A；Assets → Assets。
                string top;
                if (d.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    top = "Assets";
                }
                else if (d.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = d.Substring("Assets/".Length);
                    int slash = rest.IndexOf('/');
                    string first = slash >= 0 ? rest.Substring(0, slash) : rest;
                    top = "Assets/" + first;
                }
                else
                {
                    // 不在 Assets 下的目录（理论上不会出现），保留原样。
                    top = d;
                }

                tops.Add(top);
            }

            return new List<string>(tops);
        }

        /// <summary>
        /// 解析 .unitypackage（gzip + tar）里的条目，提取资源落到的 Assets 目录集合。
        /// 包内所有资源都在 Assets/<sub> 下；这里只取每个资源路径去掉文件名后的目录，
        /// 并去掉 "Packages/" 开头的条目与 .meta 文件条目。
        /// </summary>
        private static List<string> CollectImportedDirectories(string packagePath)
        {
            SortedSet<string> dirs = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string assetPath in ExtractAssetPaths(packagePath))
            {
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) continue;
                if (assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                string dir = Path.GetDirectoryName(assetPath);
                if (string.IsNullOrEmpty(dir)) continue;

                // 统一用正斜杠，避免 Windows 反斜杠影响显示。
                dir = dir.Replace('\\', '/');
                dirs.Add(dir);
            }

            return new List<string>(dirs);
        }

        /// <summary>解包 .unitypackage 并返回包内记录的资产路径列表（相对路径，含 Assets/...）。</summary>
        private static List<string> ExtractAssetPaths(string packagePath)
        {
            List<string> paths = new List<string>();

            try
            {
                // .unitypackage 实质是 gzip 压缩的 tar 归档，每条资产以 <guid>/pathname 形式记录路径。
                using (FileStream fs = File.OpenRead(packagePath))
                using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
                {
                    ReadTarEntries(gz, paths);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[静默导入] 解析 unitypackage 目录失败：" + e.Message);
            }

            return paths;
        }

        /// <summary>
        /// 解压一个压缩包（zip / 7z / tar / gz / rar），返回包内所有 .unitypackage 的绝对路径。
        /// 支持嵌套压缩包（如 7z 里再包 zip）：递归解压直到找到 .unitypackage。
        /// 使用 SharpCompress 纯 C# 库；7z 走 SevenZipArchive.Open，其余走 ReaderFactory.Open。
        /// </summary>
        private static List<string> ExtractUnitypackagesFromArchive(string archivePath)
        {
            string extractDir = Path.Combine(Path.GetTempPath(), "CaramelPackageImport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);

            try
            {
                ExtractArchiveRecursive(archivePath, extractDir, 0);
                return CollectPackages(extractDir);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[静默导入] 解压失败：" + e.Message);
                throw;
            }
        }

        /// <summary>
        /// 递归解压：把 archivePath 解到 targetDir；对解出的嵌套压缩包（zip/7z/rar/tar/gz）继续递归。
        /// depth 限制递归深度，防止恶意嵌套死循环。
        /// </summary>
        private static void ExtractArchiveRecursive(string archivePath, string targetDir, int depth)
        {
            if (depth > 8)
            {
                Debug.LogWarning("[静默导入] 嵌套压缩层级过深，停止继续解压：" + archivePath);
                return;
            }

            string ext = Path.GetExtension(archivePath).ToLowerInvariant();

            // 7z 必须用 SevenZipArchive.Open（ReaderFactory 的流式探测不支持 7z）。
            if (ext == ".7z")
            {
                ExtractSevenZip(archivePath, targetDir, depth);
            }
            else
            {
                ExtractViaReaderFactory(archivePath, targetDir, depth);
            }
        }

        /// <summary>用 ReaderFactory 解 zip / tar / gz / rar，并递归处理嵌套压缩包。</summary>
        private static void ExtractViaReaderFactory(string archivePath, string targetDir, int depth)
        {
            List<string> nested = new List<string>();

            using (Stream stream = File.OpenRead(archivePath))
            using (SharpCompress.Readers.IReader reader =
                SharpCompress.Readers.ReaderFactory.Open(stream, new SharpCompress.Readers.ReaderOptions()))
            {
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory) continue;

                    string entryPath = SanitizeEntryPath(reader.Entry.Key);
                    string fullPath = Path.Combine(targetDir, entryPath);
                    string dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    using (SharpCompress.Common.EntryStream entryStream = reader.OpenEntryStream())
                    using (FileStream outStream = File.Create(fullPath))
                    {
                        entryStream.CopyTo(outStream);
                    }

                    if (IsArchiveExtension(Path.GetExtension(fullPath)))
                    {
                        nested.Add(fullPath);
                    }
                }
            }

            // 递归解压嵌套压缩包。
            foreach (string n in nested)
            {
                string subDir = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(n) + "_extracted");
                Directory.CreateDirectory(subDir);
                ExtractArchiveRecursive(n, subDir, depth + 1);
            }
        }

        /// <summary>用 SevenZipArchive 解 7z，并递归处理嵌套压缩包。</summary>
        private static void ExtractSevenZip(string archivePath, string targetDir, int depth)
        {
            List<string> nested = new List<string>();

            using (Stream stream = File.OpenRead(archivePath))
            using (SharpCompress.Archives.SevenZip.SevenZipArchive archive =
                SharpCompress.Archives.SevenZip.SevenZipArchive.Open(stream, new SharpCompress.Readers.ReaderOptions()))
            {
                foreach (SharpCompress.Archives.SevenZip.SevenZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;

                    string entryPath = SanitizeEntryPath(entry.Key);
                    string fullPath = Path.Combine(targetDir, entryPath);
                    string dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    using (Stream entryStream = entry.OpenEntryStream())
                    using (FileStream outStream = File.Create(fullPath))
                    {
                        entryStream.CopyTo(outStream);
                    }

                    if (IsArchiveExtension(Path.GetExtension(fullPath)))
                    {
                        nested.Add(fullPath);
                    }
                }
            }

            // 递归解压嵌套压缩包。
            foreach (string n in nested)
            {
                string subDir = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(n) + "_extracted");
                Directory.CreateDirectory(subDir);
                ExtractArchiveRecursive(n, subDir, depth + 1);
            }
        }

        /// <summary>收集解压目录下所有 .unitypackage 的绝对路径。</summary>
        private static List<string> CollectPackages(string extractDir)
        {
            List<string> packages = new List<string>();
            foreach (string file in Directory.GetFiles(extractDir, "*.unitypackage", SearchOption.AllDirectories))
            {
                packages.Add(file);
            }

            return packages;
        }

        /// <summary>清理压缩包内条目路径，去掉盘符与上级目录，防止解压时路径穿越。</summary>
        private static string SanitizeEntryPath(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath)) return entryPath;

            string path = entryPath.Replace('\\', '/');

            // 去掉盘符（如 C:/）。
            int colon = path.IndexOf(':');
            if (colon >= 0) path = path.Substring(colon + 1);

            // 去掉开头的斜杠和 ".."，防止逃出解压目录。
            path = path.TrimStart('/');
            while (path.StartsWith("../", StringComparison.Ordinal))
            {
                path = path.Substring(3);
            }

            return path;
        }

        /// <summary>从 tar 流里读出每个文件的路径名（tar 头 0~99 字节为 name 字段）。</summary>
        private static void ReadTarEntries(Stream stream, List<string> paths)
        {
            byte[] header = new byte[512];

            while (true)
            {
                int read = stream.Read(header, 0, 512);
                if (read <= 0) break;
                if (read < 512) break;

                // tar 头全是 0 表示归档结束。
                bool allZero = true;
                for (int i = 0; i < 512; i++)
                {
                    if (header[i] != 0) { allZero = false; break; }
                }
                if (allZero) break;

                string name = ReadTarString(header, 0, 100);
                if (string.IsNullOrEmpty(name)) break;

                // 每个 tar 条目都有 pathname 文件（形如 <guid>/pathname），里面是真实资产路径。
                if (name.EndsWith("/pathname", StringComparison.OrdinalIgnoreCase) ||
                    name == "pathname")
                {
                    long size = ReadTarOctal(header, 124, 12);
                    if (size > 0 && size < 4096)
                    {
                        byte[] content = new byte[size];
                        int got = 0;
                        while (got < size)
                        {
                            int n = stream.Read(content, got, (int)(size - got));
                            if (n <= 0) break;
                            got += n;
                        }

                        string assetPath = Encoding.UTF8.GetString(content, 0, got).Trim('\0', '\r', '\n', ' ');
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            paths.Add(assetPath);
                        }
                    }
                }

                // 跳过数据块，按 512 对齐。
                long dataSize = ReadTarOctal(header, 124, 12);
                long padded = ((dataSize + 511) / 512) * 512;
                SkipBytes(stream, padded);
            }
        }

        private static string ReadTarString(byte[] header, int offset, int length)
        {
            int end = offset;
            for (int i = offset; i < offset + length && i < header.Length; i++)
            {
                if (header[i] == 0) break;
                end = i + 1;
            }

            return Encoding.UTF8.GetString(header, offset, end - offset);
        }

        private static long ReadTarOctal(byte[] header, int offset, int length)
        {
            long value = 0;
            for (int i = offset; i < offset + length && i < header.Length; i++)
            {
                byte b = header[i];
                if (b == 0 || b == ' ') continue;
                if (b < '0' || b > '7') break;
                value = value * 8 + (b - '0');
            }

            return value;
        }

        private static void SkipBytes(Stream stream, long count)
        {
            long remaining = count;
            byte[] buffer = new byte[8192];
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int n = stream.Read(buffer, 0, toRead);
                if (n <= 0) break;
                remaining -= n;
            }
        }

        // --------------------------------------------------------------------
        // 日志
        // --------------------------------------------------------------------

        /// <summary>拼一条导入日志：时间 + 包名 + 导入到哪些文件夹，并写入结构化日志（文件夹可点击）。</summary>
        private void AppendImportLog(string packagePath, List<string> dirs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
            sb.Append("导入 ").Append(Path.GetFileName(packagePath)).Append(" → ");

            LogEntry entry = new LogEntry { Directories = new List<string>() };

            if (dirs.Count == 0)
            {
                sb.Append("未识别到 Assets 目录");
            }
            else
            {
                sb.Append(dirs.Count).Append(" 个文件夹");
                foreach (string d in dirs)
                {
                    sb.AppendLine();
                    sb.Append("    · ").Append(d);
                    entry.Directories.Add(d);
                }
            }

            entry.Line = sb.ToString();
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > 500) _logEntries.RemoveRange(500, _logEntries.Count - 500);

            WriteLogToFile(entry.Line);
            Repaint();
        }

        /// <summary>追加一条普通日志（无文件夹）到内存列表与 log 文件。</summary>
        private void AppendLog(string message, bool isError)
        {
            LogEntry entry = new LogEntry { Line = message, Directories = new List<string>() };
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > 500) _logEntries.RemoveRange(500, _logEntries.Count - 500);

            WriteLogToFile(message);
            Repaint();
        }

        /// <summary>把单条日志追加写入 CaramelToolbox/log/package_import.log。</summary>
        private static void WriteLogToFile(string line)
        {
            try
            {
                string fullDir = Path.GetFullPath(LogRelativeDir);
                Directory.CreateDirectory(fullDir);
                string fullPath = Path.GetFullPath(LogRelativePath);
                File.AppendAllText(fullPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[静默导入] 写入日志文件失败：" + e.Message);
            }
        }

        /// <summary>窗口打开时把已有日志读进内存显示。</summary>
        private void LoadExistingLog()
        {
            _logEntries.Clear();

            try
            {
                string fullPath = Path.GetFullPath(LogRelativePath);
                if (!File.Exists(fullPath)) return;

                string[] lines = File.ReadAllLines(fullPath, Encoding.UTF8);

                // 落盘格式（正序，旧→新）：标题行以 "[" 开头，其后紧跟 "    · <dir>" 文件夹行。
                // 正序解析：遇标题新建条目，遇文件夹行挂到当前最新条目；最后反转成最新在上。
                List<LogEntry> ordered = new List<LogEntry>();
                LogEntry current = null;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("·", StringComparison.Ordinal))
                    {
                        // 文件夹行：挂到当前标题。
                        string dir = trimmed.Substring(1).Trim();
                        if (current != null && !string.IsNullOrEmpty(dir))
                        {
                            current.Directories.Add(dir);
                        }
                    }
                    else
                    {
                        // 标题行：新建条目。
                        current = new LogEntry { Line = line, Directories = new List<string>() };
                        ordered.Add(current);
                    }
                }

                // 反转成最新在上。
                for (int i = ordered.Count - 1; i >= 0; i--)
                {
                    _logEntries.Add(ordered[i]);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[静默导入] 读取日志文件失败：" + e.Message);
            }
        }

        private void DrawLog()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            if (_logEntries.Count == 0)
            {
                EditorGUILayout.LabelField("暂无导入记录", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                GUIStyle linkStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(0.35f, 0.68f, 0.98f) },
                };

                foreach (LogEntry entry in _logEntries)
                {
                    // 标题行：时间 + 包名。
                    string firstLine = entry.Line;
                    int newline = firstLine.IndexOf('\n');
                    if (newline >= 0) firstLine = firstLine.Substring(0, newline);
                    EditorGUILayout.LabelField(firstLine, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));

                    // 文件夹行：可点击跳转。
                    foreach (string dir in entry.Directories)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(16);
                            if (GUILayout.Button(dir, linkStyle, GUILayout.ExpandWidth(true)))
                            {
                                RevealDirectory(dir);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>在 Unity Project 窗口里定位到指定目录（相对工程路径，如 Assets/xxx）。</summary>
        private static void RevealDirectory(string relativeDir)
        {
            if (string.IsNullOrEmpty(relativeDir)) return;

            try
            {
                // 统一用正斜杠，且确保是 Assets 下的相对路径。
                string assetPath = relativeDir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(assetPath)) return;

                // Unity 里文件夹是 DefaultAsset，用 LoadAssetAtPath 拿目录对象再 Ping。
                UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(assetPath);
                if (folder == null)
                {
                    Debug.LogWarning("[静默导入] 目录不存在：" + assetPath);
                    return;
                }

                Selection.activeObject = folder;
                EditorGUIUtility.PingObject(folder);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[静默导入] 在 Project 中定位目录失败：" + e.Message);
            }
        }
    }

    /// <summary>
    /// 压缩包内 unitypackage 多选弹窗：列出包内所有 .unitypackage 让用户勾选要导入哪些。
    /// 文件名含 material / tex 等字样的高亮并排在最上面，每行右侧标注文件大小。
    /// </summary>
    public class PackageSelectWindow : EditorWindow
    {
        private List<string> _packages;
        private List<bool> _selected;
        private List<bool> _highlight; // 是否高亮（文件名含 material/tex 等关键字）
        private Action<List<string>> _onConfirm;
        private Vector2 _scroll;

        /// <summary>文件名里命中这些关键字则高亮并置顶（不区分大小写）。</summary>
        private static readonly string[] HighlightKeywords =
        {
            "material", "tex", "texture", "mat",
        };

        /// <summary>显示弹窗；用户确认后把勾选的包路径通过 onConfirm 回调返回。</summary>
        public static void Show(List<string> packages, Action<List<string>> onConfirm)
        {
            if (packages == null || packages.Count == 0) return;

            // 先排序：命中的排最上面（命中之间保持原顺序），未命中按原顺序排后面。
            List<string> ordered = new List<string>();
            foreach (string p in packages)
            {
                if (IsHighlighted(p)) ordered.Add(p);
            }
            foreach (string p in packages)
            {
                if (!IsHighlighted(p)) ordered.Add(p);
            }

            PackageSelectWindow window = GetWindow<PackageSelectWindow>(true, "选择要导入的包");
            window.minSize = new Vector2(480f, 340f);
            window._packages = ordered;
            window._selected = new List<bool>();
            window._highlight = new List<bool>();
            for (int i = 0; i < ordered.Count; i++)
            {
                window._selected.Add(true);
                window._highlight.Add(IsHighlighted(ordered[i]));
            }
            window._onConfirm = onConfirm;
            window.Show();
        }

        /// <summary>文件名是否命中高亮关键字。</summary>
        private static bool IsHighlighted(string packagePath)
        {
            string name = Path.GetFileName(packagePath).ToLowerInvariant();
            foreach (string kw in HighlightKeywords)
            {
                if (name.Contains(kw)) return true;
            }
            return false;
        }

        /// <summary>把字节数格式化成可读大小（B / KB / MB / GB）。</summary>
        private static string FormatSize(string packagePath)
        {
            try
            {
                long bytes = new FileInfo(packagePath).Length;
                if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB";
                if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
                if (bytes >= 1024) return (bytes / 1024.0).ToString("F0") + " KB";
                return bytes + " B";
            }
            catch
            {
                return string.Empty;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("压缩包内找到 " + _packages.Count + " 个 .unitypackage：", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            GUIStyle highlightStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.91f, 0.64f, 0.29f) }, // 焦糖色高亮
            };
            GUIStyle normalStyle = EditorStyles.label;
            GUIStyle sizeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _packages.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string fileName = Path.GetFileName(_packages[i]);
                    string size = FormatSize(_packages[i]);

                    _selected[i] = EditorGUILayout.ToggleLeft(fileName, _selected[i],
                        _highlight[i] ? highlightStyle : normalStyle);

                    if (!string.IsNullOrEmpty(size))
                    {
                        GUILayout.Label(size, sizeStyle, GUILayout.Width(72));
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全选", GUILayout.Width(60)))
                {
                    for (int i = 0; i < _selected.Count; i++) _selected[i] = true;
                }

                if (GUILayout.Button("全不选", GUILayout.Width(60)))
                {
                    for (int i = 0; i < _selected.Count; i++) _selected[i] = false;
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("取消", GUILayout.Width(70)))
                {
                    Close();
                }

                if (CaramelUI.CaramelButton("导入选中", 26, GUILayout.Width(100)))
                {
                    List<string> chosen = new List<string>();
                    for (int i = 0; i < _packages.Count; i++)
                    {
                        if (_selected[i]) chosen.Add(_packages[i]);
                    }

                    Close();
                    if (_onConfirm != null) _onConfirm(chosen);
                }
            }
        }
    }
}
#endif
