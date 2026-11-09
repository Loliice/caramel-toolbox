using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>「当前外观」面板的场景操作：槽位改名、「转为」其它类别，以及 ObjectToggle 引用的增删工具。</summary>
    public static class SlotOperations
    {
        public const string RenameUndoName = "快速改模：槽位改名";

        // ================================================================== 改名

        /// <summary>
        /// 改槽位 / 部件名（外观设置下各容器的直接子物体）。默认服装/默认发型不允许改名。
        /// 同级重名追加「 (2)」。改名后刷新引用路径。一个 Undo 组。
        /// </summary>
        public static bool RenameNode(VRCAvatarDescriptor avatar, GameObject node, string desired, out string message)
        {
            message = string.Empty;
            if (avatar == null || node == null)
            {
                message = "角色或对象已丢失";
                return false;
            }

            string name = desired != null ? desired.Trim() : string.Empty;
            if (name == node.name) return false;
            if (name.Length == 0)
            {
                message = "名字不能为空";
                return false;
            }

            if (name.IndexOf('/') >= 0)
            {
                message = "名字不能包含「/」";
                return false;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            Transform parent = node.transform.parent;
            if (root == null || parent == null || parent.parent != root.transform)
            {
                message = "只能修改外观设置中的槽位 / 部件名";
                return false;
            }

            if (IsDefaultItemNode(node))
            {
                message = "「" + node.name + "」是默认项，不能改名";
                return false;
            }

            HashSet<string> siblings = new HashSet<string>();
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c != node.transform) siblings.Add(c.name);
            }

            string finalName = AppearanceStructure.MakeUniqueName(name, siblings);

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(RenameUndoName);
            try
            {
                string oldName = node.name;
                Undo.RecordObject(node, RenameUndoName);
                node.name = finalName;
                AppearanceStructure.RefreshReferences(root, avatar.transform, null);
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                Undo.SetCurrentGroupName(RenameUndoName);
                Undo.CollapseUndoOperations(group);
                message = "已将「" + oldName + "」改名为「" + finalName + "」" +
                          (finalName != name ? "（与同级重名，已追加编号）" : string.Empty);
                Debug.Log("[快速改模] " + message);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                message = "改名失败，已回滚：" + e.Message;
                return false;
            }
        }

        /// <summary>node 是否为 服装切换/发型切换 下的默认项。</summary>
        public static bool IsDefaultItemNode(GameObject node)
        {
            if (node == null || node.transform.parent == null) return false;
            Transform container = node.transform.parent;
            foreach (AssetCategory category in new[] { AssetCategory.Clothes, AssetCategory.Hair })
            {
                if (container.name != QuickOutfitConst.ContainerName(category)) continue;
                return AppearanceStructure.FindDefaultItem(container, category) == node;
            }

            return false;
        }

        // ================================================================== 转为

        /// <summary>
        /// 把槽位中的素材迁移到目标类别（一个 Undo 组）：
        /// 从原槽内容 Toggle 移除 → 放进目标类第一个空槽（没有则新建）→ 按目标类规则补绑定 → 保持关闭 →
        /// 写入目标内容 Toggle（Active=ON）→ 槽名沿用原槽名（重名追加「 (2)」；原槽名是「服装_3」这类默认编号名时改用目标类的编号名）→
        /// 原槽为空则删除（每类至少保留 1 个）→ 刷新引用。离开「服装切换」时同时从服装部件中移除这些素材的网格。
        /// </summary>
        public static bool ConvertSlot(VRCAvatarDescriptor avatar, GameObject slotObject, AssetCategory target,
            List<string> logs, List<string> warnings, out string message)
        {
            message = string.Empty;
            if (logs == null) logs = new List<string>();
            if (warnings == null) warnings = new List<string>();
            if (avatar == null || slotObject == null)
            {
                message = "角色或槽位已丢失";
                return false;
            }

            if (Array.IndexOf(QuickOutfitConst.SlotCategories, target) < 0)
            {
                message = "目标类别无效";
                return false;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            if (root == null)
            {
                message = "该角色没有外观设置";
                return false;
            }

            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            AssetCategory source;
            SlotInfo src = FindSlot(info, slotObject, out source);
            if (src == null)
            {
                message = "「" + slotObject.name + "」不是可转换的槽位";
                return false;
            }

            if (source == target)
            {
                message = "「" + slotObject.name + "」已经属于" + QuickOutfitConst.CategoryLabel(target);
                return false;
            }

            if (info.Get(target) == null)
            {
                message = "外观设置中缺少「" + QuickOutfitConst.ContainerName(target) + "」节点";
                return false;
            }

            List<GameObject> physical;
            List<GameObject> referenced;
            SplitContents(src, root, out physical, out referenced);
            if (physical.Count == 0 && referenced.Count == 0)
            {
                message = "槽位「" + slotObject.name + "」为空，无需转换";
                return false;
            }

            foreach (GameObject go in physical)
            {
                if (!AppearanceStructure.CanReparent(go))
                {
                    message = "「" + go.name + "」属于 Prefab 实例内部，无法移动";
                    return false;
                }
            }

            string undoName = "快速改模：转为" + QuickOutfitConst.CategoryLabel(target);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                // 结构修复（与快速应用一致），之后重新分析。
                AppearanceStructure.EnsureDefaults(root, logs);
                AppearanceStructure.RepairSlots(root, logs);
                info = AppearanceStructure.Analyze(root, false);
                src = FindSlot(info, slotObject, out source);
                if (src == null) throw new InvalidOperationException("修复结构后找不到原槽位");
                CategoryInfo sourceInfo = info.Get(source);
                CategoryInfo targetInfo = info.Get(target);
                if (targetInfo == null) throw new InvalidOperationException("缺少「" + QuickOutfitConst.ContainerName(target) + "」");

                string sourceName = slotObject.name;
                int sourceSlotCount = sourceInfo != null ? sourceInfo.Slots.Count : 0;
                List<GameObject> moving = new List<GameObject>(physical);
                moving.AddRange(referenced);

                // 1. 从原槽内容 Toggle 移除。
                if (src.ContentToggle != null)
                {
                    int removed = RemoveToggleTargets(src.ContentToggle, moving, true, undoName);
                    if (removed > 0) logs.Add("从原槽「" + sourceName + "」内容开关移除 " + removed + " 项");
                }

                // 1b. 离开服装：从服装部件中移除这些素材的网格。
                if (source == AssetCategory.Clothes && info.PartsContainer != null)
                {
                    int removedParts = 0;
                    foreach (ModularAvatarObjectToggle t in info.PartsContainer.GetComponentsInChildren<ModularAvatarObjectToggle>(true))
                    {
                        removedParts += RemoveToggleTargets(t, moving, true, undoName);
                    }

                    if (removedParts > 0) logs.Add("已从服装部件中移除 " + removedParts + " 个网格引用");
                }

                // 2. 目标槽：第一个空槽，没有则新建。
                string desired = IsNumberedDefaultName(sourceName, source) ? null : sourceName;
                SlotInfo dst = null;
                foreach (SlotInfo s in targetInfo.Slots)
                {
                    if (s.IsEmpty && s.GameObject != null && s.GameObject != slotObject)
                    {
                        dst = s;
                        break;
                    }
                }

                bool created = false;
                if (dst == null)
                {
                    HashSet<string> names = new HashSet<string>();
                    for (int i = 0; i < targetInfo.Container.childCount; i++) names.Add(targetInfo.Container.GetChild(i).name);
                    string newName = desired != null
                        ? AppearanceStructure.MakeUniqueName(desired, names)
                        : AppearanceStructure.NextSlotName(target, names);
                    Transform after = targetInfo.Slots.Count > 0
                        ? targetInfo.Slots[targetInfo.Slots.Count - 1].GameObject.transform
                        : (targetInfo.DefaultItem != null ? targetInfo.DefaultItem.transform : null);
                    int siblingIndex = after != null ? after.GetSiblingIndex() + 1 : targetInfo.Container.childCount;
                    dst = AppearanceStructure.CreateSlot(targetInfo, newName, siblingIndex,
                        AppearanceStructure.FindSiblingIcon(targetInfo));
                    if (dst == null) throw new InvalidOperationException("新建槽位失败：" + newName);
                    created = true;
                    logs.Add("新建槽位「" + newName + "」（" + QuickOutfitConst.ContainerName(target) + "）");
                }
                else
                {
                    logs.Add("使用空槽「" + dst.GameObject.name + "」（" + QuickOutfitConst.ContainerName(target) + "）");
                }

                // 3. 移动、补绑定、保持关闭。
                foreach (GameObject mat in physical)
                {
                    if (mat == null) continue;
                    Undo.SetTransformParent(mat.transform, dst.GameObject.transform, undoName);
                    PrepareForTarget(mat, target, logs);

                    // 绑定时临时开启（Setup Outfit 等对关闭的物体可能不完整），绑定后关闭。
                    Undo.RecordObject(mat, undoName);
                    mat.SetActive(true);
                    BindingHelper.Bind(mat, target, avatar, logs, warnings);
                    Undo.SetCurrentGroupName(undoName);
                    if (mat == null) throw new InvalidOperationException("绑定过程中素材丢失");
                    Undo.RecordObject(mat, undoName);
                    mat.SetActive(false);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(mat);
                    logs.Add("「" + mat.name + "」→「" + dst.GameObject.name + "」");
                }

                foreach (GameObject refObj in referenced)
                {
                    if (refObj == null) continue;
                    Undo.RecordObject(refObj, undoName);
                    refObj.SetActive(false);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(refObj);
                    warnings.Add("「" + refObj.name + "」不在槽位之下，只迁移了开关引用，未移动其位置、未补绑定");
                }

                // 4. 写入目标内容 Toggle（Active=ON）。
                ModularAvatarObjectToggle content = dst.ContentToggle;
                if (content == null)
                {
                    content = Undo.AddComponent<ModularAvatarObjectToggle>(dst.GameObject);
                    dst.ContentToggle = content;
                }

                foreach (GameObject mat in moving)
                {
                    if (mat != null) ApplyPlanner.AppendContent(content, mat);
                }

                // 5. 槽名沿用原槽名。
                if (!created && desired != null)
                {
                    ApplyPlanner.RenameSlot(dst.GameObject, desired, warnings, logs);
                }

                // 6. 原槽为空则删除（每类至少保留 1 个）。「空」= 没有子物体，且内容 Toggle 中没有剩余的有效引用。
                if (slotObject != null)
                {
                    bool hasRemainingRefs = HasValidTargets(src.ContentToggle, slotObject);
                    bool isEmpty = slotObject.transform.childCount == 0 && !hasRemainingRefs;
                    if (isEmpty && sourceSlotCount > 1 && AppearanceStructure.CanDestroy(slotObject))
                    {
                        logs.Add("删除空槽「" + sourceName + "」");
                        Undo.DestroyObjectImmediate(slotObject);
                    }
                    else if (isEmpty)
                    {
                        logs.Add("保留原槽「" + sourceName + "」（" + QuickOutfitConst.ContainerName(source) + "至少保留 1 个槽）");
                    }
                    else if (hasRemainingRefs && slotObject.transform.childCount == 0)
                    {
                        warnings.Add("原槽「" + sourceName + "」的内容开关中仍有其它有效引用，已保留该槽");
                    }
                }

                // 7. 刷新引用。
                AppearanceStructure.RefreshReferences(root, avatar.transform, logs);
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                Undo.SetCurrentGroupName(undoName);
                Undo.CollapseUndoOperations(group);

                List<string> names2 = new List<string>();
                foreach (GameObject m in moving)
                {
                    if (m != null) names2.Add(m.name);
                }

                message = "已将「" + string.Join("、", names2) + "」从" + QuickOutfitConst.CategoryLabel(source) + "转为" +
                          QuickOutfitConst.CategoryLabel(target) + "，放入「" + (dst.GameObject != null ? dst.GameObject.name : "?") +
                          "」（可 Ctrl+Z 撤销）";
                StringBuilder log = new StringBuilder("[快速改模] " + message);
                foreach (string l in logs) log.Append("\n  ").Append(l);
                foreach (string w in warnings) log.Append("\n  警告：").Append(w);
                Debug.Log(log.ToString());
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                message = "转换失败，已回滚：" + e.Message;
                return false;
            }
        }

        /// <summary>在各类别的槽位中查找 slotObject。</summary>
        public static SlotInfo FindSlot(AppearanceInfo info, GameObject slotObject, out AssetCategory category)
        {
            category = AssetCategory.Unknown;
            if (info == null || slotObject == null) return null;
            foreach (KeyValuePair<AssetCategory, CategoryInfo> kv in info.Categories)
            {
                foreach (SlotInfo s in kv.Value.Slots)
                {
                    if (s.GameObject == slotObject)
                    {
                        category = kv.Key;
                        return s;
                    }
                }
            }

            return null;
        }

        /// <summary>把槽位内容分为：槽位下的直接子物体（会被移动）与仅被内容 Toggle 引用的外部对象。</summary>
        private static void SplitContents(SlotInfo slot, GameObject root, out List<GameObject> physical,
            out List<GameObject> referenced)
        {
            physical = new List<GameObject>();
            referenced = new List<GameObject>();
            foreach (GameObject go in AppearanceOverview.CollectSlotContents(slot, root))
            {
                if (go.transform.parent == slot.GameObject.transform) physical.Add(go);
                else referenced.Add(go);
            }
        }

        /// <summary>名字是否为该类别的默认编号名（服装_3、配饰_2、旧式 配饰2）。</summary>
        public static bool IsNumberedDefaultName(string name, AssetCategory category)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string prefix = QuickOutfitConst.SlotPrefix(category);
            if (string.IsNullOrEmpty(prefix)) return false;
            return Regex.IsMatch(name, "^" + Regex.Escape(prefix) + @"_?\d+$");
        }

        /// <summary>
        /// 按目标类别做绑定前的准备：转为衣服且素材带 Hips 时，移除根上的 Bone Proxy（否则与 Setup Outfit 冲突）。
        /// </summary>
        private static void PrepareForTarget(GameObject material, AssetCategory target, List<string> logs)
        {
            if (material == null) return;
            if (target != AssetCategory.Clothes) return;
            if (material.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null) return;
            ModularAvatarBoneProxy proxy = material.GetComponent<ModularAvatarBoneProxy>();
            if (proxy == null || !BindingHelper.HasHipsHeuristic(material)) return;
            Undo.DestroyObjectImmediate(proxy);
            logs?.Add("「" + material.name + "」转为衣服：移除根上的 Bone Proxy，改用 Setup Outfit");
        }

        // ================================================================== Toggle 工具

        /// <summary>
        /// 从 ObjectToggle 中移除引用了 targets（includeDescendants 时也包括其子孙）的项，同时清除完全空的项。返回移除的数量。
        /// </summary>
        public static int RemoveToggleTargets(ModularAvatarObjectToggle toggle, ICollection<GameObject> targets,
            bool includeDescendants, string undoName)
        {
            if (toggle == null || targets == null || targets.Count == 0 || toggle.Objects == null) return 0;
            List<ResolvedToggle> resolved = AppearanceStructure.ResolveTargets(toggle);
            List<ToggledObject> list = new List<ToggledObject>();
            int removed = 0;
            for (int i = 0; i < toggle.Objects.Count; i++)
            {
                GameObject t = i < resolved.Count ? resolved[i].Target : null;
                if (t != null && Matches(t, targets, includeDescendants))
                {
                    removed++;
                    continue;
                }

                list.Add(toggle.Objects[i]);
            }

            if (removed == 0) return 0;
            Undo.RecordObject(toggle, string.IsNullOrEmpty(undoName) ? QuickOutfitConst.UndoName : undoName);
            toggle.Objects = list;
            PrefabUtility.RecordPrefabInstancePropertyModifications(toggle);
            return removed;
        }

        /// <summary>
        /// 在 ObjectToggle 中把 go 设为 {go, active}：已存在则更新 Active，否则追加；同时清除完全空的项。返回是否有改动。
        /// </summary>
        public static bool SetToggleTarget(ModularAvatarObjectToggle toggle, GameObject go, bool active, string undoName)
        {
            if (toggle == null || go == null) return false;
            List<ResolvedToggle> resolved = AppearanceStructure.ResolveTargets(toggle);
            List<ToggledObject> list = new List<ToggledObject>();
            bool found = false;
            bool changed = false;
            int count = toggle.Objects != null ? toggle.Objects.Count : 0;
            for (int i = 0; i < count; i++)
            {
                GameObject t = i < resolved.Count ? resolved[i].Target : null;
                string path = i < resolved.Count ? resolved[i].ReferencePath : string.Empty;
                if (t == null && string.IsNullOrEmpty(path))
                {
                    changed = true;
                    continue;
                }

                if (t == go)
                {
                    if (found)
                    {
                        changed = true;
                        continue;
                    }

                    found = true;
                    ToggledObject entry = toggle.Objects[i];
                    if (entry.Active != active)
                    {
                        entry.Active = active;
                        changed = true;
                    }

                    list.Add(entry);
                    continue;
                }

                list.Add(toggle.Objects[i]);
            }

            if (!found)
            {
                list.Add(new ToggledObject { Object = new AvatarObjectReference(go), Active = active });
                changed = true;
            }

            if (!changed) return false;
            Undo.RecordObject(toggle, string.IsNullOrEmpty(undoName) ? QuickOutfitConst.UndoName : undoName);
            toggle.Objects = list;
            PrefabUtility.RecordPrefabInstancePropertyModifications(toggle);
            return true;
        }

        /// <summary>ObjectToggle 中是否还有能解析到对象的引用（忽略指向 self 自身的项）。toggle 为空返回 false。</summary>
        public static bool HasValidTargets(ModularAvatarObjectToggle toggle, GameObject self)
        {
            if (toggle == null) return false;
            foreach (ResolvedToggle rt in AppearanceStructure.ResolveTargets(toggle))
            {
                if (rt.Target != null && rt.Target != self) return true;
            }

            return false;
        }

        /// <summary>ObjectToggle 是否引用了 go。</summary>
        public static bool ToggleReferences(ModularAvatarObjectToggle toggle, GameObject go)
        {
            if (toggle == null || go == null) return false;
            foreach (ResolvedToggle rt in AppearanceStructure.ResolveTargets(toggle))
            {
                if (rt.Target == go) return true;
            }

            return false;
        }

        private static bool Matches(GameObject t, ICollection<GameObject> targets, bool includeDescendants)
        {
            if (targets.Contains(t)) return true;
            if (!includeDescendants) return false;
            foreach (GameObject g in targets)
            {
                if (g != null && t.transform.IsChildOf(g.transform)) return true;
            }

            return false;
        }
    }
}
