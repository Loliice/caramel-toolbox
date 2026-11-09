// ImageGalleryWindow.cs
// 焦糖工具箱 · 商品图预览 —— 扫描 Assets 下每个第一层商品目录里的预览图，用自适应网格展示。
// 放置路径：Assets/CaramelToolbox/Editor/ImageGallery/ImageGalleryWindow.cs
//
// 背景：静默导入插件会把商品预览图（png/jpg）复制到「Assets/<商品目录>/」这一层。
//       本插件把这些散落在各商品目录第一层的图片汇总成网格列表，方便浏览与定位。
//
// 设计：
//   - 扫描范围：Assets 下每个第一层子目录的第一层图片（不递归，避免误扫贴图资源）。
//   - 自适应网格：列数随窗口宽度自动调整（每格约 160px）。
//   - 点击缩略图：在 Project 窗口定位到对应图片资源。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>商品图预览窗口：自适应网格展示 Assets 各商品目录第一层的图片。</summary>
    public class ImageGalleryWindow : EditorWindow
    {
        private const string MenuPath = "焦糖工具箱/商品图预览";
        private const string ExcludePrefKey = "CaramelToolbox.ImageGallery.ExcludedFolders";
        private const string ThumbPrefKey = "CaramelToolbox.ImageGallery.ThumbSize";

        private const float MinThumbSize = 80f;    // 缩略图最小边长
        private const float MaxThumbSize = 256f;   // 缩略图最大边长
        private const float DefaultThumbSize = 150f;

        // 缩略图边长（用户可调，持久化到 EditorPrefs）。
        private float _thumbSize = DefaultThumbSize;

        // 每格基准宽度 = 缩略图 + 边距（用于自适应列数）。
        private float CellWidth { get { return _thumbSize + 12f; } }

        // 扫描结果。
        private readonly List<ImageEntry> _entries = new List<ImageEntry>();

        // 被排除的目录名集合（持久化到 EditorPrefs）。
        private readonly HashSet<string> _excludedFolders = new HashSet<string>();

        private Vector2 _scroll;

        private class ImageEntry
        {
            public string AssetPath;   // 资源相对路径（Assets/xxx/yyy.png）
            public string FileName;    // 文件名
            public string FolderName;  // 所在第一层目录名
            public Texture2D Thumb;    // 缓存的缩略图
        }

        [MenuItem(MenuPath, false, 104)]
        private static void Open()
        {
            ImageGalleryWindow win = GetWindow<ImageGalleryWindow>("商品图预览");
            win.minSize = new Vector2(520, 360);
            win.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("商品图预览");
            LoadThumbSize();
            LoadExcludedFolders();
            Scan();
        }

        private void OnGUI()
        {
            CaramelUI.DrawTitle("商品图预览", "Assets 各商品目录第一层的预览图");
            EditorGUILayout.Space(6);

            DrawToolbar();
            EditorGUILayout.Space(4);

            DrawGrid();
        }

        /// <summary>从 EditorPrefs 读回缩略图大小。</summary>
        private void LoadThumbSize()
        {
            _thumbSize = EditorPrefs.GetFloat(ThumbPrefKey, DefaultThumbSize);
            _thumbSize = Mathf.Clamp(_thumbSize, MinThumbSize, MaxThumbSize);
        }

        /// <summary>从 EditorPrefs 读回被排除的目录。</summary>
        private void LoadExcludedFolders()
        {
            _excludedFolders.Clear();
            string saved = EditorPrefs.GetString(ExcludePrefKey, string.Empty);
            if (string.IsNullOrEmpty(saved)) return;

            foreach (string part in saved.Split('\n'))
            {
                string p = part.Trim();
                if (!string.IsNullOrEmpty(p)) _excludedFolders.Add(p);
            }
        }

        /// <summary>把被排除的目录写回 EditorPrefs。</summary>
        private void SaveExcludedFolders()
        {
            EditorPrefs.SetString(ExcludePrefKey, string.Join("\n", _excludedFolders));
        }

        /// <summary>供设置窗口读取当前排除的目录集合。</summary>
        public HashSet<string> ExcludedFolders
        {
            get { return _excludedFolders; }
        }

        /// <summary>供设置窗口写回新的排除目录集合（并持久化）。</summary>
        public void SetExcludedFolders(HashSet<string> folders)
        {
            _excludedFolders.Clear();
            if (folders != null)
            {
                foreach (string f in folders) _excludedFolders.Add(f);
            }
            SaveExcludedFolders();
        }

        /// <summary>重新扫描（供设置窗口确认后调用）。</summary>
        public void Rescan()
        {
            Scan();
            Repaint();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("共 " + _entries.Count + " 张图片", EditorStyles.miniLabel, GUILayout.Width(100));
                GUILayout.FlexibleSpace();

                // 缩略图大小滑条。
                EditorGUILayout.LabelField("大小", EditorStyles.miniLabel, GUILayout.Width(28));
                float newSize = GUILayout.HorizontalSlider(_thumbSize, MinThumbSize, MaxThumbSize, GUILayout.Width(120));
                if (Mathf.Abs(newSize - _thumbSize) > 0.01f)
                {
                    _thumbSize = Mathf.Round(newSize);
                    EditorPrefs.SetFloat(ThumbPrefKey, _thumbSize);
                }

                if (GUILayout.Button("设置", GUILayout.Width(56)))
                {
                    ExcludeSettingsWindow.Show(this);
                }

                if (GUILayout.Button("刷新", GUILayout.Width(56)))
                {
                    Scan();
                }
            }
        }

        /// <summary>扫描 Assets 下每个第一层子目录里的第一层图片。</summary>
        private void Scan()
        {
            _entries.Clear();

            string assetsDir = Application.dataPath;
            if (!Directory.Exists(assetsDir)) return;

            string[] ext = { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.gif" };

            foreach (string dir in Directory.GetDirectories(assetsDir))
            {
                string folderName = Path.GetFileName(dir);
                // 跳过隐藏目录（如 .svn、.git）和明显的系统目录。
                if (folderName.StartsWith(".", StringComparison.Ordinal)) continue;
                // 跳过用户在设置里排除的目录。
                if (_excludedFolders.Contains(folderName)) continue;

                foreach (string pattern in ext)
                {
                    foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        string rel = ToAssetPath(file);
                        _entries.Add(new ImageEntry
                        {
                            AssetPath = rel,
                            FileName = Path.GetFileName(file),
                            FolderName = folderName,
                            Thumb = null,
                        });
                    }
                }
            }

            // 按文件名排序，浏览更稳定。
            _entries.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>自适应网格：根据当前窗口宽度算列数，每格均匀铺满整行，不留右侧空白。</summary>
        private void DrawGrid()
        {
            if (_entries.Count == 0)
            {
                EditorGUILayout.LabelField("未找到任何商品预览图。", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // 用「每格基准宽度」估算列数，再让每格实际宽度 = 可用宽 ÷ 列数，均匀填满整行。
            int columns = Mathf.Max(1, Mathf.FloorToInt(position.width / CellWidth));
            columns = Mathf.Min(columns, _entries.Count);
            int rows = Mathf.CeilToInt(_entries.Count / (float)columns);

            // 每格实际宽度：均分整行（扣除滚动条约 18px）。
            float availWidth = position.width - 18f;
            float cellW = availWidth / columns;

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            for (int row = 0; row < rows; row++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int index = row * columns + col;
                        if (index >= _entries.Count)
                        {
                            GUILayout.Space(cellW);
                            continue;
                        }

                        DrawCell(_entries[index], cellW);
                    }
                }

                EditorGUILayout.Space(6);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>绘制一个图片格子：缩略图 + 文件名 + 所属目录。cellW 为均分后的格子宽度。</summary>
        private void DrawCell(ImageEntry entry, float cellW)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(cellW)))
            {
                // 缩略图（懒加载缓存）。
                if (entry.Thumb == null)
                {
                    entry.Thumb = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.AssetPath);
                }

                // 缩略图：在 cellW 宽的格子里水平居中，尺寸跟随滑条。
                Rect thumbArea = GUILayoutUtility.GetRect(cellW, _thumbSize, GUILayout.Width(cellW), GUILayout.Height(_thumbSize));
                float xOffset = Mathf.Max(0f, (cellW - _thumbSize) / 2f);
                Rect thumbRect = new Rect(thumbArea.x + xOffset, thumbArea.y, _thumbSize, _thumbSize);

                if (entry.Thumb != null)
                {
                    GUI.DrawTexture(thumbRect, entry.Thumb, ScaleMode.ScaleToFit, true);
                }
                else
                {
                    GUI.Box(thumbRect, "无预览", EditorStyles.centeredGreyMiniLabel);
                }

                // 点击缩略图：左键在 Project 定位；右键弹菜单（排除此目录）。
                if (Event.current.type == EventType.MouseDown && thumbRect.Contains(Event.current.mousePosition))
                {
                    if (Event.current.button == 1)
                    {
                        ShowContextMenu(entry);
                    }
                    else
                    {
                        UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.AssetPath);
                        if (asset != null)
                        {
                            Selection.activeObject = asset;
                            EditorGUIUtility.PingObject(asset);
                        }
                    }
                    Event.current.Use();
                }

                // 文件名（居中，超长省略）。
                GUIStyle nameStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
                EditorGUILayout.LabelField(entry.FileName, nameStyle, GUILayout.Width(cellW));

                // 所属目录（灰色小字）。
                GUIStyle folderStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                };
                EditorGUILayout.LabelField(entry.FolderName, folderStyle, GUILayout.Width(cellW));
            }
        }

        /// <summary>右键菜单：排除此目录 / 打开所在目录。</summary>
        private void ShowContextMenu(ImageEntry entry)
        {
            GenericMenu menu = new GenericMenu();
            string folder = entry.FolderName;

            menu.AddItem(new GUIContent("排除此目录：" + folder), false, () =>
            {
                _excludedFolders.Add(folder);
                SaveExcludedFolders();
                Scan();
                Repaint();
            });

            menu.AddItem(new GUIContent("打开所在目录"), false, () =>
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.AssetPath);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            });

            menu.ShowAsContext();
        }

        /// <summary>把绝对路径转成 Assets 相对路径。</summary>
        private static string ToAssetPath(string absolutePath)
        {
            string dataPath = Application.dataPath;
            if (absolutePath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                string rel = absolutePath.Substring(dataPath.Length).TrimStart('/', '\\');
                return "Assets/" + rel;
            }
            return absolutePath;
        }
    }

    /// <summary>排除目录设置弹窗：列出 Assets 第一层目录，勾选要排除的。</summary>
    public class ExcludeSettingsWindow : EditorWindow
    {
        private ImageGalleryWindow _owner;
        private List<string> _allFolders;
        private HashSet<string> _excluded; // 当前勾选（排除）的目录
        private Vector2 _scroll;
        private string _filter = string.Empty;

        /// <summary>显示设置弹窗；确认后把排除结果写回主窗口并刷新。</summary>
        public static void Show(ImageGalleryWindow owner)
        {
            ExcludeSettingsWindow win = GetWindow<ExcludeSettingsWindow>(true, "排除目录");
            win.minSize = new Vector2(360, 420);
            win._owner = owner;
            win.LoadAllFolders();
            win._excluded = new HashSet<string>(owner.ExcludedFolders);
            win.Show();
        }

        private void LoadAllFolders()
        {
            _allFolders = new List<string>();
            string assetsDir = Application.dataPath;
            if (!Directory.Exists(assetsDir)) return;

            foreach (string dir in Directory.GetDirectories(assetsDir))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                _allFolders.Add(name);
            }

            _allFolders.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("勾选要从「商品图预览」中排除的目录：", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            // 搜索过滤。
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("筛选", GUILayout.Width(30));
                _filter = EditorGUILayout.TextField(_filter);
            }
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (string folder in _allFolders)
            {
                if (!string.IsNullOrEmpty(_filter) &&
                    folder.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                bool isExcluded = _excluded.Contains(folder);
                bool newValue = EditorGUILayout.ToggleLeft(folder, isExcluded);
                if (newValue != isExcluded)
                {
                    if (newValue) _excluded.Add(folder);
                    else _excluded.Remove(folder);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全选", GUILayout.Width(60)))
                {
                    foreach (string f in _allFolders) _excluded.Add(f);
                }

                if (GUILayout.Button("清空", GUILayout.Width(60)))
                {
                    _excluded.Clear();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("取消", GUILayout.Width(70)))
                {
                    Close();
                }

                if (CaramelUI.CaramelButton("确定", 26, GUILayout.Width(80)))
                {
                    _owner.SetExcludedFolders(_excluded);
                    _owner.Rescan();
                    Close();
                }
            }
        }
    }
}
#endif
