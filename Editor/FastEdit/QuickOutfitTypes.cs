using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>素材类型。</summary>
    public enum AssetCategory
    {
        Unknown = 0,
        Clothes = 1,
        Hair = 2,
        Accessory = 3,
        Prop = 4,
    }

    /// <summary>骨骼绑定动作。</summary>
    public enum BindAction
    {
        None = 0,
        SetupOutfit = 1,
        BoneProxyHead = 2,
        AlreadyBound = 3,
        /// <summary>衣服未找到 Hips：不调用 Setup Outfit，仅警告。</summary>
        SetupOutfitNoHips = 4,
    }

    /// <summary>全局常量与类型相关的工具函数。</summary>
    public static class QuickOutfitConst
    {
        public const string UndoName = "快速改模";
        public const string AppearanceRootName = "外观设置";
        public const string ClothesContainerName = "服装切换";
        public const string HairContainerName = "发型切换";
        public const string AccessoryContainerName = "配饰";
        public const string PropContainerName = "道具";
        public const string PartContainerName = "服装部件";
        public const string DefaultClothesName = "默认服装";
        public const string DefaultHairName = "默认发型";
        public const string DefaultClothesParam = "MA服装切换";
        public const string DefaultHairParam = "MA发型切换";
        public const string TemplateGuid = "f47e66b06d69d424a91f35a62561e463";
        public const string TemplatePath = "Assets/CaramelToolbox/Editor/FastEdit/外观设置.prefab";
        public const int MenuItemLimit = 8;

        /// <summary>参与槽位管理的四个类别（按处理顺序）。</summary>
        public static readonly AssetCategory[] SlotCategories =
        {
            AssetCategory.Clothes,
            AssetCategory.Hair,
            AssetCategory.Accessory,
            AssetCategory.Prop,
        };

        /// <summary>类型按钮中可手动选择的类别。</summary>
        public static readonly AssetCategory[] SelectableCategories = SlotCategories;

        public static string CategoryLabel(AssetCategory category)
        {
            switch (category)
            {
                case AssetCategory.Clothes: return "衣服";
                case AssetCategory.Hair: return "发型";
                case AssetCategory.Accessory: return "配饰";
                case AssetCategory.Prop: return "道具";
                default: return "未识别";
            }
        }

        /// <summary>该类别在外观设置下的容器节点名。</summary>
        public static string ContainerName(AssetCategory category)
        {
            switch (category)
            {
                case AssetCategory.Clothes: return ClothesContainerName;
                case AssetCategory.Hair: return HairContainerName;
                case AssetCategory.Accessory: return AccessoryContainerName;
                case AssetCategory.Prop: return PropContainerName;
                default: return string.Empty;
            }
        }

        /// <summary>槽位默认命名前缀（服装_N 等）。</summary>
        public static string SlotPrefix(AssetCategory category)
        {
            switch (category)
            {
                case AssetCategory.Clothes: return "服装";
                case AssetCategory.Hair: return "发型";
                case AssetCategory.Accessory: return "配饰";
                case AssetCategory.Prop: return "道具";
                default: return string.Empty;
            }
        }

        /// <summary>是否为带“默认项”的互斥切换类别（服装/发型）。</summary>
        public static bool UsesDefaultItem(AssetCategory category)
        {
            return category == AssetCategory.Clothes || category == AssetCategory.Hair;
        }

        public static string DefaultItemName(AssetCategory category)
        {
            switch (category)
            {
                case AssetCategory.Clothes: return DefaultClothesName;
                case AssetCategory.Hair: return DefaultHairName;
                default: return string.Empty;
            }
        }

        public static string DefaultParamName(AssetCategory category)
        {
            switch (category)
            {
                case AssetCategory.Clothes: return DefaultClothesParam;
                case AssetCategory.Hair: return DefaultHairParam;
                default: return string.Empty;
            }
        }

        public static string BindActionLabel(BindAction action)
        {
            switch (action)
            {
                case BindAction.SetupOutfit: return "Setup Outfit";
                case BindAction.BoneProxyHead: return "Bone Proxy→Head";
                case BindAction.AlreadyBound: return "已有绑定，跳过";
                case BindAction.SetupOutfitNoHips: return "⚠ 未找到 Hips，Setup Outfit 将跳过";
                default: return "不绑定";
            }
        }
    }

    /// <summary>素材列表中的一个条目。</summary>
    [Serializable]
    public class OutfitEntry
    {
        public GameObject Source = null;
        public AssetCategory Category = AssetCategory.Unknown;
        public AssetCategory AutoCategory = AssetCategory.Unknown;

        /// <summary>
        /// 用户是否手动指定了类型。为 true 时任何自动识别结果都不得覆盖 <see cref="Category"/>。
        /// （v1 字段名为 ManualCategory，保留序列化兼容。）
        /// </summary>
        [FormerlySerializedAs("ManualCategory")] public bool IsManual = false;

        /// <summary>自动识别的打分明细（用于提示）。</summary>
        public string AutoDetail = string.Empty;

        public string SlotName = string.Empty;

        /// <summary>是否为 Project 中的资源（prefab/模型），否则为场景对象。</summary>
        public bool IsAsset
        {
            get { return Source != null && EditorUtility.IsPersistent(Source); }
        }

        public string DisplayName
        {
            get { return Source != null ? Source.name : "(已丢失)"; }
        }

        /// <summary>列表中显示的类型标签：自动识别显示「衣服(自动)」，手动指定显示「衣服」。</summary>
        public string CategoryDisplay
        {
            get
            {
                string label = QuickOutfitConst.CategoryLabel(Category);
                if (Category == AssetCategory.Unknown || IsManual) return label;
                return label + "(自动)";
            }
        }

        /// <summary>用户手动指定类型（之后自动识别不再覆盖）。</summary>
        public void SetManualCategory(AssetCategory category)
        {
            Category = category;
            IsManual = true;
        }

        /// <summary>
        /// 写入自动识别结果。仅当 <see cref="IsManual"/> 为 false 时才会改变 <see cref="Category"/>；返回是否改变了类型。
        /// </summary>
        public bool ApplyAutoCategory(AssetCategory category, string detail)
        {
            AutoCategory = category;
            AutoDetail = detail ?? string.Empty;
            if (IsManual) return false;
            bool changed = Category != category;
            Category = category;
            return changed;
        }
    }

    /// <summary>规划中单个素材的处理方案。</summary>
    public class PlanItem
    {
        public OutfitEntry Entry = null;
        public AssetCategory Category = AssetCategory.Unknown;
        public bool Skip = false;
        public string SkipReason = string.Empty;
        public bool IsNewSlot = false;
        /// <summary>复用已有槽位时，在该类别已有槽位列表（层级顺序）中的索引。</summary>
        public int ExistingSlotIndex = -1;
        /// <summary>放置时的槽位名（已有槽位的规范化名，或新建槽位名）。</summary>
        public string SlotNameBefore = string.Empty;
        /// <summary>最终槽位名（改名后）。</summary>
        public string SlotNameAfter = string.Empty;
        public BindAction Bind = BindAction.None;
        public string Note = string.Empty;

        public bool WillRename
        {
            get { return !Skip && !string.Equals(SlotNameBefore, SlotNameAfter, StringComparison.Ordinal); }
        }
    }

    /// <summary>将被删除的空槽。</summary>
    public class PlanDeletion
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public int ExistingSlotIndex = -1;
        public string SlotName = string.Empty;
    }

    /// <summary>为保证“每类至少 1 个槽”而新建的空槽。</summary>
    public class PlanCreation
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public string SlotName = string.Empty;
    }

    /// <summary>规范化改名（配饰1→配饰_1）。</summary>
    public class PlanRename
    {
        public AssetCategory Category = AssetCategory.Unknown;
        public string OldName = string.Empty;
        public string NewName = string.Empty;
    }

    /// <summary>一次“快速应用”的完整规划。预览与执行共用。</summary>
    public class ApplyPlan
    {
        public VRCAvatarDescriptor Avatar = null;
        public bool CreateAppearance = false;
        public AppearanceInfo Info = null;
        public List<PlanRename> NormalizeRenames = new List<PlanRename>();
        public List<PlanItem> Items = new List<PlanItem>();
        public List<PlanDeletion> Deletions = new List<PlanDeletion>();
        public List<PlanCreation> MinimumCreations = new List<PlanCreation>();
        public Dictionary<AssetCategory, int> ExistingSlotCounts = new Dictionary<AssetCategory, int>();
        public Dictionary<AssetCategory, List<bool>> ExistingEmptyFlags = new Dictionary<AssetCategory, List<bool>>();
        public List<string> Warnings = new List<string>();
        public List<string> Errors = new List<string>();

        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }

        public PlanItem FindItem(OutfitEntry entry)
        {
            foreach (PlanItem item in Items)
            {
                if (item.Entry == entry) return item;
            }

            return null;
        }

        /// <summary>
        /// 规划摘要（条目→槽位/新建名/改名/绑定、删除列表、补建列表、规范化、是否创建外观设置）。
        /// 用于应用前比对预览与最新规划是否一致。
        /// </summary>
        public string Signature()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(Avatar != null ? Avatar.GetInstanceID().ToString() : "null").Append('|');
            sb.Append(CreateAppearance ? "C" : "E").Append('|');
            foreach (PlanRename r in NormalizeRenames) sb.Append("R:").Append(r.OldName).Append('>').Append(r.NewName).Append(';');
            foreach (PlanItem i in Items)
            {
                sb.Append("I:").Append(i.Entry != null && i.Entry.Source != null ? i.Entry.Source.GetInstanceID() : 0)
                    .Append(',').Append((int)i.Category)
                    .Append(',').Append(i.Skip ? "S" + i.SkipReason : string.Empty)
                    .Append(',').Append(i.IsNewSlot ? "N" : "O").Append(i.ExistingSlotIndex)
                    .Append(',').Append(i.SlotNameBefore).Append('>').Append(i.SlotNameAfter)
                    .Append(',').Append((int)i.Bind).Append(';');
            }

            foreach (PlanDeletion d in Deletions) sb.Append("D:").Append((int)d.Category).Append(',').Append(d.ExistingSlotIndex).Append(',').Append(d.SlotName).Append(';');
            foreach (PlanCreation c in MinimumCreations) sb.Append("M:").Append((int)c.Category).Append(',').Append(c.SlotName).Append(';');
            foreach (string e in Errors) sb.Append("E:").Append(e).Append(';');
            return sb.ToString();
        }
    }

    /// <summary>执行结果。</summary>
    public class ApplyResult
    {
        public bool Success = false;
        public List<string> Logs = new List<string>();
        public List<string> Warnings = new List<string>();
        public string Summary = string.Empty;
    }
}
