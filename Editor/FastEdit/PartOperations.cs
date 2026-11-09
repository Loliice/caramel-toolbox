using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>「服装部件」下的一个部件（外套 / 鞋子 …）。</summary>
    public class PartNode
    {
        public GameObject GameObject = null;
        public ModularAvatarMenuItem MenuItem = null;
        public List<ModularAvatarObjectToggle> Toggles = new List<ModularAvatarObjectToggle>();
    }

    /// <summary>服装部件的读取、网格分配、新增（主窗口按钮行使用）。</summary>
    public static class PartOperations
    {
        public const string UndoName = "快速改模：服装部件";

        public static Transform FindPartsContainer(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            return root != null ? AppearanceStructure.FindPartsContainer(root) : null;
        }

        /// <summary>部件 = 服装部件的直接子物体中带 MenuItem 或 ObjectToggle 的（层级顺序）。</summary>
        public static List<PartNode> GetParts(Transform container)
        {
            List<PartNode> parts = new List<PartNode>();
            if (container == null) return parts;
            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                ModularAvatarObjectToggle[] toggles = child.GetComponents<ModularAvatarObjectToggle>();
                if (mi == null && toggles.Length == 0) continue;
                PartNode node = new PartNode { GameObject = child.gameObject, MenuItem = mi };
                node.Toggles.AddRange(toggles);
                parts.Add(node);
            }

            return parts;
        }

        /// <summary>
        /// 把网格加入部件的 MA Object Toggle（Active = OFF），网格会自动从其它部件移除。一个 Undo 组。
        /// 返回成功加入的数量；messages 收集提示与拒绝原因。
        /// </summary>
        public static int AssignMeshes(VRCAvatarDescriptor avatar, GameObject partObject, IList<GameObject> meshes,
            List<string> messages)
        {
            if (avatar == null || partObject == null)
            {
                messages.Add("角色或部件已丢失");
                return 0;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            Transform container = root != null ? AppearanceStructure.FindPartsContainer(root) : null;
            if (container == null || partObject.transform.parent != container)
            {
                messages.Add("「" + partObject.name + "」不是服装部件");
                return 0;
            }

            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            Dictionary<GameObject, string> owners = AppearanceOverview.ToOwnerMap(AppearanceOverview.BuildClothesMeshGroups(info));

            List<GameObject> accepted = new List<GameObject>();
            if (meshes != null)
            {
                foreach (GameObject go in meshes)
                {
                    string reason;
                    if (!Validate(avatar, go, owners, out reason))
                    {
                        messages.Add(reason);
                        continue;
                    }

                    if (!accepted.Contains(go)) accepted.Add(go);
                }
            }

            if (accepted.Count == 0) return 0;

            List<PartNode> parts = GetParts(container);
            PartNode part = null;
            foreach (PartNode p in parts)
            {
                if (p.GameObject == partObject) part = p;
            }

            if (part == null)
            {
                messages.Add("「" + partObject.name + "」缺少 MA Menu Item / MA Object Toggle");
                return 0;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            int added = 0;
            try
            {
                ModularAvatarObjectToggle main = part.Toggles.Count > 0 ? part.Toggles[0] : null;
                if (main == null)
                {
                    main = Undo.AddComponent<ModularAvatarObjectToggle>(part.GameObject);
                    part.Toggles.Add(main);
                }

                foreach (GameObject mesh in accepted)
                {
                    foreach (PartNode other in parts)
                    {
                        if (other == part || other.GameObject == null) continue;
                        int removed = 0;
                        foreach (ModularAvatarObjectToggle t in other.Toggles)
                        {
                            removed += SlotOperations.RemoveToggleTargets(t, new[] { mesh }, false, UndoName);
                        }

                        if (removed > 0)
                        {
                            messages.Add("「" + mesh.name + "」已从「" + other.GameObject.name + "」移到「" + part.GameObject.name + "」");
                        }
                    }

                    for (int i = 1; i < part.Toggles.Count; i++)
                    {
                        SlotOperations.RemoveToggleTargets(part.Toggles[i], new[] { mesh }, false, UndoName);
                    }

                    bool already = SlotOperations.ToggleReferences(main, mesh);
                    SlotOperations.SetToggleTarget(main, mesh, false, UndoName);
                    if (already) messages.Add("「" + mesh.name + "」已在「" + part.GameObject.name + "」中（已确保为 OFF）");
                    else added++;
                }

                if (part.MenuItem != null && part.MenuItem.isDefault)
                {
                    Undo.RecordObject(part.MenuItem, UndoName);
                    part.MenuItem.isDefault = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(part.MenuItem);
                }

                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(group);
                messages.Insert(0, "已添加 " + added + " 个网格到「" + part.GameObject.name + "」（状态 OFF，可 Ctrl+Z 撤销）");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                messages.Insert(0, "添加失败，已回滚：" + e.Message);
                return 0;
            }

            return added;
        }

        private static bool Validate(VRCAvatarDescriptor avatar, GameObject go, Dictionary<GameObject, string> owners,
            out string reason)
        {
            reason = string.Empty;
            if (go == null)
            {
                reason = "对象已丢失";
                return false;
            }

            if (EditorUtility.IsPersistent(go))
            {
                reason = "「" + go.name + "」是 Project 资源，请在层级中选中衣服里的网格";
                return false;
            }

            if (AvatarLocator.FromGameObject(go) != avatar)
            {
                reason = "「" + go.name + "」不属于当前角色";
                return false;
            }

            if (go.GetComponent<SkinnedMeshRenderer>() == null && go.GetComponent<MeshRenderer>() == null)
            {
                reason = "「" + go.name + "」不是网格（没有 Skinned Mesh Renderer / Mesh Renderer），请展开衣服后选中具体网格";
                return false;
            }

            ProtectedCheck check = BodyMeshDetector.Check(go, avatar);
            if (check.IsProtected)
            {
                reason = "「" + go.name + "」" + check.Reason + "，是身体/脸部网格（或骨骼），不能作为服装部件";
                return false;
            }

            if (!owners.ContainsKey(go))
            {
                reason = "「" + go.name + "」不在任何服装槽或默认服装之下，不能作为服装部件";
                return false;
            }

            return true;
        }

        /// <summary>在服装部件下新建部件（MA Menu Item + MA Object Toggle，与其它部件相同）。一个 Undo 组。</summary>
        public static GameObject AddPart(VRCAvatarDescriptor avatar, string partName, out string message)
        {
            message = string.Empty;
            string name = partName != null ? partName.Trim() : string.Empty;
            Transform container = FindPartsContainer(avatar);
            if (container == null)
            {
                message = "该角色的外观设置中没有「" + QuickOutfitConst.PartContainerName + "」";
                return null;
            }

            if (name.Length == 0)
            {
                message = "部件名不能为空";
                return null;
            }

            if (name.IndexOf('/') >= 0)
            {
                message = "部件名不能包含「/」";
                return null;
            }

            if (AppearanceStructure.FindDirectChild(container, name) != null)
            {
                message = "已存在同名部件「" + name + "」";
                return null;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("新增服装部件");
            GameObject created = AppearanceStructure.CreatePartSlot(container, name);
            Undo.CollapseUndoOperations(group);
            if (created == null)
            {
                message = "新增部件失败";
                return null;
            }

            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            message = "已新增服装部件「" + name + "」（MA Menu Item + MA Object Toggle，可 Ctrl+Z 撤销）";
            return created;
        }
    }
}
