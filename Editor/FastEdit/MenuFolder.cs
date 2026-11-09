using System.IO;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 折叠 / 展开原模型菜单：
    /// 折叠 = 在角色根下创建「原模型菜单」（MA Menu Installer + 子菜单类型的 MA Menu Item，指向原 Expressions Menu），
    /// 并把 Avatar Descriptor 的 Expressions Menu 换成一个空的占位菜单。构建时原菜单的全部内容会作为一个子菜单
    /// 出现在总菜单中，其它 MA 安装的菜单仍直接显示在总菜单里。展开 = 还原 Expressions Menu 并删除该物体。
    /// 两者都可 Ctrl+Z 撤销（占位菜单资源除外，它是一个空菜单，保留无害）。
    /// </summary>
    public static class MenuFolder
    {
        public const string FolderObjectName = "原模型菜单";
        private const string UndoName = "快速改模：折叠原模型菜单";

        /// <summary>已折叠时返回「原模型菜单」上的 Menu Item，否则返回 null。</summary>
        public static ModularAvatarMenuItem FindFolded(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            Transform t = avatar.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child.name != FolderObjectName) continue;
                if (child.GetComponent<ModularAvatarMenuInstaller>() == null) continue;
                ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                if (mi != null && mi.Control != null && mi.Control.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                    mi.Control.subMenu != null)
                {
                    return mi;
                }
            }

            return null;
        }

        public static bool IsFolded(VRCAvatarDescriptor avatar)
        {
            return FindFolded(avatar) != null;
        }

        public static bool Fold(VRCAvatarDescriptor avatar, out string message)
        {
            message = string.Empty;
            if (avatar == null)
            {
                message = "请先指定当前角色";
                return false;
            }

            if (IsFolded(avatar))
            {
                message = "原模型菜单已经折叠";
                return false;
            }

            VRCExpressionsMenu original = avatar.expressionsMenu;
            if (original == null || original.controls == null || original.controls.Count == 0)
            {
                message = "该角色的 Expressions Menu 为空，无需折叠";
                return false;
            }

            VRCExpressionsMenu placeholder = GetOrCreatePlaceholder(original);
            if (placeholder == null)
            {
                message = "无法创建占位菜单资源";
                return false;
            }

            int targetingOriginal = 0;
            foreach (ModularAvatarMenuInstaller installer in avatar.GetComponentsInChildren<ModularAvatarMenuInstaller>(true))
            {
                if (installer != null && installer.installTargetMenu == original) targetingOriginal++;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            GameObject go = new GameObject(FolderObjectName);
            go.layer = avatar.gameObject.layer;
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(avatar.transform, false);

            ModularAvatarMenuInstaller menuInstaller = Undo.AddComponent<ModularAvatarMenuInstaller>(go);
            menuInstaller.installTargetMenu = null; // 安装到总菜单（角色根菜单）。
            menuInstaller.menuToAppend = null;

            ModularAvatarMenuItem item = Undo.AddComponent<ModularAvatarMenuItem>(go);
            item.Control = new VRCExpressionsMenu.Control
            {
                name = string.Empty,
                icon = null,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty },
                value = 1,
                subMenu = original,
                subParameters = System.Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
                labels = System.Array.Empty<VRCExpressionsMenu.Control.Label>(),
            };
            item.MenuSource = SubmenuSource.MenuAsset;
            item.label = string.Empty;

            Undo.RecordObject(avatar, UndoName);
            avatar.expressionsMenu = placeholder;
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);

            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Undo.SetCurrentGroupName(UndoName);
            Undo.CollapseUndoOperations(group);

            message = "已折叠原模型菜单：原菜单「" + original.name + "」（" + original.controls.Count + " 项）将作为子菜单「" +
                      FolderObjectName + "」放在总菜单中；Expressions Menu 已换成空的占位菜单「" + placeholder.name + "」（可 Ctrl+Z 撤销）";
            if (targetingOriginal > 0)
            {
                message += "\n注意：有 " + targetingOriginal + " 个 MA Menu Installer 以原菜单为安装目标，它们的内容会一起进入「" +
                           FolderObjectName + "」";
            }

            Debug.Log("[快速改模] " + message);
            return true;
        }

        public static bool Unfold(VRCAvatarDescriptor avatar, out string message)
        {
            message = string.Empty;
            ModularAvatarMenuItem folded = FindFolded(avatar);
            if (folded == null)
            {
                message = "原模型菜单没有折叠";
                return false;
            }

            VRCExpressionsMenu original = folded.Control.subMenu as VRCExpressionsMenu;
            if (original == null)
            {
                message = "「" + FolderObjectName + "」引用的不是 Expressions Menu，无法还原";
                return false;
            }

            const string undoName = "快速改模：展开原模型菜单";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(avatar, undoName);
            avatar.expressionsMenu = original;
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
            Undo.DestroyObjectImmediate(folded.gameObject);
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Undo.SetCurrentGroupName(undoName);
            Undo.CollapseUndoOperations(group);
            message = "已展开原模型菜单：Expressions Menu 已还原为「" + original.name + "」（可 Ctrl+Z 撤销）";
            return true;
        }

        /// <summary>
        /// VRChat SDK 要求有参数时必须提供 Expressions Menu，因此用一个空菜单占位（与 MA「Extract Menu」做法一致）。
        /// 放在原菜单旁边；原菜单在 Packages 中时放到 Assets 根目录。已有同名空菜单则复用。
        /// </summary>
        private static VRCExpressionsMenu GetOrCreatePlaceholder(VRCExpressionsMenu original)
        {
            string assetPath = AssetDatabase.GetAssetPath(original);
            string basePath;
            if (string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Packages/") || assetPath.StartsWith("Packages\\"))
            {
                basePath = "Assets/" + original.name + " placeholder";
            }
            else
            {
                string dir = Path.GetDirectoryName(assetPath);
                basePath = (string.IsNullOrEmpty(dir) ? "Assets" : dir.Replace('\\', '/')) + "/" +
                           Path.GetFileNameWithoutExtension(assetPath) + " placeholder";
            }

            for (int i = 0; i < 100; i++)
            {
                string path = basePath + (i > 0 ? " " + i : string.Empty) + ".asset";
                VRCExpressionsMenu existing = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
                if (existing != null)
                {
                    if (existing.controls == null || existing.controls.Count == 0) return existing;
                    continue;
                }

                if (File.Exists(path)) continue;
                VRCExpressionsMenu created = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                AssetDatabase.CreateAsset(created, path);
                AssetDatabase.SaveAssets();
                return created;
            }

            return null;
        }
    }
}
