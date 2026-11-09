using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>ObjectToggle 中一项的解析结果。</summary>
    public struct ResolvedToggle
    {
        public GameObject Target;
        public bool Active;
        public string ReferencePath;
    }

    /// <summary>一个槽位（服装_1、配饰_2 等）的分析结果。</summary>
    public class SlotInfo
    {
        public GameObject GameObject = null;
        /// <summary>规范化后的名字（配饰1 → 配饰_1）。</summary>
        public string Name = string.Empty;
        public bool IsEmpty = true;
        public ModularAvatarMenuItem MenuItem = null;
        public ModularAvatarObjectToggle ContentToggle = null;
        public ModularAvatarObjectToggle DefaultRefToggle = null;
    }

    /// <summary>一个类别容器（服装切换/发型切换/配饰/道具）的分析结果。</summary>
    public class CategoryInfo
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public Transform Container = null;
        public GameObject DefaultItem = null;
        public string DefaultParam = string.Empty;
        public List<SlotInfo> Slots = new List<SlotInfo>();
        /// <summary>容器所有直接子物体的规范化名字（层级顺序）。</summary>
        public List<string> ChildNames = new List<string>();
        /// <summary>容器直接子物体中带 MenuItem 的数量。</summary>
        public int MenuItemCount = 0;

        public int OccupiedCount
        {
            get
            {
                int n = 0;
                foreach (SlotInfo s in Slots)
                {
                    if (!s.IsEmpty) n++;
                }

                return n;
            }
        }
    }

    /// <summary>外观设置整体分析结果。</summary>
    public class AppearanceInfo
    {
        public GameObject Root = null;
        public bool IsTemplate = false;
        public Transform PartsContainer = null;
        public Dictionary<AssetCategory, CategoryInfo> Categories = new Dictionary<AssetCategory, CategoryInfo>();

        public CategoryInfo Get(AssetCategory category)
        {
            CategoryInfo info;
            return Categories.TryGetValue(category, out info) ? info : null;
        }

        /// <summary>若 go 位于某个槽位之下（或就是槽位内容根），返回该槽位。</summary>
        public SlotInfo FindSlotContaining(GameObject go)
        {
            if (go == null) return null;
            foreach (CategoryInfo ci in Categories.Values)
            {
                foreach (SlotInfo slot in ci.Slots)
                {
                    if (slot.GameObject == null) continue;
                    if (go.transform != slot.GameObject.transform && go.transform.IsChildOf(slot.GameObject.transform))
                    {
                        return slot;
                    }
                }
            }

            return null;
        }

        /// <summary>若 go 是 默认服装/默认发型 的子孙（不含默认项自身），返回该默认项；否则 null。</summary>
        public GameObject FindDefaultItemContaining(GameObject go)
        {
            if (go == null) return null;
            foreach (CategoryInfo ci in Categories.Values)
            {
                if (ci.DefaultItem == null) continue;
                Transform d = ci.DefaultItem.transform;
                if (go.transform != d && go.transform.IsChildOf(d)) return ci.DefaultItem;
            }

            return null;
        }

        /// <summary>go 是否为外观设置的结构节点（根、容器、默认项、槽位）。</summary>
        public bool IsStructuralNode(GameObject go)
        {
            if (go == null || Root == null) return false;
            if (go == Root) return true;
            Transform parent = go.transform.parent;
            if (parent == Root.transform) return true;
            return parent != null && parent.parent == Root.transform;
        }
    }

    /// <summary>外观设置的查找、实例化、规范化、槽位识别与建槽。</summary>
    public static class AppearanceStructure
    {
        private static readonly Regex AccessoryLegacyName = new Regex(@"^配饰(\d+)$");
        private static readonly Regex PropLegacyName = new Regex(@"^道具(\d+)$");

        // ------------------------------------------------------------------ 查找 / 模板

        /// <summary>在角色根的直接子物体中查找名为「外观设置」的物体。</summary>
        public static GameObject FindAppearanceRoot(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            Transform root = avatar.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == QuickOutfitConst.AppearanceRootName) return child.gameObject;
            }

            return null;
        }

        /// <summary>加载模板 prefab（优先 GUID，失败回退固定路径）。</summary>
        public static GameObject LoadTemplate()
        {
            GameObject template = null;
            string path = AssetDatabase.GUIDToAssetPath(QuickOutfitConst.TemplateGuid);
            if (!string.IsNullOrEmpty(path))
            {
                template = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            if (template == null)
            {
                template = AssetDatabase.LoadAssetAtPath<GameObject>(QuickOutfitConst.TemplatePath);
            }

            return template;
        }

        /// <summary>
        /// 确保角色下存在外观设置；不存在则实例化模板、归零变换并完全解包（全部注册 Undo）。
        /// </summary>
        public static GameObject EnsureAppearance(VRCAvatarDescriptor avatar, List<string> logs)
        {
            if (avatar == null) return null;
            GameObject existing = FindAppearanceRoot(avatar);
            if (existing != null) return existing;

            GameObject template = LoadTemplate();
            if (template == null)
            {
                logs?.Add("错误：找不到模板 " + QuickOutfitConst.TemplatePath);
                return null;
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(template, avatar.transform) as GameObject;
            if (instance == null)
            {
                logs?.Add("错误：实例化模板失败");
                return null;
            }

            Undo.RegisterCreatedObjectUndo(instance, QuickOutfitConst.UndoName);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // 完全解包模板实例。若角色本身是 Prefab 实例，「外观设置」是其 added object，解包后即为普通的 added 子树。
            try
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(instance))
                {
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.UserAction);
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("解包「外观设置」模板实例失败：" + e.Message, e);
            }

            if (PrefabUtility.IsAnyPrefabInstanceRoot(instance) ||
                (PrefabUtility.IsPartOfPrefabInstance(instance) && !CanReparent(instance)))
            {
                throw new InvalidOperationException("「外观设置」模板实例未能完全解包（仍处于 Prefab 实例状态），已中止并回滚");
            }

            Undo.RecordObject(instance, QuickOutfitConst.UndoName);
            instance.name = QuickOutfitConst.AppearanceRootName;
            logs?.Add("已从模板创建「外观设置」并完全解包");
            return instance;
        }

        // ------------------------------------------------------------------ 规范化名字

        /// <summary>计算容器下各直接子物体的规范化名字（配饰N→配饰_N；目标名已存在时保持原名）。</summary>
        public static List<string> ComputeNormalizedNames(Transform container, AssetCategory category)
        {
            List<string> result = new List<string>();
            if (container == null) return result;

            HashSet<string> original = new HashSet<string>();
            for (int i = 0; i < container.childCount; i++) original.Add(container.GetChild(i).name);

            HashSet<string> used = new HashSet<string>(original);
            for (int i = 0; i < container.childCount; i++)
            {
                string name = container.GetChild(i).name;
                string normalized = NormalizeName(name, category);
                if (normalized != name && !used.Contains(normalized))
                {
                    used.Remove(name);
                    used.Add(normalized);
                    result.Add(normalized);
                }
                else
                {
                    result.Add(name);
                }
            }

            return result;
        }

        /// <summary>单个名字的规范化（不考虑重名）。</summary>
        public static string NormalizeName(string name, AssetCategory category)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            Regex regex = null;
            if (category == AssetCategory.Accessory) regex = AccessoryLegacyName;
            else if (category == AssetCategory.Prop) regex = PropLegacyName;
            if (regex == null) return name;

            Match m = regex.Match(name);
            if (!m.Success) return name;
            return QuickOutfitConst.SlotPrefix(category) + "_" + m.Groups[1].Value;
        }

        /// <summary>执行规范化改名（带 Undo）。</summary>
        public static void NormalizeSlotNames(GameObject root, List<string> logs)
        {
            if (root == null) return;
            foreach (AssetCategory category in new[] { AssetCategory.Accessory, AssetCategory.Prop })
            {
                Transform container = FindContainer(root, category);
                if (container == null) continue;
                List<string> names = ComputeNormalizedNames(container, category);
                for (int i = 0; i < container.childCount && i < names.Count; i++)
                {
                    GameObject child = container.GetChild(i).gameObject;
                    if (child.name == names[i]) continue;
                    logs?.Add("规范化：" + child.name + " → " + names[i]);
                    Undo.RecordObject(child, QuickOutfitConst.UndoName);
                    child.name = names[i];
                }
            }
        }

        // ------------------------------------------------------------------ 分析

        public static Transform FindContainer(GameObject root, AssetCategory category)
        {
            if (root == null) return null;
            return FindDirectChild(root.transform, QuickOutfitConst.ContainerName(category));
        }

        public static Transform FindPartsContainer(GameObject root)
        {
            if (root == null) return null;
            return FindDirectChild(root.transform, QuickOutfitConst.PartContainerName);
        }

        public static Transform FindDirectChild(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;
            }

            return null;
        }

        /// <summary>
        /// 只读分析外观设置（场景实例或模板资源均可）。槽位按结构识别而非名字。
        /// </summary>
        public static AppearanceInfo Analyze(GameObject root, bool isTemplate)
        {
            AppearanceInfo info = new AppearanceInfo { Root = root, IsTemplate = isTemplate };
            if (root == null) return info;

            info.PartsContainer = FindPartsContainer(root);
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                Transform container = FindContainer(root, category);
                if (container == null) continue;
                info.Categories[category] = AnalyzeCategory(container, category);
            }

            return info;
        }

        private static CategoryInfo AnalyzeCategory(Transform container, AssetCategory category)
        {
            CategoryInfo ci = new CategoryInfo { Category = category, Container = container };
            ci.ChildNames = ComputeNormalizedNames(container, category);

            for (int i = 0; i < container.childCount; i++)
            {
                if (container.GetChild(i).GetComponent<ModularAvatarMenuItem>() != null) ci.MenuItemCount++;
            }

            if (QuickOutfitConst.UsesDefaultItem(category))
            {
                ci.DefaultItem = FindDefaultItem(container, category);
                string param = string.Empty;
                if (ci.DefaultItem != null)
                {
                    ModularAvatarMenuItem defaultMenu = ci.DefaultItem.GetComponent<ModularAvatarMenuItem>();
                    param = GetParameterName(defaultMenu);
                }

                ci.DefaultParam = string.IsNullOrEmpty(param) ? QuickOutfitConst.DefaultParamName(category) : param;

                for (int i = 0; i < container.childCount; i++)
                {
                    Transform child = container.GetChild(i);
                    if (child.gameObject == ci.DefaultItem) continue;
                    ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                    if (mi == null) continue;
                    if (GetParameterName(mi) != ci.DefaultParam) continue;

                    SlotInfo slot = new SlotInfo
                    {
                        GameObject = child.gameObject,
                        Name = i < ci.ChildNames.Count ? ci.ChildNames[i] : child.name,
                        MenuItem = mi,
                    };
                    ClassifyToggles(slot, ci.DefaultItem);
                    slot.IsEmpty = ComputeEmpty(slot);
                    ci.Slots.Add(slot);
                }
            }
            else
            {
                for (int i = 0; i < container.childCount; i++)
                {
                    Transform child = container.GetChild(i);
                    ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                    ModularAvatarObjectToggle toggle = child.GetComponent<ModularAvatarObjectToggle>();
                    if (mi == null || toggle == null) continue;
                    SlotInfo slot = new SlotInfo
                    {
                        GameObject = child.gameObject,
                        Name = i < ci.ChildNames.Count ? ci.ChildNames[i] : child.name,
                        MenuItem = mi,
                        ContentToggle = toggle,
                    };
                    slot.IsEmpty = ComputeEmpty(slot);
                    ci.Slots.Add(slot);
                }
            }

            return ci;
        }

        /// <summary>
        /// 默认项：名为「默认服装/默认发型」的直接子物体；找不到则取被某个兄弟槽位 Toggle 以 Active=false 引用的兄弟对象。
        /// </summary>
        public static GameObject FindDefaultItem(Transform container, AssetCategory category)
        {
            if (container == null) return null;
            Transform byName = FindDirectChild(container, QuickOutfitConst.DefaultItemName(category));
            if (byName != null) return byName.gameObject;

            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                if (child.GetComponent<ModularAvatarMenuItem>() == null) continue;
                ModularAvatarObjectToggle[] toggles = child.GetComponents<ModularAvatarObjectToggle>();
                if (toggles.Length == 0) continue;
                foreach (ResolvedToggle rt in ResolveTargets(toggles[0]))
                {
                    if (rt.Target == null || rt.Active) continue;
                    if (rt.Target.transform.parent == container && rt.Target != child.gameObject)
                    {
                        return rt.Target;
                    }
                }
            }

            return null;
        }

        private static void ClassifyToggles(SlotInfo slot, GameObject defaultItem)
        {
            ModularAvatarObjectToggle[] toggles = slot.GameObject.GetComponents<ModularAvatarObjectToggle>();
            if (defaultItem != null)
            {
                foreach (ModularAvatarObjectToggle t in toggles)
                {
                    if (ToggleReferencesInactive(t, defaultItem))
                    {
                        slot.DefaultRefToggle = t;
                        break;
                    }
                }
            }

            foreach (ModularAvatarObjectToggle t in toggles)
            {
                if (t == slot.DefaultRefToggle) continue;
                slot.ContentToggle = t;
                break;
            }
        }

        private static bool ToggleReferencesInactive(ModularAvatarObjectToggle toggle, GameObject target)
        {
            foreach (ResolvedToggle rt in ResolveTargets(toggle))
            {
                if (rt.Target == target && !rt.Active) return true;
            }

            return false;
        }

        private static bool ComputeEmpty(SlotInfo slot)
        {
            if (slot.GameObject == null) return true;
            if (slot.GameObject.transform.childCount > 0) return false;
            if (slot.ContentToggle == null) return true;
            foreach (ResolvedToggle rt in ResolveTargets(slot.ContentToggle))
            {
                if (rt.Target != null) return false;
            }

            return true;
        }

        public static string GetParameterName(ModularAvatarMenuItem menuItem)
        {
            if (menuItem == null || menuItem.Control == null || menuItem.Control.parameter == null) return string.Empty;
            return menuItem.Control.parameter.name ?? string.Empty;
        }

        // ------------------------------------------------------------------ Toggle 引用解析

        /// <summary>
        /// 读取 ObjectToggle 各项的目标对象。通过 SerializedObject 读取内部 targetObject（避免访问 internal 字段与
        /// MA 的引用缓存），为空时回退到按 referencePath 在角色根下查找。
        /// </summary>
        public static List<ResolvedToggle> ResolveTargets(ModularAvatarObjectToggle toggle)
        {
            List<ResolvedToggle> result = new List<ResolvedToggle>();
            if (toggle == null || toggle.Objects == null) return result;

            SerializedObject so = new SerializedObject(toggle);
            SerializedProperty array = so.FindProperty("m_objects");
            Transform avatarRoot = null;
            bool avatarSearched = false;

            for (int i = 0; i < toggle.Objects.Count; i++)
            {
                ToggledObject entry = toggle.Objects[i];
                ResolvedToggle rt = new ResolvedToggle
                {
                    Active = entry.Active,
                    ReferencePath = entry.Object != null ? (entry.Object.referencePath ?? string.Empty) : string.Empty,
                    Target = null,
                };

                if (array != null && array.isArray && i < array.arraySize)
                {
                    SerializedProperty element = array.GetArrayElementAtIndex(i);
                    SerializedProperty targetProp = element.FindPropertyRelative("Object.targetObject");
                    if (targetProp != null) rt.Target = targetProp.objectReferenceValue as GameObject;
                }

                if (rt.Target == null && !string.IsNullOrEmpty(rt.ReferencePath))
                {
                    if (!avatarSearched)
                    {
                        VRCAvatarDescriptor desc = toggle.GetComponentInParent<VRCAvatarDescriptor>(true);
                        avatarRoot = desc != null ? desc.transform : null;
                        avatarSearched = true;
                    }

                    if (avatarRoot != null)
                    {
                        if (rt.ReferencePath == AvatarObjectReference.AVATAR_ROOT)
                        {
                            rt.Target = avatarRoot.gameObject;
                        }
                        else
                        {
                            Transform found = avatarRoot.Find(rt.ReferencePath);
                            if (found == null)
                            {
                                // 兼容旧路径：去掉每段末尾的「 30M」「 6M」之类后缀再找一次。
                                string legacy = StripLegacySuffixes(rt.ReferencePath);
                                if (legacy != rt.ReferencePath) found = avatarRoot.Find(legacy);
                            }

                            rt.Target = found != null ? found.gameObject : null;
                        }
                    }
                }

                result.Add(rt);
            }

            return result;
        }

        private static readonly Regex LegacySegmentSuffix = new Regex(@"\s+\d+M$");

        /// <summary>把路径每一段末尾的「 数字M」后缀去掉（"外观设置/服装切换 30M/默认服装" → "外观设置/服装切换/默认服装"）。</summary>
        public static string StripLegacySuffixes(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? string.Empty;
            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = LegacySegmentSuffix.Replace(segments[i], string.Empty);
            }

            return string.Join("/", segments);
        }

        /// <summary>计算 MA 所用的引用路径（相对角色根）。</summary>
        public static string ComputeAvatarPath(GameObject target, Transform avatarRoot)
        {
            if (target == null || avatarRoot == null) return string.Empty;
            if (target.transform == avatarRoot) return AvatarObjectReference.AVATAR_ROOT;
            return AnimationUtility.CalculateTransformPath(target.transform, avatarRoot);
        }

        /// <summary>
        /// 刷新外观设置内所有 ObjectToggle 的引用路径（修复模板中旧的 "服装切换 30M" 路径、改名后的路径等）。
        /// </summary>
        public static void RefreshReferences(GameObject root, Transform avatarRoot, List<string> logs)
        {
            if (root == null || avatarRoot == null) return;
            int fixedCount = 0;
            foreach (ModularAvatarObjectToggle toggle in root.GetComponentsInChildren<ModularAvatarObjectToggle>(true))
            {
                if (toggle == null) continue;
                List<ResolvedToggle> resolved = ResolveTargets(toggle);
                bool needUpdate = false;
                for (int i = 0; i < resolved.Count; i++)
                {
                    GameObject target = resolved[i].Target;
                    if (target == null || !target.transform.IsChildOf(avatarRoot)) continue;
                    if (resolved[i].ReferencePath != ComputeAvatarPath(target, avatarRoot))
                    {
                        needUpdate = true;
                        break;
                    }
                }

                if (!needUpdate) continue;

                Undo.RecordObject(toggle, QuickOutfitConst.UndoName);
                List<ToggledObject> list = new List<ToggledObject>(toggle.Objects);
                for (int i = 0; i < list.Count && i < resolved.Count; i++)
                {
                    GameObject target = resolved[i].Target;
                    if (target == null || !target.transform.IsChildOf(avatarRoot)) continue;
                    if (resolved[i].ReferencePath == ComputeAvatarPath(target, avatarRoot)) continue;
                    ToggledObject updated = list[i];
                    updated.Object = new AvatarObjectReference(target);
                    list[i] = updated;
                    fixedCount++;
                }

                toggle.Objects = list;
                PrefabUtility.RecordPrefabInstancePropertyModifications(toggle);
            }

            if (fixedCount > 0) logs?.Add("已刷新 " + fixedCount + " 个对象引用路径");
        }

        // ------------------------------------------------------------------ 修复

        /// <summary>
        /// 确保服装/发型的默认项存在且带 MenuItem，并设置默认项 isDefault=true、同参数其余项 isDefault=false。
        /// </summary>
        public static void EnsureDefaults(GameObject root, List<string> logs)
        {
            if (root == null) return;
            foreach (AssetCategory category in new[] { AssetCategory.Clothes, AssetCategory.Hair })
            {
                Transform container = FindContainer(root, category);
                if (container == null)
                {
                    // 容器缺失（例如外观设置为旧结构或手动创建）：补建，避免后续找不到默认项。
                    GameObject newContainer = new GameObject(QuickOutfitConst.ContainerName(category));
                    newContainer.transform.SetParent(root.transform, false);
                    Undo.RegisterCreatedObjectUndo(newContainer, QuickOutfitConst.UndoName);
                    container = newContainer.transform;
                    logs?.Add("已补建容器「" + newContainer.name + "」");
                }

                GameObject defaultItem = FindDefaultItem(container, category);
                if (defaultItem == null)
                {
                    defaultItem = new GameObject(QuickOutfitConst.DefaultItemName(category));
                    defaultItem.transform.SetParent(container, false);
                    defaultItem.transform.SetSiblingIndex(0);
                    Undo.RegisterCreatedObjectUndo(defaultItem, QuickOutfitConst.UndoName);
                    logs?.Add("已补建默认项「" + defaultItem.name + "」");
                }

                ModularAvatarMenuItem defaultMenu = defaultItem.GetComponent<ModularAvatarMenuItem>();
                if (defaultMenu == null)
                {
                    defaultMenu = Undo.AddComponent<ModularAvatarMenuItem>(defaultItem);
                    ConfigureToggleMenuItem(defaultMenu, QuickOutfitConst.DefaultParamName(category), null);
                    logs?.Add("已为「" + defaultItem.name + "」补齐 MA Menu Item");
                }

                if (defaultMenu.Control == null)
                {
                    Undo.RecordObject(defaultMenu, QuickOutfitConst.UndoName);
                    ConfigureToggleMenuItem(defaultMenu, QuickOutfitConst.DefaultParamName(category), null);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(defaultMenu);
                    logs?.Add("「" + defaultItem.name + "」的 Menu Item 缺少 Control，已初始化为 Toggle");
                }

                string param = GetParameterName(defaultMenu);
                if (string.IsNullOrEmpty(param))
                {
                    Undo.RecordObject(defaultMenu, QuickOutfitConst.UndoName);
                    param = QuickOutfitConst.DefaultParamName(category);
                    if (defaultMenu.Control == null) ConfigureToggleMenuItem(defaultMenu, param, null);
                    else defaultMenu.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = param };
                    PrefabUtility.RecordPrefabInstancePropertyModifications(defaultMenu);
                }

                if (!defaultMenu.isDefault)
                {
                    Undo.RecordObject(defaultMenu, QuickOutfitConst.UndoName);
                    defaultMenu.isDefault = true;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(defaultMenu);
                }

                for (int i = 0; i < container.childCount; i++)
                {
                    Transform child = container.GetChild(i);
                    if (child.gameObject == defaultItem) continue;
                    ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                    if (mi == null || !mi.isDefault) continue;
                    if (GetParameterName(mi) != param) continue;
                    Undo.RecordObject(mi, QuickOutfitConst.UndoName);
                    mi.isDefault = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(mi);
                }
            }
        }

        /// <summary>
        /// 补齐服装/发型槽结构：至少 1 个 MenuItem + 2 个 ObjectToggle，其中一个内容为默认项且 Active=OFF。
        /// </summary>
        public static void RepairSlots(GameObject root, List<string> logs)
        {
            if (root == null) return;
            AppearanceInfo info = Analyze(root, false);
            foreach (AssetCategory category in new[] { AssetCategory.Clothes, AssetCategory.Hair })
            {
                CategoryInfo ci = info.Get(category);
                if (ci == null || ci.DefaultItem == null) continue;
                foreach (SlotInfo slot in ci.Slots)
                {
                    if (slot.DefaultRefToggle == null)
                    {
                        ModularAvatarObjectToggle t = Undo.AddComponent<ModularAvatarObjectToggle>(slot.GameObject);
                        t.Objects = new List<ToggledObject>
                        {
                            new ToggledObject { Object = new AvatarObjectReference(ci.DefaultItem), Active = false },
                        };
                        PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                        logs?.Add("槽位「" + slot.GameObject.name + "」补齐默认项关闭 Toggle");
                    }

                    if (slot.ContentToggle == null)
                    {
                        Undo.AddComponent<ModularAvatarObjectToggle>(slot.GameObject);
                        logs?.Add("槽位「" + slot.GameObject.name + "」补齐内容 Toggle");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 建槽

        /// <summary>配置一个 Toggle 类型的 MA Menu Item。</summary>
        public static void ConfigureToggleMenuItem(ModularAvatarMenuItem menuItem, string parameterName, Texture2D icon)
        {
            if (menuItem == null) return;
            menuItem.Control = new VRCExpressionsMenu.Control
            {
                name = string.Empty,
                icon = icon,
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameterName ?? string.Empty },
                value = 1,
                subMenu = null,
                subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
                labels = Array.Empty<VRCExpressionsMenu.Control.Label>(),
            };
            menuItem.MenuSource = SubmenuSource.Children;
            menuItem.isSynced = true;
            menuItem.isSaved = true;
            menuItem.isDefault = false;
            menuItem.automaticValue = true;
            menuItem.label = string.Empty;
        }

        /// <summary>取同类已有槽位的图标，用于新槽位。</summary>
        public static Texture2D FindSiblingIcon(CategoryInfo ci)
        {
            if (ci == null) return null;
            foreach (SlotInfo slot in ci.Slots)
            {
                if (slot.MenuItem != null && slot.MenuItem.Control != null && slot.MenuItem.Control.icon != null)
                {
                    return slot.MenuItem.Control.icon;
                }
            }

            return null;
        }

        /// <summary>
        /// 新建一个槽位（带 Undo）。服装/发型：MenuItem(参数=默认项参数) + Toggle#1[默认项 OFF] + Toggle#2[]；
        /// 配饰/道具：MenuItem(无参数) + 1 个 Toggle。
        /// </summary>
        public static SlotInfo CreateSlot(CategoryInfo ci, string slotName, int siblingIndex, Texture2D icon)
        {
            if (ci == null || ci.Container == null) return null;
            GameObject go = new GameObject(slotName);
            go.transform.SetParent(ci.Container, false);
            int clamped = Mathf.Clamp(siblingIndex, 0, ci.Container.childCount - 1);
            go.transform.SetSiblingIndex(clamped);
            go.layer = ci.Container.gameObject.layer;
            Undo.RegisterCreatedObjectUndo(go, QuickOutfitConst.UndoName);

            ModularAvatarMenuItem mi = Undo.AddComponent<ModularAvatarMenuItem>(go);
            bool usesDefault = QuickOutfitConst.UsesDefaultItem(ci.Category);
            ConfigureToggleMenuItem(mi, usesDefault ? ci.DefaultParam : string.Empty, icon);

            SlotInfo slot = new SlotInfo { GameObject = go, Name = slotName, MenuItem = mi, IsEmpty = true };
            if (usesDefault)
            {
                ModularAvatarObjectToggle defaultRef = Undo.AddComponent<ModularAvatarObjectToggle>(go);
                List<ToggledObject> refs = new List<ToggledObject>();
                if (ci.DefaultItem != null)
                {
                    refs.Add(new ToggledObject { Object = new AvatarObjectReference(ci.DefaultItem), Active = false });
                }

                defaultRef.Objects = refs;
                slot.DefaultRefToggle = defaultRef;
                slot.ContentToggle = Undo.AddComponent<ModularAvatarObjectToggle>(go);
            }
            else
            {
                slot.ContentToggle = Undo.AddComponent<ModularAvatarObjectToggle>(go);
            }

            return slot;
        }

        /// <summary>在服装部件下新建部件槽（MenuItem Toggle 无参数 + 1 个 ObjectToggle），带 Undo。</summary>
        public static GameObject CreatePartSlot(Transform partsContainer, string partName)
        {
            if (partsContainer == null || string.IsNullOrEmpty(partName)) return null;
            Texture2D icon = null;
            for (int i = 0; i < partsContainer.childCount; i++)
            {
                ModularAvatarMenuItem existing = partsContainer.GetChild(i).GetComponent<ModularAvatarMenuItem>();
                if (existing != null && existing.Control != null && existing.Control.icon != null)
                {
                    icon = existing.Control.icon;
                    break;
                }
            }

            GameObject go = new GameObject(partName);
            go.transform.SetParent(partsContainer, false);
            go.layer = partsContainer.gameObject.layer;
            Undo.RegisterCreatedObjectUndo(go, "新增服装部件");
            ModularAvatarMenuItem mi = Undo.AddComponent<ModularAvatarMenuItem>(go);
            ConfigureToggleMenuItem(mi, string.Empty, icon);
            Undo.AddComponent<ModularAvatarObjectToggle>(go);
            return go;
        }

        /// <summary>
        /// 生成下一个默认槽位名：前缀_N，N = 已用名字中「前缀_数字」最大值 + 1，并保证不与已用名字重复。
        /// </summary>
        public static string NextSlotName(AssetCategory category, ICollection<string> usedNames)
        {
            string prefix = QuickOutfitConst.SlotPrefix(category);
            Regex regex = new Regex("^" + Regex.Escape(prefix) + @"_(\d+)$");
            int max = 0;
            if (usedNames != null)
            {
                foreach (string n in usedNames)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    Match m = regex.Match(n);
                    int value;
                    if (m.Success && int.TryParse(m.Groups[1].Value, out value) && value > max) max = value;
                }
            }

            int next = max + 1;
            string candidate = prefix + "_" + next;
            while (usedNames != null && usedNames.Contains(candidate))
            {
                next++;
                candidate = prefix + "_" + next;
            }

            return candidate;
        }

        /// <summary>在已用名字中为 desired 取一个不重复的名字（追加「 (2)」「 (3)」…）。</summary>
        public static string MakeUniqueName(string desired, ICollection<string> usedNames)
        {
            if (usedNames == null || !usedNames.Contains(desired)) return desired;
            int n = 2;
            string candidate = desired + " (" + n + ")";
            while (usedNames.Contains(candidate))
            {
                n++;
                candidate = desired + " (" + n + ")";
            }

            return candidate;
        }

        // ------------------------------------------------------------------ Prefab 约束

        /// <summary>
        /// 场景对象能否被改父级/删除（Prefab 实例内部对象不能）。
        /// 从自身向上遍历，遇到「最近的 Prefab 实例根」之前若有 added 祖先则可操作；
        /// 到达最近实例根时，仅当它就是自身且自身是 added（如 added 的嵌套 Prefab 实例根）才可操作。
        /// 因此 added 的未解包嵌套 Prefab（如手动拖入未解包的「外观设置.prefab」）内部的子物体会被正确拒绝。
        /// </summary>
        public static bool CanReparent(GameObject go)
        {
            if (go == null) return false;
            if (!PrefabUtility.IsPartOfPrefabInstance(go)) return true;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go)) return true;
            GameObject nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (nearest != null && t.gameObject == nearest)
                {
                    return t.gameObject == go && PrefabUtility.IsAddedGameObjectOverride(go);
                }

                if (PrefabUtility.IsAddedGameObjectOverride(t.gameObject)) return true;
            }

            return false;
        }

        /// <summary>场景对象能否被删除，语义与 <see cref="CanReparent"/> 一致（Prefab 实例内部对象不能）。</summary>
        public static bool CanDestroy(GameObject go)
        {
            return CanReparent(go);
        }
    }
}
