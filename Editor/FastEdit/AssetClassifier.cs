using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using nadena.dev.modular_avatar.core;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>一次类型识别的打分结果（各类别累计分数 + 计分明细）。</summary>
    public class ClassifyResult
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public string AssetPath = string.Empty;
        public string Verdict = string.Empty;
        public readonly Dictionary<AssetCategory, float> Scores = new Dictionary<AssetCategory, float>();
        public readonly List<string> Reasons = new List<string>();

        public float GetScore(AssetCategory category)
        {
            float value;
            return Scores.TryGetValue(category, out value) ? value : 0f;
        }

        /// <summary>给某类别加分并记录原因（分数 ≤ 0 或类别未知时忽略）。</summary>
        public void Add(AssetCategory category, float points, string reason)
        {
            if (category == AssetCategory.Unknown || points <= 0f) return;
            Scores[category] = GetScore(category) + points;
            Reasons.Add(QuickOutfitConst.CategoryLabel(category) + " +" + Format(points) + "：" + reason);
        }

        /// <summary>「衣服 7.5 / 发型 0.0 / 配饰 0.0 / 道具 0.0」。</summary>
        public string ScoreSummary()
        {
            List<string> parts = new List<string>();
            foreach (AssetCategory c in QuickOutfitConst.SlotCategories)
            {
                parts.Add(QuickOutfitConst.CategoryLabel(c) + " " + Format(GetScore(c)));
            }

            return string.Join(" / ", parts);
        }

        /// <summary>多行明细，用于 tooltip 与日志。</summary>
        public string Detail()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("识别：").Append(QuickOutfitConst.CategoryLabel(Category)).Append("（").Append(ScoreSummary()).Append("）");
            if (!string.IsNullOrEmpty(Verdict)) sb.Append("\n").Append(Verdict);
            if (!string.IsNullOrEmpty(AssetPath)) sb.Append("\n路径：").Append(AssetPath);
            foreach (string r in Reasons) sb.Append("\n· ").Append(r);
            return sb.ToString();
        }

        public static string Format(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 素材类型识别（打分制）。对 衣服/发型/配饰/道具 分别累计分数，取最高；最高分低于阈值或并列则判为未识别。
    /// 信号：资源路径文件夹名、根名、网格名、PhysBone 名、子物体名、蒙皮骨骼分析（最可靠）、骨架结构。
    /// 发饰规则（根名同时含发型词与 band/clip/pin 类词 → 配饰）优先于打分。
    /// </summary>
    public static class AssetClassifier
    {
        // ------------------------------------------------------------------ 权重

        /// <summary>最高分低于此值判为未识别。</summary>
        public const float Threshold = 2f;
        /// <summary>资源路径中的文件夹名（每类最多计一次）。高于子物体名。</summary>
        public const float PathWeight = 3f;
        /// <summary>根名关键词。</summary>
        public const float RootNameWeight = 4f;
        /// <summary>网格（Renderer）名命中比例 × 此值。</summary>
        public const float RendererNameMax = 2f;
        /// <summary>PhysBone 名命中比例 × 此值。</summary>
        public const float PhysBoneNameMax = 2f;
        /// <summary>其余子物体名（按命中比例，上限）。</summary>
        public const float ChildNameMax = 1.5f;
        /// <summary>蒙皮全部在 Head 之下：发型分。</summary>
        public const float SkinHeadOnlyHair = 5f;
        /// <summary>蒙皮全部在 Head 之下：配饰分（头饰也是仅头部蒙皮）。</summary>
        public const float SkinHeadOnlyAccessory = 2f;
        /// <summary>蒙皮大部分在 Head 之下（75%~95%）：发型分。</summary>
        public const float SkinMostlyHeadHair = 3f;
        /// <summary>蒙皮使用较多躯干/四肢骨骼：衣服分。</summary>
        public const float SkinBodyClothes = 6f;
        /// <summary>蒙皮只集中在少数躯干/四肢骨骼（手镯、项圈等）：衣服分。</summary>
        public const float SkinLocalClothes = 3f;
        /// <summary>蒙皮只集中在少数躯干/四肢骨骼：配饰分。</summary>
        public const float SkinLocalAccessory = 2f;
        /// <summary>没有人形骨架时，蒙皮骨骼多为 hair 命名：发型分。</summary>
        public const float SkinHairNamedBones = 3f;
        /// <summary>长发：Head 下蒙皮 40%~75% 且 PhysBone 发型命中 ≥ 50%：发型分。</summary>
        public const float SkinLongHairBonus = 4.5f;
        /// <summary>根名同时含发型词与弱发饰词（ribbon/bow/リボン/蝴蝶结）：额外配饰分。</summary>
        public const float WeakHairAccessoryBonus = 4f;
        /// <summary>含 Hips：只说明带骨架，少量衣服分。</summary>
        public const float HipsWeight = 1f;
        /// <summary>已有 Merge Armature：少量衣服分。</summary>
        public const float MergeArmatureWeight = 0.5f;
        /// <summary>含多骨骼蒙皮网格：少量衣服分。</summary>
        public const float MultiBoneWeight = 0.5f;
        /// <summary>已有 Bone Proxy → Head：少量发型/配饰分。</summary>
        public const float HeadProxyWeight = 1f;

        // ------------------------------------------------------------------ 关键词

        public static readonly string[] HairKeywords =
        {
            "hair", "髪", "发", "髮", "ヘア", "wig", "bangs", "前髪", "後ろ髪", "ponytail", "twintail", "ahoge", "アホ毛",
        };

        public static readonly string[] ClothesKeywords =
        {
            "outfit", "cloth", "服", "衣装", "costume", "dress", "shirt", "skirt", "pants", "coat", "jacket",
            "uniform", "水着", "swimsuit", "ワンピース", "shoes", "boots", "socks", "tights", "靴",
        };

        public static readonly string[] AccessoryKeywords =
        {
            "accessory", "accessories", "acc", "アクセ", "配饰", "饰", "ribbon", "リボン", "earring", "ピアス", "glasses",
            "メガネ", "眼镜", "hat", "帽", "choker", "necklace", "ネックレス", "bracelet", "halo",
            "发夹", "发卡", "发带", "发圈", "发箍", "hairpin", "hairclip", "hairband", "ヘアピン", "ヘアクリップ",
            "カチューシャ",
        };

        /// <summary>
        /// 强发饰词：与发型关键词同时出现时可确定是发饰（HairClip / HairBand / HairPin）。
        /// 只有这一组参与「根名直接判配饰」规则；名字打分中，发型词 + 强发饰词只算配饰。
        /// </summary>
        public static readonly string[] StrongHairAccessoryMarkers =
        {
            "band", "clip", "pin", "hairpin", "hairclip", "hairband", "ピン", "クリップ", "ゴム", "カチューシャ",
            "飾", "饰", "アクセ",
        };

        /// <summary>
        /// 弱发饰词：RibbonHair / BowHair 也可能是带蝴蝶结的发型，不直接判配饰，只在名字打分中额外给配饰分（发型分保留）。
        /// bow 只有 3 个字符，走词边界规则，不会误命中 Elbow / Rainbow。
        /// </summary>
        public static readonly string[] WeakHairAccessoryMarkers =
        {
            "ribbon", "リボン", "bow", "蝴蝶结",
        };

        /// <summary>兼容旧名：等同于强发饰词。</summary>
        public static readonly string[] HairAccessoryMarkers = StrongHairAccessoryMarkers;

        public static readonly string[] PropKeywords =
        {
            "prop", "道具", "weapon", "武器", "sword", "gun", "剣", "銃", "傘", "umbrella", "bag", "phone", "cup",
            "book",
        };

        /// <summary>Head 骨骼的规范化名字表（MA 的 HeuristicBoneMapper 为 internal，这里自备一份简表）。</summary>
        private static readonly HashSet<string> HeadBoneNames = new HashSet<string>
        {
            "head", "jbipchead", "chead", "biphead", "bipedhead", "headbone", "頭",
        };

        /// <summary>Hips 骨骼的规范化名字表。</summary>
        private static readonly HashSet<string> HipsBoneNames = new HashSet<string>
        {
            "hips", "hip", "pelvis", "jbipchips", "chips", "biphips", "bippelvis", "bipedpelvis",
        };

        private enum BoneRegion
        {
            Other = 0,
            Head = 1,
            Body = 2,
        }

        // ------------------------------------------------------------------ 入口

        /// <summary>识别素材类型（仅返回类别）。</summary>
        public static AssetCategory Classify(GameObject root)
        {
            return Analyze(root).Category;
        }

        /// <summary>识别素材类型并返回完整打分明细。</summary>
        public static ClassifyResult Analyze(GameObject root)
        {
            ClassifyResult result = new ClassifyResult();
            if (root == null)
            {
                result.Verdict = "素材为空";
                return result;
            }

            string rootName = root.name;
            result.AssetPath = GetSourcePath(root);

            // 发饰规则（保留 v1）：根名命中发型关键词且同时含 band/clip/pin 类词 → 配饰，直接返回。
            if (ContainsAny(rootName, HairKeywords) && ContainsAny(rootName, StrongHairAccessoryMarkers))
            {
                result.Add(AssetCategory.Accessory, 10f, "根名「" + rootName + "」同时含发型词与发饰词（HairClip/HairBand 等）");
                result.Category = AssetCategory.Accessory;
                result.Verdict = "发饰规则：判为配饰";
                return result;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            ScorePath(result);
            ScoreRootName(rootName, result);
            ScoreRendererNames(root, result);
            ScorePhysBoneNames(root, result);
            ScoreChildNames(all, root.transform, result);
            ScoreSkinning(root, result);
            ScoreSkeleton(root, result);
            Decide(result);
            return result;
        }

        // ------------------------------------------------------------------ 各信号

        /// <summary>资源路径：Project 资源用 GetAssetPath；场景对象用最近 Prefab 实例根的资源路径。</summary>
        public static string GetSourcePath(GameObject root)
        {
            if (root == null) return string.Empty;
            try
            {
                if (EditorUtility.IsPersistent(root)) return AssetDatabase.GetAssetPath(root) ?? string.Empty;
                if (!PrefabUtility.IsPartOfPrefabInstance(root)) return string.Empty;
                return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void ScorePath(ClassifyResult result)
        {
            if (string.IsNullOrEmpty(result.AssetPath)) return;
            string[] segments = result.AssetPath.Replace('\\', '/').Split('/');
            Dictionary<AssetCategory, string> folderOf = new Dictionary<AssetCategory, string>();

            // 最后一段是文件名（通常与根名相同，由根名信号负责），不计入。
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string seg = segments[i];
                if (string.IsNullOrEmpty(seg)) continue;
                if (i == 0 && (seg == "Assets" || seg == "Packages")) continue;
                foreach (AssetCategory c in CategoriesOfName(seg))
                {
                    if (!folderOf.ContainsKey(c)) folderOf[c] = seg;
                }
            }

            foreach (KeyValuePair<AssetCategory, string> kv in folderOf)
            {
                result.Add(kv.Key, PathWeight, "资源路径文件夹「" + kv.Value + "」");
            }
        }

        private static void ScoreRootName(string rootName, ClassifyResult result)
        {
            foreach (AssetCategory c in CategoriesOfName(rootName))
            {
                result.Add(c, RootNameWeight, "根名「" + rootName + "」");
            }

            // 根名同时含发型词与弱发饰词（HairRibbon / HairBow）：额外配饰分，用于抵消「仅头部蒙皮」给发型的优势；
            // 真正的发型（RibbonHair 等）还有 Hair 路径 / PB_Hair / hair 骨骼等只加发型分的信号，仍判发型。
            if (ContainsAny(rootName, HairKeywords) && !ContainsAny(rootName, StrongHairAccessoryMarkers) &&
                ContainsAny(rootName, WeakHairAccessoryMarkers))
            {
                result.Add(AssetCategory.Accessory, WeakHairAccessoryBonus, "根名同时含发型词与 ribbon/bow 类发饰词");
            }
        }

        private static void ScoreRendererNames(GameObject root, ClassifyResult result)
        {
            List<string> names = CollectRendererNames(root);
            if (names.Count == 0) return;
            Dictionary<AssetCategory, int> hits = CountHits(names);
            foreach (KeyValuePair<AssetCategory, int> kv in hits)
            {
                float ratio = (float)kv.Value / names.Count;
                result.Add(kv.Key, RendererNameMax * ratio, "网格名命中 " + kv.Value + "/" + names.Count);
            }
        }

        private static void ScorePhysBoneNames(GameObject root, ClassifyResult result)
        {
            List<string> names;
            List<string> rootNames;
            CollectPhysBoneNames(root, out names, out rootNames);
            if (names.Count == 0) return;
            Dictionary<AssetCategory, int> hits = CountPhysBoneHits(names, rootNames);
            foreach (KeyValuePair<AssetCategory, int> kv in hits)
            {
                float ratio = (float)kv.Value / names.Count;
                result.Add(kv.Key, PhysBoneNameMax * ratio, "PhysBone 名命中 " + kv.Value + "/" + names.Count + "（如 " + names[0] + "）");
            }
        }

        /// <summary>收集素材内所有 VRCPhysBone 的物体名与 rootTransform 名（按类型名识别，避免依赖 Dynamics 程序集）。</summary>
        private static void CollectPhysBoneNames(GameObject root, out List<string> names, out List<string> rootNames)
        {
            names = new List<string>();
            rootNames = new List<string>();
            if (root == null) return;
            foreach (Component comp in root.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue; // 丢失的脚本
                if (comp.GetType().Name != "VRCPhysBone") continue;
                names.Add(comp.gameObject.name);
                rootNames.Add(GetPhysBoneRootName(comp));
            }
        }

        /// <summary>每个类别被多少个 PhysBone 命中（物体名或 rootTransform 名任一命中即算一次）。</summary>
        private static Dictionary<AssetCategory, int> CountPhysBoneHits(List<string> names, List<string> rootNames)
        {
            Dictionary<AssetCategory, int> hits = new Dictionary<AssetCategory, int>();
            for (int i = 0; i < names.Count; i++)
            {
                HashSet<AssetCategory> cats = new HashSet<AssetCategory>(CategoriesOfName(names[i]));
                if (i < rootNames.Count)
                {
                    foreach (AssetCategory c in CategoriesOfName(rootNames[i])) cats.Add(c);
                }

                foreach (AssetCategory c in cats)
                {
                    int n;
                    hits.TryGetValue(c, out n);
                    hits[c] = n + 1;
                }
            }

            return hits;
        }

        /// <summary>PhysBone 中命中发型关键词的比例（没有 PhysBone 返回 0）。</summary>
        private static float PhysBoneHairRatio(GameObject root)
        {
            List<string> names;
            List<string> rootNames;
            CollectPhysBoneNames(root, out names, out rootNames);
            if (names.Count == 0) return 0f;
            int hair;
            CountPhysBoneHits(names, rootNames).TryGetValue(AssetCategory.Hair, out hair);
            return (float)hair / names.Count;
        }

        /// <summary>读取 VRCPhysBone.rootTransform 的名字（反射，避免依赖 Dynamics 程序集）；取不到返回空字符串。</summary>
        private static string GetPhysBoneRootName(Component physBone)
        {
            try
            {
                FieldInfo field = physBone.GetType().GetField("rootTransform", BindingFlags.Public | BindingFlags.Instance);
                Transform t = field != null ? field.GetValue(physBone) as Transform : null;
                return t != null ? t.name : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void ScoreChildNames(Transform[] all, Transform root, ClassifyResult result)
        {
            List<string> names = new List<string>();
            foreach (Transform t in all)
            {
                if (t == null || t == root) continue;
                names.Add(t.name);
            }

            if (names.Count == 0) return;
            Dictionary<AssetCategory, int> hits = CountHits(names);
            foreach (KeyValuePair<AssetCategory, int> kv in hits)
            {
                float ratio = (float)kv.Value / names.Count;
                // 命中比例达到 30% 即满分；只要命中就至少给 20% 的分。
                float score = ChildNameMax * Mathf.Clamp01(0.2f + ratio / 0.3f);
                result.Add(kv.Key, score, "子物体名命中 " + kv.Value + "/" + names.Count);
            }
        }

        /// <summary>
        /// 蒙皮分析：统计所有 SMR 实际有权重的骨骼（mesh 读不到时退回 bones 列表），
        /// 按骨骼是否位于 Head 之下 / 位于 Hips 之下（躯干四肢）计分。
        /// </summary>
        private static void ScoreSkinning(GameObject root, ClassifyResult result)
        {
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0) return;

            Dictionary<Transform, float> boneWeights = new Dictionary<Transform, float>();
            int fromWeights = 0;
            int fromBones = 0;
            foreach (SkinnedMeshRenderer smr in smrs)
            {
                if (smr == null) continue;
                int mode = CollectBoneWeights(smr, boneWeights);
                if (mode == 1) fromWeights++;
                else if (mode == 2) fromBones++;
            }

            if (boneWeights.Count == 0) return;

            float total = 0f;
            float head = 0f;
            float body = 0f;
            float hairNamed = 0f;
            Dictionary<Transform, float> bodyBones = new Dictionary<Transform, float>();
            foreach (KeyValuePair<Transform, float> kv in boneWeights)
            {
                total += kv.Value;
                BoneRegion region = GetRegion(kv.Key, root.transform);
                if (region == BoneRegion.Head)
                {
                    head += kv.Value;
                }
                else if (region == BoneRegion.Body)
                {
                    body += kv.Value;
                    bodyBones[kv.Key] = kv.Value;
                }

                if (IsUnderHairNamedBone(kv.Key, root.transform)) hairNamed += kv.Value;
            }

            if (total <= 0f) return;

            string source;
            if (fromBones == 0) source = "按顶点权重统计";
            else if (fromWeights == 0) source = "网格不可读，按 bones 列表统计";
            else source = "部分网格不可读，混合统计";

            float headFrac = head / total;
            float bodyFrac = body / total;
            if (headFrac + bodyFrac >= 0.5f)
            {
                string pct = "Head 下 " + Percent(headFrac) + "，躯干/四肢 " + Percent(bodyFrac) + "，" + source;
                if (headFrac >= 0.95f)
                {
                    result.Add(AssetCategory.Hair, SkinHeadOnlyHair, "蒙皮骨骼全部位于 Head 之下（" + pct + "）");
                    result.Add(AssetCategory.Accessory, SkinHeadOnlyAccessory, "仅头部蒙皮（也可能是头饰）");
                }
                else if (headFrac >= 0.75f)
                {
                    result.Add(AssetCategory.Hair, SkinMostlyHeadHair, "蒙皮骨骼大部分位于 Head 之下（" + pct + "）");
                    result.Add(AssetCategory.Clothes, 1f, "少量躯干蒙皮");
                    result.Add(AssetCategory.Accessory, 1f, "以头部蒙皮为主（也可能是头饰）");
                }
                else
                {
                    int significant = 0;
                    foreach (KeyValuePair<Transform, float> kv in bodyBones)
                    {
                        if (kv.Value / total >= 0.01f) significant++;
                    }

                    if (significant <= 3)
                    {
                        result.Add(AssetCategory.Clothes, SkinLocalClothes,
                            "蒙皮集中在 " + significant + " 根躯干/四肢骨骼（" + pct + "）");
                        result.Add(AssetCategory.Accessory, SkinLocalAccessory, "蒙皮范围很小（手镯/项圈类也是如此）");
                    }
                    else
                    {
                        result.Add(AssetCategory.Clothes, SkinBodyClothes,
                            "蒙皮使用 " + significant + " 根躯干/四肢骨骼（" + pct + "）");
                    }

                    // 长发：发梢蒙皮到背部/胸部，但仍有相当比例在 Head 下，且 PhysBone 多为 hair 命名。
                    if (headFrac >= 0.4f && headFrac < 0.75f)
                    {
                        float pbHair = PhysBoneHairRatio(root);
                        if (pbHair >= 0.5f)
                        {
                            result.Add(AssetCategory.Hair, SkinLongHairBonus,
                                "长发特征：Head 下蒙皮 " + Percent(headFrac) + " 且 PhysBone 发型命中 " + Percent(pbHair));
                        }
                    }
                }
            }
            else if (hairNamed / total >= 0.6f)
            {
                result.Add(AssetCategory.Hair, SkinHairNamedBones,
                    "无人形骨架，蒙皮骨骼多为 hair 命名（" + Percent(hairNamed / total) + "，" + source + "）");
            }
        }

        /// <summary>
        /// 统计单个 SMR 的骨骼权重。返回 1=按顶点权重；2=退回 bones 列表（每根骨骼计 1）；0=无数据。
        /// 编辑模式下非 Read/Write 的网格也可读取；播放模式下不可读的网格直接退回 bones 列表。
        /// </summary>
        private static int CollectBoneWeights(SkinnedMeshRenderer smr, Dictionary<Transform, float> acc)
        {
            Transform[] bones = smr.bones;
            if (bones == null || bones.Length == 0) return 0;

            Mesh mesh = smr.sharedMesh;
            if (mesh != null && (mesh.isReadable || !Application.isPlaying))
            {
                try
                {
                    NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights();
                    if (weights.IsCreated && weights.Length > 0)
                    {
                        bool any = false;
                        for (int i = 0; i < weights.Length; i++)
                        {
                            BoneWeight1 w = weights[i];
                            if (w.weight <= 0.0001f) continue;
                            if (w.boneIndex < 0 || w.boneIndex >= bones.Length) continue;
                            Transform bone = bones[w.boneIndex];
                            if (bone == null) continue;
                            float current;
                            acc.TryGetValue(bone, out current);
                            acc[bone] = current + w.weight;
                            any = true;
                        }

                        if (any) return 1;
                    }
                }
                catch (Exception)
                {
                    // 读取失败：退回 bones 列表。
                }
            }

            bool added = false;
            foreach (Transform bone in bones)
            {
                if (bone == null) continue;
                float current;
                acc.TryGetValue(bone, out current);
                acc[bone] = current + 1f;
                added = true;
            }

            return added ? 2 : 0;
        }

        /// <summary>
        /// 骨骼所在区域：自身或祖先先遇到 Head（排除素材根与带 Renderer 的物体）→ Head；先遇到 Hips → 躯干/四肢；否则 Other。
        /// 向上遍历不限于素材内部，因此已直接绑到角色骨架的场景对象也能判断。
        /// </summary>
        private static BoneRegion GetRegion(Transform bone, Transform materialRoot)
        {
            for (Transform t = bone; t != null; t = t.parent)
            {
                if (t == materialRoot) continue;
                string normalized = NormalizeBoneName(t.name);
                if (HeadBoneNames.Contains(normalized) && t.GetComponent<Renderer>() == null) return BoneRegion.Head;
                if (HipsBoneNames.Contains(normalized)) return BoneRegion.Body;
            }

            return BoneRegion.Other;
        }

        private static bool IsUnderHairNamedBone(Transform bone, Transform materialRoot)
        {
            for (Transform t = bone; t != null && t != materialRoot; t = t.parent)
            {
                if (ContainsAny(t.name, HairKeywords) && !ContainsAny(t.name, StrongHairAccessoryMarkers)) return true;
            }

            return false;
        }

        /// <summary>骨骼名规范化：去掉「xxx:」前缀与「bone_」前缀，转小写，删除数字/空格/./_/-。</summary>
        public static string NormalizeBoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string n = name;
            int colon = n.LastIndexOf(':');
            if (colon >= 0 && colon < n.Length - 1) n = n.Substring(colon + 1);
            n = n.ToLowerInvariant();
            if (n.StartsWith("bone_", StringComparison.Ordinal)) n = n.Substring(5);
            StringBuilder sb = new StringBuilder(n.Length);
            foreach (char c in n)
            {
                if (char.IsDigit(c) || c == ' ' || c == '.' || c == '_' || c == '-') continue;
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>骨架结构：有 Hips / Merge Armature 只说明带骨架，只给少量衣服分；已有 Bone Proxy→Head 给少量发型/配饰分。</summary>
        private static void ScoreSkeleton(GameObject root, ClassifyResult result)
        {
            if (BindingHelper.HasHipsHeuristic(root))
            {
                result.Add(AssetCategory.Clothes, HipsWeight, "含 Hips 骨骼（仅说明带骨架）");
            }

            if (root.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null)
            {
                result.Add(AssetCategory.Clothes, MergeArmatureWeight, "已有 MA Merge Armature");
            }

            ModularAvatarBoneProxy proxy = root.GetComponent<ModularAvatarBoneProxy>();
            if (proxy != null && proxy.boneReference == HumanBodyBones.Head)
            {
                result.Add(AssetCategory.Hair, HeadProxyWeight, "已有 Bone Proxy → Head");
                result.Add(AssetCategory.Accessory, HeadProxyWeight, "已有 Bone Proxy → Head");
            }

            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && smr.bones != null && smr.bones.Length > 1)
                {
                    result.Add(AssetCategory.Clothes, MultiBoneWeight, "含多骨骼蒙皮网格");
                    break;
                }
            }
        }

        private static void Decide(ClassifyResult result)
        {
            AssetCategory best = AssetCategory.Unknown;
            float bestScore = 0f;
            float second = 0f;
            foreach (AssetCategory c in QuickOutfitConst.SlotCategories)
            {
                float s = result.GetScore(c);
                if (s > bestScore)
                {
                    second = bestScore;
                    bestScore = s;
                    best = c;
                }
                else if (s > second)
                {
                    second = s;
                }
            }

            if (best == AssetCategory.Unknown || bestScore < Threshold)
            {
                result.Category = AssetCategory.Unknown;
                result.Verdict = "最高分 " + ClassifyResult.Format(bestScore) + " 低于阈值 " +
                                 ClassifyResult.Format(Threshold) + "，判为未识别";
                return;
            }

            if (bestScore - second < 0.05f)
            {
                result.Category = AssetCategory.Unknown;
                result.Verdict = "最高分并列（" + ClassifyResult.Format(bestScore) + "），请手动指定";
                return;
            }

            result.Category = best;
            result.Verdict = "取最高分：" + QuickOutfitConst.CategoryLabel(best) + " " + ClassifyResult.Format(bestScore);
        }

        // ------------------------------------------------------------------ 名字工具

        /// <summary>
        /// 一个名字命中的类别集合。发型词 + 强发饰词（HairClip、HairBand）只算配饰；
        /// 发型词 + 弱发饰词（HairRibbon、RibbonHair、HairBow）同时算发型与配饰。
        /// </summary>
        public static List<AssetCategory> CategoriesOfName(string name)
        {
            List<AssetCategory> result = new List<AssetCategory>();
            if (string.IsNullOrEmpty(name)) return result;
            bool hair = ContainsAny(name, HairKeywords);
            bool hairAccessory = hair && ContainsAny(name, StrongHairAccessoryMarkers);
            bool weakHairAccessory = hair && !hairAccessory && ContainsAny(name, WeakHairAccessoryMarkers);
            if (hair && !hairAccessory) result.Add(AssetCategory.Hair);
            if (ContainsAny(name, ClothesKeywords)) result.Add(AssetCategory.Clothes);
            if (hairAccessory || weakHairAccessory || ContainsAny(name, AccessoryKeywords)) result.Add(AssetCategory.Accessory);
            if (ContainsAny(name, PropKeywords)) result.Add(AssetCategory.Prop);
            return result;
        }

        private static Dictionary<AssetCategory, int> CountHits(List<string> names)
        {
            Dictionary<AssetCategory, int> hits = new Dictionary<AssetCategory, int>();
            foreach (string n in names)
            {
                foreach (AssetCategory c in CategoriesOfName(n))
                {
                    int count;
                    hits.TryGetValue(c, out count);
                    hits[c] = count + 1;
                }
            }

            return hits;
        }

        private static string Percent(float fraction)
        {
            return Mathf.RoundToInt(fraction * 100f) + "%";
        }

        /// <summary>名字是否包含发型关键词。</summary>
        public static bool IsHairName(string name)
        {
            return ContainsAny(name, HairKeywords);
        }

        /// <summary>名字是否包含任一关键词（忽略大小写；短英文关键词要求词边界）。</summary>
        public static bool ContainsAny(string name, IEnumerable<string> keywords)
        {
            if (string.IsNullOrEmpty(name) || keywords == null) return false;
            foreach (string kw in keywords)
            {
                if (ContainsKeyword(name, kw)) return true;
            }

            return false;
        }

        /// <summary>
        /// 判断关键词命中（查找时忽略大小写，边界判断使用原字符串大小写；遍历所有出现位置，任一满足即命中）。
        /// 对长度不超过 4 的纯 ASCII 关键词（如 hair/hat/coat/bag/bra/acc）要求词边界：
        /// 前：位于开头 / 前一字符非 ASCII 字母 / 驼峰边界（本字符大写且前一字符小写，如 LongHair、SunHat）；
        /// 后：位于末尾 / 后一字符不是小写字母 / 复数 s 且其后不是小写字母（如 Props、Hats）。
        /// 这样 chat/That/chair/Hatsune/Bracelet/Overcoat/Property 不会误命中。
        /// 特例：单字「发/髮」后跟 光/夹/卡/饰/带/圈/箍 时不命中；「dress」前一字符为 d（Headdress）时不命中。
        /// </summary>
        public static bool ContainsKeyword(string name, string keyword)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(keyword)) return false;
            bool needBoundary = keyword.Length <= 4 && IsAscii(keyword);
            bool isSingleHairChar = keyword == "发" || keyword == "髮";
            bool isDress = string.Equals(keyword, "dress", StringComparison.OrdinalIgnoreCase);
            int start = 0;
            while (start <= name.Length - keyword.Length)
            {
                int idx = name.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                start = idx + 1;
                int after = idx + keyword.Length;

                if (isSingleHairChar && after < name.Length && HairAccessorySuffixChars.IndexOf(name[after]) >= 0)
                {
                    continue;
                }

                if (isDress && idx > 0 && (name[idx - 1] == 'd' || name[idx - 1] == 'D'))
                {
                    continue;
                }

                if (!needBoundary) return true;

                bool beforeOk = idx == 0 || !IsAsciiLetter(name[idx - 1]) ||
                                (char.IsUpper(name[idx]) && char.IsLower(name[idx - 1]));
                bool afterOk = after >= name.Length || !(name[after] >= 'a' && name[after] <= 'z') ||
                               (name[after] == 's' && (after + 1 >= name.Length || !char.IsLower(name[after + 1])));
                if (beforeOk && afterOk) return true;
            }

            return false;
        }

        /// <summary>「发」后跟这些字时表示发饰（发夹/发卡/发饰/发带/发圈/发箍）或其它词（发光），不算发型。</summary>
        private const string HairAccessorySuffixChars = "光夹卡饰带圈箍";

        private static bool IsAscii(string s)
        {
            foreach (char c in s)
            {
                if (c > 127) return false;
            }

            return true;
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static List<string> CollectRendererNames(GameObject root)
        {
            List<string> names = new List<string>();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                names.Add(r.gameObject.name);
            }

            return names;
        }
    }
}
