using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 快速应用的规划器与执行器。窗口预览与实际执行都先 BuildPlan 再 Execute(plan)，保证一致。
    /// </summary>
    public static class ApplyPlanner
    {
        /// <summary>每个类别在规划过程中的模拟状态。</summary>
        private class CategoryState
        {
            public CategoryInfo Info = null;
            public List<bool> Used = new List<bool>();
            /// <summary>容器下当前（模拟）的名字集合，用于重名检测。</summary>
            public HashSet<string> Names = new HashSet<string>();
            /// <summary>出现过的所有名字，用于新槽编号（避免复用刚被改名的编号）。</summary>
            public HashSet<string> EverNames = new HashSet<string>();
            public int NewSlotCount = 0;
        }

        // ================================================================== 规划

        /// <summary>根据当前角色与素材列表生成规划（只读，不修改场景）。</summary>
        public static ApplyPlan BuildPlan(VRCAvatarDescriptor avatar, IList<OutfitEntry> entries)
        {
            ApplyPlan plan = new ApplyPlan { Avatar = avatar };
            if (avatar == null)
            {
                plan.Errors.Add("未指定角色");
                return plan;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            bool isTemplate = root == null;
            if (isTemplate)
            {
                root = AppearanceStructure.LoadTemplate();
                if (root == null)
                {
                    plan.Errors.Add("找不到模板 " + QuickOutfitConst.TemplatePath);
                    return plan;
                }
            }

            plan.CreateAppearance = isTemplate;
            AppearanceInfo info = AppearanceStructure.Analyze(root, isTemplate);
            plan.Info = info;

            // 规范化改名预览。
            foreach (AssetCategory category in new[] { AssetCategory.Accessory, AssetCategory.Prop })
            {
                CategoryInfo ci = info.Get(category);
                if (ci == null) continue;
                for (int i = 0; i < ci.Container.childCount && i < ci.ChildNames.Count; i++)
                {
                    string oldName = ci.Container.GetChild(i).name;
                    if (oldName != ci.ChildNames[i])
                    {
                        plan.NormalizeRenames.Add(new PlanRename
                        {
                            Category = category, OldName = oldName, NewName = ci.ChildNames[i],
                        });
                    }
                }
            }

            // 初始化各类别状态。
            Dictionary<AssetCategory, CategoryState> states = new Dictionary<AssetCategory, CategoryState>();
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                CategoryInfo ci = info.Get(category);
                if (ci == null)
                {
                    plan.ExistingSlotCounts[category] = -1;
                    plan.ExistingEmptyFlags[category] = new List<bool>();
                    continue;
                }

                CategoryState st = new CategoryState { Info = ci };
                List<bool> flags = new List<bool>();
                foreach (SlotInfo s in ci.Slots)
                {
                    st.Used.Add(false);
                    flags.Add(s.IsEmpty);
                }

                foreach (string n in ci.ChildNames)
                {
                    st.Names.Add(n);
                    st.EverNames.Add(n);
                }

                plan.ExistingSlotCounts[category] = ci.Slots.Count;
                plan.ExistingEmptyFlags[category] = flags;
                states[category] = st;
            }

            // 逐条目分配槽位。
            HashSet<GameObject> seen = new HashSet<GameObject>();
            if (entries != null)
            {
                foreach (OutfitEntry entry in entries)
                {
                    if (entry == null) continue;
                    PlanItem item = new PlanItem { Entry = entry, Category = entry.Category };
                    plan.Items.Add(item);

                    if (entry.Source == null)
                    {
                        item.Skip = true;
                        item.SkipReason = "素材已丢失";
                        continue;
                    }

                    if (!seen.Add(entry.Source))
                    {
                        item.Skip = true;
                        item.SkipReason = "重复的素材";
                        continue;
                    }

                    if (entry.Category == AssetCategory.Unknown)
                    {
                        item.Skip = true;
                        item.SkipReason = "类型未识别";
                        plan.Errors.Add("「" + entry.DisplayName + "」类型未识别，请手动指定类型");
                        continue;
                    }

                    if (!entry.IsAsset)
                    {
                        VRCAvatarDescriptor owner = AvatarLocator.FromGameObject(entry.Source);
                        if (entry.Source == avatar.gameObject || avatar.transform.IsChildOf(entry.Source.transform))
                        {
                            item.Skip = true;
                            item.SkipReason = "不能放入角色本身";
                            continue;
                        }

                        if (!isTemplate && owner == avatar)
                        {
                            SlotInfo occupied = info.FindSlotContaining(entry.Source);
                            if (occupied != null)
                            {
                                item.Skip = true;
                                item.SkipReason = "已在槽位「" + occupied.Name + "」中";
                                continue;
                            }

                            if (info.IsStructuralNode(entry.Source))
                            {
                                item.Skip = true;
                                item.SkipReason = "是外观设置的结构节点";
                                continue;
                            }

                            if (info.FindDefaultItemContaining(entry.Source) != null)
                            {
                                item.Skip = true;
                                item.SkipReason = "这是默认服装/默认发型的内容，不能作为素材";
                                continue;
                            }
                        }

                        if (!AppearanceStructure.CanReparent(entry.Source))
                        {
                            item.Skip = true;
                            item.SkipReason = "属于 Prefab 实例内部，无法移动";
                            continue;
                        }
                    }

                    CategoryState state;
                    if (!states.TryGetValue(entry.Category, out state))
                    {
                        item.Skip = true;
                        item.SkipReason = "外观设置中缺少「" + QuickOutfitConst.ContainerName(entry.Category) + "」节点";
                        plan.Errors.Add(item.SkipReason);
                        continue;
                    }

                    // 第一个未占用的空槽；没有则新建。
                    int index = -1;
                    for (int i = 0; i < state.Info.Slots.Count; i++)
                    {
                        if (state.Info.Slots[i].IsEmpty && !state.Used[i])
                        {
                            index = i;
                            break;
                        }
                    }

                    if (index >= 0)
                    {
                        state.Used[index] = true;
                        item.IsNewSlot = false;
                        item.ExistingSlotIndex = index;
                        item.SlotNameBefore = state.Info.Slots[index].Name;
                    }
                    else
                    {
                        string newName = AppearanceStructure.NextSlotName(entry.Category, state.EverNames);
                        item.IsNewSlot = true;
                        item.ExistingSlotIndex = -1;
                        item.SlotNameBefore = newName;
                        state.Names.Add(newName);
                        state.EverNames.Add(newName);
                        state.NewSlotCount++;
                    }

                    // 槽位改名（与执行时逐条目改名的顺序一致）。
                    item.SlotNameAfter = item.SlotNameBefore;
                    string desired = entry.SlotName != null ? entry.SlotName.Trim() : string.Empty;
                    if (desired.Length > 0 && desired != item.SlotNameBefore)
                    {
                        state.Names.Remove(item.SlotNameBefore);
                        string finalName = AppearanceStructure.MakeUniqueName(desired, state.Names);
                        if (finalName != desired)
                        {
                            item.Note = "与同级重名，改为「" + finalName + "」";
                            plan.Warnings.Add("槽位名「" + desired + "」与同级重名，将使用「" + finalName + "」");
                        }

                        item.SlotNameAfter = finalName;
                        state.Names.Add(finalName);
                        state.EverNames.Add(finalName);
                    }

                    item.Bind = BindingHelper.Describe(entry.Source, entry.Category);
                    if (item.Bind == BindAction.SetupOutfitNoHips)
                    {
                        plan.Warnings.Add("「" + entry.DisplayName + "」⚠ 未找到 Hips，Setup Outfit 将跳过（放置后请手动绑定骨骼）");
                    }
                }
            }

            // 清理：删除空槽，但每类至少保留 1 个；没有槽则新建。
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                CategoryState state;
                if (!states.TryGetValue(category, out state))
                {
                    plan.Warnings.Add("外观设置中缺少「" + QuickOutfitConst.ContainerName(category) + "」节点，跳过该类清理");
                    continue;
                }

                List<bool> emptyFlags = new List<bool>();
                foreach (SlotInfo s in state.Info.Slots) emptyFlags.Add(s.IsEmpty);
                bool needCreate;
                List<int> toDeleteIdx = SelectEmptySlotsToDelete(emptyFlags, state.Used, state.NewSlotCount, out needCreate);
                if (needCreate)
                {
                    string name = AppearanceStructure.NextSlotName(category, state.EverNames);
                    state.EverNames.Add(name);
                    state.Names.Add(name);
                    plan.MinimumCreations.Add(new PlanCreation { Category = category, SlotName = name });
                }

                foreach (int idx in toDeleteIdx)
                {
                    SlotInfo slot = state.Info.Slots[idx];
                    if (!isTemplate && !AppearanceStructure.CanDestroy(slot.GameObject))
                    {
                        plan.Warnings.Add("空槽「" + slot.Name + "」属于 Prefab 实例，无法删除，已保留");
                        continue;
                    }

                    plan.Deletions.Add(new PlanDeletion { Category = category, ExistingSlotIndex = idx, SlotName = slot.Name });
                }

                // 菜单项数量提示。
                int deletedHere = 0;
                foreach (PlanDeletion d in plan.Deletions)
                {
                    if (d.Category == category) deletedHere++;
                }

                int createdHere = state.NewSlotCount;
                foreach (PlanCreation c in plan.MinimumCreations)
                {
                    if (c.Category == category) createdHere++;
                }

                int finalCount = state.Info.MenuItemCount + createdHere - deletedHere;
                if (finalCount > QuickOutfitConst.MenuItemLimit)
                {
                    plan.Warnings.Add("「" + QuickOutfitConst.ContainerName(category) + "」菜单将有 " + finalCount +
                                      " 项，超过 8 项时 MA 会自动拆分出「More」子菜单");
                }
            }

            return plan;
        }

        // ================================================================== 执行

        /// <summary>按规划执行。整个过程为一个 Undo 组「快速改模」。</summary>
        public static ApplyResult Execute(ApplyPlan plan)
        {
            ApplyResult result = new ApplyResult();
            if (plan == null)
            {
                result.Summary = "没有可执行的规划";
                return result;
            }

            if (!plan.IsValid)
            {
                result.Summary = "规划存在错误：" + string.Join("；", plan.Errors);
                return result;
            }

            VRCAvatarDescriptor avatar = plan.Avatar;
            if (avatar == null)
            {
                result.Summary = "角色已丢失";
                return result;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(QuickOutfitConst.UndoName);

            List<string> logs = result.Logs;
            List<string> warnings = result.Warnings;
            int placedCount = 0;
            int skippedCount = 0;

            try
            {
                // 1. 确保外观设置存在。
                GameObject root = AppearanceStructure.EnsureAppearance(avatar, logs);
                if (root == null) throw new InvalidOperationException("无法创建外观设置");

                // 2~4. 规范化、修复默认项与槽结构、刷新引用。
                AppearanceStructure.NormalizeSlotNames(root, logs);
                AppearanceStructure.EnsureDefaults(root, logs);
                AppearanceStructure.RepairSlots(root, logs);
                AppearanceStructure.RefreshReferences(root, avatar.transform, logs);

                // 5. 重新分析并校验与规划一致。
                AppearanceInfo info = AppearanceStructure.Analyze(root, false);
                ValidateAgainstPlan(plan, info);

                // 5b. 收集身体形态键，供放置素材时同步同名形态键（身体→素材）。
                Dictionary<string, SkinnedMeshRenderer> bodyShapes = BlendShapeSyncHelper.CollectBodyShapes(avatar, root);

                // 记录待删除的槽位对象（在放置之前按索引取，避免受新建槽位影响）。
                List<GameObject> toDelete = new List<GameObject>();
                foreach (PlanDeletion d in plan.Deletions)
                {
                    CategoryInfo ci = info.Get(d.Category);
                    if (ci != null && d.ExistingSlotIndex >= 0 && d.ExistingSlotIndex < ci.Slots.Count)
                    {
                        toDelete.Add(ci.Slots[d.ExistingSlotIndex].GameObject);
                    }
                }

                Dictionary<AssetCategory, Transform> insertAfter = new Dictionary<AssetCategory, Transform>();
                foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
                {
                    CategoryInfo ci = info.Get(category);
                    if (ci == null) continue;
                    if (ci.Slots.Count > 0) insertAfter[category] = ci.Slots[ci.Slots.Count - 1].GameObject.transform;
                    else if (ci.DefaultItem != null) insertAfter[category] = ci.DefaultItem.transform;
                    else insertAfter[category] = null;
                }

                // 6~11. 逐条目放置、绑定、关闭、写入 Toggle、改名。
                foreach (PlanItem item in plan.Items)
                {
                    if (item.Skip)
                    {
                        skippedCount++;
                        logs.Add("跳过「" + (item.Entry != null ? item.Entry.DisplayName : "?") + "」：" + item.SkipReason);
                        continue;
                    }

                    CategoryInfo ci = info.Get(item.Category);
                    if (ci == null) throw new InvalidOperationException("缺少类别容器：" + QuickOutfitConst.ContainerName(item.Category));

                    SlotInfo slot;
                    Transform previousAfter = insertAfter.ContainsKey(item.Category) ? insertAfter[item.Category] : null;
                    if (item.IsNewSlot)
                    {
                        Transform after = previousAfter;
                        int siblingIndex = after != null ? after.GetSiblingIndex() + 1 : ci.Container.childCount;
                        slot = AppearanceStructure.CreateSlot(ci, item.SlotNameBefore, siblingIndex,
                            AppearanceStructure.FindSiblingIcon(ci));
                        if (slot == null) throw new InvalidOperationException("新建槽位失败：" + item.SlotNameBefore);
                        insertAfter[item.Category] = slot.GameObject.transform;
                        logs.Add("新建槽位「" + item.SlotNameBefore + "」");
                    }
                    else
                    {
                        slot = ci.Slots[item.ExistingSlotIndex];
                    }

                    GameObject material = PlaceMaterial(item.Entry, slot.GameObject, logs);
                    if (material == null)
                    {
                        warnings.Add("「" + item.Entry.DisplayName + "」放置失败");
                        if (item.IsNewSlot && slot.GameObject != null)
                        {
                            // 本次新建的槽位放置失败：立即删除，避免留下空槽。
                            logs.Add("删除放置失败的新建槽位「" + slot.GameObject.name + "」");
                            Undo.DestroyObjectImmediate(slot.GameObject);
                            insertAfter[item.Category] = previousAfter;
                        }

                        continue;
                    }

                    // 绑定在关闭之前做。
                    BindingHelper.Bind(material, item.Category, avatar, logs, warnings);
                    Undo.SetCurrentGroupName(QuickOutfitConst.UndoName);

                    // 形态键同步：素材与身体有同名形态键时，加 MA BlendshapeSync（身体→素材）。
                    int syncCount = BlendShapeSyncHelper.ApplySyncToMaterial(material, bodyShapes, QuickOutfitConst.UndoName);
                    if (syncCount > 0) logs.Add("「" + material.name + "」同步 " + syncCount + " 个同名形态键（跟随身体）");

                    Undo.RecordObject(material, QuickOutfitConst.UndoName);
                    material.SetActive(false);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(material);

                    ModularAvatarObjectToggle content = slot.ContentToggle;
                    if (content == null)
                    {
                        content = Undo.AddComponent<ModularAvatarObjectToggle>(slot.GameObject);
                        slot.ContentToggle = content;
                    }

                    AppendContent(content, material);

                    if (item.WillRename)
                    {
                        RenameSlot(slot.GameObject, item.SlotNameAfter, warnings, logs);
                    }

                    placedCount++;
                    logs.Add("「" + material.name + "」→「" + slot.GameObject.name + "」（" +
                             QuickOutfitConst.CategoryLabel(item.Category) + "，" +
                             QuickOutfitConst.BindActionLabel(item.Bind) + "）");
                }

                // 12. 清理空槽 / 补最少槽。
                foreach (GameObject go in toDelete)
                {
                    if (go == null) continue;
                    if (go.transform.childCount > 0)
                    {
                        warnings.Add("槽位「" + go.name + "」已有内容，未删除");
                        continue;
                    }

                    if (!AppearanceStructure.CanDestroy(go))
                    {
                        warnings.Add("空槽「" + go.name + "」属于 Prefab 实例，无法删除，已保留");
                        continue;
                    }

                    logs.Add("删除空槽「" + go.name + "」");
                    Undo.DestroyObjectImmediate(go);
                }

                foreach (PlanCreation c in plan.MinimumCreations)
                {
                    CategoryInfo ci = info.Get(c.Category);
                    if (ci == null) continue;
                    Transform after = insertAfter.ContainsKey(c.Category) ? insertAfter[c.Category] : null;
                    int siblingIndex = after != null && after.parent == ci.Container
                        ? after.GetSiblingIndex() + 1
                        : ci.Container.childCount;
                    SlotInfo created = AppearanceStructure.CreateSlot(ci, c.SlotName, siblingIndex,
                        AppearanceStructure.FindSiblingIcon(ci));
                    if (created != null)
                    {
                        insertAfter[c.Category] = created.GameObject.transform;
                        logs.Add("补建空槽「" + c.SlotName + "」");
                    }
                }

                // 13. 改名/删除后再次刷新引用路径，标记场景。
                AppearanceStructure.RefreshReferences(root, avatar.transform, logs);
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);

                Undo.SetCurrentGroupName(QuickOutfitConst.UndoName);
                Undo.CollapseUndoOperations(group);

                result.Success = true;
                StringBuilder sb = new StringBuilder();
                sb.Append("快速改模完成：放置 ").Append(placedCount).Append(" 个素材");
                if (skippedCount > 0) sb.Append("，跳过 ").Append(skippedCount).Append(" 个");
                if (plan.Deletions.Count > 0) sb.Append("，删除空槽 ").Append(plan.Deletions.Count).Append(" 个");
                if (warnings.Count > 0) sb.Append("，警告 ").Append(warnings.Count).Append(" 条");
                sb.Append("（可 Ctrl+Z 一次撤销）");
                result.Summary = sb.ToString();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                result.Success = false;
                result.Summary = "执行失败，已回滚：" + e.Message;
            }

            StringBuilder log = new StringBuilder();
            log.AppendLine("[快速改模] " + result.Summary);
            foreach (string l in logs) log.AppendLine("  " + l);
            foreach (string w in warnings) log.AppendLine("  警告：" + w);
            if (result.Success) Debug.Log(log.ToString());
            else Debug.LogWarning(log.ToString());
            return result;
        }

        // ================================================================== 空槽清理规则（规划与独立清理共用）

        /// <summary>
        /// 计算应删除的空槽索引：每类删除未使用的空槽，但至少保留 1 个槽。
        /// emptyFlags[i] 为第 i 个已有槽位是否为空；used[i] 为本次是否会放入素材；extraOccupied 为本次新建并占用的槽数。
        /// 若该类全部为空，保留第一个；若该类一个槽都没有且无新建，needCreate=true。只返回空槽，绝不含有内容的槽。
        /// </summary>
        public static List<int> SelectEmptySlotsToDelete(IList<bool> emptyFlags, IList<bool> used, int extraOccupied,
            out bool needCreate)
        {
            needCreate = false;
            List<int> unusedEmpty = new List<int>();
            int occupied = extraOccupied;
            int count = emptyFlags != null ? emptyFlags.Count : 0;
            for (int i = 0; i < count; i++)
            {
                bool isUsed = used != null && i < used.Count && used[i];
                if (!emptyFlags[i] || isUsed) occupied++;
                else unusedEmpty.Add(i);
            }

            if (occupied == 0)
            {
                if (count > 0)
                {
                    if (unusedEmpty.Count > 0) unusedEmpty.RemoveAt(0); // 全部为空：保留第一个。
                }
                else
                {
                    needCreate = true;
                }
            }

            return unusedEmpty;
        }

        /// <summary>
        /// 独立执行一次空槽清理（调用方负责 Undo 分组）：按 <see cref="SelectEmptySlotsToDelete"/> 删除空槽，
        /// 无法删除的 Prefab 内部槽位保留并警告；某类一个槽都没有时补建「前缀_N」。
        /// </summary>
        public static void CleanupEmptySlots(GameObject root, List<string> logs, List<string> warnings)
        {
            if (root == null) return;
            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                CategoryInfo ci = info.Get(category);
                if (ci == null) continue;

                List<bool> emptyFlags = new List<bool>();
                foreach (SlotInfo s in ci.Slots) emptyFlags.Add(s.IsEmpty);
                bool needCreate;
                List<int> indices = SelectEmptySlotsToDelete(emptyFlags, null, 0, out needCreate);
                foreach (int idx in indices)
                {
                    GameObject go = ci.Slots[idx].GameObject;
                    if (go == null) continue;
                    if (!AppearanceStructure.CanDestroy(go))
                    {
                        warnings?.Add("空槽「" + go.name + "」属于 Prefab 实例，无法删除，已保留");
                        continue;
                    }

                    logs?.Add("删除空槽「" + go.name + "」");
                    Undo.DestroyObjectImmediate(go);
                }

                if (needCreate)
                {
                    HashSet<string> used = new HashSet<string>(ci.ChildNames);
                    string name = AppearanceStructure.NextSlotName(category, used);
                    Transform after = ci.DefaultItem != null ? ci.DefaultItem.transform : null;
                    int siblingIndex = after != null ? after.GetSiblingIndex() + 1 : ci.Container.childCount;
                    if (AppearanceStructure.CreateSlot(ci, name, siblingIndex, null) != null)
                    {
                        logs?.Add("补建空槽「" + name + "」");
                    }
                }
            }
        }

        /// <summary>执行时重新分析的结构必须与规划时一致，否则中止（防止预览与执行不一致）。</summary>
        private static void ValidateAgainstPlan(ApplyPlan plan, AppearanceInfo info)
        {
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                int expected;
                if (!plan.ExistingSlotCounts.TryGetValue(category, out expected)) continue;
                CategoryInfo ci = info.Get(category);
                int actual = ci != null ? ci.Slots.Count : -1;
                if (actual != expected)
                {
                    throw new InvalidOperationException("「" + QuickOutfitConst.ContainerName(category) +
                                                        "」槽位数量与预览不一致，请刷新后重试");
                }

                List<bool> flags = plan.ExistingEmptyFlags[category];
                for (int i = 0; ci != null && i < ci.Slots.Count && i < flags.Count; i++)
                {
                    if (ci.Slots[i].IsEmpty != flags[i])
                    {
                        throw new InvalidOperationException("槽位「" + ci.Slots[i].Name + "」占用状态与预览不一致，请刷新后重试");
                    }
                }
            }
        }

        /// <summary>
        /// 放置素材：prefab 资源实例化到槽位下（本地位置/旋转归零，缩放保持）；场景对象改父级（保持世界位姿）。不改素材名。
        /// </summary>
        private static GameObject PlaceMaterial(OutfitEntry entry, GameObject slot, List<string> logs)
        {
            if (entry == null || entry.Source == null || slot == null) return null;
            if (entry.IsAsset)
            {
                GameObject instance = PrefabUtility.InstantiatePrefab(entry.Source, slot.transform) as GameObject;
                if (instance == null)
                {
                    instance = UnityEngine.Object.Instantiate(entry.Source, slot.transform);
                    if (instance != null) instance.name = entry.Source.name;
                }

                if (instance == null) return null;
                Undo.RegisterCreatedObjectUndo(instance, QuickOutfitConst.UndoName);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                logs.Add("实例化「" + instance.name + "」到「" + slot.name + "」");
                return instance;
            }

            Undo.SetTransformParent(entry.Source.transform, slot.transform, QuickOutfitConst.UndoName);
            logs.Add("移动「" + entry.Source.name + "」到「" + slot.name + "」");
            return entry.Source;
        }

        /// <summary>向内容 Toggle 追加 {素材, Active=true}（去重，清除完全空的项）。也供「转为」使用。</summary>
        public static void AppendContent(ModularAvatarObjectToggle toggle, GameObject material)
        {
            if (toggle == null || material == null) return;
            Undo.RecordObject(toggle, QuickOutfitConst.UndoName);
            List<ResolvedToggle> resolved = AppearanceStructure.ResolveTargets(toggle);
            List<ToggledObject> list = new List<ToggledObject>();
            for (int i = 0; i < toggle.Objects.Count; i++)
            {
                GameObject target = i < resolved.Count ? resolved[i].Target : null;
                string path = i < resolved.Count ? resolved[i].ReferencePath : string.Empty;
                if (target == material) continue;
                if (target == null && string.IsNullOrEmpty(path)) continue;
                list.Add(toggle.Objects[i]);
            }

            list.Add(new ToggledObject { Object = new AvatarObjectReference(material), Active = true });
            toggle.Objects = list;
            PrefabUtility.RecordPrefabInstancePropertyModifications(toggle);
        }

        /// <summary>改槽位名（兄弟重名则追加「 (2)」并警告）。绝不改素材名。也供「转为」使用。</summary>
        public static void RenameSlot(GameObject slot, string desired, List<string> warnings, List<string> logs)
        {
            if (slot == null || string.IsNullOrEmpty(desired) || slot.name == desired) return;
            HashSet<string> siblings = new HashSet<string>();
            Transform parent = slot.transform.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform c = parent.GetChild(i);
                    if (c != slot.transform) siblings.Add(c.name);
                }
            }

            string finalName = AppearanceStructure.MakeUniqueName(desired, siblings);
            if (finalName != desired)
            {
                warnings.Add("槽位名「" + desired + "」与同级重名，已改为「" + finalName + "」");
            }

            Undo.RecordObject(slot, QuickOutfitConst.UndoName);
            logs.Add("槽位改名「" + slot.name + "」→「" + finalName + "」");
            slot.name = finalName;
        }
    }
}
