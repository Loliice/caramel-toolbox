// ChannelSplitterWindow.cs
// 焦糖工具箱 · RGB 通道拆分 —— 把一张贴图按 R / G / B 三个通道拆成三张独立的灰度贴图。
// 放置路径：Assets/CaramelToolbox/Editor/MaterialTool/ChannelSplitterWindow.cs
//
// 用途：把一张「遮罩贴图」（Mask，通常 R/G/B 三个通道各自存不同用途的遮罩）拆成三张独立贴图，
// 便于分别调整或分别使用。也可用于提取某张彩色贴图的单一通道。
//
// 拆分结果：
//   - R 通道 → <原名>_R.png（灰度，R 通道值写入 RGB 三通道）
//   - G 通道 → <原名>_G.png
//   - B 通道 → <原名>_B.png
//
// 说明：读取像素需要贴图勾选 Read/Write Enabled，否则会临时复制一份可读副本。
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>RGB 通道拆分窗口：拖入贴图 → 选通道 → 一键拆成三张灰度图。</summary>
    public class ChannelSplitterWindow : EditorWindow
    {
        private const string MenuPath = "焦糖工具箱/RGB 通道拆分";

        private Texture2D _source;
        private string _outputFolder = string.Empty; // 留空 = 跟随源贴图所在目录
        private bool _splitR = true;
        private bool _splitG = true;
        private bool _splitB = true;
        private Vector2 _scroll;
        private string _lastStatus = string.Empty;

        [MenuItem(MenuPath, false, 103)]
        private static void Open()
        {
            ChannelSplitterWindow win = GetWindow<ChannelSplitterWindow>("RGB 通道拆分");
            win.minSize = new Vector2(420, 340);
            win.Show();
        }

        private void OnGUI()
        {
            CaramelUI.DrawTitle("RGB 通道拆分", "一张遮罩拆成 R / G / B 三张灰度图");
            EditorGUILayout.Space(8);

            DrawSourceArea();
            EditorGUILayout.Space(6);
            DrawChannelOptions();
            EditorGUILayout.Space(6);
            DrawOutputArea();
            EditorGUILayout.Space(8);

            if (!string.IsNullOrEmpty(_lastStatus))
            {
                EditorGUILayout.HelpBox(_lastStatus, MessageType.Info);
            }
        }

        private void DrawSourceArea()
        {
            EditorGUILayout.LabelField("源贴图", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _source = (Texture2D)EditorGUILayout.ObjectField(_source, typeof(Texture2D), false);
                if (GUILayout.Button(CaramelUI.UseSelectionLabel, GUILayout.Width(92)))
                {
                    UseCurrentSelection();
                }
            }

            // 拖入处理。
            Rect dropRect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "或将贴图拖入此处", EditorStyles.helpBox);
            GUI.Label(dropRect, "或将贴图拖入此处", new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
            });

            Event evt = Event.current;
            if ((evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform) &&
                dropRect.Contains(evt.mousePosition))
            {
                if (DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0)
                {
                    Texture2D tex = DragAndDrop.objectReferences[0] as Texture2D;
                    if (tex != null)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        if (evt.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            SetSource(tex);
                        }
                    }
                }
            }

            // 预览。
            if (_source != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("预览：" + _source.name + " (" + _source.width + "×" + _source.height + ")",
                    EditorStyles.miniLabel);
                Rect previewRect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
                GUI.DrawTexture(previewRect, _source, ScaleMode.ScaleToFit, true);
            }
        }

        private void DrawChannelOptions()
        {
            EditorGUILayout.LabelField("拆分通道", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _splitR = EditorGUILayout.ToggleLeft("R 通道", _splitR, GUILayout.Width(90));
                _splitG = EditorGUILayout.ToggleLeft("G 通道", _splitG, GUILayout.Width(90));
                _splitB = EditorGUILayout.ToggleLeft("B 通道", _splitB, GUILayout.Width(90));
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawOutputArea()
        {
            EditorGUILayout.LabelField("输出", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("输出目录", GUILayout.Width(60));
                _outputFolder = EditorGUILayout.TextField(_outputFolder);
                if (GUILayout.Button("选择…", GUILayout.Width(56)))
                {
                    string picked = EditorUtility.OpenFolderPanel("选择输出目录", "Assets", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _outputFolder = ToAssetPath(picked);
                    }
                }
            }

            EditorGUILayout.LabelField("留空则默认输出到源贴图所在目录", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);

            if (CaramelUI.CaramelButton("开始拆分", 28))
            {
                DoSplit();
            }
        }

        private void DoSplit()
        {
            _lastStatus = string.Empty;

            if (_source == null)
            {
                _lastStatus = "请先选择或拖入一张贴图";
                EditorUtility.DisplayDialog("RGB 通道拆分", "请先选择或拖入一张贴图", "确定");
                return;
            }

            if (!_splitR && !_splitG && !_splitB)
            {
                _lastStatus = "请至少勾选一个要拆分的通道";
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(_source);
            if (string.IsNullOrEmpty(sourcePath))
            {
                _lastStatus = "请选择工程内的贴图（Assets 目录下的贴图）";
                return;
            }

            // 拿到可读像素。
            Texture2D readable = GetReadableCopy(_source);
            if (readable == null)
            {
                _lastStatus = "无法读取贴图像素（可能不是可读贴图）";
                return;
            }

            try
            {
                string baseName = Path.GetFileNameWithoutExtension(sourcePath);

                // 输出目录：留空时默认跟随原贴图所在目录。
                string outDir;
                if (string.IsNullOrEmpty(_outputFolder) || _outputFolder.Trim() == "Assets")
                {
                    outDir = Path.GetDirectoryName(sourcePath);
                    if (string.IsNullOrEmpty(outDir)) outDir = "Assets";
                }
                else
                {
                    outDir = _outputFolder.Trim().TrimEnd('/', '\\');
                }

                int done = 0;
                if (_splitR) { if (WriteChannel(readable, outDir, baseName + "_R", 0)) done++; }
                if (_splitG) { if (WriteChannel(readable, outDir, baseName + "_G", 1)) done++; }
                if (_splitB) { if (WriteChannel(readable, outDir, baseName + "_B", 2)) done++; }

                AssetDatabase.Refresh();
                _lastStatus = "拆分完成，共生成 " + done + " 张灰度图 → " + outDir;
            }
            finally
            {
                if (readable != _source)
                {
                    DestroyImmediate(readable);
                }
            }
        }

        /// <summary>把指定通道（0=R 1=G 2=B）的值复制到 RGB 三通道，生成灰度图并写盘。</summary>
        private bool WriteChannel(Texture2D src, string outDir, string fileName, int channel)
        {
            try
            {
                Color32[] pixels = src.GetPixels32();
                Color32[] gray = new Color32[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte v = channel == 0 ? pixels[i].r : channel == 1 ? pixels[i].g : pixels[i].b;
                    gray[i] = new Color32(v, v, v, 255);
                }

                Texture2D outTex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                outTex.SetPixels32(gray);
                outTex.Apply();

                byte[] png = outTex.EncodeToPNG();
                DestroyImmediate(outTex);

                string dir = ToAbsolutePath(outDir);
                Directory.CreateDirectory(dir);
                string fullPath = Path.Combine(dir, fileName + ".png");
                File.WriteAllBytes(fullPath, png);

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[焦糖工具箱] 拆分通道失败：" + e.Message);
                return false;
            }
        }

        /// <summary>把 Assets 相对路径（如 Assets/xxx）转成绝对路径；已是绝对路径则原样返回。</summary>
        private static string ToAbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return Application.dataPath;

            string p = path.Replace('\\', '/').TrimEnd('/');
            if (Path.IsPathRooted(path)) return path;

            // 相对路径：Assets/xxx → dataPath + /xxx
            if (p.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return Application.dataPath;
            }

            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string rel = p.Substring("Assets/".Length);
                return Path.Combine(Application.dataPath, rel);
            }

            return Path.GetFullPath(path);
        }

        /// <summary>拿一张可读的贴图副本（源不可读时，通过临时 RenderTexture 重绘一份可读的）。</summary>
        private static Texture2D GetReadableCopy(Texture2D src)
        {
            if (src.isReadable) return src;

            // 不可读贴图：用 RenderTexture 重绘一张可读副本。
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            copy.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return copy;
        }

        /// <summary>把系统绝对路径转回 Assets 相对路径（用于输出目录显示）。</summary>
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

        /// <summary>把当前 Project 窗口选中的贴图设为源贴图。</summary>
        private void UseCurrentSelection()
        {
            UnityEngine.Object[] objs = Selection.objects;
            if (objs == null || objs.Length == 0)
            {
                _lastStatus = "当前没有选中任何贴图";
                return;
            }

            Texture2D tex = null;
            foreach (UnityEngine.Object o in objs)
            {
                tex = o as Texture2D;
                if (tex != null) break;
            }

            if (tex == null)
            {
                _lastStatus = "选中的对象不是贴图";
                return;
            }

            SetSource(tex);
        }

        /// <summary>设置源贴图；若输出目录为空，则默认跟随贴图所在目录。</summary>
        private void SetSource(Texture2D tex)
        {
            _source = tex;
            _lastStatus = string.Empty;

            // 输出目录留空时，跟随贴图所在目录（界面上留空，拆分时自动取源贴图目录）。
            // 这里不主动填充 _outputFolder，保持「留空 = 跟随源贴图」的约定。
        }
    }
}
#endif
