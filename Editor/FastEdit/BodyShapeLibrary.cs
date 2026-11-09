using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>收集角色身体的形态键（排除外观设置下的网格、口型/眼皮/vrc 表情网格）。</summary>
    public static class BodyShapeLibrary
    {
        private const string PrefKeyPrefix = "Milfy.QuickFix.Designated.";

        /// <summary>
        /// 收集角色身上所有身体形态键（去重，同名取第一个出现的 mesh）。
        /// 返回 形态键名 → 所属身体 SkinnedMeshRenderer 的 GameObject。
        /// </summary>
        public static Dictionary<string, GameObject> Collect(VRCAvatarDescriptor avatar, GameObject appearanceRoot)
        {
            Dictionary<string, GameObject> result = new Dictionary<string, GameObject>();
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
                    if (!result.ContainsKey(name)) result[name] = go;
                }
            }

            return result;
        }

        /// <summary>把身体形态键字典转成 FixShape 列表，标记推荐项与「空」形态键。</summary>
        public static List<FixShape> ToFixShapes(Dictionary<string, GameObject> bodyShapes)
        {
            List<FixShape> result = new List<FixShape>();
            if (bodyShapes == null) return result;
            foreach (KeyValuePair<string, GameObject> kv in bodyShapes)
            {
                SkinnedMeshRenderer smr = kv.Value != null ? kv.Value.GetComponent<SkinnedMeshRenderer>() : null;
                result.Add(new FixShape
                {
                    Name = kv.Key,
                    BodyMesh = kv.Value,
                    Recommended = QuickFixClassifier.IsRecommended(kv.Key),
                    Empty = IsEmptyShape(smr, kv.Key),
                    Value = 0f,
                    Locked = false,
                });
            }

            return result;
        }

        // ------------------------------------------------------------------ 持久化（按角色存已指定的形态键名）

        /// <summary>该角色的持久化 key（用角色名区分；同名角色共用一份记录）。</summary>
        private static string PrefKey(VRCAvatarDescriptor avatar)
        {
            return PrefKeyPrefix + (avatar != null ? avatar.gameObject.name : "null");
        }

        /// <summary>保存已指定的形态键名列表（逗号分隔）。</summary>
        public static void SaveDesignated(VRCAvatarDescriptor avatar, IList<FixShape> designated)
        {
            if (avatar == null) return;
            List<string> names = new List<string>();
            if (designated != null)
            {
                foreach (FixShape s in designated)
                {
                    if (s != null && !string.IsNullOrEmpty(s.Name)) names.Add(s.Name);
                }
            }

            EditorPrefs.SetString(PrefKey(avatar), string.Join(",", names.ToArray()));
        }

        /// <summary>读取已保存的形态键名，并按当前身体重新匹配 mesh，重建 FixShape 列表。匹配不到的（身体已无此键）跳过。</summary>
        public static List<FixShape> LoadDesignated(VRCAvatarDescriptor avatar, GameObject appearanceRoot)
        {
            List<FixShape> result = new List<FixShape>();
            if (avatar == null) return result;

            string saved = EditorPrefs.GetString(PrefKey(avatar), string.Empty);
            if (string.IsNullOrEmpty(saved)) return result;

            Dictionary<string, GameObject> bodyShapes = Collect(avatar, appearanceRoot);
            string[] names = saved.Split(',');
            foreach (string raw in names)
            {
                string name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                GameObject mesh;
                if (!bodyShapes.TryGetValue(name, out mesh)) continue; // 身体已无此键，跳过。
                result.Add(new FixShape
                {
                    Name = name,
                    BodyMesh = mesh,
                    Recommended = QuickFixClassifier.IsRecommended(name),
                    Empty = IsEmptyShape(mesh != null ? mesh.GetComponent<SkinnedMeshRenderer>() : null, name),
                    Value = 0f,
                    Locked = false,
                });
            }

            return result;
        }

        /// <summary>清除该角色已保存的形态键记录。</summary>
        public static void ClearDesignated(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return;
            EditorPrefs.DeleteKey(PrefKey(avatar));
        }

        /// <summary>形态键是否为「空」：权重 0 与 100 下 BakeMesh 的顶点完全一致（不变形网格）。</summary>
        private static bool IsEmptyShape(SkinnedMeshRenderer smr, string shapeName)
        {
            if (smr == null || smr.sharedMesh == null) return true;
            int idx = smr.sharedMesh.GetBlendShapeIndex(shapeName);
            if (idx < 0) return true;

            float original = smr.GetBlendShapeWeight(idx);
            Mesh a = new Mesh();
            Mesh b = new Mesh();
            try
            {
                smr.SetBlendShapeWeight(idx, 0f);
                smr.BakeMesh(a);
                smr.SetBlendShapeWeight(idx, 100f);
                smr.BakeMesh(b);
            }
            finally
            {
                smr.SetBlendShapeWeight(idx, original);
            }

            Vector3[] va = a.vertices;
            Vector3[] vb = b.vertices;
            if (va.Length != vb.Length) return false;
            for (int i = 0; i < va.Length; i++)
            {
                if (va[i] != vb[i]) return false;
            }

            return true;
        }
    }
}
