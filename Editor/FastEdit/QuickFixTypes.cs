using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>一个可供调整的胸/脚形态键（来自角色身体）。</summary>
    public class FixShape
    {
        /// <summary>形态键名。</summary>
        public string Name = string.Empty;
        /// <summary>所属身体 SkinnedMeshRenderer 的 GameObject。</summary>
        public GameObject BodyMesh = null;
        /// <summary>是否为推荐项（名字带 Breast / heels 等字样）。</summary>
        public bool Recommended = false;
        /// <summary>是否为「空」形态键（存在但实际不变形网格）——不进推荐。</summary>
        public bool Empty = false;
        /// <summary>当前滑条值（0~100）。</summary>
        public float Value = 0f;
        /// <summary>是否已锁定（锁定后写入 MA ShapeChanger，滑条锁定值）。</summary>
        public bool Locked = false;
    }

    /// <summary>服装切换下的一个服装项（默认服装 + 各服装槽）。</summary>
    public class FixClothRow
    {
        /// <summary>服装项对象（默认服装 或 服装_N 槽位节点）。</summary>
        public GameObject SlotObject = null;
        /// <summary>显示名（默认服装 / 服装_1 …）。</summary>
        public string Name = string.Empty;
        /// <summary>是否为默认服装。</summary>
        public bool IsDefault = false;
        /// <summary>该项是否正在预览。</summary>
        public bool Previewing = false;
        /// <summary>形态键调整区是否展开。</summary>
        public bool Expanded = false;
        /// <summary>本服装项下的形态键调整条目（每个是身体形态键的副本，含各自滑条值/锁定状态）。</summary>
        public List<FixShape> Shapes = new List<FixShape>();
    }

    /// <summary>快速修复胸/脚穿模的会话状态（本次打开窗口期间有效）。</summary>
    public class QuickFixSession
    {
        public VRCAvatarDescriptor Avatar = null;
        /// <summary>用户通过弹窗指定的胸/脚形态键（身体侧，含所属 mesh）。为空表示尚未指定。</summary>
        public List<FixShape> DesignatedShapes = new List<FixShape>();
        /// <summary>服装列表。</summary>
        public List<FixClothRow> ClothRows = new List<FixClothRow>();
    }

    /// <summary>形态键关键词与常量。</summary>
    public static class QuickFixConst
    {
        public const string UndoName = "快速修复穿模：锁定形态键";
        public const string MenuPath = "焦糖工具箱/快速修复胸、脚穿模";

        /// <summary>推荐项关键词（名字含这些字样即标为推荐）。不含 chest 等泛词。</summary>
        public static readonly string[] RecommendedKeywords =
        {
            "breast", "heels", "highheel", "high_heel", "busty", "heel",
        };

        /// <summary>形态键值范围。</summary>
        public const float ShapeMin = 0f;
        public const float ShapeMax = 100f;
    }

    /// <summary>关键词匹配工具。</summary>
    public static class QuickFixClassifier
    {
        /// <summary>名字是否命中推荐关键词。</summary>
        public static bool IsRecommended(string shapeName)
        {
            if (string.IsNullOrEmpty(shapeName)) return false;
            string lower = shapeName.ToLowerInvariant();
            foreach (string kw in QuickFixConst.RecommendedKeywords)
            {
                if (lower.IndexOf(kw, System.StringComparison.Ordinal) >= 0) return true;
            }

            return false;
        }
    }
}
