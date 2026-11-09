using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.editor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>骨骼绑定：衣服走 MA Setup Outfit，发型加 Bone Proxy→Head，配饰/道具不自动绑定。</summary>
    public static class BindingHelper
    {
        /// <summary>只读判断将执行的绑定动作（场景对象或 prefab 资源均可）。</summary>
        public static BindAction Describe(GameObject root, AssetCategory category)
        {
            if (root == null) return BindAction.None;
            switch (category)
            {
                case AssetCategory.Clothes:
                    if (root.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null) return BindAction.AlreadyBound;
                    return HasHipsHeuristic(root) ? BindAction.SetupOutfit : BindAction.SetupOutfitNoHips;
                case AssetCategory.Hair:
                    if (root.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null) return BindAction.AlreadyBound;
                    if (root.GetComponentInChildren<ModularAvatarBoneProxy>(true) != null) return BindAction.AlreadyBound;
                    return BindAction.BoneProxyHead;
                default:
                    return BindAction.None;
            }
        }

        /// <summary>
        /// 启发式查 Hips：直接子、孙节点（以及 MA 也支持的 Armature/Armature 1/Hips 曾孙层级），根节点本身不算。
        /// 名字按词边界规则命中 hips / hip，或含 pelvis（J_Bip_C_Hips、Hips、Hip、Pelvis 命中；
        /// Spaceship/Chip/Whip/Worship 不命中）。找不到时不调用 Setup Outfit，避免 MA 弹出模态错误窗。
        /// </summary>
        public static bool HasHipsHeuristic(GameObject root)
        {
            if (root == null) return false;
            return SearchHips(root.transform, 0, 3);
        }

        private static bool IsHipsName(string name)
        {
            return AssetClassifier.ContainsKeyword(name, "hips") ||
                   AssetClassifier.ContainsKeyword(name, "hip") ||
                   AssetClassifier.ContainsKeyword(name, "pelvis");
        }

        private static bool SearchHips(Transform t, int depth, int maxDepth)
        {
            if (t == null) return false;
            if (depth > 0 && IsHipsName(t.name)) return true;
            if (depth >= maxDepth) return false;
            for (int i = 0; i < t.childCount; i++)
            {
                if (SearchHips(t.GetChild(i), depth + 1, maxDepth)) return true;
            }

            return false;
        }

        /// <summary>
        /// 对已放入角色层级的素材执行绑定。必须在素材被设为关闭之前调用。
        /// </summary>
        public static void Bind(GameObject material, AssetCategory category, VRCAvatarDescriptor avatar,
            List<string> logs, List<string> warnings)
        {
            if (material == null) return;
            BindAction action = Describe(material, category);
            switch (action)
            {
                case BindAction.SetupOutfit:
                    RunSetupOutfit(material, logs, warnings);
                    break;
                case BindAction.SetupOutfitNoHips:
                    warnings?.Add("「" + material.name + "」未找到 Hips 骨骼，已跳过 Setup Outfit，请手动绑定骨骼");
                    break;
                case BindAction.BoneProxyHead:
                    AddHeadBoneProxy(material, avatar, logs, warnings);
                    break;
                case BindAction.AlreadyBound:
                    logs?.Add("「" + material.name + "」已有绑定，跳过");
                    break;
                default:
                    if (category == AssetCategory.Accessory || category == AssetCategory.Prop)
                    {
                        logs?.Add("「" + material.name + "」为" + QuickOutfitConst.CategoryLabel(category) +
                                  "，未自动绑定骨骼（如需跟随骨骼请手动添加 Bone Proxy）");
                    }

                    break;
            }
        }

        private static void RunSetupOutfit(GameObject material, List<string> logs, List<string> warnings)
        {
            try
            {
                SetupOutfit.SetupOutfitUI(material);
            }
            catch (Exception e)
            {
                warnings?.Add("「" + material.name + "」执行 Setup Outfit 出错：" + e.Message);
                Debug.LogException(e);
            }

            // Setup Outfit 会改写当前 Undo 组名，这里改回。
            Undo.SetCurrentGroupName(QuickOutfitConst.UndoName);

            if (material != null && material.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null)
            {
                logs?.Add("「" + material.name + "」已执行 Setup Outfit");
            }
            else
            {
                warnings?.Add("「" + (material != null ? material.name : "?") +
                              "」执行 Setup Outfit 后仍没有 MA Merge Armature，请检查骨架（可能找不到 Hips）");
            }
        }

        private static void AddHeadBoneProxy(GameObject material, VRCAvatarDescriptor avatar, List<string> logs,
            List<string> warnings)
        {
            ModularAvatarBoneProxy proxy = material.GetComponent<ModularAvatarBoneProxy>();
            if (proxy == null) proxy = Undo.AddComponent<ModularAvatarBoneProxy>(material);
            if (proxy == null)
            {
                warnings?.Add("「" + material.name + "」无法添加 MA Bone Proxy");
                return;
            }

            Undo.RecordObject(proxy, QuickOutfitConst.UndoName);
            proxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;

            Transform head = null;
            Animator animator = avatar != null ? avatar.GetComponent<Animator>() : null;
            if (animator != null && animator.isHuman)
            {
                head = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            bool assigned = false;
            if (head != null)
            {
                try
                {
                    proxy.target = head;
                    assigned = proxy.boneReference == HumanBodyBones.Head && string.IsNullOrEmpty(proxy.subPath);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    assigned = false;
                }
            }

            if (!assigned)
            {
                proxy.boneReference = HumanBodyBones.Head;
                proxy.subPath = string.Empty;
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(proxy);
            if (head == null)
            {
                warnings?.Add("「" + material.name + "」：角色 Animator 不可用或非 Humanoid，Bone Proxy 仅设置了 boneReference=Head");
            }

            logs?.Add("「" + material.name + "」已添加 Bone Proxy → Head（保持世界位姿）");
        }
    }
}
