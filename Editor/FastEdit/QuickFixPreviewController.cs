using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 身体形态键预览（不进 Undo）：临时修改身体 SkinnedMeshRenderer 的形态键权重以预览穿模效果。
    /// 记录原始权重，退出 / 运行 / 保存 / 重载 / 撤销时恢复。与服装预览（PreviewController）配合使用。
    /// </summary>
    [InitializeOnLoad]
    public static class QuickFixPreviewController
    {
        private class Touch
        {
            public SkinnedMeshRenderer Smr;
            public int ShapeIndex;
            public float Original;
        }

        private static readonly Dictionary<string, Touch> Touches = new Dictionary<string, Touch>();
        private static bool _suspended = false;

        /// <summary>预览变化（用于刷新窗口滑条）。</summary>
        public static event Action Changed;

        static QuickFixPreviewController()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private static string Key(SkinnedMeshRenderer smr, string shapeName)
        {
            return smr != null && smr.gameObject != null
                ? smr.gameObject.GetInstanceID() + "|" + shapeName
                : string.Empty;
        }

        /// <summary>设置一个身体形态键的预览权重（记录原始值，供恢复）。shape 携带所属身体 mesh。</summary>
        public static void SetValue(FixShape shape)
        {
            if (shape == null || shape.BodyMesh == null) return;
            SkinnedMeshRenderer smr = shape.BodyMesh.GetComponent<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null) return;
            int idx = smr.sharedMesh.GetBlendShapeIndex(shape.Name);
            if (idx < 0) return;

            string key = Key(smr, shape.Name);
            if (!Touches.ContainsKey(key))
            {
                Touches[key] = new Touch
                {
                    Smr = smr,
                    ShapeIndex = idx,
                    Original = smr.GetBlendShapeWeight(idx),
                };
            }

            smr.SetBlendShapeWeight(idx, shape.Value);
            RaiseChanged();
        }

        /// <summary>把一组形态键的当前值应用到身体（用于切换到某服装项预览时同步显示）。</summary>
        public static void ApplyShapes(IEnumerable<FixShape> shapes)
        {
            if (shapes == null) return;
            foreach (FixShape s in shapes) SetValue(s);
        }

        /// <summary>恢复所有被临时修改的身体形态键到原始权重。</summary>
        public static void RestoreAll()
        {
            if (Touches.Count == 0) return;
            foreach (Touch t in Touches.Values)
            {
                if (t.Smr != null && t.Smr.sharedMesh != null && t.ShapeIndex >= 0 && t.ShapeIndex < t.Smr.sharedMesh.blendShapeCount)
                {
                    t.Smr.SetBlendShapeWeight(t.ShapeIndex, t.Original);
                }
            }

            Touches.Clear();
            RaiseChanged();
        }

        /// <summary>暂停（恢复身体形态键），以便其间做带 Undo 的锁定写入；返回暂停前是否在预览。</summary>
        public static bool Suspend()
        {
            bool was = Touches.Count > 0;
            RestoreAll();
            _suspended = was;
            return was;
        }

        private static void RaiseChanged()
        {
            if (Changed != null) Changed();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) RestoreAll();
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            RestoreAll();
        }

        private static void OnBeforeAssemblyReload()
        {
            RestoreAll();
        }

        private static void OnQuitting()
        {
            RestoreAll();
        }

        private static void OnUndoRedo()
        {
            RestoreAll();
        }
    }
}
