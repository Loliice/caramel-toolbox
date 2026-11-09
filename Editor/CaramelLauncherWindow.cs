// CaramelLauncherWindow.cs
// 焦糖工具箱 · 快捷启动面板 —— 一键打开工具箱下的 8 个插件。
// 放置路径：Assets/CaramelToolbox/Editor/CaramelLauncherWindow.cs
//
// 说明：
//   - 菜单入口：焦糖工具箱/工具箱面板（放在第一个，便于快速打开）。
//   - 通过 EditorApplication.ExecuteMenuItem 触发各插件的 [MenuItem]，与点击菜单完全等价，
//     不依赖各窗口的 private Open()，零侵入、无需改动其它插件代码。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>焦糖工具箱的快捷启动面板：8 个图标横向单行排列，点击直达对应插件。</summary>
    public class CaramelLauncherWindow : EditorWindow
    {
        private const string MenuPath = "焦糖工具箱/工具箱面板";

        /// <summary>插件入口：短标签 / 菜单路径 / 悬停完整名称。</summary>
        private struct PluginEntry
        {
            public string Label;     // 短标签（两个中文字）
            public string MenuPath;  // 对应菜单路径
            public string FullName;  // 完整名称（tooltip）
        }

        private static readonly PluginEntry[] Plugins =
        {
            new PluginEntry { Label = "改模",   MenuPath = "焦糖工具箱/快速改模",          FullName = "快速改模" },
            new PluginEntry { Label = "修穿模", MenuPath = "焦糖工具箱/快速修复胸、脚穿模", FullName = "快速修复胸、脚穿模" },
            new PluginEntry { Label = "上传",   MenuPath = "焦糖工具箱/快速上传",          FullName = "快速上传" },
            new PluginEntry { Label = "导入",   MenuPath = "焦糖工具箱/静默导入",          FullName = "静默导入" },
            new PluginEntry { Label = "材质",   MenuPath = "焦糖工具箱/材质批量替换",      FullName = "材质批量替换" },
            new PluginEntry { Label = "贴图",   MenuPath = "焦糖工具箱/快速贴图压缩",      FullName = "快速贴图压缩" },
            new PluginEntry { Label = "通道",   MenuPath = "焦糖工具箱/RGB 通道拆分",     FullName = "RGB 通道拆分" },
            new PluginEntry { Label = "图库",   MenuPath = "焦糖工具箱/商品图预览",       FullName = "商品图预览" },
            new PluginEntry { Label = "动骨",   MenuPath = "焦糖工具箱/动骨计算工具",      FullName = "动骨计算工具" },
            new PluginEntry { Label = "场景",   MenuPath = "焦糖工具箱/快速复制场景",      FullName = "快速复制场景" },
        };

        private const float CardHeight = 40f;

        // 卡片样式缓存。
        private GUIStyle _cardStyle;

        [MenuItem(MenuPath, false, 0)]
        private static void Open()
        {
            CaramelLauncherWindow window = GetWindow<CaramelLauncherWindow>(false, "焦糖工具箱", true);
            window.minSize = new Vector2(640f, 52f);
            window.maxSize = new Vector2(1100f, 52f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("焦糖工具箱");
            _cardStyle = null;
        }

        private void OnGUI()
        {
            DrawPluginRow();
        }

        /// <summary>8 个卡片横向单行排列。</summary>
        private void DrawPluginRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (PluginEntry plugin in Plugins)
                {
                    DrawPluginCard(plugin);
                }
            }
        }

        /// <summary>画一个小方块卡片：短标签居中，整卡可点击，悬停显示完整名称。</summary>
        private void DrawPluginCard(PluginEntry plugin)
        {
            if (_cardStyle == null)
            {
                _cardStyle = new GUIStyle(GUI.skin.button)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    richText = true,
                };
            }

            GUIContent content = new GUIContent(
                "<color=#E8A24A>" + plugin.Label + "</color>",
                plugin.FullName);

            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.91f, 0.72f, 0.45f, 0.18f);

            bool clicked = GUILayout.Button(content, _cardStyle,
                GUILayout.Height(CardHeight), GUILayout.ExpandWidth(true));

            GUI.backgroundColor = prev;

            if (clicked)
            {
                OpenPlugin(plugin);
            }
        }

        /// <summary>打开指定插件（触发其菜单项）。</summary>
        private static void OpenPlugin(PluginEntry plugin)
        {
            if (string.IsNullOrEmpty(plugin.MenuPath)) return;

            bool ok = EditorApplication.ExecuteMenuItem(plugin.MenuPath);
            if (!ok)
            {
                Debug.LogWarning("[焦糖工具箱] 打开插件失败，请确认菜单项存在：" + plugin.MenuPath);
            }
        }
    }
}
#endif
