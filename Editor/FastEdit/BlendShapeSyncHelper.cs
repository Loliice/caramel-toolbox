using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 形态键同步：对比素材 mesh 与角色身体的形态键，若有同名键，则在素材 mesh 上添加 MA BlendshapeSync，
    /// 让素材的同名形态键跟随身体同步（身体→素材）。
    /// </summary>
    public static class BlendShapeSyncHelper
    {
        /// <summary>收集身体形态键 → 归属的 SkinnedMeshRenderer（排除外观设置下的网格、口型/眼皮/vrc 表情网格）。</summary>
        public static Dictionary<string, SkinnedMeshRenderer> CollectBodyShapes(VRCAvatarDescriptor avatar, GameObject appearanceRoot)
        {
            Dictionary<string, SkinnedMeshRenderer> result = new Dictionary<string, SkinnedMeshRenderer>();
            if (avatar == null) return result;

            SkinnedMeshRenderer eyelids = null;
            try
            {
                eyelids = avatar.customEyeLookSettings.eyelidsSkinnedMesh;
            }
            catch (System.Exception)
            {
                eyelids = null;
            }

            SkinnedMeshRenderer[] all = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in all)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                GameObject go = smr.gameObject;
                // 排除外观设置下的网格（那些是衣服/发型/配饰，不是身体）。
                if (appearanceRoot != null && go.transform.IsChildOf(appearanceRoot.transform)) continue;
                // 排除口型 / 眼皮 / vrc 表情网格。
                if (avatar.VisemeSkinnedMesh == smr) continue;
                if (eyelids == smr) continue;
                if (BodyMeshDetector.HasVrcBlendshapes(smr)) continue;

                Mesh mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!result.ContainsKey(name)) result[name] = smr;
                }
            }

            return result;
        }

        /// <summary>
        /// 对素材 mesh：找出与身体同名的形态键，在素材对应 SkinnedMeshRenderer 上加 MA BlendshapeSync（身体→素材）。
        /// 返回新绑定的键数量（0 表示无同名键或未改动）。
        /// </summary>
        public static int ApplySyncToMaterial(GameObject material, Dictionary<string, SkinnedMeshRenderer> bodyShapes, string undoName)
        {
            if (material == null || bodyShapes == null || bodyShapes.Count == 0) return 0;

            SkinnedMeshRenderer[] smrs = material.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int added = 0;
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;

                // 该素材 smr 上与身体同名的形态键。
                List<string> matched = new List<string>();
                Mesh mesh = smr.sharedMesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!bodyShapes.ContainsKey(name)) continue;
                    // 跳过自引用（素材自身即身体网格的极端情况）。
                    SkinnedMeshRenderer bodySmr = bodyShapes[name];
                    if (bodySmr != null && bodySmr.gameObject == smr.gameObject) continue;
                    matched.Add(name);
                }

                if (matched.Count == 0) continue;

                ModularAvatarBlendshapeSync sync = smr.GetComponent<ModularAvatarBlendshapeSync>();
                bool created = false;
                if (sync == null)
                {
                    sync = Undo.AddComponent<ModularAvatarBlendshapeSync>(smr.gameObject);
                    created = true;
                }

                Undo.RecordObject(sync, undoName);

                List<BlendshapeBinding> bindings = new List<BlendshapeBinding>();
                if (sync.Bindings != null) bindings.AddRange(sync.Bindings);

                int addedHere = 0;
                foreach (string name in matched)
                {
                    SkinnedMeshRenderer bodySmr = bodyShapes[name];
                    if (bodySmr == null) continue;
                    string bodyPath = new AvatarObjectReference(bodySmr.gameObject).referencePath;

                    bool exists = false;
                    foreach (BlendshapeBinding b in bindings)
                    {
                        if (b.ReferenceMesh != null && b.ReferenceMesh.referencePath == bodyPath && b.Blendshape == name)
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (exists) continue;

                    bindings.Add(new BlendshapeBinding
                    {
                        ReferenceMesh = new AvatarObjectReference(bodySmr.gameObject),
                        Blendshape = name,
                        LocalBlendshape = name,
                    });
                    addedHere++;
                }

                if (addedHere == 0) continue;

                sync.Bindings = bindings;
                PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
                EditorUtility.SetDirty(sync);
                added += addedHere;
            }

            return added;
        }
    }
}
