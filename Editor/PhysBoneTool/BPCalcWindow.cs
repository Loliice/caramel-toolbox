// BPCalcWindow.cs
// 放置路径：Assets/Editor/BPCalcWindow.cs
// 需要安装 VRChat Avatars SDK3

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

public class BPCalcWindow : EditorWindow
{
    private GameObject targetRoot;
    private int totalCount = 0;
    private List<GameObject> resultObjects = new List<GameObject>();
    private List<string> resultPaths = new List<string>();
    private List<int> resultGroups = new List<int>();   // 每条结果所属的参数分组
    private int groupCount = 0;                         // 参数一致的分组数量（只用于配色，界面不标注）
    private int mergeGroupCount = 0;                    // 识别到的 AAO 合并组数量
    private int mergedBoneCount = 0;                    // 被 AAO 合并掉的动骨根数
    private Vector2 scrollPos;
    private bool calculated = false;
    private GameObject lastAutoTarget;       // 已自动取用过的选中物体，避免每帧重复计算
    private bool autoFilled = false;         // 当前根物体是否为自动取用的
    private bool autoPickSuppressed = false; // 用户手动清空过，不再自动填充（修复"清不掉"的 bug）
    private bool helpExpanded = false;       // 顶部说明是否展开
    private double helpHoverStart = 0.0;     // 鼠标悬停在问号按钮上的起始时刻

    // 分组配色：作为明细条目的背景色，文字统一白色、描边黑色
    private static readonly Color[] GroupColors =
    {
        new Color(0.18f, 0.53f, 0.76f), // 蓝
        new Color(0.75f, 0.22f, 0.17f), // 红
        new Color(0.15f, 0.68f, 0.38f), // 绿
        new Color(0.84f, 0.54f, 0.06f), // 橙
        new Color(0.56f, 0.27f, 0.68f), // 紫
        new Color(0.09f, 0.63f, 0.52f), // 青
        new Color(0.69f, 0.38f, 0.10f), // 棕
        new Color(0.20f, 0.29f, 0.37f), // 蓝灰
        new Color(0.85f, 0.53f, 0.50f), // 粉
        new Color(0.36f, 0.43f, 0.49f), // 灰蓝
    };

    [MenuItem("焦糖工具箱/动骨计算工具")]
    public static void OpenWindow()
    {
        BPCalcWindow window = GetWindow<BPCalcWindow>("动骨计算工具");
        window.minSize = new Vector2(420, 350);
        window.Show();
    }

    private void OnEnable()
    {
        // 让窗口能收到 MouseMove 事件，问号按钮的悬停计时才可靠
        wantsMouseMove = true;
    }

    private void OnSelectionChange()
    {
        // 选中变化时刷新高亮
        Repaint();
    }

    /// <summary>根物体为空时，自动取当前选中物体并立即计算。</summary>
    private void AutoPickFromSelection()
    {
        if (autoPickSuppressed) return; // 用户手动清空过，尊重用户意愿，不再自动填回
        if (targetRoot != null) return;

        GameObject sel = Selection.activeGameObject;
        if (sel == null)
        {
            lastAutoTarget = null;
            return;
        }
        if (sel == lastAutoTarget) return; // 同一个物体只自动算一次

        targetRoot = sel;
        lastAutoTarget = sel;
        autoFilled = true;
        CalculatePhysBones();
    }

    /// <summary>点击计算按钮：直接以当前选中物体为根进行计算。</summary>
    private void CalculateFromSelection()
    {
        GameObject sel = Selection.activeGameObject;
        if (sel != null)
        {
            targetRoot = sel;
            lastAutoTarget = sel;      // 与自动取值闸门同步，避免重复计算
            autoFilled = false;        // 这是用户主动指定的
            autoPickSuppressed = false;
        }
        if (targetRoot == null) return;
        CalculatePhysBones();
    }

    /// <summary>清空根物体与结果。置上 autoPickSuppressed，否则会被自动取值立刻填回。</summary>
    private void ClearRoot()
    {
        targetRoot = null;
        lastAutoTarget = null;
        autoFilled = false;
        autoPickSuppressed = true;

        calculated = false;
        totalCount = 0;
        groupCount = 0;
        mergeGroupCount = 0;
        mergedBoneCount = 0;
        resultObjects.Clear();
        resultPaths.Clear();
        resultGroups.Clear();
    }

    private void OnGUI()
    {
        AutoPickFromSelection(); // 根物体为空时，自动取当前选中物体并计算

        GUILayout.Space(10);

        // 标题行：旁边放一个问号按钮，点击或悬停 1 秒展开说明
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("焦糖工具箱 · 动骨计算工具", EditorStyles.boldLabel);

            if (GUILayout.Button("?", EditorStyles.miniButton, GUILayout.Width(20)))
                helpExpanded = !helpExpanded;

            Rect helpRect = GUILayoutUtility.GetLastRect();
            bool hovering = helpRect.Contains(Event.current.mousePosition);
            if (hovering)
            {
                if (helpHoverStart <= 0.0)
                    helpHoverStart = EditorApplication.timeSinceStartup; // 开始计时
                else if (!helpExpanded && EditorApplication.timeSinceStartup - helpHoverStart >= 1.0)
                    helpExpanded = true;                                 // 悬停满 1 秒自动展开

                // 鼠标停在按钮上不动时不会再有事件，靠持续重绘把计时跑完
                if (!helpExpanded) Repaint();
            }
            else
            {
                helpHoverStart = 0.0;
            }

            GUILayout.FlexibleSpace();
        }

        GUILayout.Space(5);

        if (helpExpanded)
        {
            EditorGUILayout.HelpBox(
                "选择一个根物体，统计其层级下所有 VRCPhysBone（动骨）组件。\n" +
                "点击【计算（当前选中）】会直接以当前选中物体为根计算。\n" +
                "参数一致的动骨会用相同的背景色标出。",
                MessageType.Info
            );
            GUILayout.Space(5);
        }

        GUILayout.Space(10);

        EditorGUI.BeginChangeCheck();
        targetRoot = (GameObject)EditorGUILayout.ObjectField(
            "根物体",
            targetRoot,
            typeof(GameObject),
            true
        );
        if (EditorGUI.EndChangeCheck())
        {
            autoFilled = false; // 手动改过就不再是自动取用的了
            if (targetRoot != null)
            {
                autoPickSuppressed = false;
                CalculatePhysBones(); // 换根物体后立即重算，避免结果与根物体对不上
            }
            else
            {
                ClearRoot(); // 手动清空
            }
        }

        GUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = targetRoot != null || Selection.activeGameObject != null;
            if (GUILayout.Button("计算（当前选中）", GUILayout.Height(30)))
            {
                CalculateFromSelection();
            }
            GUI.enabled = targetRoot != null || calculated;
            if (GUILayout.Button("清空", GUILayout.Width(50), GUILayout.Height(30)))
            {
                ClearRoot();
            }
            GUI.enabled = true;
        }

        GUILayout.Space(10);

        if (calculated)
        {
#if VRC_SDK_VRCSDK3
            GUIStyle countStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 };
            EditorGUILayout.LabelField($"动骨组件总数：{totalCount}", countStyle);
            GUILayout.Space(5);
            if (mergeGroupCount > 0)
                EditorGUILayout.LabelField(
                    $"已识别 AAO 合并：{mergeGroupCount} 组，{mergedBoneCount} 根动骨已合并为一条",
                    EditorStyles.miniLabel);
            if (autoFilled && targetRoot != null)
                EditorGUILayout.LabelField("（根物体原为空，已自动取用当前选中物体）", EditorStyles.miniLabel);
            GUILayout.Space(10);

            if (resultObjects.Count > 0)
            {
                GUILayout.Label("明细（点击可选中对应物体）：", EditorStyles.boldLabel);

                // 排版基准样式（取按钮的对齐/换行/字号；边框与背景在 MakeTextStyle 里清掉）
                GUIStyle itemStyle = new GUIStyle(GUI.skin.button)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true,
                    fontSize = 11,
                    padding = new RectOffset(8, 8, 3, 3),
                    margin = new RectOffset(2, 2, 1, 1)
                };

                // 每组一套样式：正文 / 选中（加粗） / 描边（正文色的反色）
                int styleCount = Mathf.Max(groupCount, 1);
                GUIStyle[] textStyles = new GUIStyle[styleCount];
                GUIStyle[] textStylesSelected = new GUIStyle[styleCount];
                GUIStyle[] outlineStyles = new GUIStyle[styleCount];
                for (int g = 0; g < styleCount; g++)
                {
                    textStyles[g] = MakeTextStyle(itemStyle, false, false);
                    textStylesSelected[g] = MakeTextStyle(itemStyle, true, false);
                    outlineStyles[g] = MakeTextStyle(itemStyle, false, true);
                }

                // 透明点击热区样式：清空所有状态的背景、边框，并把文字设为全透明
                // 注意不能依赖 GUIStyle.none —— 它会画出自己的背景，把自绘的颜色盖掉
                GUIStyle clickStyle = new GUIStyle(GUI.skin.button);
                clickStyle.border = new RectOffset(0, 0, 0, 0);
                Color invisible = new Color(0f, 0f, 0f, 0f);
                clickStyle.normal.background = null;
                clickStyle.hover.background = null;
                clickStyle.active.background = null;
                clickStyle.focused.background = null;
                clickStyle.onNormal.background = null;
                clickStyle.onHover.background = null;
                clickStyle.onActive.background = null;
                clickStyle.onFocused.background = null;
                clickStyle.normal.textColor = invisible;
                clickStyle.hover.textColor = invisible;
                clickStyle.active.textColor = invisible;
                clickStyle.focused.textColor = invisible;
                clickStyle.onNormal.textColor = invisible;
                clickStyle.onHover.textColor = invisible;
                clickStyle.onActive.textColor = invisible;
                clickStyle.onFocused.textColor = invisible;

                const float swatchWidth = 10f; // 左侧色条宽度，按钮从这里往右缩

                scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));

                for (int i = 0; i < resultObjects.Count; i++)
                {
                    if (resultObjects[i] == null) continue;

                    int g = i < resultGroups.Count ? resultGroups[i] : 0;
                    int gi = g % styleCount;
                    Color bgColor = GroupColors[g % GroupColors.Length];
                    bool isSelected = Selection.activeGameObject == resultObjects[i];

                    GUIContent content = new GUIContent(resultPaths[i]);

                    // 行布局交给隐形按钮：布局系统会按换行算出正确行高
                    Rect textRect;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(swatchWidth); // 左边让出一条给色条
                        if (GUILayout.Button(content, clickStyle,
                                GUILayout.ExpandWidth(true), GUILayout.MinHeight(18)))
                        {
                            Selection.activeGameObject = resultObjects[i];
                            EditorGUIUtility.PingObject(resultObjects[i]);
                            Repaint();
                        }
                        textRect = GUILayoutUtility.GetLastRect();
                    }

                    Rect swatchRect = new Rect(textRect.x - swatchWidth, textRect.y,
                        swatchWidth, textRect.height);

                    if (Event.current.type == EventType.Repaint)
                    {
                        // 左侧色条 + 一条细分隔线（色条在按钮矩形之外，不可能被盖住）
                        EditorGUI.DrawRect(swatchRect, bgColor);
                        EditorGUI.DrawRect(new Rect(swatchRect.xMax, textRect.y, 1f, textRect.height),
                            new Color(0f, 0f, 0f, 0.4f));

                        // 条目底色 + 悬停提亮
                        EditorGUI.DrawRect(textRect, bgColor);
                        if (textRect.Contains(Event.current.mousePosition))
                            EditorGUI.DrawRect(textRect, new Color(1f, 1f, 1f, 0.10f));

                        // 反色描边四向偏移 1px，正文最后画
                        GUIStyle os = outlineStyles[gi];
                        GUI.Label(new Rect(textRect.x - 1, textRect.y, textRect.width, textRect.height), content, os);
                        GUI.Label(new Rect(textRect.x + 1, textRect.y, textRect.width, textRect.height), content, os);
                        GUI.Label(new Rect(textRect.x, textRect.y - 1, textRect.width, textRect.height), content, os);
                        GUI.Label(new Rect(textRect.x, textRect.y + 1, textRect.width, textRect.height), content, os);
                        GUI.Label(textRect, content, isSelected ? textStylesSelected[gi] : textStyles[gi]);
                    }
                }

                EditorGUILayout.EndScrollView();
            }
            else
            {
                EditorGUILayout.HelpBox("该层级下未找到任何 VRCPhysBone 组件。", MessageType.Warning);
            }
#else
            EditorGUILayout.HelpBox(
                "未检测到 VRC SDK3，请先安装 VRChat Avatars SDK 后再使用本工具。",
                MessageType.Error
            );
#endif
        }
        else if (targetRoot == null)
        {
            EditorGUILayout.HelpBox(
                autoPickSuppressed
                    ? "根物体已清空。点击【计算（当前选中）】可重新取用当前选中物体。"
                    : "请在上方指定一个根物体，或先在场景中选中一个物体。",
                MessageType.None
            );
        }
    }

    private void CalculatePhysBones()
    {
        totalCount = 0;
        resultObjects.Clear();
        resultPaths.Clear();
        resultGroups.Clear();
        groupCount = 0;
        mergeGroupCount = 0;
        mergedBoneCount = 0;
        calculated = true;

#if VRC_SDK_VRCSDK3
        if (targetRoot == null) return;

        VRCPhysBone[] bones = targetRoot.GetComponentsInChildren<VRCPhysBone>(includeInactive: true);

        // 先找出 AAO 的 MergePhysBone：它引用到的动骨会被屏蔽，整组视为一个
        var mergeGroups = new List<MergeGroup>();
        var boneToGroup = new Dictionary<VRCPhysBone, int>();
        CollectMergeGroups(targetRoot, bones, mergeGroups, boneToGroup);

        var sigToGroup = new Dictionary<string, int>();
        var emittedGroups = new HashSet<int>();
        int rows = 0;

        foreach (var bone in bones)
        {
            string sig;
            GameObject go;
            string path;

            if (boneToGroup.TryGetValue(bone, out int mg))
            {
                if (!emittedGroups.Add(mg)) continue; // 整组只显示一行，其余元素屏蔽
                MergeGroup grp = mergeGroups[mg];
                sig = grp.bones.Count > 0 ? BuildParameterSignature(grp.bones[0]) : "";
                go = grp.component != null ? grp.component.gameObject : null;
                string basePath = grp.component != null
                    ? GetRelativePath(targetRoot.transform, grp.component.transform)
                    : "(AAO 合并)";
                path = $"{basePath}  「AAO 合并 · {grp.bones.Count} 根动骨」";
                mergedBoneCount += grp.bones.Count;
            }
            else
            {
                sig = BuildParameterSignature(bone);
                go = bone.gameObject;
                path = GetRelativePath(targetRoot.transform, bone.transform);
            }

            if (!sigToGroup.TryGetValue(sig, out int g))
            {
                g = groupCount++;
                sigToGroup[sig] = g;
            }
            resultObjects.Add(go);
            resultPaths.Add(path);
            resultGroups.Add(g);
            rows++;
        }

        mergeGroupCount = emittedGroups.Count;
        totalCount = rows; // 被合并的动骨按"一个"计数，更贴近实际运行开销
#endif
    }

#if VRC_SDK_VRCSDK3
    private class MergeGroup
    {
        public Component component;
        public readonly List<VRCPhysBone> bones = new List<VRCPhysBone>();
    }

    /// <summary>
    /// 收集 AAO「Merge PhysBone」组件。按类型名识别，不在代码里引用 AAO 程序集，
    /// 这样没装 AAO 的工程也能编译通过；元素通过 SerializedObject 遍历引用拿到，
    /// 不依赖 AAO 的具体字段名（它换字段名也不会失效）。
    /// </summary>
    private static void CollectMergeGroups(GameObject root, VRCPhysBone[] allBones,
        List<MergeGroup> groups, Dictionary<VRCPhysBone, int> boneToGroup)
    {
        var scope = new HashSet<VRCPhysBone>(allBones); // 只统计本次范围内的动骨
        foreach (var c in root.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            if (c.GetType().Name != "MergePhysBone") continue;

            var group = new MergeGroup { component = c };
            foreach (var bone in CollectReferencedPhysBones(c))
            {
                if (!scope.Contains(bone)) continue;
                if (group.bones.Contains(bone)) continue;
                group.bones.Add(bone);
            }
            if (group.bones.Count == 0) continue; // 解析不到元素就当普通组件忽略

            int index = groups.Count;
            groups.Add(group);
            foreach (var bone in group.bones)
                if (!boneToGroup.ContainsKey(bone)) boneToGroup[bone] = index;
        }
    }

    // 遍历组件的所有序列化引用，凡是能解析出 VRCPhysBone 的都算一个被合并的元素
    private static List<VRCPhysBone> CollectReferencedPhysBones(Component mergeComponent)
    {
        var list = new List<VRCPhysBone>();
        SerializedObject so = new SerializedObject(mergeComponent);
        SerializedProperty it = so.GetIterator();
        while (it.NextVisible(true))
        {
            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
            Object obj = it.objectReferenceValue;
            if (obj == null) continue;
            AddPhysBonesFrom(obj, list);
        }
        return list;
    }

    private static void AddPhysBonesFrom(Object obj, List<VRCPhysBone> list)
    {
        if (obj is VRCPhysBone pb)
        {
            if (!list.Contains(pb)) list.Add(pb);
            return;
        }
        if (obj is GameObject go)
        {
            foreach (var b in go.GetComponents<VRCPhysBone>())
                if (!list.Contains(b)) list.Add(b);
            return;
        }
        if (obj is Transform t)
        {
            foreach (var b in t.GetComponents<VRCPhysBone>())
                if (!list.Contains(b)) list.Add(b);
        }
    }

    /// <summary>
    /// 生成动骨参数的签名字符串：只比较组件内的数值（float / int / bool / 枚举 / 曲线）。
    /// 引用类（colliders、rootTransform…）与几何量（endpointPosition 是 Vector3）都不参与比较，
    /// 因为它们每根骨头天然不同，算进去的话分组会退化成"每根一个颜色"。
    /// </summary>
    private string BuildParameterSignature(VRCPhysBone bone)
    {
        StringBuilder sb = new StringBuilder();
        SerializedObject so = new SerializedObject(bone);
        SerializedProperty it = so.GetIterator();
        while (it.NextVisible(true))
        {
            string path = it.propertyPath;
            if (path.StartsWith("m_")) continue;     // Unity 自带属性，每个组件都不同
            if (!IsNumericValue(it.propertyType)) continue;

            sb.Append(path).Append('=');
            AppendPropertyValue(sb, it);
            sb.Append(';');
        }
        return sb.ToString();
    }

    // 只认数值：引用、字符串、Vector/Color 等一律不比较
    private static bool IsNumericValue(SerializedPropertyType t)
    {
        return t == SerializedPropertyType.Float
            || t == SerializedPropertyType.Integer
            || t == SerializedPropertyType.Boolean
            || t == SerializedPropertyType.Enum
            || t == SerializedPropertyType.AnimationCurve;
    }

    private static void AppendPropertyValue(StringBuilder sb, SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Float:
                sb.Append(p.floatValue.ToString("0.####")); break;
            case SerializedPropertyType.Integer:
                sb.Append(p.intValue); break;
            case SerializedPropertyType.Boolean:
                sb.Append(p.boolValue); break;
            case SerializedPropertyType.Enum:
                sb.Append(p.enumValueIndex); break;
            case SerializedPropertyType.AnimationCurve:
                AnimationCurve curve = p.animationCurveValue;
                if (curve != null)
                    foreach (Keyframe k in curve.keys)
                        sb.Append(k.time.ToString("0.###")).Append(':').Append(k.value.ToString("0.###")).Append(' ');
                break;
        }
    }
#endif

    // ---------------- 分组配色：背景色 + 白字 + 黑描边 ----------------
    // 注意：背景用 EditorGUI.DrawRect、文字用 GUI.Label 手动绘制，不走 GUIStyle 的
    // background 纹理 —— 按钮皮肤自带的 border(9-slice) 比小纹理大时，背景会整体画不出来。
    //
    // 文字统一白色，描边统一黑色：浅色背景（如粉、橙）靠黑描边勾出字形，深色背景直接白字即可。

    // 正文/描边样式：清掉背景和边框，只保留排版参数，字色按用途区分
    private static GUIStyle MakeTextStyle(GUIStyle baseStyle, bool bold, bool outline)
    {
        GUIStyle s = new GUIStyle(baseStyle);
        s.border = new RectOffset(0, 0, 0, 0);
        s.normal.background = null;
        s.hover.background = null;
        s.active.background = null;
        s.focused.background = null;
        s.onNormal.background = null;
        s.onHover.background = null;
        s.onActive.background = null;
        s.onFocused.background = null;

        Color fg = outline ? Color.black : Color.white; // 白字 + 黑描边
        s.normal.textColor = fg;
        s.hover.textColor = fg;
        s.active.textColor = fg;
        s.focused.textColor = fg;
        s.onNormal.textColor = fg;
        s.onHover.textColor = fg;
        s.onActive.textColor = fg;
        s.onFocused.textColor = fg;

        if (bold) s.fontStyle = FontStyle.Bold;
        return s;
    }

    private string GetRelativePath(Transform root, Transform target)
    {
        if (target == root) return root.name;

        List<string> parts = new List<string>();
        Transform current = target;
        while (current != null && current != root)
        {
            parts.Insert(0, current.name);
            current = current.parent;
        }
        parts.Insert(0, root.name);
        return string.Join("/", parts);
    }
}
#endif
