using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>受保护对象的种类。</summary>
    public enum ProtectedKind
    {
        None = 0,
        /// <summary>身体/脸部网格：移动会导致口型/眨眼/表情失效，可由用户确认后继续。</summary>
        BodyOrFace = 1,
        /// <summary>骨骼（Armature 骨架根或角色 Hips 之下）：一律拒绝。</summary>
        Bone = 2,
    }

    /// <summary>受保护对象检测结果。</summary>
    public class ProtectedCheck
    {
        public ProtectedKind Kind = ProtectedKind.None;
        public string Reason = string.Empty;
        /// <summary>命中的具体物体（可能是被检测物体的子孙）。</summary>
        public GameObject Hit = null;

        public bool IsProtected
        {
            get { return Kind != ProtectedKind.None; }
        }
    }

    /// <summary>
    /// 身体/脸部网格与骨骼检测。满足任一即判定为身体/脸：Viseme 网格、眼皮网格、含 vrc.v_ / vrc.blink 形态键、
    /// 名字为 Body/Face/Head/Kao/顔/身体/脸 或以 Body_ / Body. 开头（名字规则仅对被检测物体本身）；
    /// 被检测物体的子孙中含 Viseme/眼皮/vrc 形态键网格也算。
    /// 骨骼：Armature 骨架根（包含角色 Hips）或位于角色 Hips 之下。
    /// </summary>
    public static class BodyMeshDetector
    {
        private static readonly HashSet<string> BodyExactNames = new HashSet<string>
        {
            "body", "face", "head", "kao", "顔", "身体", "脸", "身體", "臉",
        };

        public const string ConsequenceText = "移动会导致口型/眨眼/表情失效";

        /// <summary>
        /// 检测一个物体（场景对象或资源）。avatar 为空或对象为资源时，跳过 Viseme/眼皮与骨骼判定。
        /// 骨骼判定优先于身体/脸判定。
        /// </summary>
        public static ProtectedCheck Check(GameObject go, VRCAvatarDescriptor avatar)
        {
            ProtectedCheck result = new ProtectedCheck();
            if (go == null) return result;
            bool isScene = !EditorUtility.IsPersistent(go);

            string boneReason;
            if (isScene && avatar != null && IsBone(go, avatar, out boneReason))
            {
                result.Kind = ProtectedKind.Bone;
                result.Reason = boneReason;
                result.Hit = go;
                return result;
            }

            if (IsBodyName(go.name))
            {
                result.Kind = ProtectedKind.BodyOrFace;
                result.Reason = "名字为「" + go.name + "」";
                result.Hit = go;
                return result;
            }

            // 名字规则只用于被检测物体本身（上面已判断）；子孙只按 Viseme / 眼皮网格 / vrc.v_ / vrc.blink 形态键判定，
            // 避免自带名为 Body 的网格的衣服被误报。
            VRCAvatarDescriptor ctx = isScene ? avatar : null;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string reason;
                if (!IsBodyOrFaceRenderer(r, ctx, false, out reason)) continue;
                result.Kind = ProtectedKind.BodyOrFace;
                result.Hit = r.gameObject;
                result.Reason = r.gameObject == go ? reason : "子物体「" + r.gameObject.name + "」" + reason;
                return result;
            }

            return result;
        }

        /// <summary>
        /// 单个 Renderer 是否为身体/脸部网格。checkName 为 false 时不使用名字规则（只看 Viseme、眼皮网格与 vrc 形态键）。
        /// </summary>
        public static bool IsBodyOrFaceRenderer(Renderer renderer, VRCAvatarDescriptor avatar, bool checkName, out string reason)
        {
            reason = string.Empty;
            if (renderer == null) return false;
            SkinnedMeshRenderer smr = renderer as SkinnedMeshRenderer;
            if (smr == null && !(renderer is MeshRenderer)) return false;

            if (avatar != null && smr != null)
            {
                if (avatar.VisemeSkinnedMesh == smr)
                {
                    reason = "是 Avatar Descriptor 的口型网格（Viseme Mesh）";
                    return true;
                }

                SkinnedMeshRenderer eyelids = null;
                try
                {
                    eyelids = avatar.customEyeLookSettings.eyelidsSkinnedMesh;
                }
                catch (Exception)
                {
                    eyelids = null;
                }

                if (eyelids != null && eyelids == smr)
                {
                    reason = "是 Avatar Descriptor 的眼皮网格（Eyelids Mesh）";
                    return true;
                }
            }

            if (smr != null && HasVrcBlendshapes(smr))
            {
                reason = "含 vrc.v_ / vrc.blink 口型或眨眼形态键";
                return true;
            }

            if (checkName && IsBodyName(renderer.gameObject.name))
            {
                reason = "名字为「" + renderer.gameObject.name + "」";
                return true;
            }

            return false;
        }

        /// <summary>名字（忽略大小写）为 Body/Face/Head/Kao/顔/身体/脸，或以 Body_ / Body. 开头。</summary>
        public static bool IsBodyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.Trim().ToLowerInvariant();
            if (BodyExactNames.Contains(lower)) return true;
            return lower.StartsWith("body_", StringComparison.Ordinal) || lower.StartsWith("body.", StringComparison.Ordinal);
        }

        /// <summary>sharedMesh 是否含以 vrc.v_ 或 vrc.blink 开头的 blendshape。</summary>
        public static bool HasVrcBlendshapes(SkinnedMeshRenderer smr)
        {
            if (smr == null) return false;
            Mesh mesh = smr.sharedMesh;
            if (mesh == null) return false;
            int count = mesh.blendShapeCount;
            for (int i = 0; i < count; i++)
            {
                string n = mesh.GetBlendShapeName(i);
                if (string.IsNullOrEmpty(n)) continue;
                if (n.StartsWith("vrc.v_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("vrc.blink", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>是否为骨骼：角色 Hips 本身或其子孙；包含 Hips 的骨架根；角色根下名字含 Armature 的直接子物体。</summary>
        public static bool IsBone(GameObject go, VRCAvatarDescriptor avatar, out string reason)
        {
            reason = string.Empty;
            if (go == null || avatar == null) return false;
            Transform t = go.transform;
            if (t == avatar.transform) return false;

            Transform hips = FindAvatarHips(avatar);
            if (hips != null)
            {
                if (t == hips || t.IsChildOf(hips))
                {
                    reason = "位于角色 Hips 之下（骨骼）";
                    return true;
                }

                if (hips.IsChildOf(t))
                {
                    reason = "是角色的 Armature 骨架根（包含 Hips）";
                    return true;
                }
            }

            if (t.parent == avatar.transform && go.name.IndexOf("armature", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "是 Armature 骨架根";
                return true;
            }

            return false;
        }

        /// <summary>角色的 Hips：优先 Humanoid Animator；否则在角色根下（跳过「外观设置」）浅层按名字查找。</summary>
        public static Transform FindAvatarHips(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            Animator animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null)
            {
                try
                {
                    Transform h = animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (h != null) return h;
                }
                catch (Exception)
                {
                    // 忽略，走名字查找。
                }
            }

            Queue<KeyValuePair<Transform, int>> queue = new Queue<KeyValuePair<Transform, int>>();
            Transform root = avatar.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == QuickOutfitConst.AppearanceRootName) continue;
                queue.Enqueue(new KeyValuePair<Transform, int>(child, 1));
            }

            while (queue.Count > 0)
            {
                KeyValuePair<Transform, int> item = queue.Dequeue();
                string n = AssetClassifier.NormalizeBoneName(item.Key.name);
                if (n == "hips" || n == "hip" || n == "pelvis" || n == "jbipchips") return item.Key;
                if (item.Value >= 3) continue;
                for (int i = 0; i < item.Key.childCount; i++)
                {
                    queue.Enqueue(new KeyValuePair<Transform, int>(item.Key.GetChild(i), item.Value + 1));
                }
            }

            return null;
        }

        /// <summary>警告对话框正文。</summary>
        public static string BuildWarningText(GameObject go, ProtectedCheck check)
        {
            string name = go != null ? go.name : "?";
            if (check == null || check.Kind == ProtectedKind.None) return string.Empty;
            if (check.Kind == ProtectedKind.Bone)
            {
                return "「" + name + "」" + check.Reason + "。\n这是骨骼，" + ConsequenceText + "，骨骼不能移动。";
            }

            return "「" + name + "」" + check.Reason + "。\n这是身体/脸部网格，" + ConsequenceText + "。";
        }
    }
}
