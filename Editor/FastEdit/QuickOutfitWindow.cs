using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>「VRC 快速改模」主窗口。</summary>
    public class QuickOutfitWindow : EditorWindow
    {
        private const float DropSize = 220f;
        private const float ListHeight = 360f;
        private static readonly Color SelectedRowColor = new Color(0.24f, 0.49f, 0.91f, 0.35f);
        private static readonly Color ActiveButtonColor = new Color(0.45f, 0.8f, 1f, 1f);
        private static readonly Color UnknownColor = new Color(1f, 0.85f, 0.2f, 1f);

        [SerializeField] private VRCAvatarDescriptor _avatar = null;
        [SerializeField] private List<OutfitEntry> _entries = new List<OutfitEntry>();
        [SerializeField] private int _selected = -1;

        private string _avatarNotice = string.Empty;

        /// <summary>操作日志（最新在前，最多 200 条）。</summary>
        [Serializable]
        private class LogEntry
        {
            public string Time = string.Empty;
            public string Text = string.Empty;
            public MessageType Type = MessageType.Info;
        }

        [SerializeField] private List<LogEntry> _log = new List<LogEntry>();
        /// <summary>操作日志是否展开（默认折叠）。</summary>
        [SerializeField] private bool _logExpanded = false;
        private Vector2 _logScroll = Vector2.zero;
        private const int MaxLogCount = 200;
        private const float LogHeight = 200f;
        private const float TypeRowHeight = 44f;
        private const float PartRowHeight = 30f;

        /// <summary>列表中选中的已有槽位（与 _selected 待应用素材互斥）。</summary>
        private GameObject _selectedSlot = null;
        private AssetCategory _selectedSlotCategory = AssetCategory.Unknown;

        private ApplyPlan _plan = null;
        private bool _planDirty = true;
        private string _dragHoverLabel = null;
        private Vector2 _listScroll = Vector2.zero;
        private Vector2 _windowScroll = Vector2.zero;
        private readonly List<Action> _deferred = new List<Action>();
        private readonly Dictionary<int, AssetCategory> _hoverCache = new Dictionary<int, AssetCategory>();

        // 「当前外观」缓存：只在角色变化、OnHierarchyChange、Undo/Redo、本窗口操作后重建。
        private AppearanceOverview _overview = null;
        private bool _overviewDirty = true;
        private VRCAvatarDescriptor _overviewAvatar = null;

        // 右侧合并列表（当前外观 + 待应用素材）：只在 Layout 事件中、规划或外观重建后重建，保证 Layout/Repaint 控件一致。
        private enum RowKind
        {
            Header,
            Default,
            Existing,
            Empty,
            Created,
            Pending,
            Part,
        }

        private class ListRow
        {
            public RowKind Kind = RowKind.Header;
            public string Title = string.Empty;
            public AssetCategory Category = AssetCategory.Unknown;
            /// <summary>内容对象（素材 / 默认项内容）。</summary>
            public GameObject Target = null;
            /// <summary>所在槽位 / 默认项 / 部件节点（模板或将新建的槽位为 null，不可定位/改名）。</summary>
            public GameObject SlotObject = null;
            public string SlotName = string.Empty;
            /// <summary>同一槽位的第一行（只有第一行显示槽位名输入框与「转为」）。</summary>
            public bool FirstOfSlot = true;
            public bool WillDelete = false;
            public string Tooltip = string.Empty;
            /// <summary>内容是否可点击（定位 / 选中）。</summary>
            public bool Clickable = false;
            /// <summary>部件内容所属衣服（槽位名）。</summary>
            public string Owner = string.Empty;
            public OutfitEntry Entry = null;
            public PlanItem Item = null;
        }

        private readonly List<ListRow> _rows = new List<ListRow>();
        private bool _rowsDirty = true;

        // 样式不参与序列化：脚本重载后会被重新创建（否则新增的样式字段会为 null）。
        [NonSerialized] private GUIStyle _centerStyle;
        [NonSerialized] private GUIStyle _tagStyle;
        [NonSerialized] private GUIStyle _placeholderStyle;
        [NonSerialized] private GUIStyle _typeLabelStyle;
        [NonSerialized] private GUIStyle _emptySlotStyle;
        [NonSerialized] private GUIStyle _groupHeaderStyle;
        [NonSerialized] private GUIStyle _bigButtonStyle;
        [NonSerialized] private GUIStyle _logInfoStyle;
        [NonSerialized] private GUIStyle _logWarnStyle;
        [NonSerialized] private GUIStyle _logErrorStyle;
        [NonSerialized] private GUIStyle _handleStyle;

        private static readonly Color PendingRowColor = new Color(0.3f, 0.85f, 0.45f, 0.16f);
        private static readonly Color DeleteTextColor = new Color(1f, 0.55f, 0.45f, 1f);
        private static readonly Color DragRowColor = new Color(0.45f, 0.8f, 1f, 0.22f);
        private const float TypeColWidth = 56f;
        private const float SlotColWidth = 120f;
        private const float HandleColWidth = 16f;
        private const float PreviewColWidth = 48f;
        private const float DeleteColWidth = 22f;
        private const float ListRowHeight = 20f;

        [MenuItem("焦糖工具箱/快速改模")]
        public static void ShowWindow()
        {
            QuickOutfitWindow window = GetWindow<QuickOutfitWindow>();
            window.titleContent = new GUIContent("快速改模");
            window.minSize = new Vector2(620f, 560f);

            // 窗口已打开时：若当前选中物体属于某角色，切换到该角色（选中优先级最高）。
            VRCAvatarDescriptor fromSelection = AvatarLocator.FromSelection();
            if (fromSelection != null && fromSelection != window._avatar)
            {
                window._avatar = fromSelection;
                window._avatarNotice = string.Empty;
                window._planDirty = true;
            }

            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("快速改模");
            if (_entries == null) _entries = new List<OutfitEntry>();
            if (_avatar == null)
            {
                string notice;
                _avatar = AvatarLocator.AutoDetect(out notice);
                _avatarNotice = notice;
            }

            _planDirty = true;
            _overviewDirty = true;
            if (_log == null) _log = new List<LogEntry>();
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
            PreviewController.Changed -= OnPreviewChanged;
            PreviewController.Changed += OnPreviewChanged;
            PreviewController.AutoExited -= OnPreviewAutoExited;
            PreviewController.AutoExited += OnPreviewAutoExited;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            PreviewController.Changed -= OnPreviewChanged;
            PreviewController.AutoExited -= OnPreviewAutoExited;
        }

        private void OnDestroy()
        {
            // 关闭窗口后无法再手动退出预览，因此关闭时恢复。
            PreviewController.ExitAll(null);
        }

        private void OnPreviewChanged()
        {
            Repaint();
        }

        private void OnPreviewAutoExited(string message)
        {
            Log(message, MessageType.Info);
        }

        private void OnUndoRedo()
        {
            _planDirty = true;
            _overviewDirty = true;
            _hoverCache.Clear();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            if (_avatar == null)
            {
                string notice;
                _avatar = AvatarLocator.AutoDetect(out notice);
                _avatarNotice = notice;
            }

            _planDirty = true;
            _overviewDirty = true;
            _hoverCache.Clear();
            Repaint();
        }

        private void OnProjectChange()
        {
            _planDirty = true;
            _hoverCache.Clear();
            Repaint();
        }

        private void OnFocus()
        {
            _planDirty = true;
        }

        // ================================================================== GUI

        private void EnsureStyles()
        {
            if (_centerStyle != null && _handleStyle != null && _bigButtonStyle != null && _logErrorStyle != null) return;
            _centerStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                wordWrap = true,
            };
            _tagStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.UpperRight };
            _placeholderStyle = new GUIStyle(EditorStyles.label);
            _placeholderStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            _placeholderStyle.padding = new RectOffset(4, 2, 1, 0);
            _typeLabelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            _emptySlotStyle = new GUIStyle(EditorStyles.miniLabel);
            _emptySlotStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f, 1f);
            _groupHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { padding = new RectOffset(2, 2, 4, 0) };
            _bigButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            _logInfoStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = false };
            _logWarnStyle = new GUIStyle(_logInfoStyle);
            _logWarnStyle.normal.textColor = new Color(1f, 0.82f, 0.3f, 1f);
            _logErrorStyle = new GUIStyle(_logInfoStyle);
            _logErrorStyle.normal.textColor = new Color(1f, 0.45f, 0.4f, 1f);
            _handleStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
            _handleStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f, 1f);
        }

        private void OnGUI()
        {
            EnsureStyles();
            if (Event.current.type == EventType.Layout)
            {
                if (_planDirty)
                {
                    RebuildPlan();
                    _rowsDirty = true;
                }

                if (_overviewDirty || _overviewAvatar != _avatar)
                {
                    RebuildOverview();
                    _rowsDirty = true;
                }

                if (_rowsDirty) BuildRows();
            }

            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);
            DrawTitle();
            DrawAvatarRow();
            DrawStatusRow();
            DrawOverviewWarnings();
            EditorGUILayout.Space(4f);
            DrawMainArea();
            DrawTypeButtons();
            EditorGUILayout.Space(4f);
            DrawApplyArea();
            DrawPartButtons();
            EditorGUILayout.Space(4f);
            DrawLog();
            EditorGUILayout.EndScrollView();

            if (_deferred.Count > 0)
            {
                List<Action> actions = new List<Action>(_deferred);
                _deferred.Clear();
                foreach (Action a in actions) a();
                _planDirty = true;
                _overviewDirty = true;
                Repaint();
            }
        }

        /// <summary>重建「当前外观」缓存（只在 Layout 事件中调用）。</summary>
        private void RebuildOverview()
        {
            _overviewDirty = false;
            if (_overviewAvatar != _avatar)
            {
                // 换角色：退出旧角色的预览，清除选中的槽位。
                if (_overviewAvatar != null) PreviewController.ExitAll("切换角色");
                _selectedSlot = null;
            }

            _overviewAvatar = _avatar;
            try
            {
                _overview = _avatar != null ? AppearanceOverview.Build(_avatar) : null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _overview = null;
            }
        }

        private void RebuildPlan()
        {
            _planDirty = false;
            _entries.RemoveAll(e => e == null || e.Source == null);
            if (_selected >= _entries.Count) _selected = _entries.Count - 1;
            if (_selected < 0 && _entries.Count > 0 && _selectedSlot == null) _selected = 0;
            try
            {
                _plan = ApplyPlanner.BuildPlan(_avatar, _entries);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _plan = null;
            }
        }

        /// <summary>统一标题栏（焦糖工具箱 · 快速改模）。</summary>
        private void DrawTitle()
        {
            EditorGUILayout.LabelField(
                "<size=18><color=#E8A24A>焦糖工具箱 · 快速改模</color></size>",
                new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.MiddleCenter },
                GUILayout.ExpandWidth(true));
        }

        private void DrawAvatarRow()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("当前角色", GUILayout.Width(56f));
            VRCAvatarDescriptor picked = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                _avatar, typeof(VRCAvatarDescriptor), true, GUILayout.MinWidth(160f));
            if (picked != _avatar)
            {
                VRCAvatarDescriptor captured = picked;
                _deferred.Add(() =>
                {
                    _avatar = captured;
                    _avatarNotice = string.Empty;
                });
            }

            if (GUILayout.Button("使用当前选中", GUILayout.Width(96f)))
            {
                _deferred.Add(UseSelectedAvatar);
            }

            GUILayout.FlexibleSpace();
            bool folded = _avatar != null && MenuFolder.IsFolded(_avatar);
            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                GUIContent foldContent = folded
                    ? new GUIContent("展开原模型菜单", "还原角色自带的 Expressions Menu，并删除「原模型菜单」子菜单")
                    : new GUIContent("折叠原模型菜单", "把角色自带的菜单收进一个子菜单「原模型菜单」，放在总菜单中");
                if (GUILayout.Button(foldContent, GUILayout.Width(110f)))
                {
                    VRCAvatarDescriptor avatar = _avatar;
                    bool unfold = folded;
                    EditorApplication.delayCall += () =>
                    {
                        if (this == null || avatar == null) return;
                        string message;
                        bool ok = unfold ? MenuFolder.Unfold(avatar, out message) : MenuFolder.Fold(avatar, out message);
                        Log(message, ok ? MessageType.Info : MessageType.Warning);
                        _overviewDirty = true;
                        _planDirty = true;
                        Repaint();
                    };
                }
            }

            if (GUILayout.Button("设置默认衣服和头发", GUILayout.Width(140f)))
            {
                VRCAvatarDescriptor avatar = _avatar;
                if (avatar == null)
                {
                    _deferred.Add(() => Log("请先指定当前角色", MessageType.Warning));
                }
                else
                {
                    EditorApplication.delayCall += () =>
                    {
                        PreviewController.ExitAll("打开设置默认衣服和头发");
                        DefaultOutfitWindow.Open(avatar);
                    };
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_avatarNotice))
            {
                EditorGUILayout.HelpBox(_avatarNotice, MessageType.Info);
            }
        }

        private void UseSelectedAvatar()
        {
            VRCAvatarDescriptor fromSelection = AvatarLocator.FromSelection();
            if (fromSelection == null)
            {
                _avatarNotice = "当前选中物体不属于任何角色";
                return;
            }

            _avatar = fromSelection;
            _avatarNotice = string.Empty;
        }

        private void DrawStatusRow()
        {
            string text;
            if (_avatar == null)
            {
                text = "未指定角色";
            }
            else if (_plan == null || _plan.Info == null)
            {
                text = "无法读取外观设置";
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(_plan.CreateAppearance ? "外观设置：未添加（应用时从模板创建）" : "外观设置：已存在");
                sb.Append("    ");
                List<string> parts = new List<string>();
                foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
                {
                    CategoryInfo ci = _plan.Info.Get(category);
                    string label = category == AssetCategory.Clothes ? "服装" : QuickOutfitConst.CategoryLabel(category);
                    if (ci == null) parts.Add(label + " 缺失");
                    else parts.Add(label + " " + ci.OccupiedCount + "/" + ci.Slots.Count);
                }

                sb.Append(string.Join(" · ", parts));
                text = sb.ToString();
            }

            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel);
        }

        // ================================================================== 合并列表：当前外观 + 待应用素材

        /// <summary>外观设置中的红色警告（例如默认服装里有身体/脸部网格）。</summary>
        private void DrawOverviewWarnings()
        {
            if (_avatar == null || _overview == null) return;
            foreach (string w in _overview.Warnings)
            {
                EditorGUILayout.HelpBox(w, MessageType.Error);
            }
        }

        /// <summary>
        /// 重建右侧列表（只在 Layout 事件中调用）。按「应用后的样子」排列：
        /// 每个类别 = 默认项内容 + 已有槽位（已有内容 / 待放入的新素材 / 空槽或将删除）+ 将新建的槽位；
        /// 然后是无法放置的素材，最后是服装部件。
        /// </summary>
        private void BuildRows()
        {
            _rowsDirty = false;
            _rows.Clear();
            ApplyPlan plan = _plan;
            bool structured = _avatar != null && plan != null && plan.Info != null && plan.Avatar == _avatar;
            if (!structured)
            {
                if (_entries.Count == 0) return;
                _rows.Add(new ListRow { Kind = RowKind.Header, Title = "待应用素材（" + _entries.Count + "）" });
                foreach (OutfitEntry e in _entries)
                {
                    if (e == null) continue;
                    _rows.Add(new ListRow
                    {
                        Kind = RowKind.Pending,
                        Entry = e,
                        Category = e.Category,
                        Target = e.Source,
                        Clickable = e.Source != null,
                    });
                }

                return;
            }

            AppearanceInfo info = plan.Info;
            bool isTemplate = plan.CreateAppearance;
            GameObject root = isTemplate ? null : AppearanceStructure.FindAppearanceRoot(_avatar);

            foreach (AssetCategory category in QuickOutfitConst.SlotCategories)
            {
                CategoryInfo ci = info.Get(category);
                string container = QuickOutfitConst.ContainerName(category);
                if (ci == null)
                {
                    _rows.Add(new ListRow { Kind = RowKind.Header, Category = category, Title = container + "（缺失）" });
                    continue;
                }

                HashSet<int> deleted = new HashSet<int>();
                foreach (PlanDeletion d in plan.Deletions)
                {
                    if (d.Category == category) deleted.Add(d.ExistingSlotIndex);
                }

                Dictionary<int, PlanItem> intoExisting = new Dictionary<int, PlanItem>();
                List<PlanItem> intoNew = new List<PlanItem>();
                foreach (PlanItem item in plan.Items)
                {
                    if (item.Skip || item.Category != category) continue;
                    if (item.IsNewSlot) intoNew.Add(item);
                    else if (item.ExistingSlotIndex >= 0) intoExisting[item.ExistingSlotIndex] = item;
                }

                int minCreate = 0;
                foreach (PlanCreation c in plan.MinimumCreations)
                {
                    if (c.Category == category) minCreate++;
                }

                int occupiedAfter = ci.OccupiedCount + intoExisting.Count + intoNew.Count;
                int totalAfter = ci.Slots.Count - deleted.Count + intoNew.Count + minCreate;
                int pendingCount = intoExisting.Count + intoNew.Count;
                string title = container + "（已占用 " + occupiedAfter + " / " + totalAfter + "）";
                if (pendingCount > 0) title += "   +" + pendingCount + " 待应用";
                _rows.Add(new ListRow { Kind = RowKind.Header, Category = category, Title = title });

                // 默认项（默认服装 / 默认发型）。
                if (ci.DefaultItem != null)
                {
                    GameObject defObj = isTemplate ? null : ci.DefaultItem;
                    string defName = ci.DefaultItem.name;
                    int added = 0;
                    if (defObj != null)
                    {
                        Transform dt = defObj.transform;
                        for (int i = 0; i < dt.childCount; i++)
                        {
                            GameObject child = dt.GetChild(i).gameObject;
                            _rows.Add(new ListRow
                            {
                                Kind = RowKind.Default,
                                Category = category,
                                SlotObject = defObj,
                                SlotName = defName,
                                Target = child,
                                Clickable = true,
                                FirstOfSlot = added == 0,
                            });
                            added++;
                        }
                    }

                    if (added == 0)
                    {
                        _rows.Add(new ListRow
                        {
                            Kind = RowKind.Default,
                            Category = category,
                            SlotObject = defObj,
                            SlotName = defName,
                        });
                    }
                }

                // 已有槽位。
                for (int i = 0; i < ci.Slots.Count; i++)
                {
                    SlotInfo slot = ci.Slots[i];
                    GameObject slotObj = isTemplate ? null : slot.GameObject;
                    string slotName = isTemplate || slot.GameObject == null ? slot.Name : slot.GameObject.name;
                    int count = 0;
                    if (slotObj != null)
                    {
                        foreach (GameObject content in AppearanceOverview.CollectSlotContents(slot, root))
                        {
                            if (content == null) continue;
                            bool refOnly = !content.transform.IsChildOf(slotObj.transform);
                            _rows.Add(new ListRow
                            {
                                Kind = RowKind.Existing,
                                Category = category,
                                SlotObject = slotObj,
                                SlotName = slotName,
                                Target = content,
                                Clickable = true,
                                FirstOfSlot = count == 0,
                                Tooltip = refOnly ? "仅由开关引用（不在槽位之下）" : string.Empty,
                            });
                            count++;
                        }
                    }

                    PlanItem pending;
                    if (intoExisting.TryGetValue(i, out pending))
                    {
                        _rows.Add(MakePendingRow(pending, slotObj, count == 0));
                        count++;
                    }

                    if (count == 0)
                    {
                        _rows.Add(new ListRow
                        {
                            Kind = RowKind.Empty,
                            Category = category,
                            SlotObject = slotObj,
                            SlotName = slotName,
                            WillDelete = deleted.Contains(i),
                        });
                    }
                }

                // 将新建的槽位。
                foreach (PlanItem item in intoNew)
                {
                    _rows.Add(MakePendingRow(item, null, true));
                }

                foreach (PlanCreation c in plan.MinimumCreations)
                {
                    if (c.Category != category) continue;
                    _rows.Add(new ListRow { Kind = RowKind.Created, Category = category, SlotName = c.SlotName });
                }
            }

            // 无法放置的素材（未识别 / 重复 / 已在槽位中 等）。
            List<PlanItem> skipped = new List<PlanItem>();
            foreach (PlanItem item in plan.Items)
            {
                if (item.Skip) skipped.Add(item);
            }

            if (skipped.Count > 0)
            {
                _rows.Add(new ListRow { Kind = RowKind.Header, Title = "未放置（" + skipped.Count + "）" });
                foreach (PlanItem item in skipped)
                {
                    _rows.Add(MakePendingRow(item, null, true));
                }
            }

            // 服装部件（只读展示 + 定位 / 改名）。
            if (!isTemplate && _overview != null && _overview.HasAppearance)
            {
                foreach (OverviewGroup group in _overview.Groups)
                {
                    if (!group.IsParts || group.Missing) continue;
                    _rows.Add(new ListRow { Kind = RowKind.Header, Title = group.Title + "（" + group.Slots.Count + " 个部件）" });
                    foreach (OverviewSlot part in group.Slots)
                    {
                        if (part.GameObject == null) continue;
                        int count = 0;
                        foreach (OverviewContent content in part.Contents)
                        {
                            if (content.Target == null) continue;
                            _rows.Add(new ListRow
                            {
                                Kind = RowKind.Part,
                                SlotObject = part.GameObject,
                                SlotName = part.GameObject.name,
                                Target = content.Target,
                                Clickable = true,
                                FirstOfSlot = count == 0,
                                Owner = content.Owner ?? string.Empty,
                            });
                            count++;
                        }

                        if (count == 0)
                        {
                            _rows.Add(new ListRow
                            {
                                Kind = RowKind.Part,
                                SlotObject = part.GameObject,
                                SlotName = part.GameObject.name,
                            });
                        }
                    }
                }
            }
        }

        private static ListRow MakePendingRow(PlanItem item, GameObject slotObject, bool firstOfSlot)
        {
            OutfitEntry entry = item.Entry;
            return new ListRow
            {
                Kind = RowKind.Pending,
                Category = item.Category,
                Entry = entry,
                Item = item,
                Target = entry != null ? entry.Source : null,
                Clickable = entry != null && entry.Source != null,
                SlotObject = slotObject,
                SlotName = item.Skip ? string.Empty : item.SlotNameBefore,
                FirstOfSlot = firstOfSlot,
            };
        }

        /// <summary>右侧合并列表（按矩形手动排版，保证各列对齐、按钮右对齐）。</summary>
        private void DrawMergedList()
        {
            // 每个事件都先申请拖动排序用的控件 ID，保证 ID 序列在 Layout / Repaint 之间一致。
            int reorderId = GUIUtility.GetControlID(FocusType.Passive);
            if (_rows.Count == 0)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(_avatar == null ? "（指定角色或拖入素材后在这里显示）" : "（无法读取外观设置）", _centerStyle);
                GUILayout.FlexibleSpace();
                return;
            }

            Rect header = GUILayoutUtility.GetRect(10f, 16f, GUILayout.ExpandWidth(true));
            header.xMin += 4f;
            header.xMax -= 16f;
            ColumnRects headerCols = SplitColumns(header);
            GUI.Label(headerCols.Slot, "槽位（≡ 拖动排序）", EditorStyles.miniLabel);
            GUI.Label(headerCols.Content, "内容（绿色 = 待应用）", EditorStyles.miniLabel);
            GUI.Label(headerCols.Type, "类型", EditorStyles.miniLabel);

            _reorderBlocks.Clear();
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            foreach (ListRow row in _rows)
            {
                DrawListRow(row, reorderId);
            }

            HandleReorder(reorderId);
            EditorGUILayout.EndScrollView();
        }

        private struct ColumnRects
        {
            public Rect Handle;
            public Rect Slot;
            public Rect Content;
            public Rect Type;
            public Rect Preview;
            public Rect Delete;
        }

        /// <summary>从右往左分配：删除 / 预览 / 类型，左侧依次为拖动柄 / 槽位，其余给内容。</summary>
        private static ColumnRects SplitColumns(Rect r)
        {
            ColumnRects c = new ColumnRects();
            float x = r.xMax;
            c.Delete = new Rect(x - DeleteColWidth, r.y + 1f, DeleteColWidth, r.height - 2f);
            x -= DeleteColWidth + 2f;
            c.Preview = new Rect(x - PreviewColWidth, r.y + 1f, PreviewColWidth, r.height - 2f);
            x -= PreviewColWidth + 4f;
            c.Type = new Rect(x - TypeColWidth, r.y, TypeColWidth, r.height);
            x -= TypeColWidth + 4f;
            c.Handle = new Rect(r.x, r.y, HandleColWidth, r.height);
            float slotX = r.x + HandleColWidth + 2f;
            c.Slot = new Rect(slotX, r.y + 1f, SlotColWidth, r.height - 2f);
            float contentX = slotX + SlotColWidth + 4f;
            c.Content = new Rect(contentX, r.y, Mathf.Max(20f, x - contentX), r.height);
            return c;
        }

        private static bool IsReorderable(ListRow row)
        {
            if (row.SlotObject == null) return false;
            if (row.Kind != RowKind.Existing && row.Kind != RowKind.Empty && row.Kind != RowKind.Pending) return false;
            return row.Category == AssetCategory.Clothes || row.Category == AssetCategory.Hair ||
                   row.Category == AssetCategory.Accessory || row.Category == AssetCategory.Prop;
        }

        private void DrawListRow(ListRow row, int reorderId)
        {
            if (row.Kind == RowKind.Header)
            {
                Rect hr = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
                GUI.Label(hr, row.Title, _groupHeaderStyle);
                return;
            }

            Rect rect = GUILayoutUtility.GetRect(10f, ListRowHeight, GUILayout.ExpandWidth(true));
            ColumnRects cols = SplitColumns(rect);

            bool isPending = row.Kind == RowKind.Pending;
            bool isSkipped = isPending && row.Item != null && row.Item.Skip;
            bool selected = isPending
                ? row.Entry != null && _selectedSlot == null && row.Entry == SelectedEntry()
                : row.Kind == RowKind.Existing && row.SlotObject != null && row.SlotObject == _selectedSlot;
            bool dragging = _reorderSlot != null && row.SlotObject == _reorderSlot;
            if (Event.current.type == EventType.Repaint)
            {
                if (dragging) EditorGUI.DrawRect(rect, DragRowColor);
                else if (selected) EditorGUI.DrawRect(rect, SelectedRowColor);
                else if (isPending && !isSkipped) EditorGUI.DrawRect(rect, PendingRowColor);
            }

            // 拖动排序：记录每个槽位块的范围；第一行画拖动柄。
            if (IsReorderable(row))
            {
                RecordReorderBlock(row.SlotObject, row.Category, rect);
                if (row.FirstOfSlot) DrawReorderHandle(cols.Handle, row, reorderId);
            }

            DrawSlotCell(cols.Slot, row, isSkipped);
            DrawContentCell(cols.Content, row, isSkipped);
            DrawTypeCell(cols.Type, row);
            DrawPreviewCell(cols.Preview, row);
            DrawDeleteCell(cols.Delete, row);
        }

        private void DrawSlotCell(Rect r, ListRow row, bool isSkipped)
        {
            if (!row.FirstOfSlot) return;
            switch (row.Kind)
            {
                case RowKind.Default:
                    GUI.Label(r, new GUIContent("★ " + row.SlotName, "默认项（不可改名、不可删除）"), EditorStyles.boldLabel);
                    return;
                case RowKind.Pending:
                {
                    OutfitEntry entry = row.Entry;
                    if (entry == null || isSkipped)
                    {
                        GUI.Label(r, "—");
                        return;
                    }

                    // 待应用素材：槽位名为应用时使用的名字（空 = 默认名，以灰色占位显示）。
                    string current = entry.SlotName ?? string.Empty;
                    string edited = EditorGUI.TextField(r, current);
                    if (current.Length == 0 && !string.IsNullOrEmpty(row.SlotName) && Event.current.type == EventType.Repaint)
                    {
                        string placeholder = row.SlotObject == null ? row.SlotName + "（新建）" : row.SlotName;
                        GUI.Label(r, placeholder, _placeholderStyle);
                    }

                    if (edited != current)
                    {
                        entry.SlotName = edited;
                        _planDirty = true;
                    }

                    return;
                }
                case RowKind.Existing:
                case RowKind.Empty:
                case RowKind.Part:
                    if (row.SlotObject != null)
                    {
                        // 已存在的槽位 / 部件：直接改名（回车或失焦提交）。
                        string renamed = EditorGUI.DelayedTextField(r, row.SlotName);
                        if (renamed != row.SlotName)
                        {
                            GameObject node = row.SlotObject;
                            string newName = renamed;
                            _deferred.Add(() => DoRename(node, newName));
                        }

                        return;
                    }

                    break;
            }

            GUI.Label(r, row.Kind == RowKind.Created ? row.SlotName + "（新建）" : row.SlotName);
        }

        private void DrawContentCell(Rect r, ListRow row, bool isSkipped)
        {
            string text;
            string tooltip = row.Tooltip;
            GUIStyle style = EditorStyles.label;
            Color prev = GUI.color;
            Color color = prev;
            switch (row.Kind)
            {
                case RowKind.Pending:
                {
                    OutfitEntry entry = row.Entry;
                    string name = entry != null ? entry.DisplayName + (entry.IsAsset ? "  [资源]" : "  [场景]") : "(已丢失)";
                    if (isSkipped)
                    {
                        text = "× " + name + "：" + row.Item.SkipReason;
                        color = UnknownColor;
                    }
                    else
                    {
                        text = "+ " + name;
                        if (row.Item != null) tooltip = "应用后：" + QuickOutfitConst.BindActionLabel(row.Item.Bind);
                    }

                    tooltip = string.IsNullOrEmpty(tooltip) ? "点击选中（可用下方类型按钮修改类型）" : tooltip + "\n点击选中";
                    break;
                }
                case RowKind.Empty:
                    text = row.WillDelete ? "（空槽，应用后删除）" : "（空槽）";
                    style = _emptySlotStyle;
                    if (row.WillDelete) color = DeleteTextColor;
                    break;
                case RowKind.Created:
                    text = "（将新建空槽，每类至少保留 1 个）";
                    style = _emptySlotStyle;
                    break;
                case RowKind.Default:
                    text = row.Clickable ? "· " + NameOf(row.Target) : "（空）";
                    if (!row.Clickable) style = _emptySlotStyle;
                    break;
                case RowKind.Part:
                    if (row.Clickable)
                    {
                        text = "· " + NameOf(row.Target) + "  ← " +
                               (string.IsNullOrEmpty(row.Owner) ? "（不在衣服中）" : row.Owner);
                    }
                    else
                    {
                        text = "（未引用网格）";
                        style = _emptySlotStyle;
                    }

                    break;
                default:
                    text = "· " + NameOf(row.Target) + (string.IsNullOrEmpty(row.Tooltip) ? string.Empty : "（仅开关引用）");
                    tooltip = string.IsNullOrEmpty(tooltip) ? "点击选中（可用下方类型按钮修改类型）" : tooltip + "\n点击选中";
                    break;
            }

            GUI.color = color;
            GUIContent content = new GUIContent(text, tooltip);
            if (row.Clickable)
            {
                if (GUI.Button(r, content, style)) OnContentClicked(row);
            }
            else
            {
                GUI.Label(r, content, style);
            }

            GUI.color = prev;
        }

        private void OnContentClicked(ListRow row)
        {
            if (row.Kind == RowKind.Pending)
            {
                OutfitEntry captured = row.Entry;
                _deferred.Add(() =>
                {
                    int idx = _entries.IndexOf(captured);
                    if (idx >= 0) _selected = idx;
                    _selectedSlot = null;
                    if (captured != null && captured.Source != null) EditorGUIUtility.PingObject(captured.Source);
                });
            }
            else if (row.Kind == RowKind.Existing && row.SlotObject != null)
            {
                // 选中已有槽位：之后可用下方类型按钮修改其类型。
                GameObject slot = row.SlotObject;
                AssetCategory category = row.Category;
                GameObject target = row.Target;
                _deferred.Add(() =>
                {
                    _selectedSlot = slot;
                    _selectedSlotCategory = category;
                    Locate(target);
                });
            }
            else
            {
                Locate(row.Target);
            }
        }

        private void DrawTypeCell(Rect r, ListRow row)
        {
            if (row.Kind == RowKind.Pending && row.Entry != null)
            {
                OutfitEntry entry = row.Entry;
                Color prev = GUI.color;
                bool unknown = entry.Category == AssetCategory.Unknown;
                if (unknown) GUI.color = UnknownColor;
                string detail = string.IsNullOrEmpty(entry.AutoDetail) ? "自动识别" : entry.AutoDetail;
                string tooltip = entry.IsManual ? "手动指定（自动识别不会覆盖）\n" + detail : detail;
                GUI.Label(r, new GUIContent(entry.CategoryDisplay, tooltip), unknown ? EditorStyles.helpBox : _typeLabelStyle);
                GUI.color = prev;
                return;
            }

            if (!row.FirstOfSlot) return;
            string label;
            switch (row.Kind)
            {
                case RowKind.Default: label = "默认"; break;
                case RowKind.Part: label = "部件"; break;
                default: label = QuickOutfitConst.CategoryLabel(row.Category); break;
            }

            GUI.Label(r, label, _typeLabelStyle);
        }

        private void DrawPreviewCell(Rect r, ListRow row)
        {
            if (!row.FirstOfSlot || row.SlotObject == null) return;
            if (row.Kind == RowKind.Existing)
            {
                bool on = PreviewController.IsSlotPreviewing(row.SlotObject);
                Color prev = GUI.backgroundColor;
                if (on) GUI.backgroundColor = ActiveButtonColor;
                if (GUI.Button(r, new GUIContent(on ? "● 预览" : "预览",
                        on ? "退出预览，恢复反转条件" : "临时勾上这一项第二个 MA Object Toggle 的「反转条件」（需开启 NDMF 预览，不进 Undo）"), EditorStyles.miniButton))
                {
                    GameObject slot = row.SlotObject;
                    _deferred.Add(() => TogglePreview(slot));
                }

                GUI.backgroundColor = prev;
            }
            else if (row.Kind == RowKind.Default)
            {
                // 默认项：正在预览同类其它项时，点击即回到默认状态。
                bool categoryOn = PreviewController.IsCategoryPreviewing(row.Category);
                using (new EditorGUI.DisabledScope(!categoryOn))
                {
                    if (GUI.Button(r, new GUIContent("预览", categoryOn ? "退出同类预览，恢复反转条件" : "当前没有同类预览"),
                            EditorStyles.miniButton))
                    {
                        AssetCategory category = row.Category;
                        _deferred.Add(() =>
                        {
                            PreviewController.ExitCategory(category);
                            Log("已退出「" + QuickOutfitConst.ContainerName(category) + "」预览", MessageType.Info);
                        });
                    }
                }
            }
        }

        private void DrawDeleteCell(Rect r, ListRow row)
        {
            string tooltip;
            Action action;
            switch (row.Kind)
            {
                case RowKind.Pending:
                {
                    OutfitEntry captured = row.Entry;
                    tooltip = "从待应用列表移除";
                    action = () =>
                    {
                        int idx = _entries.IndexOf(captured);
                        if (idx < 0) return;
                        _entries.RemoveAt(idx);
                        if (_selected >= _entries.Count) _selected = _entries.Count - 1;
                    };
                    break;
                }
                case RowKind.Existing:
                case RowKind.Empty:
                {
                    if (!row.FirstOfSlot || row.SlotObject == null) return;
                    GameObject slot = row.SlotObject;
                    tooltip = row.Kind == RowKind.Empty ? "删除这个空槽" : "删除这个槽位及其中的素材（可 Ctrl+Z 撤销）";
                    action = () => EditorApplication.delayCall += () =>
                    {
                        if (this != null) DeleteSlot(slot);
                    };
                    break;
                }
                case RowKind.Part:
                {
                    if (row.SlotObject == null) return;
                    GameObject part = row.SlotObject;
                    if (row.Clickable)
                    {
                        GameObject mesh = row.Target;
                        tooltip = "从「" + part.name + "」移除这个网格";
                        action = () => RemoveMeshFromPart(part, mesh);
                    }
                    else
                    {
                        tooltip = "删除部件「" + part.name + "」";
                        action = () => EditorApplication.delayCall += () =>
                        {
                            if (this != null) DeletePart(part);
                        };
                    }

                    break;
                }
                default:
                    return;
            }

            if (GUI.Button(r, new GUIContent("×", tooltip), EditorStyles.miniButton))
            {
                _deferred.Add(action);
            }
        }

        private void TogglePreview(GameObject slot)
        {
            string message;
            bool ok = PreviewController.ToggleSlot(_avatar, slot, out message);
            Log(message, ok ? MessageType.Info : MessageType.Warning);
        }

        // ================================================================== 拖动排序

        private class ReorderBlock
        {
            public GameObject Slot = null;
            public AssetCategory Category = AssetCategory.Unknown;
            public Rect Rect;
        }

        private readonly List<ReorderBlock> _reorderBlocks = new List<ReorderBlock>();
        private GameObject _reorderSlot = null;
        private AssetCategory _reorderCategory = AssetCategory.Unknown;
        private int _reorderInsert = -1;

        private void RecordReorderBlock(GameObject slot, AssetCategory category, Rect rect)
        {
            ReorderBlock last = _reorderBlocks.Count > 0 ? _reorderBlocks[_reorderBlocks.Count - 1] : null;
            if (last != null && last.Slot == slot)
            {
                last.Rect = new Rect(last.Rect.x, last.Rect.y, last.Rect.width, rect.yMax - last.Rect.y);
                return;
            }

            _reorderBlocks.Add(new ReorderBlock { Slot = slot, Category = category, Rect = rect });
        }

        private void DrawReorderHandle(Rect r, ListRow row, int reorderId)
        {
            EditorGUIUtility.AddCursorRect(r, MouseCursor.Pan);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.Label(r, new GUIContent("≡", "按住拖动以调整顺序（菜单中的顺序同步改变）"), _handleStyle);
            }

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                _reorderSlot = row.SlotObject;
                _reorderCategory = row.Category;
                _reorderInsert = -1;
                GUIUtility.hotControl = reorderId;
                e.Use();
            }
        }

        /// <summary>在所有行绘制完成后（块范围已知）处理拖动排序的事件与插入线。</summary>
        private void HandleReorder(int reorderId)
        {
            if (_reorderSlot == null) return;
            Event e = Event.current;
            List<ReorderBlock> blocks = new List<ReorderBlock>();
            foreach (ReorderBlock b in _reorderBlocks)
            {
                if (b.Category == _reorderCategory) blocks.Add(b);
            }

            switch (e.GetTypeForControl(reorderId))
            {
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != reorderId) break;
                    _reorderInsert = ComputeInsertIndex(blocks, e.mousePosition.y);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != reorderId) break;
                    GUIUtility.hotControl = 0;
                    int insert = _reorderInsert >= 0 ? _reorderInsert : ComputeInsertIndex(blocks, e.mousePosition.y);
                    GameObject moving = _reorderSlot;
                    GameObject before = insert >= 0 && insert < blocks.Count ? blocks[insert].Slot : null;
                    GameObject lastSlot = blocks.Count > 0 ? blocks[blocks.Count - 1].Slot : null;
                    _reorderSlot = null;
                    _reorderInsert = -1;
                    e.Use();
                    _deferred.Add(() => ReorderSlot(moving, before, lastSlot));
                    break;
                case EventType.Repaint:
                    if (_reorderInsert >= 0 && blocks.Count > 0)
                    {
                        float y = _reorderInsert < blocks.Count ? blocks[_reorderInsert].Rect.yMin : blocks[blocks.Count - 1].Rect.yMax;
                        Rect first = blocks[0].Rect;
                        EditorGUI.DrawRect(new Rect(first.x, y - 1f, first.width, 2f), ActiveButtonColor);
                    }

                    break;
            }

            // 鼠标在窗口外松开等情况：热控件已被释放，结束拖动。
            if (_reorderSlot != null && GUIUtility.hotControl != reorderId && e.type != EventType.Layout)
            {
                _reorderSlot = null;
                _reorderInsert = -1;
                Repaint();
            }
        }

        private static int ComputeInsertIndex(List<ReorderBlock> blocks, float mouseY)
        {
            int index = 0;
            foreach (ReorderBlock b in blocks)
            {
                if (mouseY > b.Rect.center.y) index++;
            }

            return index;
        }

        /// <summary>把槽位移到 before 之前（before 为空则移到同类最后一个槽位之后）。一个 Undo 组。</summary>
        private void ReorderSlot(GameObject moving, GameObject before, GameObject lastSlot)
        {
            if (moving == null || moving.transform.parent == null) return;
            Transform t = moving.transform;
            int current = t.GetSiblingIndex();
            int target;
            if (before != null)
            {
                if (before == moving || before.transform.parent != t.parent) return;
                target = before.transform.GetSiblingIndex();
                if (current < target) target--;
            }
            else
            {
                if (lastSlot == null || lastSlot == moving || lastSlot.transform.parent != t.parent) return;
                target = lastSlot.transform.GetSiblingIndex();
                if (current > target) target++;
            }

            if (target == current) return;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("快速改模：调整顺序");
            Undo.SetSiblingIndex(t, target, "快速改模：调整顺序");
            Undo.CollapseUndoOperations(group);
            if (_avatar != null) EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
            Log("已调整「" + moving.name + "」的顺序" + (before != null ? "（移到「" + before.name + "」之前）" : "（移到最后）") +
                "，菜单顺序同步改变（可 Ctrl+Z 撤销）", MessageType.Info);
            _overviewDirty = true;
            _planDirty = true;
        }

        // ================================================================== 删除

        /// <summary>删除槽位及其中的素材（确认后执行，一个 Undo 组）。每类至少保留 1 个槽：最后一个槽只清空不删除。</summary>
        private void DeleteSlot(GameObject slotObject)
        {
            if (_avatar == null || slotObject == null) return;
            GameObject root = AppearanceStructure.FindAppearanceRoot(_avatar);
            if (root == null) return;
            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            AssetCategory category;
            SlotInfo slot = SlotOperations.FindSlot(info, slotObject, out category);
            CategoryInfo ci = info.Get(category);
            if (slot == null || ci == null)
            {
                Log("「" + slotObject.name + "」不是槽位，未删除", MessageType.Warning);
                return;
            }

            List<GameObject> contents = AppearanceOverview.CollectSlotContents(slot, root);
            List<GameObject> children = new List<GameObject>();
            foreach (GameObject c in contents)
            {
                if (c != null && c.transform.IsChildOf(slotObject.transform)) children.Add(c);
            }

            bool keepSlot = ci.Slots.Count <= 1;
            if (contents.Count > 0)
            {
                string names = string.Join("、", contents.ConvertAll(c => c != null ? c.name : "?"));
                string msg = keepSlot
                    ? "「" + slotObject.name + "」是「" + ci.Container.name + "」中唯一的槽位，将清空其中的素材并保留空槽：\n" + names
                    : "删除槽位「" + slotObject.name + "」及其中的素材：\n" + names;
                if (!EditorUtility.DisplayDialog("删除槽位", msg + "\n\n可 Ctrl+Z 撤销。", "删除", "取消")) return;
            }
            else if (keepSlot)
            {
                Log("「" + slotObject.name + "」是「" + ci.Container.name + "」中唯一的空槽，每类至少保留 1 个槽位", MessageType.Warning);
                return;
            }

            if (!keepSlot && !AppearanceStructure.CanDestroy(slotObject))
            {
                Log("「" + slotObject.name + "」属于 Prefab 实例内部，无法删除", MessageType.Warning);
                return;
            }

            PreviewController.ExitAll(null);
            bool resume = PreviewController.SuspendParts();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            const string undoName = "快速改模：删除槽位";
            Undo.SetCurrentGroupName(undoName);
            string slotName = slotObject.name;
            try
            {
                // 同时从服装部件中移除这些素材里的网格引用。
                Transform parts = AppearanceStructure.FindPartsContainer(root);
                if (parts != null && contents.Count > 0)
                {
                    foreach (PartNode p in PartOperations.GetParts(parts))
                    {
                        foreach (ModularAvatarObjectToggle t in p.Toggles)
                        {
                            SlotOperations.RemoveToggleTargets(t, contents, true, undoName);
                        }
                    }
                }

                if (keepSlot)
                {
                    foreach (GameObject c in children)
                    {
                        if (c != null) Undo.DestroyObjectImmediate(c);
                    }

                    if (slot.ContentToggle != null)
                    {
                        Undo.RecordObject(slot.ContentToggle, undoName);
                        slot.ContentToggle.Objects = new List<ToggledObject>();
                        PrefabUtility.RecordPrefabInstancePropertyModifications(slot.ContentToggle);
                    }

                    HashSet<string> names = new HashSet<string>(ci.ChildNames);
                    names.Remove(slotObject.name);
                    string emptyName = AppearanceStructure.NextSlotName(category, names);
                    Undo.RecordObject(slotObject, undoName);
                    slotObject.name = emptyName;
                    Log("已清空「" + slotName + "」（改名为「" + emptyName + "」，每类至少保留 1 个槽位，可 Ctrl+Z 撤销）", MessageType.Info);
                }
                else
                {
                    Undo.DestroyObjectImmediate(slotObject);
                    Log("已删除槽位「" + slotName + "」" + (contents.Count > 0 ? "及其中的 " + contents.Count + " 个素材" : string.Empty) +
                        "（可 Ctrl+Z 撤销）", MessageType.Info);
                }

                AppearanceStructure.RefreshReferences(root, _avatar.transform, null);
                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(undoName);
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                Log("删除失败，已回滚：" + e.Message, MessageType.Error);
            }
            finally
            {
                Undo.FlushUndoRecordObjects();
                if (resume) PreviewController.ResumeParts(_avatar);
            }

            if (_selectedSlot == slotObject) _selectedSlot = null;
            _overviewDirty = true;
            _planDirty = true;
            Repaint();
        }

        private void RemoveMeshFromPart(GameObject part, GameObject mesh)
        {
            if (_avatar == null || part == null || mesh == null) return;
            bool resume = PreviewController.SuspendParts();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(PartOperations.UndoName);
            int removed = 0;
            try
            {
                foreach (ModularAvatarObjectToggle t in part.GetComponents<ModularAvatarObjectToggle>())
                {
                    removed += SlotOperations.RemoveToggleTargets(t, new[] { mesh }, false, PartOperations.UndoName);
                }

                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.CollapseUndoOperations(group);
            }
            finally
            {
                Undo.FlushUndoRecordObjects();
                if (resume) PreviewController.ResumeParts(_avatar);
            }

            Log(removed > 0 ? "已从「" + part.name + "」移除「" + mesh.name + "」（可 Ctrl+Z 撤销）" : "「" + mesh.name + "」不在「" + part.name + "」中",
                MessageType.Info);
            _overviewDirty = true;
        }

        private void DeletePart(GameObject part)
        {
            if (_avatar == null || part == null) return;
            if (!EditorUtility.DisplayDialog("删除服装部件", "删除服装部件「" + part.name + "」？\n\n可 Ctrl+Z 撤销。", "删除", "取消")) return;
            if (!AppearanceStructure.CanDestroy(part))
            {
                Log("「" + part.name + "」属于 Prefab 实例内部，无法删除", MessageType.Warning);
                return;
            }

            bool resume = PreviewController.SuspendParts();
            string name = part.name;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("删除服装部件");
            try
            {
                Undo.DestroyObjectImmediate(part);
                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.CollapseUndoOperations(group);
            }
            finally
            {
                Undo.FlushUndoRecordObjects();
                if (resume) PreviewController.ResumeParts(_avatar);
            }

            Log("已删除服装部件「" + name + "」（可 Ctrl+Z 撤销）", MessageType.Info);
            _overviewDirty = true;
            _planDirty = true;
            Repaint();
        }

        private static string NameOf(GameObject go)
        {
            return go != null ? go.name : "(已丢失)";
        }

        private static void Locate(GameObject go)
        {
            if (go == null) return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        /// <summary>槽位 / 部件改名（在 OnGUI 末尾执行）。</summary>
        private void DoRename(GameObject node, string newName)
        {
            if (node == null || _avatar == null) return;
            // 改名会记录该物体的 Undo；若预览改动过它的激活状态，先退出预览，避免把预览状态写进 Undo。
            if (PreviewController.Touches(node)) PreviewController.ExitAll("改名");
            string message;
            bool ok = SlotOperations.RenameNode(_avatar, node, newName, out message);
            if (!string.IsNullOrEmpty(message)) Log(message, ok ? MessageType.Info : MessageType.Warning);
            GUI.FocusControl(null);
            _overviewDirty = true;
            _planDirty = true;
        }

        /// <summary>用类型按钮修改已有槽位的类型（迁移到目标类别），完成后尽量保持选中迁移后的槽位。</summary>
        private void ConvertSelectedSlot(GameObject slotObject, AssetCategory target)
        {
            if (this == null || _avatar == null || slotObject == null) return;
            PreviewController.ExitAll("修改类型");
            List<GameObject> contents = new List<GameObject>();
            GameObject root = AppearanceStructure.FindAppearanceRoot(_avatar);
            if (root != null)
            {
                AppearanceInfo before = AppearanceStructure.Analyze(root, false);
                AssetCategory ignored;
                SlotInfo slot = SlotOperations.FindSlot(before, slotObject, out ignored);
                if (slot != null) contents = AppearanceOverview.CollectSlotContents(slot, root);
            }

            DoConvert(slotObject, target);

            _selectedSlot = null;
            root = AppearanceStructure.FindAppearanceRoot(_avatar);
            if (root == null) return;
            AppearanceInfo after = AppearanceStructure.Analyze(root, false);
            foreach (GameObject c in contents)
            {
                if (c == null) continue;
                SlotInfo moved = after.FindSlotContaining(c);
                if (moved == null || moved.GameObject == null) continue;
                AssetCategory category;
                if (SlotOperations.FindSlot(after, moved.GameObject, out category) == null) continue;
                _selectedSlot = moved.GameObject;
                _selectedSlotCategory = category;
                break;
            }
        }

        /// <summary>把槽位素材迁移到其它类别（在 OnGUI 之外或末尾执行，结构变化在下一次 Layout 时重建）。</summary>
        private void DoConvert(GameObject slotObject, AssetCategory target)
        {
            if (this == null || _avatar == null || slotObject == null) return;
            List<string> logs = new List<string>();
            List<string> warnings = new List<string>();
            string message;
            bool ok = SlotOperations.ConvertSlot(_avatar, slotObject, target, logs, warnings, out message);
            StringBuilder sb = new StringBuilder(message);
            foreach (string w in warnings) sb.Append("\n警告：").Append(w);
            Log(sb.ToString(), !ok ? MessageType.Error : (warnings.Count > 0 ? MessageType.Warning : MessageType.Info));
            _overviewDirty = true;
            _planDirty = true;
            _hoverCache.Clear();
            Repaint();
        }

        private void DrawMainArea()
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Height(ListHeight));

            EditorGUILayout.BeginVertical(GUILayout.Width(DropSize));
            Rect dropRect = GUILayoutUtility.GetRect(DropSize, DropSize, GUILayout.Width(DropSize), GUILayout.Height(DropSize));
            DrawDropZone(dropRect);
            HandleDrag(dropRect);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();

            GUILayout.Space(6f);
            EditorGUILayout.BeginVertical(GUILayout.Height(ListHeight));
            DrawMergedList();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDropZone(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            if (Event.current.type == EventType.Repaint)
            {
                Color border = _dragHoverLabel != null ? ActiveButtonColor : new Color(0.5f, 0.5f, 0.5f, 0.8f);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2f), border);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), border);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), border);
                EditorGUI.DrawRect(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), border);
            }

            GUI.Label(rect, "将素材拖入这里", _centerStyle);

            string tag = _dragHoverLabel;
            if (tag == null)
            {
                GameObject selSlot = SelectedSlot();
                OutfitEntry sel = selSlot == null ? SelectedEntry() : null;
                if (selSlot != null) tag = QuickOutfitConst.CategoryLabel(_selectedSlotCategory) + "：" + selSlot.name;
                else tag = sel != null ? sel.CategoryDisplay : "—";
            }

            Color prev = GUI.color;
            if (tag.StartsWith("未识别", StringComparison.Ordinal)) GUI.color = UnknownColor;
            GUI.Label(new Rect(rect.x + 4f, rect.y + 6f, rect.width - 10f, 20f), "【" + tag + "】", _tagStyle);
            GUI.color = prev;
        }

        private void HandleDrag(Rect rect)
        {
            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                {
                    if (!rect.Contains(evt.mousePosition))
                    {
                        if (_dragHoverLabel != null)
                        {
                            _dragHoverLabel = null;
                            Repaint();
                        }

                        break;
                    }

                    List<GameObject> dragged = CollectDraggedGameObjects();
                    if (dragged.Count == 0)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        evt.Use();
                        break;
                    }

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    string hover = DescribeHover(dragged);
                    if (hover != _dragHoverLabel)
                    {
                        _dragHoverLabel = hover;
                        Repaint();
                    }

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        List<GameObject> copy = new List<GameObject>(dragged);
                        _deferred.Add(() => AddEntries(copy));
                        _dragHoverLabel = null;
                    }

                    evt.Use();
                    break;
                }
                case EventType.DragExited:
                    if (_dragHoverLabel != null)
                    {
                        _dragHoverLabel = null;
                        Repaint();
                    }

                    break;
            }
        }

        private static List<GameObject> CollectDraggedGameObjects()
        {
            List<GameObject> result = new List<GameObject>();
            UnityEngine.Object[] refs = DragAndDrop.objectReferences;
            if (refs == null) return result;
            foreach (UnityEngine.Object o in refs)
            {
                GameObject go = o as GameObject;
                if (go == null && o is Component c) go = c.gameObject;
                if (go != null && !result.Contains(go)) result.Add(go);
            }

            return result;
        }

        private string DescribeHover(List<GameObject> dragged)
        {
            GameObject first = dragged[0];
            AssetCategory category;
            int id = first.GetInstanceID();
            if (!_hoverCache.TryGetValue(id, out category))
            {
                category = AssetClassifier.Classify(first);
                _hoverCache[id] = category;
            }

            string label = QuickOutfitConst.CategoryLabel(category);
            if (dragged.Count > 1) label += " 等" + dragged.Count + "个";
            return label;
        }

        private OutfitEntry SelectedEntry()
        {
            if (_selected < 0 || _selected >= _entries.Count) return null;
            return _entries[_selected];
        }

        /// <summary>当前选中的已有槽位（已被删除时返回 null）。</summary>
        private GameObject SelectedSlot()
        {
            return _selectedSlot != null ? _selectedSlot : null;
        }

        /// <summary>类型按钮：作用于列表中选中的待应用素材（手动指定类型）或已有槽位（迁移到目标类别）。</summary>
        private void DrawTypeButtons()
        {
            GameObject slot = SelectedSlot();
            OutfitEntry sel = slot == null ? SelectedEntry() : null;
            AssetCategory current = AssetCategory.Unknown;
            if (slot != null) current = _selectedSlotCategory;
            else if (sel != null) current = sel.Category;

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(slot == null && sel == null))
            {
                foreach (AssetCategory category in QuickOutfitConst.SelectableCategories)
                {
                    string tooltip = slot != null
                        ? "把「" + slot.name + "」迁移到「" + QuickOutfitConst.ContainerName(category) + "」"
                        : (sel != null ? "把「" + sel.DisplayName + "」指定为" + QuickOutfitConst.CategoryLabel(category) : "先在列表中选中一项");
                    if (!TypeButton(QuickOutfitConst.CategoryLabel(category), current == category, tooltip)) continue;
                    AssetCategory value = category;
                    if (slot != null)
                    {
                        if (value == current) continue;
                        GameObject captured = slot;
                        // 迁移可能触发 Setup Outfit 的模态错误窗，放到 OnGUI 之外执行。
                        EditorApplication.delayCall += () =>
                        {
                            if (this != null) ConvertSelectedSlot(captured, value);
                        };
                    }
                    else
                    {
                        // 点击即为手动指定（IsManual=true），之后任何自动识别都不会覆盖。
                        OutfitEntry captured = sel;
                        _deferred.Add(() => captured.SetManualCategory(value));
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private bool TypeButton(string label, bool active, string tooltip)
        {
            Color prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = ActiveButtonColor;
            bool clicked = GUILayout.Button(new GUIContent(active ? "● " + label : label, tooltip), _bigButtonStyle,
                GUILayout.Height(TypeRowHeight), GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true));
            GUI.backgroundColor = prev;
            return clicked;
        }

        /// <summary>把规划转为文字（与 Execute 使用同一份 plan），显示在操作日志的「待应用」部分。</summary>
        private static List<string> BuildPreviewLines(ApplyPlan plan)
        {
            List<string> lines = new List<string>();
            if (plan.CreateAppearance)
            {
                lines.Add("• 将从模板创建「外观设置」到角色根下并完全解包");
            }

            foreach (PlanRename r in plan.NormalizeRenames)
            {
                lines.Add("• 规范化：" + r.OldName + " → " + r.NewName);
            }

            foreach (PlanItem item in plan.Items)
            {
                string name = item.Entry != null ? item.Entry.DisplayName : "?";
                if (item.Skip)
                {
                    lines.Add("• " + name + "：跳过（" + item.SkipReason + "）");
                    continue;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("• ").Append(name).Append(" → ")
                    .Append(QuickOutfitConst.ContainerName(item.Category)).Append("/")
                    .Append(item.SlotNameBefore)
                    .Append(item.IsNewSlot ? "（新建）" : "（已有）");
                if (item.WillRename) sb.Append(" → 改名「").Append(item.SlotNameAfter).Append("」");
                sb.Append(" → ").Append(QuickOutfitConst.BindActionLabel(item.Bind));
                if (!string.IsNullOrEmpty(item.Note)) sb.Append("（").Append(item.Note).Append("）");
                lines.Add(sb.ToString());
            }

            Dictionary<AssetCategory, List<string>> deletions = new Dictionary<AssetCategory, List<string>>();
            foreach (PlanDeletion d in plan.Deletions)
            {
                if (!deletions.ContainsKey(d.Category)) deletions[d.Category] = new List<string>();
                deletions[d.Category].Add(d.SlotName);
            }

            foreach (KeyValuePair<AssetCategory, List<string>> kv in deletions)
            {
                lines.Add("• 将删除空槽（" + QuickOutfitConst.ContainerName(kv.Key) + "）：" + string.Join("、", kv.Value));
            }

            foreach (PlanCreation c in plan.MinimumCreations)
            {
                lines.Add("• 将新建空槽「" + c.SlotName + "」（" + QuickOutfitConst.ContainerName(c.Category) + "至少保留 1 个槽）");
            }

            lines.Add("• 同时：修复默认项引用路径；默认服装/默认发型设为默认项；素材设为关闭");
            return lines;
        }

        private List<string> GetDisableReasons()
        {
            List<string> reasons = new List<string>();
            if (_avatar == null) reasons.Add("请先指定当前角色");
            if (_entries.Count == 0) reasons.Add("请先拖入素材");
            List<string> unknown = new List<string>();
            foreach (OutfitEntry e in _entries)
            {
                if (e != null && e.Category == AssetCategory.Unknown) unknown.Add(e.DisplayName);
            }

            if (unknown.Count > 0)
            {
                reasons.Add("存在未识别类型的素材：" + string.Join("、", unknown) + "，请选中后用类型按钮手动指定");
            }

            if (_plan != null && _avatar != null && unknown.Count == 0)
            {
                foreach (string err in _plan.Errors) reasons.Add(err);
            }

            return reasons;
        }

        private void DrawApplyArea()
        {
            List<string> reasons = GetDisableReasons();
            using (new EditorGUI.DisabledScope(reasons.Count > 0))
            {
                GUIContent content = new GUIContent("快速应用到模型", reasons.Count > 0 ? "无法应用：" + string.Join("\n", reasons) : string.Empty);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.75f, 0.35f);
                if (GUILayout.Button(content, GUILayout.Height(32f)))
                {
                    EditorApplication.delayCall += DoApply;
                }
                GUI.backgroundColor = prev;
            }
        }

        // ================================================================== 服装部件按钮行

        /// <summary>
        /// 一行铺满：[预览] [外套] [鞋子] … [＋]。用法：先在层级中选中衣服的网格，再点部件按钮，
        /// 网格会加入该部件的 MA Object Toggle（状态 OFF）。
        /// </summary>
        private void DrawPartButtons()
        {
            OverviewGroup partsGroup = null;
            if (_overview != null && _overview.HasAppearance)
            {
                foreach (OverviewGroup g in _overview.Groups)
                {
                    if (g.IsParts && !g.Missing) partsGroup = g;
                }
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_avatar == null || partsGroup == null))
            {
                bool previewOn = PreviewController.IsPartsPreviewing;
                Color prev = GUI.backgroundColor;
                if (previewOn) GUI.backgroundColor = ActiveButtonColor;
                if (GUILayout.Button(new GUIContent(previewOn ? "● 预览中" : "预览",
                        previewOn ? "退出预览，恢复反转条件" : "临时勾上服装部件下所有 MA Object Toggle 的「反转条件」"),
                        _bigButtonStyle, GUILayout.Height(PartRowHeight), GUILayout.MinWidth(40f), GUILayout.ExpandWidth(true)))
                {
                    _deferred.Add(TogglePartsPreview);
                }

                GUI.backgroundColor = prev;

                if (partsGroup == null)
                {
                    GUILayout.Label(new GUIContent(_avatar == null ? "（请先指定角色）" : "（没有外观设置或服装部件，应用后可用）"),
                        _emptySlotStyle, GUILayout.Height(PartRowHeight), GUILayout.ExpandWidth(true));
                }
                else
                {
                    foreach (OverviewSlot part in partsGroup.Slots)
                    {
                        if (part.GameObject == null) continue;
                        string label = part.Contents.Count > 0 ? part.Name + " (" + part.Contents.Count + ")" : part.Name;
                        GUIContent content = new GUIContent(label,
                            "先在层级中选中衣服的网格，再点击此按钮：加入「" + part.Name + "」的 MA Object Toggle（状态 OFF）");
                        if (GUILayout.Button(content, _bigButtonStyle, GUILayout.Height(PartRowHeight), GUILayout.MinWidth(40f),
                                GUILayout.ExpandWidth(true)))
                        {
                            GameObject partObject = part.GameObject;
                            _deferred.Add(() => AssignSelectionToPart(partObject));
                        }
                    }
                }

                if (GUILayout.Button(new GUIContent("＋", "新增服装部件（MA Menu Item + MA Object Toggle）"), _bigButtonStyle,
                        GUILayout.Height(PartRowHeight), GUILayout.Width(40f)))
                {
                    Rect rect = GUILayoutUtility.GetLastRect();
                    VRCAvatarDescriptor avatar = _avatar;
                    PopupWindow.Show(rect, new NamePromptPopup("新增服装部件", "部件名", partName =>
                    {
                        if (this == null) return;
                        AddPart(avatar, partName);
                    }));
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void TogglePartsPreview()
        {
            if (_avatar == null) return;
            string message;
            bool ok = PreviewController.ToggleParts(_avatar, out message);
            Log(message, ok ? MessageType.Info : MessageType.Warning);
        }

        private void AssignSelectionToPart(GameObject partObject)
        {
            if (_avatar == null || partObject == null) return;
            List<GameObject> selected = new List<GameObject>(Selection.gameObjects);
            if (selected.Count == 0)
            {
                Log("请先在层级中选中衣服的网格，再点击「" + partObject.name + "」", MessageType.Warning);
                return;
            }

            // 部件预览会改动反转条件：修改期间先暂停，写完 Undo 后再恢复预览，保证 Undo 记录的是非预览状态。
            bool resume = PreviewController.SuspendParts();
            List<string> messages = new List<string>();
            int added;
            try
            {
                added = PartOperations.AssignMeshes(_avatar, partObject, selected, messages);
            }
            finally
            {
                Undo.FlushUndoRecordObjects();
                if (resume) PreviewController.ResumeParts(_avatar);
            }

            bool hasWarning = messages.Count > 0 && (added == 0 || messages.Count > 1);
            Log(messages.Count > 0 ? string.Join("\n", messages) : "没有可添加的网格",
                added > 0 ? (hasWarning ? MessageType.Warning : MessageType.Info) : MessageType.Warning);
            _overviewDirty = true;
            _planDirty = true;
            Repaint();
        }

        private void AddPart(VRCAvatarDescriptor avatar, string partName)
        {
            if (avatar == null || avatar != _avatar)
            {
                Log("角色已切换，未新增部件", MessageType.Warning);
                return;
            }

            bool resume = PreviewController.SuspendParts();
            string message;
            GameObject created;
            try
            {
                created = PartOperations.AddPart(avatar, partName, out message);
            }
            finally
            {
                Undo.FlushUndoRecordObjects();
                if (resume) PreviewController.ResumeParts(avatar);
            }

            Log(message, created != null ? MessageType.Info : MessageType.Warning);
            _overviewDirty = true;
            _planDirty = true;
            Repaint();
        }

        // ================================================================== 操作日志

        private void Log(string text, MessageType type)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_log == null) _log = new List<LogEntry>();
            _log.Insert(0, new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Type = type });
            while (_log.Count > MaxLogCount) _log.RemoveAt(_log.Count - 1);
            _logScroll = Vector2.zero;
            Repaint();
        }

        private GUIStyle LogStyle(MessageType type)
        {
            switch (type)
            {
                case MessageType.Warning: return _logWarnStyle;
                case MessageType.Error: return _logErrorStyle;
                default: return _logInfoStyle;
            }
        }

        /// <summary>
        /// 插件最下方的操作日志（默认折叠，点右侧三角或标题展开）：
        /// 上半部分为「待应用」规划，下半部分为历史记录（最新在前）。折叠时标题右侧显示最新一条记录。
        /// </summary>
        private void DrawLog()
        {
            Rect header = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorStyles.helpBox.Draw(header, false, false, false, false);

            Rect arrowRect = new Rect(header.xMax - 18f, header.y + 3f, 16f, 16f);
            Rect clearRect = new Rect(arrowRect.x - 48f, header.y + 3f, 44f, 16f);
            Rect titleRect = new Rect(header.x + 6f, header.y + 2f, clearRect.x - header.x - 10f, 18f);

            int count = _log != null ? _log.Count : 0;
            string title = "操作日志" + (count > 0 ? "（" + count + "）" : string.Empty);
            GUIContent titleContent = new GUIContent(title, _logExpanded ? "点击折叠" : "点击展开");
            Vector2 titleSize = EditorStyles.boldLabel.CalcSize(titleContent);
            Rect titleLabelRect = new Rect(titleRect.x, titleRect.y, Mathf.Min(titleSize.x, titleRect.width), titleRect.height);
            bool toggle = GUI.Button(titleRect, GUIContent.none, GUIStyle.none);
            GUI.Label(titleLabelRect, titleContent, EditorStyles.boldLabel);

            // 折叠时在标题右侧显示最新一条记录（第一行）。
            if (!_logExpanded && count > 0 && _log[0] != null)
            {
                string latest = _log[0].Text ?? string.Empty;
                int nl = latest.IndexOf('\n');
                if (nl >= 0) latest = latest.Substring(0, nl) + " …";
                Rect latestRect = new Rect(titleLabelRect.xMax + 8f, titleRect.y + 1f,
                    Mathf.Max(0f, titleRect.xMax - titleLabelRect.xMax - 8f), titleRect.height);
                GUIStyle latestStyle = new GUIStyle(LogStyle(_log[0].Type)) { wordWrap = false, clipping = TextClipping.Clip };
                GUI.Label(latestRect, new GUIContent(latest, _log[0].Text), latestStyle);
            }

            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUI.Button(clearRect, "清空", EditorStyles.miniButton))
                {
                    _deferred.Add(() => _log.Clear());
                }
            }

            bool expanded = EditorGUI.Foldout(arrowRect, _logExpanded, GUIContent.none, true);
            if (expanded != _logExpanded) toggle = true;
            if (toggle)
            {
                // 延迟到 OnGUI 末尾切换，保证本次事件的 Layout / 绘制控件数量一致。
                bool next = !_logExpanded;
                _deferred.Add(() => _logExpanded = next);
            }

            if (!_logExpanded) return;

            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, EditorStyles.helpBox, GUILayout.Height(LogHeight));
            bool any = false;
            if (_entries.Count > 0)
            {
                any = true;
                GUILayout.Label("待应用：", EditorStyles.miniBoldLabel);
                if (_plan != null && _avatar != null)
                {
                    foreach (string line in BuildPreviewLines(_plan)) GUILayout.Label(line, _logInfoStyle);
                    foreach (string w in _plan.Warnings) GUILayout.Label("⚠ " + w, _logWarnStyle);
                }

                foreach (string reason in GetDisableReasons()) GUILayout.Label("无法应用：" + reason, _logErrorStyle);
                GUILayout.Space(4f);
            }

            if (count > 0)
            {
                any = true;
                foreach (LogEntry entry in _log)
                {
                    if (entry == null) continue;
                    GUILayout.Label("[" + entry.Time + "] " + entry.Text, LogStyle(entry.Type));
                }
            }

            if (!any) GUILayout.Label("（暂无记录）", _emptySlotStyle);
            EditorGUILayout.EndScrollView();
        }

        // ================================================================== 操作

        private void DoApply()
        {
            if (this == null) return;
            PreviewController.ExitAll("应用");
            ApplyPlan plan = ApplyPlanner.BuildPlan(_avatar, _entries);
            if (!plan.IsValid)
            {
                Log("无法应用：" + string.Join("；", plan.Errors), MessageType.Error);
                _planDirty = true;
                Repaint();
                return;
            }

            // 与用户看到的列表比对；不一致说明场景已变化（或列表未生成），不执行，刷新让用户确认。
            bool previewMissing = _plan == null;
            string shownSignature = previewMissing ? string.Empty : _plan.Signature();
            if (previewMissing || shownSignature != plan.Signature())
            {
                _plan = plan;
                _planDirty = false;
                _rowsDirty = true;
                Log(previewMissing
                    ? "列表尚未生成或生成失败，已刷新，请确认后再应用"
                    : "场景已变化，列表已刷新，请确认后再应用", MessageType.Warning);
                Repaint();
                return;
            }

            ApplyResult result = ApplyPlanner.Execute(plan);
            StringBuilder sb = new StringBuilder(result.Summary);
            foreach (string l in result.Logs) sb.Append("\n  ").Append(l);
            foreach (string w in result.Warnings) sb.Append("\n警告：").Append(w);
            Log(sb.ToString(), !result.Success
                ? MessageType.Error
                : (result.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info));

            if (result.Success)
            {
                _entries.Clear();
                _selected = -1;
            }

            _planDirty = true;
            _overviewDirty = true;
            Repaint();
        }

        /// <summary>添加拖入的素材，执行各种拒绝规则。</summary>
        private void AddEntries(List<GameObject> objects)
        {
            List<string> messages = new List<string>();
            bool hasRejection = false;

            foreach (GameObject raw in objects)
            {
                if (raw == null) continue;
                GameObject go = raw;
                bool isAsset = EditorUtility.IsPersistent(go);

                if (isAsset)
                {
                    if (go.transform.parent != null)
                    {
                        go = go.transform.root.gameObject;
                        messages.Add("「" + raw.name + "」是资源内部对象，已改用其根「" + go.name + "」");
                    }

                    if (go.GetComponentInChildren<VRCAvatarDescriptor>(true) != null)
                    {
                        messages.Add("「" + go.name + "」是角色（含 VRC Avatar Descriptor），不能作为素材");
                        hasRejection = true;
                        continue;
                    }
                }
                else
                {
                    if (_avatar == null)
                    {
                        VRCAvatarDescriptor owner = AvatarLocator.FromGameObject(go);
                        if (owner != null && owner.gameObject != go)
                        {
                            _avatar = owner;
                            _avatarNotice = string.Empty;
                            messages.Add("已自动设为角色「" + owner.gameObject.name + "」");
                        }
                    }

                    if (go.GetComponentInChildren<VRCAvatarDescriptor>(true) != null ||
                        (_avatar != null && _avatar.transform.IsChildOf(go.transform)))
                    {
                        messages.Add("「" + go.name + "」是角色本身（或包含角色），不能作为素材");
                        hasRejection = true;
                        continue;
                    }

                    if (_avatar != null && AvatarLocator.FromGameObject(go) == _avatar)
                    {
                        GameObject root = AppearanceStructure.FindAppearanceRoot(_avatar);
                        if (root != null)
                        {
                            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
                            SlotInfo slot = info.FindSlotContaining(go);
                            if (slot != null)
                            {
                                messages.Add("「" + go.name + "」已在外观设置槽位「" + slot.GameObject.name + "」内，已跳过");
                                hasRejection = true;
                                continue;
                            }

                            if (info.IsStructuralNode(go))
                            {
                                messages.Add("「" + go.name + "」是外观设置的结构节点，不能作为素材");
                                hasRejection = true;
                                continue;
                            }

                            if (info.FindDefaultItemContaining(go) != null)
                            {
                                messages.Add("「" + go.name + "」这是默认服装/默认发型的内容，不能作为素材");
                                hasRejection = true;
                                continue;
                            }
                        }
                    }

                    if (!AppearanceStructure.CanReparent(go))
                    {
                        messages.Add("「" + go.name + "」属于 Prefab 实例内部，无法移动（请先解包或拖入其 Prefab 根）");
                        hasRejection = true;
                        continue;
                    }
                }

                bool duplicate = false;
                foreach (OutfitEntry e in _entries)
                {
                    if (e == null || e.Source == null) continue;
                    if (e.Source == go)
                    {
                        duplicate = true;
                        break;
                    }

                    if (!isAsset && !e.IsAsset &&
                        (go.transform.IsChildOf(e.Source.transform) || e.Source.transform.IsChildOf(go.transform)))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (duplicate)
                {
                    messages.Add("「" + go.name + "」已在列表中（或与列表中的对象存在父子关系），已跳过");
                    hasRejection = true;
                    continue;
                }

                ClassifyResult classified = AssetClassifier.Analyze(go);
                OutfitEntry entry = new OutfitEntry
                {
                    Source = go,
                    IsManual = false,
                    SlotName = string.Empty,
                };
                entry.ApplyAutoCategory(classified.Category, classified.Detail());
                AssetCategory category = entry.Category;
                Debug.Log("[快速改模] 类型识别「" + go.name + "」\n" + classified.Detail());
                _entries.Add(entry);
                _selected = _entries.Count - 1;
                _selectedSlot = null;
                if (category == AssetCategory.Unknown)
                {
                    messages.Add("「" + go.name + "」未能识别类型，请手动指定");
                }
                else
                {
                    messages.Add("已添加「" + go.name + "」：识别为" + QuickOutfitConst.CategoryLabel(category));
                }
            }

            if (messages.Count > 0)
            {
                Log(string.Join("\n", messages), hasRejection ? MessageType.Warning : MessageType.Info);
            }

            _planDirty = true;
        }
    }

    /// <summary>输入名字的小弹窗（回车确认、Esc 取消）。</summary>
    internal class NamePromptPopup : PopupWindowContent
    {
        private const string FieldName = "QuickOutfitNamePromptField";
        private readonly string _title;
        private readonly string _label;
        private readonly Action<string> _onConfirm;
        private string _value = string.Empty;
        private bool _focused = false;

        public NamePromptPopup(string title, string label, Action<string> onConfirm)
        {
            _title = title;
            _label = label;
            _onConfirm = onConfirm;
        }

        public override Vector2 GetWindowSize()
        {
            return new Vector2(260f, 84f);
        }

        public override void OnGUI(Rect rect)
        {
            Event e = Event.current;
            bool submit = false;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    submit = true;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    editorWindow.Close();
                    e.Use();
                    return;
                }
            }

            GUILayout.Label(_title, EditorStyles.boldLabel);
            GUI.SetNextControlName(FieldName);
            _value = EditorGUILayout.TextField(_label, _value);
            if (!_focused)
            {
                _focused = true;
                EditorGUI.FocusTextInControl(FieldName);
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("取消", GUILayout.Width(60f)))
            {
                editorWindow.Close();
                GUIUtility.ExitGUI();
            }

            if (GUILayout.Button("确定", GUILayout.Width(60f))) submit = true;
            EditorGUILayout.EndHorizontal();

            if (submit)
            {
                string value = _value;
                Action<string> callback = _onConfirm;
                editorWindow.Close();
                EditorApplication.delayCall += () => callback?.Invoke(value);
                GUIUtility.ExitGUI();
            }
        }
    }
}
