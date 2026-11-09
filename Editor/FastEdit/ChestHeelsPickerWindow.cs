using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 指定胸/脚形态键弹窗：列出角色身体所有形态键，把带 Breast / heels 字样的排到最上方并标「推荐」，
    /// 用户勾选后点「确认」写回会话的已指定键列表。
    /// </summary>
    public class ChestHeelsPickerWindow : EditorWindow
    {
        private List<FixShape> _all = new List<FixShape>();
        private readonly List<bool> _checked = new List<bool>();
        private Vector2 _scroll;
        private System.Action<List<FixShape>> _onConfirm;

        /// <summary>打开弹窗。source 为身体形态键（会被拷贝，推荐项置顶）；onConfirm 回调返回勾选结果。</summary>
        public static void Open(List<FixShape> source, System.Action<List<FixShape>> onConfirm)
        {
            ChestHeelsPickerWindow window = GetWindow<ChestHeelsPickerWindow>("指定胸/脚形态键");
            window.minSize = new Vector2(380f, 440f);

            window._all = new List<FixShape>();
            window._checked.Clear();

            if (source != null)
            {
                // 推荐项置顶（推荐且非空），其余（含空形态键）排在下方。
                List<FixShape> recommended = new List<FixShape>();
                List<FixShape> others = new List<FixShape>();
                foreach (FixShape s in source)
                {
                    if (s.Recommended && !s.Empty) recommended.Add(s);
                    else others.Add(s);
                }

                window._all.AddRange(recommended);
                window._all.AddRange(others);
            }

            for (int i = 0; i < window._all.Count; i++) window._checked.Add(false);

            window._onConfirm = onConfirm;
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("勾选要处理的胸/脚形态键", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("带「推荐」标记的（名字含 Breast / heels 等）已排在最上方。\n勾选后点「确认」，这些键会出现在每件服装的形态键调整区。", MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _all.Count; i++)
            {
                FixShape s = _all[i];
                string label = s.Recommended ? ("★ 推荐  " + s.Name) : s.Name;
                _checked[i] = EditorGUILayout.Toggle(label, _checked[i]);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全选推荐", GUILayout.Height(26f)))
                {
                    for (int i = 0; i < _all.Count; i++) _checked[i] = _all[i].Recommended;
                }

                if (GUILayout.Button("清空", GUILayout.Height(26f)))
                {
                    for (int i = 0; i < _all.Count; i++) _checked[i] = false;
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("确认", GUILayout.Width(90f), GUILayout.Height(28f)))
                {
                    Confirm();
                }
            }
        }

        private void Confirm()
        {
            List<FixShape> selected = new List<FixShape>();
            for (int i = 0; i < _all.Count; i++)
            {
                if (!_checked[i]) continue;
                FixShape src = _all[i];
                selected.Add(new FixShape
                {
                    Name = src.Name,
                    BodyMesh = src.BodyMesh,
                    Recommended = src.Recommended,
                    Value = 0f,
                    Locked = false,
                });
            }

            if (_onConfirm != null) _onConfirm(selected);
            Close();
        }
    }
}
