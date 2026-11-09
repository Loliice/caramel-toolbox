using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>负责定位当前操作的角色（VRCAvatarDescriptor）。</summary>
    public static class AvatarLocator
    {
        /// <summary>取某个场景物体所属的角色；资源对象返回 null。</summary>
        public static VRCAvatarDescriptor FromGameObject(GameObject go)
        {
            if (go == null) return null;
            if (EditorUtility.IsPersistent(go)) return null;
            return go.GetComponentInParent<VRCAvatarDescriptor>(true);
        }

        /// <summary>当前选中物体所属的角色。</summary>
        public static VRCAvatarDescriptor FromSelection()
        {
            return FromGameObject(Selection.activeGameObject);
        }

        /// <summary>列出所有已加载场景中激活的角色（层级顺序）。</summary>
        public static List<VRCAvatarDescriptor> FindActiveSceneAvatars()
        {
            List<VRCAvatarDescriptor> result = new List<VRCAvatarDescriptor>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject rootGo in scene.GetRootGameObjects())
                {
                    if (rootGo == null) continue;
                    VRCAvatarDescriptor[] descriptors = rootGo.GetComponentsInChildren<VRCAvatarDescriptor>(false);
                    foreach (VRCAvatarDescriptor descriptor in descriptors)
                    {
                        if (descriptor != null && descriptor.gameObject.activeInHierarchy && !result.Contains(descriptor))
                        {
                            result.Add(descriptor);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 自动检测角色：1) 选中物体所属角色；2) 场景中激活的角色（唯一则用它，多个取第一个并提示）。
        /// </summary>
        /// <param name="notice">需要展示给用户的提示，没有则为空字符串。</param>
        public static VRCAvatarDescriptor AutoDetect(out string notice)
        {
            notice = string.Empty;
            VRCAvatarDescriptor fromSelection = FromSelection();
            if (fromSelection != null) return fromSelection;

            List<VRCAvatarDescriptor> avatars = FindActiveSceneAvatars();
            if (avatars.Count == 0) return null;
            if (avatars.Count > 1)
            {
                notice = "场景中有多个角色，已选 " + avatars[0].gameObject.name + "，可点“用选中”切换";
            }

            return avatars[0];
        }
    }
}
