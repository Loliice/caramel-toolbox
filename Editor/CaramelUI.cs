// CaramelUI.cs
// 焦糖工具箱 - 各窗口共享的界面常量与辅助方法（标题格式 / 焦糖配色 / 状态反馈配色 / 通用文案）。
// 放置路径：Assets/CaramelToolbox/Editor/CaramelUI.cs
// 说明：本文件位于默认 Assembly-CSharp-Editor 程序集，供 PhysBoneTool / MaterialTool / SceneTool
//       下的窗口共用；FastEdit 有独立 asmdef，无法引用本文件，其窗口内以相同色值各自定义。
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CaramelToolbox
{
    /// <summary>焦糖工具箱统一的界面样式常量与辅助方法。</summary>
    public static class CaramelUI
    {
        // ---- 焦糖配色 ----
        /// <summary>标题 / 品牌强调色（琥珀焦糖）。</summary>
        public static readonly Color Accent = Hex("#E8A24A");
        /// <summary>主操作按钮背景色（焦糖橙）。</summary>
        public static readonly Color Button = new Color(1f, 0.75f, 0.35f);
        /// <summary>状态反馈：成功 / 正常。</summary>
        public static readonly Color Success = new Color(0.30f, 0.78f, 0.42f);
        /// <summary>状态反馈：警告 / 提示。</summary>
        public static readonly Color Warning = new Color(0.90f, 0.70f, 0.20f);
        /// <summary>状态反馈：错误 / 失败。</summary>
        public static readonly Color Error = new Color(0.86f, 0.33f, 0.28f);

        // ---- 通用文案 ----
        public const string UseSelectionLabel = "使用当前选中";

        /// <summary>把 #RRGGBB / #RRGGBBAA 转成 Color。</summary>
        public static Color Hex(string hex)
        {
            Color c;
            if (ColorUtility.TryParseHtmlString(hex, out c)) return c;
            return Color.white;
        }

        /// <summary>统一标题：焦糖色 + 居中 + 副标题（可选）。</summary>
        public static void DrawTitle(string name, string subtitle = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "<size=18><color=#E8A24A>焦糖工具箱 · " + name + "</color></size>",
                    new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.MiddleCenter },
                    GUILayout.ExpandWidth(true));
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                EditorGUILayout.LabelField(
                    subtitle,
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
            }
        }

        /// <summary>状态行：按类型上色（成功 / 警告 / 错误 / 普通）。</summary>
        public static void DrawStatus(string message, MessageType type)
        {
            if (string.IsNullOrEmpty(message)) return;
            Color prev = GUI.contentColor;
            switch (type)
            {
                case MessageType.Error: GUI.contentColor = Error; break;
                case MessageType.Warning: GUI.contentColor = Warning; break;
                case MessageType.Info: GUI.contentColor = Success; break;
                default: GUI.contentColor = prev; break;
            }

            EditorGUILayout.LabelField(message, EditorStyles.miniLabel);
            GUI.contentColor = prev;
        }

        /// <summary>画一个焦糖色主按钮，返回是否被点击。</summary>
        public static bool CaramelButton(GUIContent content, float height, params GUILayoutOption[] options)
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = Button;
            bool clicked = GUILayout.Button(content, options != null && options.Length > 0
                ? options
                : new[] { GUILayout.Height(height) });
            GUI.backgroundColor = prev;
            return clicked;
        }

        /// <summary>画一个焦糖色主按钮（文本重载），返回是否被点击。</summary>
        public static bool CaramelButton(string text, float height, params GUILayoutOption[] options)
        {
            return CaramelButton(new GUIContent(text), height, options);
        }
    }
}
#endif
