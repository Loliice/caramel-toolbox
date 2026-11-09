// Version: v4
// CaramelToolbox - QuickTextureCompressor
// Path: Assets/CaramelToolbox/Editor/QuickTextureCompressor.cs
// Menu: 焦糖工具箱 / 快速贴图压缩
// 统计部分完全复刻 Thry 的 VRAM Calculator（Assets / Show assets of disabled Objects / Textures / Meshes）
// 在其基础上附加：快速压缩（每张贴图降一个阶段）、高级压缩（分别限制普通贴图 / 蒙版法线贴图的最大分辨率）
// 注意：不再依赖 Thry.AvatarHelpers（Packages/... 的程序集在 Assets 下引用不到，会 CS0246）。
// 需要的 ToMebiByteString / ToShortMebiByteString / Quality 都在本文件内自实现，与原插件口径一致。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace CaramelToolbox
{
    public class QuickTextureCompressor : EditorWindow
    {
        private enum Mode
        {
            StatsOnly = 0, // 仅统计（界面与 VRAM Calculator 一致，不修改任何资源）
            Quick = 1,     // 快速压缩：每张贴图降一个阶段（2048 -> 1024）
            Advanced = 2,  // 高级压缩：分别限制普通贴图 / 蒙版·法线贴图的最大分辨率
        }

        // 显存档位（原本取自 Thry.AvatarHelpers.Quality，改为本地枚举）
        private enum Quality { Excellent, Good, Medium, Poor, VeryPoor }

        private class TexEntry
        {
            public Texture texture;
            public string print;
            public long size;
            public bool isActive;
            public float BPP;
            public int minBPP;
            public string formatString = "";
            public TextureFormat format;
            public bool hasAlpha;

            public List<Material> materials = new List<Material>();
            public bool materialDropDown;

            // 压缩相关
            public bool canCompress;
            public bool isMask;
            public bool isNormal;
            public bool isGrayscale; // 黑白（灰度）贴图
            public bool selected = true;

            public TextureImporter importer;
            public TextureImporterFormat currentFormat; // 当前（Standalone）生效的压缩格式
            public bool crunchedOn;                     // 当前是否开着 Crunch

            public int currentMaxSize;                  // 导入设置里的分辨率上限
            public int targetSize;                      // 目标分辨率上限
            public TextureImporterFormat targetFormat;  // 目标压缩格式
            public bool sizeWillChange;
            public bool formatWillChange;
            public bool crunchWillChange;
            public bool willChange;
            public long targetBytes;

            public int CurrentRes { get { return Mathf.Max(texture.width, texture.height); } }
        }

        private class MeshEntry
        {
            public Mesh mesh;
            public string print;
            public long size;
            public bool isActive;
        }

        // ---------------- 每像素位数（bits per pixel），取自 VRAM Calculator ----------------
        private static readonly Dictionary<TextureFormat, float> BPP = new Dictionary<TextureFormat, float>()
        {
            { TextureFormat.Alpha8, 9 },
            { TextureFormat.ARGB4444, 16 },
            { TextureFormat.RGB24, 24 },
            { TextureFormat.RGBA32, 32 },
            { TextureFormat.ARGB32, 32 },
            { TextureFormat.RGB565, 16 },
            { TextureFormat.R16, 16 },
            { TextureFormat.DXT1, 4 },
            { TextureFormat.DXT5, 8 },
            { TextureFormat.RGBA4444, 16 },
            { TextureFormat.BGRA32, 32 },
            { TextureFormat.RHalf, 16 },
            { TextureFormat.RGHalf, 32 },
            { TextureFormat.RGBAHalf, 64 },
            { TextureFormat.RFloat, 32 },
            { TextureFormat.RGFloat, 64 },
            { TextureFormat.RGBAFloat, 128 },
            { TextureFormat.YUY2, 16 },
            { TextureFormat.RGB9e5Float, 32 },
            { TextureFormat.BC6H, 8 },
            { TextureFormat.BC7, 8 },
            { TextureFormat.BC4, 4 },
            { TextureFormat.BC5, 8 },
            { TextureFormat.DXT1Crunched, 4 },
            { TextureFormat.DXT5Crunched, 8 },
            { TextureFormat.PVRTC_RGB2, 6 },
            { TextureFormat.PVRTC_RGBA2, 8 },
            { TextureFormat.PVRTC_RGB4, 12 },
            { TextureFormat.PVRTC_RGBA4, 16 },
            { TextureFormat.ETC_RGB4, 12 },
            { TextureFormat.EAC_R, 4 },
            { TextureFormat.EAC_R_SIGNED, 4 },
            { TextureFormat.EAC_RG, 8 },
            { TextureFormat.EAC_RG_SIGNED, 8 },
            { TextureFormat.ETC2_RGB, 12 },
            { TextureFormat.ETC2_RGBA1, 12 },
            { TextureFormat.ETC2_RGBA8, 32 },
            { TextureFormat.ASTC_4x4, 8 },
            { TextureFormat.ASTC_5x5, 5.12f },
            { TextureFormat.ASTC_6x6, 3.55f },
            { TextureFormat.ASTC_8x8, 2 },
            { TextureFormat.ASTC_10x10, 1.28f },
            { TextureFormat.ASTC_12x12, 1 },
            { TextureFormat.RG16, 16 },
            { TextureFormat.R8, 8 },
            { TextureFormat.ETC_RGB4Crunched, 12 },
            { TextureFormat.ETC2_RGBA8Crunched, 32 },
            { TextureFormat.ASTC_HDR_4x4, 8 },
            { TextureFormat.ASTC_HDR_5x5, 5.12f },
            { TextureFormat.ASTC_HDR_6x6, 3.55f },
            { TextureFormat.ASTC_HDR_8x8, 2 },
            { TextureFormat.ASTC_HDR_10x10, 1.28f },
            { TextureFormat.ASTC_HDR_12x12, 1 },
            { TextureFormat.RG32, 32 },
            { TextureFormat.RGB48, 48 },
            { TextureFormat.RGBA64, 64 },
        };

        private static readonly Dictionary<RenderTextureFormat, float> RT_BPP = new Dictionary<RenderTextureFormat, float>()
        {
            { RenderTextureFormat.ARGB32, 32 },
            { RenderTextureFormat.Depth, 0 },
            { RenderTextureFormat.ARGBHalf, 64 },
            { RenderTextureFormat.Shadowmap, 8 },
            { RenderTextureFormat.RGB565, 32 },
            { RenderTextureFormat.ARGB4444, 16 },
            { RenderTextureFormat.ARGB1555, 16 },
            { RenderTextureFormat.Default, 32 },
            { RenderTextureFormat.ARGB2101010, 32 },
            { RenderTextureFormat.DefaultHDR, 128 },
            { RenderTextureFormat.ARGB64, 64 },
            { RenderTextureFormat.ARGBFloat, 128 },
            { RenderTextureFormat.RGFloat, 64 },
            { RenderTextureFormat.RGHalf, 32 },
            { RenderTextureFormat.RFloat, 32 },
            { RenderTextureFormat.RHalf, 16 },
            { RenderTextureFormat.R8, 8 },
            { RenderTextureFormat.ARGBInt, 128 },
            { RenderTextureFormat.RGInt, 64 },
            { RenderTextureFormat.RInt, 32 },
            { RenderTextureFormat.BGRA32, 32 },
            { RenderTextureFormat.RGB111110Float, 32 },
            { RenderTextureFormat.RG32, 32 },
            { RenderTextureFormat.RGBAUShort, 64 },
            { RenderTextureFormat.RG16, 16 },
            { RenderTextureFormat.BGRA10101010_XR, 40 },
            { RenderTextureFormat.BGR101010_XR, 30 },
            { RenderTextureFormat.R16, 16 },
        };

        private static readonly Dictionary<VertexAttributeFormat, int> VertexAttributeByteSize =
            new Dictionary<VertexAttributeFormat, int>()
            {
                { VertexAttributeFormat.UNorm8, 1 },
                { VertexAttributeFormat.SNorm8, 1 },
                { VertexAttributeFormat.UInt8, 1 },
                { VertexAttributeFormat.SInt8, 1 },
                { VertexAttributeFormat.UNorm16, 2 },
                { VertexAttributeFormat.SNorm16, 2 },
                { VertexAttributeFormat.UInt16, 2 },
                { VertexAttributeFormat.SInt16, 2 },
                { VertexAttributeFormat.Float16, 2 },
                { VertexAttributeFormat.Float32, 4 },
                { VertexAttributeFormat.UInt32, 4 },
                { VertexAttributeFormat.SInt32, 4 },
            };

        // 质量阈值（MiB），与 VRAM Calculator 一致
        private const long PC_TEXTURE_MEMORY_EXCELLENT_MiB = 40;
        private const long PC_TEXTURE_MEMORY_GOOD_MiB = 75;
        private const long PC_TEXTURE_MEMORY_MEDIUM_MiB = 110;
        private const long PC_TEXTURE_MEMORY_POOR_MiB = 150;

        private const long PC_MESH_MEMORY_EXCELLENT_MiB = 20;
        private const long PC_MESH_MEMORY_GOOD_MiB = 35;
        private const long PC_MESH_MEMORY_MEDIUM_MiB = 55;
        private const long PC_MESH_MEMORY_POOR_MiB = 80;

        private const long QUEST_TEXTURE_MEMORY_EXCELLENT_MiB = 10;
        private const long QUEST_TEXTURE_MEMORY_GOOD_MiB = 18;
        private const long QUEST_TEXTURE_MEMORY_MEDIUM_MiB = 25;
        private const long QUEST_TEXTURE_MEMORY_POOR_MiB = 40;

        private const long QUEST_MESH_MEMORY_EXCELLENT_MiB = 5;
        private const long QUEST_MESH_MEMORY_GOOD_MiB = 10;
        private const long QUEST_MESH_MEMORY_MEDIUM_MiB = 15;
        private const long QUEST_MESH_MEMORY_POOR_MiB = 25;

        // 推荐压缩格式对应的每像素位数（bits），用于估算改格式后的显存
        private static readonly Dictionary<TextureImporterFormat, float> ImporterFormatBPP =
            new Dictionary<TextureImporterFormat, float>()
            {
                { TextureImporterFormat.DXT1, 4 },   // BC1
                { TextureImporterFormat.DXT5, 8 },   // BC3
                { TextureImporterFormat.BC4, 4 },
                { TextureImporterFormat.BC5, 8 },
                { TextureImporterFormat.BC7, 8 },
            };

        // 标题栏 ? 按钮展开的说明（原先放在 Textures 折叠里的 HelpBox）
        private const string CompressionHelpText =
            "决定贴图显存占用的是「分辨率」和「压缩格式」，两者都可以在贴图的导入设置里修改。\n\n" +
            "分辨率：站在离模型约 1 米的位置观察，一路降低分辨率直到能看出明显劣化为止。\n" +
            "压缩格式（推荐配置）：法线 BC5 · 黑白（灰度）贴图 BC1 · 彩色贴图 BC7；同时关闭 Crunch。\n\n" +
            "黑白判定需要贴图开启 Read/Write（读不到像素时按彩色处理，走 BC7）。\n\n" +
            "BC7 与 DXT5(BC3) 的显存占用相同，BC7 画质更好，DXT5 的下载体积更小。";

        // 蒙版类关键词（AO、Mask、Metallic、Opacity、Smoothness 等）
        private static readonly string[] MaskKeywords =
        {
            "ao", "occlusion", "mask", "metallic", "metal", "opacity",
            "smoothness", "smooth", "gloss", "roughness", "rough", "alpha"
        };

        // 法线类关键词
        private static readonly string[] NormalKeywords = { "normal", "bump", "nrm" };

        private static readonly int[] SizeValues = { 128, 256, 512, 1024, 2048, 4096, 8192 };
        private static readonly string[] SizeLabels = { "128", "256", "512", "1024", "2048", "4096", "8192" };

        private static readonly Dictionary<Mesh, long> meshSizeCache = new Dictionary<Mesh, long>();

        // 加 SerializeField：重编译 / 域重载后窗口不会丢掉已选的物体和设置
        [SerializeField] private GameObject _avatar;
        private long _sizeActive;
        private long _sizeAll;
        private long _sizeAllTextures;
        private long _sizeAllMeshes;
        private Quality _pcTextureQuality;
        private Quality _questTextureQuality;
        private Quality _pcMeshQuality;
        private Quality _questMeshQuality;

        private bool _includeInactive = true;
        private List<TexEntry> _texturesList;
        private List<MeshEntry> _meshesList;
        private Vector2 _scrollPosMajor;

        private GUIStyle styleButtonTextFloatLeft;
        private GUIStyle styleStatTitle;
        private GUIStyle styleStatValue;
        private GUIContent matActiveIcon;
        private GUIContent matInactiveIcon;
        private GUIContent meshInactiveIcon;
        private GUIContent meshActiveIcon;
        private GUIContent refreshIcon;
        private Texture infoIcon;

        private bool _texturesFoldout = true;
        private bool _meshesFoldout;

        private int[] _textureSizeOptions = { 0, 128, 256, 512, 1024, 2048, 4096, 8192 };
        private readonly TextureImporterFormat[] _compressionFormatOptions =
        {
            TextureImporterFormat.Automatic, TextureImporterFormat.Automatic,
            TextureImporterFormat.BC7, TextureImporterFormat.DXT1, TextureImporterFormat.DXT5
        };

        // 压缩模式
        [SerializeField] private bool _helpExpanded; // 标题栏 ? 按钮：展开压缩说明
        [SerializeField] private Mode _mode = Mode.StatsOnly;
        [SerializeField] private int _maxSizeNormal = 1024;    // 普通贴图最大分辨率
        [SerializeField] private int _maxSizeMaskNormal = 512; // 蒙版 / 法线贴图最大分辨率
        [SerializeField] private bool _applyRecommendedFormat = true; // 同时按推荐配置改压缩格式
        [SerializeField] private bool _disableCrunch = true;          // 关闭 Crunch 压缩

        [MenuItem("焦糖工具箱/快速贴图压缩")]
        private static void Open()
        {
            // 注意：不能用 GetWindow(type, true, title)，第二个参数 true 会创建 utility 悬浮窗，
            // 这类窗口无法被堆叠 / 停靠到 Unity 的其他窗口里。这里与工具箱其他插件保持一致。
            QuickTextureCompressor win = GetWindow<QuickTextureCompressor>("快速贴图压缩");
            win.minSize = new Vector2(620, 420);
            if (Selection.activeGameObject != null)
            {
                win._avatar = Selection.activeGameObject;
                win.Calc(win._avatar);
            }
            win.Show();
        }

        private void OnEnable()
        {
            matActiveIcon = EditorGUIUtility.IconContent("d_Material Icon");
            matInactiveIcon = EditorGUIUtility.IconContent("d_Material On Icon");
            infoIcon = EditorGUIUtility.Load("console.infoicon") as Texture;
            meshInactiveIcon = EditorGUIUtility.IconContent("d_Mesh Icon");
            meshActiveIcon = EditorGUIUtility.IconContent("d_MeshCollider Icon");
            refreshIcon = EditorGUIUtility.IconContent("RotateTool On", "重新计算");
        }

        private void InitializeStyles()
        {
            styleButtonTextFloatLeft = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };

            // 三块表格的样式：从 GUI.skin.label 派生（不要从 EditorStyles 复制，
            // 复制出来的样式原生指针容易失效，EditorGUILayout.LabelField 里会直接 NullReferenceException）
            styleStatTitle = new GUIStyle(GUI.skin.label);
            styleStatTitle.alignment = TextAnchor.MiddleCenter;
            styleStatTitle.fontSize = 10;
            styleStatTitle.fontStyle = FontStyle.Normal;
            styleStatTitle.normal.textColor = Color.white;

            styleStatValue = new GUIStyle(GUI.skin.label);
            styleStatValue.alignment = TextAnchor.MiddleCenter;
            styleStatValue.fontSize = 13;
            styleStatValue.fontStyle = FontStyle.Bold;
            styleStatValue.normal.textColor = Color.white;
        }

        // 一行三块中的一格：上标题、下数值
        private void StatCell(string title, string value, string tooltip = null)
        {
            if (styleStatTitle == null || styleStatValue == null) InitializeStyles();

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label(new GUIContent(title, tooltip), styleStatTitle, GUILayout.ExpandWidth(true));
            GUILayout.Label(new GUIContent(value, tooltip), styleStatValue, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndVertical();
        }

        // 单元格之间的 1px 竖分隔线
        private static void StatSeparator()
        {
            Rect rect = GUILayoutUtility.GetRect(1f, 34f, GUILayout.Width(1));
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.55f));
        }

        // ================================ GUI ================================
        private void OnGUI()
        {
            if (styleButtonTextFloatLeft == null) InitializeStyles();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "<size=18><color=#E8A24A>焦糖工具箱 · 快速贴图压缩</color></size>",
                    new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.MiddleCenter },
                    GUILayout.ExpandWidth(true));

                if (GUILayout.Button(new GUIContent("?", "显示 / 隐藏 压缩说明"),
                        EditorStyles.miniButton, GUILayout.Width(20), GUILayout.Height(20)))
                    _helpExpanded = !_helpExpanded;
            }
            EditorGUILayout.LabelField(
                "原插件 VRAM Calculator 改进优化，仅自用勿传播",
                new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });

            if (_helpExpanded) EditorGUILayout.HelpBox(CompressionHelpText, MessageType.Info);

            EditorGUILayout.Space();

            DrawModeToolbar();
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _avatar != null;
                if (GUILayout.Button(refreshIcon, GUILayout.Width(30), GUILayout.Height(30)))
                {
                    meshSizeCache.Clear();
                    Calc(_avatar);
                }
                GUI.enabled = true;

                _avatar = (GameObject)EditorGUILayout.ObjectField(
                    GUIContent.none, _avatar, typeof(GameObject), true, GUILayout.Height(30));

                if (EditorGUI.EndChangeCheck() && _avatar != null) Calc(_avatar);

                using (new EditorGUI.DisabledGroupScope(Selection.activeGameObject == null))
                {
                    if (GUILayout.Button("使用当前选中", GUILayout.Width(90), GUILayout.Height(30)))
                    {
                        _avatar = Selection.activeGameObject;
                        if (_avatar != null)
                        {
                            meshSizeCache.Clear();
                            Calc(_avatar);
                        }
                    }
                }
            }

            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("请在上方指定一个物体（也可以先在场景中选中，再打开本窗口）。", MessageType.Info);
                return;
            }

            // 域重载 / 重编译后列表会丢，先补算一次，避免三块显示 0
            if (_texturesList == null || _meshesList == null) Calc(_avatar);

            GUILine();

            // 摘要行放在滚动区之外：展开 Textures 后贴图行会把滚动区内容撑宽，
            // 三块若放在里面会被一起拉宽，右侧的 Combined 就跑出可视范围了
            DrawStatRow();
            EditorGUILayout.Space(5);

            if (_texturesList == null || _meshesList == null) return;

            _scrollPosMajor = EditorGUILayout.BeginScrollView(_scrollPosMajor);
            DrawAssets();
            EditorGUILayout.EndScrollView();

            DrawCompressionActions();
        }

        private void DrawModeToolbar()
        {
            EditorGUI.BeginChangeCheck();
            _mode = (Mode)GUILayout.Toolbar((int)_mode,
                new[] { "仅统计", "快速压缩", "高级压缩" }, EditorStyles.toolbarButton);
            if (EditorGUI.EndChangeCheck()) ComputeTargets();

            if (_mode == Mode.Quick)
            {
                EditorGUILayout.HelpBox(
                    "快速压缩：把每张贴图的最大边降一个阶段（2048 → 1024、1024 → 512 ……），最低 32。",
                    MessageType.None);
            }
            else if (_mode == Mode.Advanced)
            {
                EditorGUI.BeginChangeCheck();
                _maxSizeNormal = EditorGUILayout.IntPopup("贴图最大分辨率", _maxSizeNormal, SizeLabels, SizeValues);
                _maxSizeMaskNormal = EditorGUILayout.IntPopup("蒙版 / 法线最大分辨率", _maxSizeMaskNormal, SizeLabels, SizeValues);
                if (EditorGUI.EndChangeCheck()) ComputeTargets();

                EditorGUILayout.HelpBox(
                    "蒙版包括 AO、Mask、Metallic、Opacity、Smoothness、Roughness 等；" +
                    "判定依据是材质属性名、贴图文件名以及导入设置中的 Normal Map 类型。", MessageType.None);
            }

            if (_mode != Mode.StatsOnly)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    _applyRecommendedFormat = EditorGUILayout.ToggleLeft(
                        "按推荐配置改压缩格式（法线 BC5 · 黑白贴图 BC1 · 彩色贴图 BC7）", _applyRecommendedFormat);
                    _disableCrunch = EditorGUILayout.ToggleLeft("关闭 Crunch 压缩", _disableCrunch);
                    if (EditorGUI.EndChangeCheck()) ComputeTargets();
                }
            }
        }

        // ---- 摘要行：固定在滚动区外，宽度永远等于窗口宽，不会被下方贴图行撑开 ----
        private void DrawStatRow()
        {
            EditorGUILayout.BeginHorizontal(GUI.skin.box);
            StatCell("Texture Memory", ToMebiByteString(_sizeAllTextures));
            StatSeparator();
            StatCell("Mesh Memory", ToMebiByteString(_sizeAllMeshes));
            StatSeparator();
            StatCell("Combined", ToMebiByteString(_sizeAll),
                "仅激活部分：" + ToMebiByteString(_sizeActive));
            EditorGUILayout.EndHorizontal();
        }

        // ---- 滚动区：Assets / Textures / Meshes ----
        private void DrawAssets()
        {
            EditorGUILayout.LabelField("Assets", EditorStyles.boldLabel);
            _includeInactive = EditorGUILayout.ToggleLeft("Show assets of disabled Objects（显示隐藏物体的资源）", _includeInactive);

            // ---------------- Textures ----------------
            _texturesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_texturesFoldout,
                string.Format("Textures  ({0})", ToMebiByteString(_sizeAllTextures)));
            if (_texturesFoldout)
            {
                // 不再给 Textures 单独套 ScrollView：自带滚动条会和整个面板的滚动条打架，
                // 内容超高时统一由最外层的 _scrollPosMajor 负责滚动
                for (int texIdx = 0; texIdx < _texturesList.Count; texIdx++)
                {
                    TexEntry texInfo = _texturesList[texIdx];
                    if (!_includeInactive && !texInfo.isActive) continue;
                    DrawTextureRow(texInfo);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ---------------- Meshes ----------------
            _meshesFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_meshesFoldout,
                string.Format("Meshes  ({0})", ToMebiByteString(_sizeAllMeshes)));
            if (_meshesFoldout)
            {
                // 同上：Meshes 也不单独套 ScrollView
                for (int mIdx = 0; mIdx < _meshesList.Count; mIdx++)
                {
                    MeshEntry meshInfo = _meshesList[mIdx];
                    if (!_includeInactive && !meshInfo.isActive) continue;

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(25);
                    using (new EditorGUILayout.HorizontalScope("box"))
                    {
                        EditorGUILayout.LabelField(new GUIContent(meshInfo.print, meshInfo.print),
                            GUILayout.Width(100), GUILayout.Height(20));
                        EditorGUILayout.ObjectField(meshInfo.mesh, typeof(Mesh), false,
                            GUILayout.Width(400), GUILayout.Height(20));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // 单行贴图：与 VRAM Calculator 的设计保持一致
        private void DrawTextureRow(TexEntry texInfo)
        {
            List<string> infoMessages = new List<string>();
            if (texInfo.formatString.Length < 1) infoMessages.Add("这张贴图无法被压缩");
            if (texInfo.materials.Count < 1) infoMessages.Add("这张贴图用在了材质切换（material swap）里");

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(25);
            EditorGUILayout.BeginVertical("box");
            using (new EditorGUILayout.HorizontalScope())
            {
                // 压缩模式下额外给一个勾选框，用于排除不想动的贴图
                if (_mode != Mode.StatsOnly)
                {
                    texInfo.selected = EditorGUILayout.Toggle(texInfo.selected, GUILayout.Width(16));
                    GUILayout.Space(4);
                }

                using (new EditorGUI.DisabledGroupScope(texInfo.materials.Count < 1))
                {
                    GUIContent content = texInfo.materialDropDown ? matActiveIcon : matInactiveIcon;
                    if (GUILayout.Button(content, GUILayout.Width(25), GUILayout.Height(20)))
                        texInfo.materialDropDown = !texInfo.materialDropDown;
                }

                EditorGUIUtility.labelWidth = 50;
                GUILayout.Label(texInfo.print, GUILayout.Width(70), GUILayout.Height(20));

                if (infoMessages.Count > 0)
                    GUILayout.Label(new GUIContent(infoIcon, string.Join("\n", infoMessages)), GUILayout.Width(20), GUILayout.Height(20));
                else
                    GUILayout.Space(25);

                EditorGUILayout.ObjectField(texInfo.texture, typeof(object), false, GUILayout.MinWidth(200), GUILayout.Height(20));

                int resolution = Mathf.Max(texInfo.texture.width, texInfo.texture.height);
                _textureSizeOptions[0] = resolution;
                string[] sizeLabels = _textureSizeOptions.Select(x => x.ToString()).ToArray();
                int newResolution = EditorGUILayout.IntPopup(resolution, sizeLabels, _textureSizeOptions, GUILayout.Width(55), GUILayout.Height(20));
                if (newResolution != resolution) ChangeImportSize(texInfo, newResolution);

                TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texInfo.texture)) as TextureImporter;
                bool isTextureWithChangableFormat = texInfo.formatString.Length > 0 && texInfo.texture is Texture2D && importer != null;

                if (texInfo.formatString.Length > 0)
                {
                    if (texInfo.format != 0 && isTextureWithChangableFormat)
                    {
                        _compressionFormatOptions[0] = (TextureImporterFormat)texInfo.format;
                        string[] formatLabels = _compressionFormatOptions.Select(x => x.ToString()).ToArray();
                        int newFormat = EditorGUILayout.Popup(0, formatLabels, GUILayout.Width(75), GUILayout.Height(20));
                        if (newFormat != 0) ChangeCompression(texInfo, _compressionFormatOptions[newFormat]);
                    }
                    else
                    {
                        if (GUILayout.Button(new GUIContent(texInfo.formatString), EditorStyles.label, GUILayout.Width(75), GUILayout.Height(20)))
                            Application.OpenURL("https://docs.unity.cn/2019.4/Documentation/Manual/class-TextureImporterOverride.html");
                    }
                }
                else
                {
                    GUILayout.Space(75);
                }

                if (isTextureWithChangableFormat && texInfo.BPP > texInfo.minBPP)
                {
                    TextureImporterFormat newImporterFormat =
                        texInfo.hasAlpha || importer.textureType == TextureImporterType.NormalMap
                            ? TextureImporterFormat.BC7
                            : TextureImporterFormat.DXT1;
                    TextureFormat newFormat = newImporterFormat == TextureImporterFormat.BC7 ? TextureFormat.BC7 : TextureFormat.DXT1;
                    float newBPP;
                    if (!BPP.TryGetValue(newFormat, out newBPP)) newBPP = 8;
                    string savedSize = ToShortMebiByteString(texInfo.size - TextureToBytesUsingBPP(texInfo.texture, newBPP));
                    if (GUILayout.Button(string.Format("{0} → -{1}", newFormat, savedSize), styleButtonTextFloatLeft, GUILayout.Width(120), GUILayout.Height(20))
                        && EditorUtility.DisplayDialog("确认修改压缩格式",
                            string.Format("即将把贴图 '{0}' 的压缩格式从 {1} 改为 {2}。\n\n该操作无法撤销，若想改回 {3} 需要手动设置。\n\n确定继续吗？",
                                texInfo.texture.name, texInfo.format, newFormat, texInfo.formatString),
                            "确定", "取消"))
                    {
                        ChangeCompression(texInfo, newImporterFormat);
                    }
                }
                else
                {
                    EditorGUILayout.GetControlRect(GUILayout.Width(120), GUILayout.Height(20));
                }

                if (resolution > 2048 && importer != null)
                {
                    string savedSize = ToShortMebiByteString(
                        texInfo.size - TextureToBytesUsingBPP(texInfo.texture, texInfo.BPP, 2048f / resolution));
                    if (GUILayout.Button(string.Format("2k → -{0}", savedSize), styleButtonTextFloatLeft, GUILayout.Width(120), GUILayout.Height(20)))
                        ChangeImportSize(texInfo, 2048);
                }
                else
                {
                    EditorGUILayout.GetControlRect(GUILayout.Width(120), GUILayout.Height(20));
                }

                // 压缩模式下在行尾标出这张贴图的目标压缩参数（例如 4096 → 1024 / → BC7）
                if (_mode != Mode.StatsOnly)
                {
                    if (texInfo.sizeWillChange)
                        GUILayout.Label(string.Format("{0} → {1}", texInfo.CurrentRes, texInfo.targetSize), GUILayout.Width(85));
                    else
                        GUILayout.Space(85);

                    if (texInfo.formatWillChange)
                    {
                        string tip = texInfo.isNormal ? "法线贴图 → BC5"
                            : texInfo.isMask ? "蒙版类贴图 → BC1"
                            : texInfo.isGrayscale ? "黑白（灰度）贴图 → BC1"
                            : "彩色贴图 → BC7";
                        GUILayout.Label(new GUIContent(string.Format("→ {0}", FormatName(texInfo.targetFormat)), tip),
                            GUILayout.Width(60));
                    }
                    else if (texInfo.crunchWillChange)
                        GUILayout.Label("→ 关 Crunch", GUILayout.Width(60));
                    else
                        GUILayout.Space(60);
                }

                GUILayout.FlexibleSpace();
            }

            if (texInfo.materialDropDown)
            {
                GUILayout.Label(string.Format("在 '{0}' 上被 {1} 个材质使用", _avatar.name, texInfo.materials.Count), EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                foreach (Material mat in texInfo.materials)
                    EditorGUILayout.ObjectField(mat, typeof(Material), false, GUILayout.Width(395), GUILayout.Height(20));
                EditorGUI.indentLevel--;
                GUILayout.Space(5);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        // ---- 压缩模式的操作区 ----
        private void DrawCompressionActions()
        {
            if (_mode == Mode.StatsOnly)
            {
                EditorGUILayout.HelpBox("当前为「仅统计」模式，不会修改任何资源。", MessageType.None);
                return;
            }
            if (_texturesList == null) return;

            var todo = _texturesList.Where(e => e.selected && e.willChange).ToList();
            long before = todo.Sum(e => e.size);
            long after = todo.Sum(e => e.targetBytes);
            long saved = before - after;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                int fmtCount = todo.Count(e => e.formatWillChange);
                int crunchCount = todo.Count(e => e.crunchWillChange);
                EditorGUILayout.LabelField(
                    string.Format("将压缩 {0} 张贴图：{1} → {2}（省 {3}）　改格式 {4} 张 · 关 Crunch {5} 张",
                        todo.Count, ToMebi(before), ToMebi(after), ToMebi(saved), fmtCount, crunchCount),
                    EditorStyles.miniBoldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("全选")) _texturesList.ForEach(e => e.selected = true);
                    if (GUILayout.Button("全不选")) _texturesList.ForEach(e => e.selected = false);
                    if (GUILayout.Button("仅选会改动的"))
                        _texturesList.ForEach(e => e.selected = e.willChange);
                }

                GUI.enabled = todo.Count > 0;
                if (CaramelToolbox.CaramelUI.CaramelButton(string.Format("执行压缩（{0} 张）", todo.Count), 32f))
                    Apply(todo);
                GUI.enabled = true;

                EditorGUILayout.LabelField("提示：会改写贴图的导入设置并重新导入，建议先备份或使用版本管理。", EditorStyles.miniLabel);
            }
        }

        // ================================ 计算 ================================
        public long Calc(GameObject avatar)
        {
            if (avatar == null) return 0;
            try
            {
                EditorUtility.DisplayProgressBar("快速贴图压缩", "获取材质", 0.5f);

                List<Material> allMaterials = avatar.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(mat => mat != null)
                    .Distinct()
                    .ToList();
                HashSet<Material> activeMaterials = new HashSet<Material>(
                    avatar.GetComponentsInChildren<Renderer>(false)
                        .SelectMany(r => r.sharedMaterials)
                        .Where(mat => mat != null));

                Dictionary<Texture, TexEntry> map = new Dictionary<Texture, TexEntry>();
                foreach (Material m in allMaterials)
                {
                    bool isActive = activeMaterials.Contains(m);
                    foreach (string prop in m.GetTexturePropertyNames())
                    {
                        if (!m.HasProperty(prop)) continue;
                        Texture t = m.GetTexture(prop);
                        if (t == null) continue;

                        TexEntry e;
                        if (!map.TryGetValue(t, out e))
                        {
                            e = new TexEntry { texture = t };
                            map.Add(t, e);

                            string path = AssetDatabase.GetAssetPath(t);
                            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
                            if (ti != null)
                            {
                                e.hasAlpha = ti.DoesSourceTextureHaveAlpha();
                                if (ti.textureType == TextureImporterType.NormalMap) e.isNormal = true;
                                else if (t is Texture2D) e.isGrayscale = DetectGrayscale((Texture2D)t);

                                e.importer = ti;
                                e.crunchedOn = ti.crunchedCompression;

                                // 当前生效的压缩格式：优先看 Standalone 的平台覆盖，否则看运行时格式
                                TextureImporterPlatformSettings ps = ti.GetPlatformTextureSettings("Standalone");
                                if (ps != null && ps.overridden)
                                {
                                    e.currentFormat = ps.format;
                                    e.currentMaxSize = ps.maxTextureSize > 0 ? ps.maxTextureSize : ti.maxTextureSize;
                                }
                                else
                                {
                                    e.currentFormat = t is Texture2D ? (TextureImporterFormat)((Texture2D)t).format : TextureImporterFormat.Automatic;
                                    e.currentMaxSize = ti.maxTextureSize;
                                }
                            }
                            else
                            {
                                e.currentMaxSize = Mathf.Max(t.width, t.height);
                            }
                            e.canCompress = t is Texture2D && !string.IsNullOrEmpty(path) && ti != null;

                            Classify(e, t.name);
                        }
                        if (!e.materials.Contains(m)) e.materials.Add(m);
                        if (isActive) e.isActive = true;
                        Classify(e, prop);
                    }
                }

                EditorUtility.DisplayProgressBar("快速贴图压缩", "计算贴图显存", 0.6f);
                _texturesList = new List<TexEntry>();
                _sizeAll = 0;
                _sizeActive = 0;
                _sizeAllTextures = 0;
                _sizeAllMeshes = 0;

                foreach (KeyValuePair<Texture, TexEntry> pair in map)
                {
                    TexEntry e = pair.Value;
                    CalculateTextureSize(e);
                    e.print = ToMebiByteString(e.size);
                    e.materialDropDown = false;
                    _texturesList.Add(e);

                    if (e.isActive) _sizeActive += e.size;
                    _sizeAllTextures += e.size;
                }
                _texturesList.Sort((t1, t2) => t2.size.CompareTo(t1.size));

                EditorUtility.DisplayProgressBar("快速贴图压缩", "计算网格显存", 0.8f);
                Dictionary<Mesh, bool> meshes = new Dictionary<Mesh, bool>();
                foreach (Mesh m in GetMeshes(avatar, true))
                {
                    if (m == null) continue;
                    if (!meshes.ContainsKey(m)) meshes.Add(m, false);
                }
                foreach (Mesh m in GetMeshes(avatar, false))
                {
                    if (m == null) continue;
                    if (meshes.ContainsKey(m)) meshes[m] = true;
                }

                _meshesList = new List<MeshEntry>();
                foreach (KeyValuePair<Mesh, bool> pair in meshes)
                {
                    long bytes = CalculateMeshSize(pair.Key);
                    if (pair.Value) _sizeActive += bytes;
                    _sizeAllMeshes += bytes;

                    _meshesList.Add(new MeshEntry
                    {
                        mesh = pair.Key,
                        size = bytes,
                        print = ToMebiByteString(bytes),
                        isActive = pair.Value,
                    });
                }
                _meshesList.Sort((m1, m2) => m2.size.CompareTo(m1.size));

                _sizeAll = _sizeAllTextures + _sizeAllMeshes;

                _pcTextureQuality = GetTextureQuality(_sizeAllTextures, false);
                _pcMeshQuality = GetMeshQuality(_sizeAllMeshes, false);
                _questTextureQuality = GetTextureQuality(_sizeAllTextures, true);
                _questMeshQuality = GetMeshQuality(_sizeAllMeshes, true);

                ComputeTargets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return _sizeAll;
        }

        private static IEnumerable<Mesh> GetMeshes(GameObject avatar, bool includeInactive)
        {
            return avatar.GetComponentsInChildren<Renderer>(includeInactive)
                .Select(r => r is SkinnedMeshRenderer
                    ? ((SkinnedMeshRenderer)r).sharedMesh
                    : r is MeshRenderer ? r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null : null);
        }

        private static void CalculateTextureSize(TexEntry info)
        {
            Texture t = info.texture;
            if (t is Texture2D)
            {
                TextureFormat format = ((Texture2D)t).format;
                float bpp;
                if (!BPP.TryGetValue(format, out bpp)) bpp = 16;
                info.BPP = bpp;
                info.formatString = format.ToString();
                info.format = format;
                info.size = TextureToBytesUsingBPP(t, info.BPP);

                string path = AssetDatabase.GetAssetPath(t);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    AssetImporter assetImporter = AssetImporter.GetAtPath(path);
                    TextureImporter textureImporter = assetImporter as TextureImporter;
                    if (textureImporter != null)
                    {
                        info.hasAlpha = textureImporter.DoesSourceTextureHaveAlpha();
                        info.minBPP = (info.hasAlpha || textureImporter.textureType == TextureImporterType.NormalMap) ? 8 : 4;
                    }
                }
            }
            else if (t is Texture2DArray)
            {
                Texture2DArray t2dArray = (Texture2DArray)t;
                float bpp;
                if (!BPP.TryGetValue(t2dArray.format, out bpp)) bpp = 16;
                info.BPP = bpp;
                info.formatString = t2dArray.format.ToString();
                info.format = t2dArray.format;
                info.size = TextureToBytesUsingBPP(t, info.BPP) * t2dArray.depth;
            }
            else if (t is Cubemap)
            {
                Cubemap cm = (Cubemap)t;
                float bpp;
                if (!BPP.TryGetValue(cm.format, out bpp)) bpp = 16;
                info.BPP = bpp;
                info.formatString = cm.format.ToString();
                info.format = cm.format;
                info.size = TextureToBytesUsingBPP(t, info.BPP);
                if (cm.dimension == TextureDimension.Tex3D) info.size *= 6;
            }
            else if (t is RenderTexture)
            {
                RenderTexture rt = (RenderTexture)t;
                float bpp;
                if (!RT_BPP.TryGetValue(rt.format, out bpp)) bpp = 16;
                info.BPP = bpp + rt.depth;
                info.formatString = rt.format.ToString();
                info.hasAlpha = rt.format == RenderTextureFormat.ARGB32 ||
                                rt.format == RenderTextureFormat.ARGBHalf ||
                                rt.format == RenderTextureFormat.ARGBFloat;
                info.size = TextureToBytesUsingBPP(t, info.BPP);
            }
            else
            {
                info.size = Profiler.GetRuntimeMemorySizeLong(t);
            }
        }

        private static long TextureToBytesUsingBPP(Texture t, float bpp, float resolutionScale = 1)
        {
            int width = (int)(t.width * resolutionScale);
            int height = (int)(t.height * resolutionScale);
            long bytes = 0;
            if (t is Texture2D || t is Texture2DArray || t is Cubemap)
            {
                for (int index = 0; index < t.mipmapCount; ++index)
                    bytes += (long)Mathf.RoundToInt((float)((width * height) >> (2 * index)) * bpp / 8);
            }
            else if (t is RenderTexture)
            {
                RenderTexture rt = (RenderTexture)t;
                double mipmaps = 1;
                for (int i = 0; i < rt.mipmapCount; i++) mipmaps += Math.Pow(0.25, i + 1);
                float rtBpp;
                if (!RT_BPP.TryGetValue(rt.format, out rtBpp)) rtBpp = 16;
                bytes = (long)((rtBpp + rt.depth) * width * height * (rt.useMipMap ? mipmaps : 1) / 8);
            }
            else
            {
                bytes = Profiler.GetRuntimeMemorySizeLong(t);
            }
            return bytes;
        }

        private static long CalculateMeshSize(Mesh mesh)
        {
            long cached;
            if (meshSizeCache.TryGetValue(mesh, out cached)) return cached;

            long bytes = 0;
            long vertexAttributeVRAMSize = 0;
            foreach (VertexAttributeDescriptor vertexAttribute in mesh.GetVertexAttributes())
            {
                int skinnedMeshPositionNormalTangentMultiplier = 1;
                if (mesh.HasVertexAttribute(VertexAttribute.BlendIndices) && mesh.HasVertexAttribute(VertexAttribute.BlendWeight) &&
                    (vertexAttribute.attribute == VertexAttribute.Position || vertexAttribute.attribute == VertexAttribute.Normal ||
                     vertexAttribute.attribute == VertexAttribute.Tangent))
                    skinnedMeshPositionNormalTangentMultiplier = 2;

                int byteSize;
                if (!VertexAttributeByteSize.TryGetValue(vertexAttribute.format, out byteSize)) byteSize = 4;
                vertexAttributeVRAMSize += byteSize * vertexAttribute.dimension * skinnedMeshPositionNormalTangentMultiplier;
            }

            Vector3[] deltaPositions = new Vector3[mesh.vertexCount];
            Vector3[] deltaNormals = new Vector3[mesh.vertexCount];
            Vector3[] deltaTangents = new Vector3[mesh.vertexCount];
            long blendShapeVRAMSize = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(i);
                for (int j = 0; j < frameCount; j++)
                {
                    mesh.GetBlendShapeFrameVertices(i, j, deltaPositions, deltaNormals, deltaTangents);
                    for (int k = 0; k < deltaPositions.Length; k++)
                    {
                        if (deltaPositions[k] != Vector3.zero || deltaNormals[k] != Vector3.zero || deltaTangents[k] != Vector3.zero)
                            blendShapeVRAMSize += 40;
                    }
                }
            }

            bytes = vertexAttributeVRAMSize * mesh.vertexCount + blendShapeVRAMSize;
            meshSizeCache[mesh] = bytes;
            return bytes;
        }

        // ---------------- 目标分辨率 ----------------
        private void ComputeTargets()
        {
            if (_texturesList == null) return;

            bool compressing = _mode != Mode.StatsOnly;

            foreach (TexEntry e in _texturesList)
            {
                int res = Mathf.Max(e.texture.width, e.texture.height);
                int target = res;

                if (_mode == Mode.Quick) target = NextLowerStep(res);
                else if (_mode == Mode.Advanced)
                    target = (e.isMask || e.isNormal) ? _maxSizeMaskNormal : _maxSizeNormal;

                if (target > res) target = res;

                e.targetSize = target;
                e.targetFormat = compressing ? RecommendedFormat(e) : TextureImporterFormat.Automatic;

                e.sizeWillChange = compressing && e.canCompress && target < res;
                e.formatWillChange = compressing && e.canCompress && _applyRecommendedFormat &&
                                     e.importer != null && e.targetFormat != e.currentFormat;
                e.crunchWillChange = compressing && e.canCompress && _disableCrunch &&
                                     e.importer != null && e.crunchedOn;
                e.willChange = e.sizeWillChange || e.formatWillChange || e.crunchWillChange;

                float bpp = e.BPP;
                if (e.formatWillChange)
                {
                    float newBpp;
                    if (ImporterFormatBPP.TryGetValue(e.targetFormat, out newBpp)) bpp = newBpp;
                }
                e.targetBytes = TextureToBytesUsingBPP(e.texture, bpp, target / (float)res);
            }
        }

        // 推荐配置：法线 BC5 · 蒙版类 BC1 · 黑白（灰度）贴图 BC1 · 彩色贴图 BC7
        // 注：Unity 的 TextureImporterFormat 里没有 BC1 / BC3 这两个名字，BC1 = DXT1、BC3 = DXT5
        private static TextureImporterFormat RecommendedFormat(TexEntry e)
        {
            if (e.isNormal) return TextureImporterFormat.BC5;
            if (e.isMask) return TextureImporterFormat.DXT1;
            if (e.isGrayscale) return TextureImporterFormat.DXT1;
            return TextureImporterFormat.BC7;
        }

        // 判断贴图是否为黑白（灰度）图：网格采样，只要出现明显的通道差就判为彩色
        // 需要贴图开启 Read/Write，读不到像素时按「彩色」处理（最终走 BC7）
        private static bool DetectGrayscale(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;
            try
            {
                // 用 mip 2（约 1/16 的数据）采样：灰度判定与分辨率无关，省时省内存
                int mip = tex.mipmapCount > 2 ? 2 : 0;
                Color32[] pixels = tex.GetPixels32(mip);
                if (pixels == null || pixels.Length == 0) return false;

                int w = Mathf.Max(1, tex.width >> mip);
                int h = Mathf.Max(1, pixels.Length / w);
                int stepX = Mathf.Max(1, w / 48);
                int stepY = Mathf.Max(1, h / 48);

                for (int y = 0; y < h; y += stepY)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x += stepX)
                    {
                        int idx = row + x;
                        if (idx >= pixels.Length) break;

                        Color32 c = pixels[idx];
                        int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        int min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                        if (max - min > 12) return false; // 有明显色度差 → 彩色
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 显示名：把 DXT1 / DXT5 还原成大家习惯的 BC1 / BC3
        private static string FormatName(TextureImporterFormat format)
        {
            if (format == TextureImporterFormat.DXT1) return "BC1";
            if (format == TextureImporterFormat.DXT5) return "BC3";
            return format.ToString();
        }

        private static int NextLowerStep(int value)
        {
            int p = Mathf.ClosestPowerOfTwo(value);
            int next = p >= value ? p / 2 : p;
            return Mathf.Clamp(next, 32, 8192);
        }

        private static void Classify(TexEntry entry, string hint)
        {
            if (string.IsNullOrEmpty(hint)) return;
            string s = hint.ToLowerInvariant();
            foreach (string k in NormalKeywords)
            {
                if (s.Contains(k)) { entry.isNormal = true; return; }
            }
            foreach (string k in MaskKeywords)
            {
                if (s.Contains(k)) { entry.isMask = true; return; }
            }
        }

        // ---------------- 执行 ----------------
        private void Apply(List<TexEntry> todo)
        {
            if (todo == null || todo.Count == 0) return;

            string modeName = _mode == Mode.Quick ? "快速压缩" : "高级压缩";
            if (!EditorUtility.DisplayDialog("快速贴图压缩",
                    string.Format("将以【{0}】方式压缩 {1} 张贴图。\n该操作会改写贴图的导入设置并重新导入，是否继续？", modeName, todo.Count),
                    "执行", "取消"))
                return;

            int done = 0;
            try
            {
                for (int i = 0; i < todo.Count; i++)
                {
                    TexEntry e = todo[i];
                    EditorUtility.DisplayProgressBar("快速贴图压缩", e.texture.name, (float)i / todo.Count);

                    TextureImporter importer = e.importer != null
                        ? e.importer
                        : AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(e.texture)) as TextureImporter;
                    if (importer == null) continue;

                    bool changed = false;
                    if (e.sizeWillChange && SetMaxTextureSize(importer, e.targetSize)) changed = true;
                    if (e.formatWillChange && SetCompressionFormat(importer, e.targetSize, e.targetFormat)) changed = true;
                    if (e.crunchWillChange && DisableCrunch(importer)) changed = true;
                    if (!changed) continue;

                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                    done++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            meshSizeCache.Clear();
            Calc(_avatar);
            Debug.Log(string.Format("[焦糖工具箱] 快速贴图压缩完成：{0} 张，贴图显存 {1}。", done, ToMebi(_sizeAllTextures)));
            ShowNotification(new GUIContent(string.Format("已压缩 {0} 张贴图", done)));
        }

        // 只降不升：默认设置 + 已开启覆盖的平台一起压到 target 以内
        private static bool SetMaxTextureSize(TextureImporter importer, int target)
        {
            target = Mathf.Clamp(Mathf.ClosestPowerOfTwo(target), 32, 8192);
            bool changed = false;

            if (importer.maxTextureSize > target)
            {
                importer.maxTextureSize = target;
                changed = true;
            }

            string[] platforms = { "Standalone", "Android", "iPhone", "WebGL" };
            foreach (string platform in platforms)
            {
                TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(platform);
                if (settings == null || !settings.overridden) continue;
                if (settings.maxTextureSize > target)
                {
                    settings.maxTextureSize = target;
                    importer.SetPlatformTextureSettings(settings);
                    changed = true;
                }
            }
            return changed;
        }

        // 按推荐配置写压缩格式：写在 Standalone 平台覆盖上，Android / iPhone 仍走 Automatic（让 Unity 自己挑 ASTC/ETC2）
        private static bool SetCompressionFormat(TextureImporter importer, int targetSize, TextureImporterFormat format)
        {
            TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings("Standalone");
            if (settings == null) return false;

            bool changed = !settings.overridden || settings.format != format;

            settings.name = "Standalone";
            settings.overridden = true;
            settings.format = format;
            settings.compressionQuality = 100;

            int current = settings.maxTextureSize > 0 ? settings.maxTextureSize : importer.maxTextureSize;
            if (targetSize > 0 && (current <= 0 || targetSize < current)) settings.maxTextureSize = targetSize;

            importer.SetPlatformTextureSettings(settings);
            return changed;
        }

        // 关闭 Crunch 压缩（默认设置，未被平台覆盖的平台会继承它）
        private static bool DisableCrunch(TextureImporter importer)
        {
            if (!importer.crunchedCompression) return false;
            importer.crunchedCompression = false;
            return true;
        }

        // ---------------- 单行操作（与 VRAM Calculator 一致） ----------------
        private void ChangeImportSize(TexEntry texInfo, int size)
        {
            TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texInfo.texture)) as TextureImporter;
            if (importer == null) return;
            importer.maxTextureSize = size;
            TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings("Standalone");
            settings.maxTextureSize = size;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            meshSizeCache.Clear();
            Calc(_avatar);
        }

        private void ChangeCompression(TexEntry texInfo, TextureImporterFormat format)
        {
            TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texInfo.texture)) as TextureImporter;
            if (importer == null) return;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings()
            {
                name = "Standalone",
                overridden = (int)format != -1,
                format = format,
                maxTextureSize = importer.maxTextureSize,
                compressionQuality = 100,
            });
            importer.SaveAndReimport();
            meshSizeCache.Clear();
            Calc(_avatar);
        }

        // ---------------- 质量 / 工具 ----------------
        private static Quality GetTextureQuality(long size, bool quest)
        {
            if (quest)
                return GetQuality(size, QUEST_TEXTURE_MEMORY_EXCELLENT_MiB, QUEST_TEXTURE_MEMORY_GOOD_MiB,
                    QUEST_TEXTURE_MEMORY_MEDIUM_MiB, QUEST_TEXTURE_MEMORY_POOR_MiB);
            return GetQuality(size, PC_TEXTURE_MEMORY_EXCELLENT_MiB, PC_TEXTURE_MEMORY_GOOD_MiB,
                PC_TEXTURE_MEMORY_MEDIUM_MiB, PC_TEXTURE_MEMORY_POOR_MiB);
        }

        private static Quality GetMeshQuality(long size, bool quest)
        {
            if (quest)
                return GetQuality(size, QUEST_MESH_MEMORY_EXCELLENT_MiB, QUEST_MESH_MEMORY_GOOD_MiB,
                    QUEST_MESH_MEMORY_MEDIUM_MiB, QUEST_MESH_MEMORY_POOR_MiB);
            return GetQuality(size, PC_MESH_MEMORY_EXCELLENT_MiB, PC_MESH_MEMORY_GOOD_MiB,
                PC_MESH_MEMORY_MEDIUM_MiB, PC_MESH_MEMORY_POOR_MiB);
        }

        private static Quality GetQuality(long size, long excellent, long good, long medium, long poor)
        {
            if (size < excellent * 1048576) return Quality.Excellent;
            if (size < good * 1048576) return Quality.Good;
            if (size < medium * 1048576) return Quality.Medium;
            if (size < poor * 1048576) return Quality.Poor;
            return Quality.VeryPoor;
        }

        private static string ToMebi(long l)
        {
            return ToMebiByteString(l);
        }

        // 以下两个是 Thry AvatarEvaluator 的同名方法，改为本地实现（口径完全一致）
        private static string ToMebiByteString(long l)
        {
            if (l < Math.Pow(2, 10)) return l + " B";
            if (l < Math.Pow(2, 20)) return (l / Math.Pow(2, 10)).ToString("n2") + " KiB";
            if (l < Math.Pow(2, 30)) return (l / Math.Pow(2, 20)).ToString("n2") + " MiB";
            return (l / Math.Pow(2, 30)).ToString("n2") + " GiB";
        }

        private static string ToShortMebiByteString(long l)
        {
            if (l < Math.Pow(2, 10)) return l + " B";
            if (l < Math.Pow(2, 20)) return (l / Math.Pow(2, 10)).ToString("n0") + " KiB";
            if (l < Math.Pow(2, 30)) return (l / Math.Pow(2, 20)).ToString("n1") + " MiB";
            return (l / Math.Pow(2, 30)).ToString("n1") + " GiB";
        }

        private static void GUILine(int height = 1)
        {
            GUILayout.Space(10);
            Rect rect = EditorGUILayout.GetControlRect(false, height);
            rect.width = EditorGUIUtility.currentViewWidth;
            GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture);
            GUILayout.Space(10);
        }
    }
}
#endif
