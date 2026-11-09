using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 临时预览（不进 Undo）：
    /// · 槽位预览：临时勾上目标槽位两个 MA Object Toggle 的「反转条件」（内容 Toggle 打开目标；
    ///   服装/发型的默认引用 Toggle 关闭默认项）。每个类别同时只有一个预览。
    /// · 服装部件预览：临时勾上「服装部件」下所有 MA Object Toggle 的「反转条件」。
    /// 再次点击、进入运行模式、保存场景、脚本重载、撤销/重做、关闭编辑器时自动退出并恢复原状态。
    /// 恢复时只恢复「仍保持预览值」的对象，避免覆盖用户在预览期间做的修改。
    /// </summary>
    [InitializeOnLoad]
    public static class PreviewController
    {
        private class SlotSession
        {
            public AssetCategory Category = AssetCategory.Unknown;
            public GameObject Slot = null;
            /// <summary>
            /// 被临时勾上「反转条件」的 Toggle：内容 Toggle（第二个，打开目标）+ 默认引用 Toggle（第一个，关闭默认服装/发型）。
            /// 只记录预览时由本工具勾上的（原本已勾上的不记录，退出时也不动）。
            /// </summary>
            public readonly List<ModularAvatarObjectToggle> Toggles = new List<ModularAvatarObjectToggle>();
        }

        private static readonly Dictionary<AssetCategory, SlotSession> Sessions = new Dictionary<AssetCategory, SlotSession>();
        private static readonly List<ModularAvatarObjectToggle> InvertedToggles = new List<ModularAvatarObjectToggle>();
        private static bool _partsActive = false;

        /// <summary>预览状态变化（用于刷新窗口）。</summary>
        public static event Action Changed;

        /// <summary>因外部原因自动退出预览时的提示（用于写入操作日志）。</summary>
        public static event Action<string> AutoExited;

        static PreviewController()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public static bool IsAnyActive
        {
            get { return Sessions.Count > 0 || _partsActive; }
        }

        public static bool IsPartsPreviewing
        {
            get { return _partsActive; }
        }

        public static bool IsCategoryPreviewing(AssetCategory category)
        {
            return Sessions.ContainsKey(category);
        }

        public static bool IsSlotPreviewing(GameObject slot)
        {
            if (slot == null) return false;
            foreach (SlotSession s in Sessions.Values)
            {
                if (s.Slot == slot) return true;
            }

            return false;
        }

        /// <summary>该对象是否为正在预览的槽位。</summary>
        public static bool Touches(GameObject go)
        {
            return IsSlotPreviewing(go);
        }

        // ================================================================== 槽位预览

        /// <summary>切换槽位预览：未预览则进入（同类已有的预览会先退出），已在预览则退出。</summary>
        public static bool ToggleSlot(VRCAvatarDescriptor avatar, GameObject slotObject, out string message)
        {
            message = string.Empty;
            if (avatar == null || slotObject == null)
            {
                message = "角色或槽位已丢失";
                return false;
            }

            AssetCategory previewing = AssetCategory.Unknown;
            foreach (KeyValuePair<AssetCategory, SlotSession> kv in Sessions)
            {
                if (kv.Value.Slot == slotObject) previewing = kv.Key;
            }

            if (previewing != AssetCategory.Unknown)
            {
                ExitCategory(previewing);
                message = "已退出预览「" + slotObject.name + "」";
                return true;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                message = "运行模式下无法预览";
                return false;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            if (root == null)
            {
                message = "该角色没有外观设置";
                return false;
            }

            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            AssetCategory category;
            SlotInfo slot = SlotOperations.FindSlot(info, slotObject, out category);
            CategoryInfo ci = info.Get(category);
            if (slot == null || ci == null)
            {
                message = "「" + slotObject.name + "」不是可预览的槽位";
                return false;
            }

            ModularAvatarObjectToggle content = slot.ContentToggle;
            if (content == null)
            {
                message = "「" + slotObject.name + "」缺少第二个 MA Object Toggle（内容开关），无法预览";
                return false;
            }

            bool hasTarget = false;
            foreach (ResolvedToggle rt in AppearanceStructure.ResolveTargets(content))
            {
                if (rt.Target != null) hasTarget = true;
            }

            if (!hasTarget)
            {
                message = "「" + slotObject.name + "」是空槽，没有可预览的内容";
                return false;
            }

            ExitCategory(category);
            SlotSession session = new SlotSession { Category = category, Slot = slotObject };
            InvertForPreview(session, content);

            // 服装/发型：同时勾上引用默认服装/默认发型（OFF）的那个 Toggle，使默认项在预览时关闭。
            bool defaultHidden = false;
            if (QuickOutfitConst.UsesDefaultItem(category))
            {
                if (slot.DefaultRefToggle != null && slot.DefaultRefToggle != content)
                {
                    InvertForPreview(session, slot.DefaultRefToggle);
                    defaultHidden = true;
                }

                // 兜底：若默认引用 Toggle 未被正确识别（例如默认项名字不匹配、引用路径解析失败），
                // 但槽位上还有其它 MA Object Toggle，则把它们一并临时反转，保证与服装一致地反转两个。
                if (!defaultHidden)
                {
                    ModularAvatarObjectToggle[] all = slotObject.GetComponents<ModularAvatarObjectToggle>();
                    foreach (ModularAvatarObjectToggle t in all)
                    {
                        if (t == content) continue;
                        if (t == null) continue;
                        InvertForPreview(session, t);
                        defaultHidden = true;
                    }
                }
            }

            Sessions[category] = session;
            message = "正在预览「" + slotObject.name + "」（" + QuickOutfitConst.ContainerName(category) + "）：已临时勾上" +
                      (defaultHidden ? "两个 MA Object Toggle" : "内容 MA Object Toggle") + "的「反转条件」" +
                      (defaultHidden ? "（打开目标、关闭" + QuickOutfitConst.DefaultItemName(category) + "）" : string.Empty) +
                      "，需开启 NDMF 预览；再次点击、运行或保存场景时自动恢复";
            RaiseChanged();
            return true;
        }

        private static void InvertForPreview(SlotSession session, ModularAvatarObjectToggle toggle)
        {
            if (toggle == null || toggle.Inverted) return;
            SetInverted(toggle, true);
            session.Toggles.Add(toggle);
        }

        public static void ExitCategory(AssetCategory category)
        {
            SlotSession session;
            if (!Sessions.TryGetValue(category, out session)) return;
            Sessions.Remove(category);
            // 只恢复由预览勾上、且仍保持勾上的开关，避免覆盖用户在预览期间的手动修改。
            foreach (ModularAvatarObjectToggle t in session.Toggles)
            {
                if (t != null && t.Inverted) SetInverted(t, false);
            }

            session.Toggles.Clear();

            RaiseChanged();
        }

        // ================================================================== 服装部件预览（反转条件）

        public static bool ToggleParts(VRCAvatarDescriptor avatar, out string message)
        {
            if (_partsActive)
            {
                ExitParts();
                message = "已退出服装部件预览";
                return true;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                message = "运行模式下无法预览";
                return false;
            }

            int count = EnterParts(avatar, out message);
            return count >= 0;
        }

        /// <summary>进入服装部件预览，返回部件 Toggle 数量；失败返回 -1。可重复调用（会补上新增部件）。</summary>
        public static int EnterParts(VRCAvatarDescriptor avatar, out string message)
        {
            message = string.Empty;
            Transform container = PartOperations.FindPartsContainer(avatar);
            if (container == null)
            {
                message = "该角色的外观设置中没有「" + QuickOutfitConst.PartContainerName + "」";
                return -1;
            }

            int total = 0;
            foreach (PartNode part in PartOperations.GetParts(container))
            {
                foreach (ModularAvatarObjectToggle t in part.Toggles)
                {
                    if (t == null) continue;
                    total++;
                    if (t.Inverted || InvertedToggles.Contains(t)) continue;
                    SetInverted(t, true);
                    InvertedToggles.Add(t);
                }
            }

            if (total == 0)
            {
                message = "「" + QuickOutfitConst.PartContainerName + "」下没有 MA Object Toggle";
                return -1;
            }

            _partsActive = true;
            message = "正在预览服装部件：已临时勾上 " + total + " 个 MA Object Toggle 的「反转条件」" +
                      "（需开启 NDMF 预览）；再次点击、运行或保存场景时自动恢复";
            RaiseChanged();
            return total;
        }

        public static void ExitParts()
        {
            if (!_partsActive && InvertedToggles.Count == 0) return;
            foreach (ModularAvatarObjectToggle t in InvertedToggles)
            {
                if (t != null && t.Inverted) SetInverted(t, false);
            }

            InvertedToggles.Clear();
            _partsActive = false;
            RaiseChanged();
        }

        /// <summary>
        /// 暂停服装部件预览（恢复反转条件），以便在其间做带 Undo 的修改；返回暂停前是否在预览。
        /// 修改完成后先 Undo.FlushUndoRecordObjects 再调用 <see cref="ResumeParts"/>，保证 Undo 记录的是非预览状态。
        /// </summary>
        public static bool SuspendParts()
        {
            bool was = _partsActive;
            if (was)
            {
                foreach (ModularAvatarObjectToggle t in InvertedToggles)
                {
                    if (t != null && t.Inverted) SetInverted(t, false);
                }

                InvertedToggles.Clear();
                _partsActive = false;
            }

            return was;
        }

        public static void ResumeParts(VRCAvatarDescriptor avatar)
        {
            string ignored;
            EnterParts(avatar, out ignored);
        }

        private static void SetInverted(ModularAvatarObjectToggle toggle, bool value)
        {
            SerializedObject so = new SerializedObject(toggle);
            SerializedProperty prop = so.FindProperty("m_inverted");
            if (prop != null)
            {
                prop.boolValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                toggle.Inverted = value;
                EditorUtility.SetDirty(toggle);
            }
        }

        // ================================================================== 退出

        /// <summary>退出全部预览。reason 非空且确有预览被退出时，通过 <see cref="AutoExited"/> 通知。</summary>
        public static void ExitAll(string reason)
        {
            bool had = IsAnyActive;
            List<AssetCategory> keys = new List<AssetCategory>(Sessions.Keys);
            foreach (AssetCategory k in keys) ExitCategory(k);
            ExitParts();
            if (had && !string.IsNullOrEmpty(reason) && AutoExited != null)
            {
                AutoExited("已退出预览（" + reason + "），场景已恢复原状态");
            }

            if (had) RaiseChanged();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) ExitAll("进入运行模式");
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            ExitAll("保存场景");
        }

        private static void OnBeforeAssemblyReload()
        {
            ExitAll(null);
        }

        private static void OnQuitting()
        {
            ExitAll(null);
        }

        private static void OnUndoRedo()
        {
            ExitAll("撤销/重做");
        }

        private static void RaiseChanged()
        {
            if (Changed != null) Changed();
        }
    }
}
