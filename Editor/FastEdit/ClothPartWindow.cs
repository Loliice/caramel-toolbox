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
    /// <summary>
    /// 「指定服装部件」子窗口（以部件为中心）：每个部件（服装部件 的子物体）一块，列出其 ObjectToggle 引用的网格与所属衣服，
    /// 支持拖入网格、× 移除、定位。拖入/移除立即生效并带 Undo。写入规则：{网格, Active=false}；部件 MenuItem isDefault=false。
    /// 同一网格只属于一个部件。底部可折叠的「未分配网格」按衣服分组，可用下拉快速指定部件。
    /// </summary>
    public class ClothPartWindow : EditorWindow
    {
        private const string UndoName = "指定服装部件";
        private static readonly Color HoverColor = new Color(0.45f, 0.8f, 1f, 1f);
        private static readonly Color BorderColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);
        private static readonly Color WarnColor = new Color(1f, 0.85f, 0.2f, 1f);

        private class PartItem
        {
            public GameObject Target = null;
            public string Owner = string.Empty;
        }

        private class PartBlock
        {
            public GameObject GameObject = null;
            public ModularAvatarMenuItem MenuItem = null;
            public List<ModularAvatarObjectToggle> Toggles = new List<ModularAvatarObjectToggle>();
            public List<PartItem> Items = new List<PartItem>();
        }

        private class UnassignedRow
        {
            public GameObject Mesh = null;
            public bool Protected = false;
            public string ProtectedReason = string.Empty;
        }

        private class UnassignedGroup
        {
            public string Owner = string.Empty;
            public List<UnassignedRow> Rows = new List<UnassignedRow>();
        }

        /// <summary>按名字建议部件：关键词 → 部件名。袜子规则排在鞋子之前，使「靴下」不会被「靴」误判为鞋子。</summary>
        private static readonly KeyValuePair<string[], string>[] SuggestRules =
        {
            new KeyValuePair<string[], string>(new[] { "jacket", "coat", "外套", "アウター" }, "外套"),
            new KeyValuePair<string[], string>(new[] { "socks", "tights", "stocking", "靴下", "ソックス", "袜" }, "袜子"),
            new KeyValuePair<string[], string>(new[] { "shoes", "boots", "靴", "鞋" }, "鞋子"),
            new KeyValuePair<string[], string>(new[] { "bra", "underwear", "panties", "下着", "内衣" }, "内衣"),
            new KeyValuePair<string[], string>(new[] { "acc", "ribbon", "リボン", "饰" }, "饰品"),
        };

        private VRCAvatarDescriptor _avatar = null;
        private GameObject _appearanceRoot = null;
        private Transform _partsContainer = null;
        private readonly List<PartBlock> _parts = new List<PartBlock>();
        private readonly List<UnassignedGroup> _unassigned = new List<UnassignedGroup>();
        private Dictionary<GameObject, string> _meshOwners = new Dictionary<GameObject, string>();
        private string[] _partOptions = new string[0];
        private bool _dirty = true;
        private bool _unassignedFoldout = true;
        private Vector2 _scroll = Vector2.zero;
        private string _newPartName = string.Empty;
        private string _message = string.Empty;
        private MessageType _messageType = MessageType.Info;
        private GameObject _hoverPart = null;
        private readonly List<Action> _deferred = new List<Action>();
        private GUIStyle _dropStyle;

        public static void Open(VRCAvatarDescriptor avatar)
        {
            ClothPartWindow window = GetWindow<ClothPartWindow>(true, "指定服装部件", true);
            window._avatar = avatar;
            window.minSize = new Vector2(500f, 480f);
            window._message = string.Empty;
            window._dirty = true;
            window.Show();
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            _dirty = true;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo()
        {
            _dirty = true;
            Repaint();
        }

        private void OnHierarchyChange()
        {
            _dirty = true;
            Repaint();
        }

        // ================================================================== 数据

        private void Rebuild()
        {
            _dirty = false;
            _parts.Clear();
            _unassigned.Clear();
            _meshOwners = new Dictionary<GameObject, string>();
            _appearanceRoot = AppearanceStructure.FindAppearanceRoot(_avatar);
            _partsContainer = AppearanceStructure.FindPartsContainer(_appearanceRoot);
            _partOptions = new string[0];
            if (_appearanceRoot == null) return;

            AppearanceInfo info = AppearanceStructure.Analyze(_appearanceRoot, false);
            List<ClothesMeshGroup> groups = AppearanceOverview.BuildClothesMeshGroups(info);
            _meshOwners = AppearanceOverview.ToOwnerMap(groups);

            HashSet<GameObject> assigned = new HashSet<GameObject>();
            if (_partsContainer != null)
            {
                for (int i = 0; i < _partsContainer.childCount; i++)
                {
                    Transform child = _partsContainer.GetChild(i);
                    ModularAvatarMenuItem mi = child.GetComponent<ModularAvatarMenuItem>();
                    ModularAvatarObjectToggle[] toggles = child.GetComponents<ModularAvatarObjectToggle>();
                    if (mi == null && toggles.Length == 0) continue;
                    PartBlock block = new PartBlock { GameObject = child.gameObject, MenuItem = mi };
                    block.Toggles.AddRange(toggles);
                    foreach (GameObject target in AppearanceOverview.CollectToggleTargets(toggles))
                    {
                        string owner;
                        _meshOwners.TryGetValue(target, out owner);
                        block.Items.Add(new PartItem { Target = target, Owner = owner ?? string.Empty });
                        assigned.Add(target);
                    }

                    _parts.Add(block);
                }
            }

            List<string> options = new List<string> { "选择部件…" };
            foreach (PartBlock p in _parts) options.Add(p.GameObject.name);
            _partOptions = options.ToArray();

            foreach (ClothesMeshGroup g in groups)
            {
                UnassignedGroup ug = new UnassignedGroup { Owner = g.Owner };
                foreach (GameObject mesh in g.Meshes)
                {
                    if (mesh == null || assigned.Contains(mesh)) continue;
                    ProtectedCheck check = BodyMeshDetector.Check(mesh, _avatar);
                    ug.Rows.Add(new UnassignedRow
                    {
                        Mesh = mesh,
                        Protected = check.IsProtected,
                        ProtectedReason = check.Reason,
                    });
                }

                if (ug.Rows.Count > 0) _unassigned.Add(ug);
            }
        }

        // ================================================================== GUI

        private void EnsureStyles()
        {
            if (_dropStyle != null) return;
            _dropStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
            };
        }

        private void OnGUI()
        {
            EnsureStyles();
            if (Event.current.type == EventType.Layout && _dirty)
            {
                Rebuild();
            }

            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("角色已丢失，请关闭本窗口后在主窗口重新指定角色。", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("角色：" + _avatar.gameObject.name, EditorStyles.boldLabel);
            if (_appearanceRoot == null)
            {
                EditorGUILayout.HelpBox("该角色还没有「外观设置」，请先在主窗口应用素材。", MessageType.Warning);
                if (GUILayout.Button("刷新")) _dirty = true;
                return;
            }

            if (_partsContainer == null)
            {
                EditorGUILayout.HelpBox("外观设置下缺少「服装部件」节点。", MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "把衣服里的网格拖到对应部件中。部件开关「开 = 隐藏该部件」，默认关闭。\n" +
                "只接受位于服装槽或默认服装之下的网格；身体/脸部网格会被拒绝。一个网格只能属于一个部件。拖入/移除立即生效，可 Ctrl+Z 撤销。",
                MessageType.Info);

            DrawToolbar();
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, _messageType);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_parts.Count == 0)
            {
                EditorGUILayout.LabelField("「服装部件」下没有部件，请先用上方「新增部件」创建。", EditorStyles.wordWrappedLabel);
            }

            foreach (PartBlock block in _parts)
            {
                DrawPartBlock(block);
                EditorGUILayout.Space(4f);
            }

            DrawUnassigned();
            EditorGUILayout.EndScrollView();

            if (Event.current.type == EventType.DragExited && _hoverPart != null)
            {
                _hoverPart = null;
                Repaint();
            }

            if (_deferred.Count > 0 && Event.current.type != EventType.Layout)
            {
                List<Action> actions = new List<Action>(_deferred);
                _deferred.Clear();
                foreach (Action a in actions) a();
                _dirty = true;
                Repaint();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_parts.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("按名字自动分配", "只处理还未分配的网格"), GUILayout.Width(110f)))
                {
                    _deferred.Add(AutoAssign);
                }
            }

            if (GUILayout.Button("刷新", GUILayout.Width(50f)))
            {
                _dirty = true;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("新增部件", GUILayout.Width(52f));
            _newPartName = EditorGUILayout.TextField(_newPartName, GUILayout.Width(110f));
            using (new EditorGUI.DisabledScope(_partsContainer == null))
            {
                if (GUILayout.Button("新增", GUILayout.Width(44f)))
                {
                    string name = _newPartName;
                    _deferred.Add(() => AddPart(name));
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPartBlock(PartBlock block)
        {
            if (block.GameObject == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(block.GameObject.name + "（" + block.Items.Count + "）", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40f)))
            {
                Locate(block.GameObject);
            }

            EditorGUILayout.EndHorizontal();

            if (block.MenuItem != null && block.MenuItem.isDefault)
            {
                EditorGUILayout.HelpBox("此部件的 Menu Item 为默认开启（会默认隐藏这些网格），拖入网格时将自动改为默认关闭。", MessageType.Warning);
            }

            if (block.Items.Count == 0)
            {
                EditorGUILayout.LabelField("    （暂无网格）", EditorStyles.miniLabel);
            }

            foreach (PartItem item in block.Items)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                string label = item.Target != null ? item.Target.name : "(已丢失)";
                if (GUILayout.Button(new GUIContent(label, "点击定位"), EditorStyles.label, GUILayout.MinWidth(80f)))
                {
                    Locate(item.Target);
                }

                Color prev = GUI.color;
                string owner;
                if (string.IsNullOrEmpty(item.Owner))
                {
                    GUI.color = WarnColor;
                    owner = "（不在衣服中）";
                }
                else
                {
                    owner = "所属：" + item.Owner;
                }

                GUILayout.Label(owner, EditorStyles.miniLabel, GUILayout.MinWidth(80f));
                GUI.color = prev;
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40f)))
                {
                    Locate(item.Target);
                }

                if (GUILayout.Button(new GUIContent("×", "从此部件移除"), EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    GameObject target = item.Target;
                    PartBlock captured = block;
                    _deferred.Add(() => RemoveFromPart(target, captured));
                }

                EditorGUILayout.EndHorizontal();
            }

            Rect drop = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            DrawDropRect(drop, "拖入网格到此部件", _hoverPart == block.GameObject);
            HandlePartDrop(drop, block);
            EditorGUILayout.EndVertical();
        }

        private void DrawDropRect(Rect rect, string text, bool hover)
        {
            GUI.Box(rect, text, _dropStyle);
            if (Event.current.type != EventType.Repaint) return;
            Color border = hover ? HoverColor : BorderColor;
            float w = hover ? 2f : 1f;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, w), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - w, rect.width, w), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, w, rect.height), border);
            EditorGUI.DrawRect(new Rect(rect.xMax - w, rect.y, w, rect.height), border);
        }

        private void HandlePartDrop(Rect rect, PartBlock block)
        {
            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!rect.Contains(evt.mousePosition))
            {
                if (_hoverPart == block.GameObject)
                {
                    _hoverPart = null;
                    Repaint();
                }

                return;
            }

            List<GameObject> dragged = CollectDragged();
            bool anyMesh = false;
            foreach (GameObject go in dragged)
            {
                if (HasMeshRenderer(go))
                {
                    anyMesh = true;
                    break;
                }
            }

            DragAndDrop.visualMode = anyMesh ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
            if (_hoverPart != block.GameObject)
            {
                _hoverPart = block.GameObject;
                Repaint();
            }

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                _hoverPart = null;
                List<GameObject> copy = new List<GameObject>(dragged);
                PartBlock captured = block;
                _deferred.Add(() => AssignMeshes(copy, captured));
            }

            evt.Use();
        }

        private void DrawUnassigned()
        {
            int total = 0;
            foreach (UnassignedGroup g in _unassigned) total += g.Rows.Count;
            EditorGUILayout.Space(6f);
            _unassignedFoldout = EditorGUILayout.Foldout(_unassignedFoldout, "未分配网格（" + total + "）", true);
            if (!_unassignedFoldout) return;
            if (total == 0)
            {
                EditorGUILayout.LabelField("    衣服中的网格都已分配。", EditorStyles.miniLabel);
                return;
            }

            foreach (UnassignedGroup g in _unassigned)
            {
                EditorGUILayout.LabelField(g.Owner, EditorStyles.miniBoldLabel);
                foreach (UnassignedRow row in g.Rows)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(12f);
                    string label = row.Mesh != null ? row.Mesh.name : "(已丢失)";
                    if (GUILayout.Button(new GUIContent(label, "点击定位"), EditorStyles.label, GUILayout.MinWidth(80f)))
                    {
                        Locate(row.Mesh);
                    }

                    GUILayout.FlexibleSpace();
                    if (row.Protected)
                    {
                        Color prev = GUI.color;
                        GUI.color = WarnColor;
                        GUILayout.Label(new GUIContent("身体/脸部，不可分配", row.ProtectedReason), EditorStyles.miniLabel);
                        GUI.color = prev;
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(_parts.Count == 0))
                        {
                            int picked = EditorGUILayout.Popup(0, _partOptions, GUILayout.Width(120f));
                            if (picked > 0 && picked - 1 < _parts.Count)
                            {
                                GameObject mesh = row.Mesh;
                                PartBlock part = _parts[picked - 1];
                                _deferred.Add(() => AssignMeshes(new List<GameObject> { mesh }, part));
                            }
                        }
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        private static void Locate(GameObject go)
        {
            if (go == null) return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        private static List<GameObject> CollectDragged()
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

        private static bool HasMeshRenderer(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<SkinnedMeshRenderer>() != null || go.GetComponent<MeshRenderer>() != null;
        }

        // ================================================================== 操作

        /// <summary>校验被拖入的对象能否作为服装部件网格。</summary>
        private bool ValidateMesh(GameObject go, out string reason)
        {
            reason = string.Empty;
            if (go == null)
            {
                reason = "对象已丢失";
                return false;
            }

            if (EditorUtility.IsPersistent(go))
            {
                reason = "「" + go.name + "」是 Project 资源，请拖入场景中衣服里的网格";
                return false;
            }

            if (AvatarLocator.FromGameObject(go) != _avatar)
            {
                reason = "「" + go.name + "」不属于当前角色";
                return false;
            }

            if (!HasMeshRenderer(go))
            {
                reason = "「" + go.name + "」不是网格（没有 Skinned Mesh Renderer / Mesh Renderer），请展开后拖入具体网格";
                return false;
            }

            ProtectedCheck check = BodyMeshDetector.Check(go, _avatar);
            if (check.IsProtected)
            {
                reason = "「" + go.name + "」" + check.Reason + "，是身体/脸部网格（或骨骼），不能作为服装部件";
                return false;
            }

            if (!_meshOwners.ContainsKey(go))
            {
                reason = "「" + go.name + "」不在任何服装槽或默认服装之下，不能作为服装部件";
                return false;
            }

            return true;
        }

        /// <summary>把网格分配到部件（立即生效，一个 Undo 组）。网格会自动从其它部件移除。</summary>
        private void AssignMeshes(List<GameObject> meshes, PartBlock part)
        {
            if (this == null || _avatar == null || part == null || part.GameObject == null) return;
            List<string> messages = new List<string>();
            List<GameObject> accepted = new List<GameObject>();
            bool rejected = false;
            foreach (GameObject go in meshes)
            {
                string reason;
                if (!ValidateMesh(go, out reason))
                {
                    messages.Add(reason);
                    rejected = true;
                    continue;
                }

                if (!accepted.Contains(go)) accepted.Add(go);
            }

            if (accepted.Count == 0)
            {
                SetMessage(messages.Count > 0 ? string.Join("\n", messages) : "没有可分配的网格", MessageType.Warning);
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            try
            {
                ModularAvatarObjectToggle main = part.Toggles.Count > 0 ? part.Toggles[0] : null;
                if (main == null)
                {
                    main = Undo.AddComponent<ModularAvatarObjectToggle>(part.GameObject);
                    part.Toggles.Add(main);
                }

                int added = 0;
                foreach (GameObject mesh in accepted)
                {
                    foreach (PartBlock other in _parts)
                    {
                        if (other == part || other.GameObject == null) continue;
                        int removed = 0;
                        foreach (ModularAvatarObjectToggle t in other.Toggles)
                        {
                            removed += SlotOperations.RemoveToggleTargets(t, new[] { mesh }, false, UndoName);
                        }

                        if (removed > 0)
                        {
                            messages.Add("「" + mesh.name + "」已从「" + other.GameObject.name + "」移到「" + part.GameObject.name + "」");
                        }
                    }

                    // 同一部件的其它 Toggle 中的重复引用也清掉，只保留在主 Toggle。
                    for (int i = 1; i < part.Toggles.Count; i++)
                    {
                        SlotOperations.RemoveToggleTargets(part.Toggles[i], new[] { mesh }, false, UndoName);
                    }

                    bool already = SlotOperations.ToggleReferences(main, mesh);
                    if (SlotOperations.SetToggleTarget(main, mesh, false, UndoName) && !already)
                    {
                        added++;
                    }
                    else if (already)
                    {
                        messages.Add("「" + mesh.name + "」已在「" + part.GameObject.name + "」中");
                    }
                }

                if (part.MenuItem != null && part.MenuItem.isDefault)
                {
                    Undo.RecordObject(part.MenuItem, UndoName);
                    part.MenuItem.isDefault = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(part.MenuItem);
                    messages.Add("部件「" + part.GameObject.name + "」的 Menu Item 已改为默认关闭");
                }

                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(group);
                messages.Insert(0, "已添加 " + added + " 个网格到「" + part.GameObject.name + "」（可 Ctrl+Z 撤销）");
                SetMessage(string.Join("\n", messages), rejected ? MessageType.Warning : MessageType.Info);
                Debug.Log("[快速改模] 指定服装部件\n  " + string.Join("\n  ", messages));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                SetMessage("执行失败，已回滚：" + e.Message, MessageType.Error);
            }

            _dirty = true;
        }

        /// <summary>从部件移除网格（立即生效，一个 Undo 组）。</summary>
        private void RemoveFromPart(GameObject mesh, PartBlock part)
        {
            if (this == null || part == null || part.GameObject == null) return;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            try
            {
                int removed = 0;
                foreach (ModularAvatarObjectToggle t in part.Toggles)
                {
                    if (mesh != null)
                    {
                        removed += SlotOperations.RemoveToggleTargets(t, new[] { mesh }, false, UndoName);
                    }
                    else
                    {
                        removed += RemoveMissingTargets(t);
                    }
                }

                if (_avatar != null) EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(group);
                string name = mesh != null ? mesh.name : "(已丢失)";
                SetMessage(removed > 0
                        ? "已从「" + part.GameObject.name + "」移除「" + name + "」（可 Ctrl+Z 撤销）"
                        : "「" + name + "」不在「" + part.GameObject.name + "」中",
                    MessageType.Info);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                SetMessage("执行失败，已回滚：" + e.Message, MessageType.Error);
            }

            _dirty = true;
        }

        /// <summary>移除目标已丢失的项。</summary>
        private static int RemoveMissingTargets(ModularAvatarObjectToggle toggle)
        {
            if (toggle == null || toggle.Objects == null) return 0;
            List<ResolvedToggle> resolved = AppearanceStructure.ResolveTargets(toggle);
            List<ToggledObject> list = new List<ToggledObject>();
            int removed = 0;
            for (int i = 0; i < toggle.Objects.Count; i++)
            {
                if (i < resolved.Count && resolved[i].Target == null)
                {
                    removed++;
                    continue;
                }

                list.Add(toggle.Objects[i]);
            }

            if (removed == 0) return 0;
            Undo.RecordObject(toggle, UndoName);
            toggle.Objects = list;
            PrefabUtility.RecordPrefabInstancePropertyModifications(toggle);
            return removed;
        }

        /// <summary>「按名字自动分配」：只处理还未分配的网格（一个 Undo 组）。</summary>
        private void AutoAssign()
        {
            if (this == null || _avatar == null) return;
            Dictionary<PartBlock, List<GameObject>> plan = new Dictionary<PartBlock, List<GameObject>>();
            foreach (UnassignedGroup g in _unassigned)
            {
                foreach (UnassignedRow row in g.Rows)
                {
                    if (row.Mesh == null || row.Protected) continue;
                    string partName = SuggestPartName(row.Mesh.name);
                    if (string.IsNullOrEmpty(partName)) continue;
                    foreach (PartBlock p in _parts)
                    {
                        if (p.GameObject == null || p.GameObject.name != partName) continue;
                        if (!plan.ContainsKey(p)) plan[p] = new List<GameObject>();
                        plan[p].Add(row.Mesh);
                        break;
                    }
                }
            }

            if (plan.Count == 0)
            {
                SetMessage("没有可按名字分配的网格", MessageType.Info);
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            int total = 0;
            StringBuilder sb = new StringBuilder();
            try
            {
                foreach (KeyValuePair<PartBlock, List<GameObject>> kv in plan)
                {
                    PartBlock part = kv.Key;
                    ModularAvatarObjectToggle main = part.Toggles.Count > 0 ? part.Toggles[0] : null;
                    if (main == null)
                    {
                        main = Undo.AddComponent<ModularAvatarObjectToggle>(part.GameObject);
                        part.Toggles.Add(main);
                    }

                    foreach (GameObject mesh in kv.Value)
                    {
                        string reason;
                        if (!ValidateMesh(mesh, out reason)) continue;
                        if (SlotOperations.SetToggleTarget(main, mesh, false, UndoName))
                        {
                            total++;
                            sb.Append("\n「").Append(mesh.name).Append("」→「").Append(part.GameObject.name).Append("」");
                        }
                    }

                    if (part.MenuItem != null && part.MenuItem.isDefault)
                    {
                        Undo.RecordObject(part.MenuItem, UndoName);
                        part.MenuItem.isDefault = false;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(part.MenuItem);
                    }
                }

                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(group);
                SetMessage("已按名字自动分配 " + total + " 个网格（可 Ctrl+Z 撤销）" + sb, MessageType.Info);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                SetMessage("执行失败，已回滚：" + e.Message, MessageType.Error);
            }

            _dirty = true;
        }

        /// <summary>根据网格名给出建议部件名，无匹配返回空字符串。「靴下」优先判为袜子。</summary>
        public static string SuggestPartName(string meshName)
        {
            if (string.IsNullOrEmpty(meshName)) return string.Empty;
            foreach (KeyValuePair<string[], string> rule in SuggestRules)
            {
                if (AssetClassifier.ContainsAny(meshName, rule.Key))
                {
                    return rule.Value;
                }
            }

            return string.Empty;
        }

        private void AddPart(string partName)
        {
            if (this == null) return;
            string name = partName != null ? partName.Trim() : string.Empty;
            if (_partsContainer == null)
            {
                SetMessage("缺少「服装部件」节点", MessageType.Error);
                return;
            }

            if (name.Length == 0)
            {
                SetMessage("请输入部件名", MessageType.Warning);
                return;
            }

            if (AppearanceStructure.FindDirectChild(_partsContainer, name) != null)
            {
                SetMessage("已存在同名部件「" + name + "」", MessageType.Warning);
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("新增服装部件");
            GameObject created = AppearanceStructure.CreatePartSlot(_partsContainer, name);
            Undo.CollapseUndoOperations(group);
            if (created == null)
            {
                SetMessage("新增部件失败", MessageType.Error);
                return;
            }

            EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
            _newPartName = string.Empty;
            GUI.FocusControl(null);
            _dirty = true;
            SetMessage("已新增部件「" + name + "」", MessageType.Info);
        }

        private void SetMessage(string message, MessageType type)
        {
            _message = message;
            _messageType = type;
            Repaint();
        }
    }
}
