using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>「当前外观」中的一项内容（槽内素材 / 默认项子物体 / 部件引用的网格）。</summary>
    public class OverviewContent
    {
        public GameObject Target = null;
        /// <summary>部件内容：所属衣服（槽位名）；不在衣服中时为空。</summary>
        public string Owner = string.Empty;
        /// <summary>内容是否不在槽位之下（仅由内容 Toggle 引用）。</summary>
        public bool ReferencedOnly = false;
    }

    /// <summary>「当前外观」中的一行：槽位 / 默认项 / 服装部件。</summary>
    public class OverviewSlot
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public GameObject GameObject = null;
        public string Name = string.Empty;
        public bool IsDefault = false;
        public bool IsPart = false;
        public bool IsEmpty = true;
        public List<OverviewContent> Contents = new List<OverviewContent>();
    }

    /// <summary>「当前外观」中的一个分组（服装切换 / 发型切换 / 配饰 / 道具 / 服装部件）。</summary>
    public class OverviewGroup
    {
        public string Key = string.Empty;
        public string Title = string.Empty;
        public AssetCategory Category = AssetCategory.Unknown;
        public bool IsParts = false;
        public bool Missing = false;
        public List<OverviewSlot> Slots = new List<OverviewSlot>();

        public int OccupiedCount
        {
            get
            {
                int n = 0;
                foreach (OverviewSlot s in Slots)
                {
                    if (!s.IsDefault && !s.IsEmpty) n++;
                }

                return n;
            }
        }

        public int SlotCount
        {
            get
            {
                int n = 0;
                foreach (OverviewSlot s in Slots)
                {
                    if (!s.IsDefault) n++;
                }

                return n;
            }
        }
    }

    /// <summary>一件衣服（默认服装或某个服装槽）中的网格列表。</summary>
    public class ClothesMeshGroup
    {
        public string Owner = string.Empty;
        public GameObject OwnerObject = null;
        public bool IsDefault = false;
        public List<GameObject> Meshes = new List<GameObject>();
    }

    /// <summary>
    /// 角色外观设置的只读展示模型。由窗口缓存，仅在角色变化 / 层级变化 / Undo/Redo 后重建。
    /// </summary>
    public class AppearanceOverview
    {
        public VRCAvatarDescriptor Avatar = null;
        public GameObject Root = null;
        public bool HasAppearance = false;
        public List<OverviewGroup> Groups = new List<OverviewGroup>();
        /// <summary>需要以红色提示的问题（例如默认服装里有身体/脸部网格）。</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>读取角色的外观设置并生成展示模型。</summary>
        public static AppearanceOverview Build(VRCAvatarDescriptor avatar)
        {
            AppearanceOverview overview = new AppearanceOverview { Avatar = avatar };
            if (avatar == null) return overview;
            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            overview.Root = root;
            overview.HasAppearance = root != null;
            if (root == null) return overview;

            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                OverviewGroup group = new OverviewGroup
                {
                    Key = "cat" + (int)category,
                    Title = QuickOutfitConst.ContainerName(category),
                    Category = category,
                };
                overview.Groups.Add(group);

                CategoryInfo ci = info.Get(category);
                if (ci == null)
                {
                    group.Missing = true;
                    continue;
                }

                if (ci.DefaultItem != null)
                {
                    OverviewSlot def = new OverviewSlot
                    {
                        Category = category,
                        GameObject = ci.DefaultItem,
                        Name = ci.DefaultItem.name,
                        IsDefault = true,
                    };
                    Transform dt = ci.DefaultItem.transform;
                    for (int i = 0; i < dt.childCount; i++)
                    {
                        GameObject child = dt.GetChild(i).gameObject;
                        def.Contents.Add(new OverviewContent { Target = child });
                        ProtectedCheck check = BodyMeshDetector.Check(child, avatar);
                        if (check.IsProtected)
                        {
                            overview.Warnings.Add("「" + ci.DefaultItem.name + "」中的「" + child.name + "」" + check.Reason +
                                                  "，" + BodyMeshDetector.ConsequenceText + "，建议移回角色根");
                        }
                    }

                    def.IsEmpty = def.Contents.Count == 0;
                    group.Slots.Add(def);
                }

                foreach (SlotInfo slot in ci.Slots)
                {
                    if (slot.GameObject == null) continue;
                    OverviewSlot row = new OverviewSlot
                    {
                        Category = category,
                        GameObject = slot.GameObject,
                        Name = slot.GameObject.name,
                        IsEmpty = slot.IsEmpty,
                    };
                    foreach (GameObject content in CollectSlotContents(slot, root))
                    {
                        row.Contents.Add(new OverviewContent
                        {
                            Target = content,
                            ReferencedOnly = !content.transform.IsChildOf(slot.GameObject.transform),
                        });
                    }

                    group.Slots.Add(row);
                }
            }

            OverviewGroup parts = new OverviewGroup
            {
                Key = "parts",
                Title = QuickOutfitConst.PartContainerName,
                IsParts = true,
            };
            overview.Groups.Add(parts);
            if (info.PartsContainer == null)
            {
                parts.Missing = true;
                return overview;
            }

            Dictionary<GameObject, string> owners = ToOwnerMap(BuildClothesMeshGroups(info));
            Transform pc = info.PartsContainer;
            for (int i = 0; i < pc.childCount; i++)
            {
                Transform child = pc.GetChild(i);
                ModularAvatarObjectToggle[] toggles = child.GetComponents<ModularAvatarObjectToggle>();
                if (toggles.Length == 0 && child.GetComponent<ModularAvatarMenuItem>() == null) continue;
                OverviewSlot row = new OverviewSlot
                {
                    GameObject = child.gameObject,
                    Name = child.name,
                    IsPart = true,
                };
                foreach (GameObject target in CollectToggleTargets(toggles))
                {
                    string owner;
                    owners.TryGetValue(target, out owner);
                    row.Contents.Add(new OverviewContent { Target = target, Owner = owner ?? string.Empty });
                }

                row.IsEmpty = row.Contents.Count == 0;
                parts.Slots.Add(row);
            }

            return overview;
        }

        /// <summary>
        /// 槽位内容：槽位的直接子物体 + 内容 Toggle 引用的、不在槽位之下也不在外观设置结构内的对象（去重，层级顺序）。
        /// </summary>
        public static List<GameObject> CollectSlotContents(SlotInfo slot, GameObject appearanceRoot)
        {
            List<GameObject> result = new List<GameObject>();
            if (slot == null || slot.GameObject == null) return result;
            Transform st = slot.GameObject.transform;
            for (int i = 0; i < st.childCount; i++) result.Add(st.GetChild(i).gameObject);

            if (slot.ContentToggle == null) return result;
            foreach (ResolvedToggle rt in AppearanceStructure.ResolveTargets(slot.ContentToggle))
            {
                GameObject t = rt.Target;
                if (t == null || t == slot.GameObject) continue;
                if (t.transform.IsChildOf(st)) continue;
                if (appearanceRoot != null && t.transform.IsChildOf(appearanceRoot.transform)) continue;
                if (slot.MenuItem != null)
                {
                    VRCAvatarDescriptor desc = slot.MenuItem.GetComponentInParent<VRCAvatarDescriptor>(true);
                    if (desc != null && t == desc.gameObject) continue;
                }

                if (!result.Contains(t)) result.Add(t);
            }

            return result;
        }

        /// <summary>一组 ObjectToggle 引用的全部对象（去重，忽略丢失的引用）。</summary>
        public static List<GameObject> CollectToggleTargets(IEnumerable<ModularAvatarObjectToggle> toggles)
        {
            List<GameObject> result = new List<GameObject>();
            if (toggles == null) return result;
            foreach (ModularAvatarObjectToggle toggle in toggles)
            {
                if (toggle == null) continue;
                foreach (ResolvedToggle rt in AppearanceStructure.ResolveTargets(toggle))
                {
                    if (rt.Target != null && !result.Contains(rt.Target)) result.Add(rt.Target);
                }
            }

            return result;
        }

        /// <summary>
        /// 按衣服分组列出网格（SkinnedMeshRenderer / MeshRenderer）：默认服装（若存在）+ 每个服装槽（含空槽）。
        /// 网格物体本身即槽位/默认项时不计。
        /// </summary>
        public static List<ClothesMeshGroup> BuildClothesMeshGroups(AppearanceInfo info)
        {
            List<ClothesMeshGroup> groups = new List<ClothesMeshGroup>();
            if (info == null) return groups;
            CategoryInfo clothes = info.Get(AssetCategory.Clothes);
            if (clothes == null) return groups;

            if (clothes.DefaultItem != null)
            {
                groups.Add(CollectMeshes(clothes.DefaultItem, clothes.DefaultItem.name, true));
            }

            foreach (SlotInfo slot in clothes.Slots)
            {
                if (slot.GameObject == null) continue;
                groups.Add(CollectMeshes(slot.GameObject, slot.GameObject.name, false));
            }

            return groups;
        }

        private static ClothesMeshGroup CollectMeshes(GameObject owner, string title, bool isDefault)
        {
            ClothesMeshGroup group = new ClothesMeshGroup { Owner = title, OwnerObject = owner, IsDefault = isDefault };
            foreach (Renderer r in owner.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (r.gameObject == owner) continue;
                if (!group.Meshes.Contains(r.gameObject)) group.Meshes.Add(r.gameObject);
            }

            return group;
        }

        /// <summary>网格 → 所属衣服名。</summary>
        public static Dictionary<GameObject, string> ToOwnerMap(List<ClothesMeshGroup> groups)
        {
            Dictionary<GameObject, string> map = new Dictionary<GameObject, string>();
            if (groups == null) return map;
            foreach (ClothesMeshGroup g in groups)
            {
                foreach (GameObject m in g.Meshes)
                {
                    if (m != null && !map.ContainsKey(m)) map[m] = g.Owner;
                }
            }

            return map;
        }
    }
}
