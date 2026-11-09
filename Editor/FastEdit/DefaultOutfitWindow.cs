using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 「设置默认衣服和头发」子窗口：两个拖放区「默认服装」「默认发型」。
    /// 场景对象（必须在当前角色下）保持世界位姿与激活状态移入；Project 中的 Prefab 实例化到容器下并按规则绑定骨骼。
    /// 身体/脸部网格弹窗警告（可「仍然添加」），骨骼一律拒绝。
    /// </summary>
    public class DefaultOutfitWindow : EditorWindow
    {
        private const string UndoName = "设置默认衣服和头发";
        private const string MoveBackUndoName = "移回角色根";
        private static readonly Color HoverColor = new Color(0.45f, 0.8f, 1f, 1f);
        private static readonly Color BorderColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);

        private VRCAvatarDescriptor _avatar = null;
        private GameObject _root = null;
        private GameObject _defaultClothes = null;
        private GameObject _defaultHair = null;
        private readonly List<GameObject> _clothesItems = new List<GameObject>();
        private readonly List<GameObject> _hairItems = new List<GameObject>();
        private readonly List<string> _protectedWarnings = new List<string>();
        private bool _dirty = true;
        private Vector2 _scroll = Vector2.zero;
        private string _message = string.Empty;
        private MessageType _messageType = MessageType.Info;
        private string _lastProtectedMessage = string.Empty;
        private AssetCategory _hover = AssetCategory.Unknown;
        private readonly List<Action> _deferred = new List<Action>();
        private GUIStyle _dropStyle;

        public static void Open(VRCAvatarDescriptor avatar)
        {
            DefaultOutfitWindow window = GetWindow<DefaultOutfitWindow>(true, "设置默认衣服和头发", true);
            window._avatar = avatar;
            window.minSize = new Vector2(480f, 460f);
            window._message = string.Empty;
            window._lastProtectedMessage = string.Empty;
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
            _clothesItems.Clear();
            _hairItems.Clear();
            _protectedWarnings.Clear();
            _defaultClothes = null;
            _defaultHair = null;
            _root = AppearanceStructure.FindAppearanceRoot(_avatar);
            if (_root == null) return;

            _defaultClothes = FindDefaultItemFor(_root, AssetCategory.Clothes);
            _defaultHair = FindDefaultItemFor(_root, AssetCategory.Hair);
            CollectItems(_defaultClothes, _clothesItems);
            CollectItems(_defaultHair, _hairItems);
        }

        private void CollectItems(GameObject defaultItem, List<GameObject> items)
        {
            if (defaultItem == null) return;
            Transform t = defaultItem.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                GameObject child = t.GetChild(i).gameObject;
                items.Add(child);
                ProtectedCheck check = BodyMeshDetector.Check(child, _avatar);
                if (check.IsProtected)
                {
                    _protectedWarnings.Add("「" + defaultItem.name + "」中的「" + child.name + "」" + check.Reason + "：这是身体/脸部网格（或骨骼），" +
                                           BodyMeshDetector.ConsequenceText + "。建议点「移回角色根」。");
                }
            }
        }

        private static GameObject FindDefaultItemFor(GameObject root, AssetCategory category)
        {
            Transform container = AppearanceStructure.FindContainer(root, category);
            return container != null ? AppearanceStructure.FindDefaultItem(container, category) : null;
        }

        // ================================================================== GUI

        private void EnsureStyles()
        {
            if (_dropStyle != null) return;
            _dropStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
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
            EditorGUILayout.HelpBox(
                "不要拖入身体/脸部网格（Body、Face、Body_base、带 vrc.v_ / vrc.blink 形态键的网格）或骨骼（Armature、Hips 之下的物体）：" +
                BodyMeshDetector.ConsequenceText + "。骨骼一律拒绝。",
                MessageType.Error);
            EditorGUILayout.HelpBox(
                "把角色自带的衣服/头发（当前角色下的场景物体）或 Project 中的 Prefab 拖入下方区域：\n" +
                "· 场景物体：保持世界位姿与激活状态移入，必要时逐层解包 Prefab；\n" +
                "· Prefab：实例化到容器下（本地位置/旋转归零，保持开启），衣服无 Merge Armature 时执行 Setup Outfit，头发加 Bone Proxy→Head。",
                MessageType.Info);

            if (_root == null)
            {
                EditorGUILayout.LabelField("外观设置：未添加（第一次拖入时将从模板创建）", EditorStyles.miniBoldLabel);
            }

            if (!string.IsNullOrEmpty(_lastProtectedMessage))
            {
                EditorGUILayout.HelpBox(_lastProtectedMessage, MessageType.Error);
            }

            foreach (string w in _protectedWarnings)
            {
                EditorGUILayout.HelpBox(w, MessageType.Error);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawZone(AssetCategory.Clothes, _defaultClothes, _clothesItems);
            EditorGUILayout.Space(6f);
            DrawZone(AssetCategory.Hair, _defaultHair, _hairItems);
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, _messageType);

            if (GUILayout.Button("刷新", GUILayout.Height(24f)))
            {
                _dirty = true;
            }

            if (Event.current.type == EventType.DragExited && _hover != AssetCategory.Unknown)
            {
                _hover = AssetCategory.Unknown;
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

        private void DrawZone(AssetCategory category, GameObject defaultItem, List<GameObject> items)
        {
            string title = QuickOutfitConst.DefaultItemName(category);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(title + "（" + items.Count + "）", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(defaultItem == null))
            {
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40f)))
                {
                    Locate(defaultItem);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_root != null && defaultItem == null)
            {
                EditorGUILayout.LabelField("    （「" + title + "」不存在，拖入时将自动补建）", EditorStyles.miniLabel);
            }
            else if (items.Count == 0)
            {
                EditorGUILayout.LabelField("    （空）", EditorStyles.miniLabel);
            }

            foreach (GameObject item in items)
            {
                if (item == null) continue;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                if (GUILayout.Button(new GUIContent(item.name, "点击定位"), EditorStyles.label, GUILayout.MinWidth(80f)))
                {
                    Locate(item);
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40f)))
                {
                    Locate(item);
                }

                if (GUILayout.Button(new GUIContent("移回角色根", "移回角色根下，保持世界位姿"), EditorStyles.miniButton,
                        GUILayout.Width(72f)))
                {
                    GameObject captured = item;
                    _deferred.Add(() => MoveBackToRoot(captured));
                }

                EditorGUILayout.EndHorizontal();
            }

            Rect drop = GUILayoutUtility.GetRect(0f, 44f, GUILayout.ExpandWidth(true));
            string text = category == AssetCategory.Clothes
                ? "拖入衣服到「" + title + "」"
                : "拖入头发到「" + title + "」";
            DrawDropRect(drop, text, _hover == category);
            HandleDrop(drop, category);
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

        private void HandleDrop(Rect rect, AssetCategory category)
        {
            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            if (!rect.Contains(evt.mousePosition))
            {
                if (_hover == category)
                {
                    _hover = AssetCategory.Unknown;
                    Repaint();
                }

                return;
            }

            List<GameObject> dragged = CollectDragged();
            DragAndDrop.visualMode = dragged.Count > 0 ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected;
            if (_hover != category)
            {
                _hover = category;
                Repaint();
            }

            if (evt.type == EventType.DragPerform && dragged.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                _hover = AssetCategory.Unknown;
                List<GameObject> copy = new List<GameObject>(dragged);
                AssetCategory captured = category;
                // 需要弹出模态对话框，放到 delayCall 中执行（不在 OnGUI 内弹窗）。
                EditorApplication.delayCall += () => ProcessDrop(copy, captured);
            }

            evt.Use();
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

        private static void Locate(GameObject go)
        {
            if (go == null) return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // ================================================================== 操作

        /// <summary>
        /// 身体/脸部网格确认框：「取消」为默认按钮（回车）。用 DisplayDialogComplex 保证 Esc / 关闭窗口（返回 1）也等于取消，
        /// 只有明确点击「仍然添加」（返回 2）才返回 true。
        /// </summary>
        private bool ConfirmProtected(GameObject go, ProtectedCheck check, string containerName)
        {
            string text = BodyMeshDetector.BuildWarningText(go, check);
            int choice = EditorUtility.DisplayDialogComplex("身体/脸部网格警告",
                text + "\n\n确定仍要添加到「" + containerName + "」吗？", "取消", "不添加", "仍然添加");
            if (choice != 2)
            {
                _lastProtectedMessage = "已拒绝：" + text;
                return false;
            }

            _lastProtectedMessage = "已按你的确认添加：" + text;
            return true;
        }

        private void ProcessDrop(List<GameObject> objects, AssetCategory category)
        {
            if (this == null || _avatar == null || objects == null) return;
            string containerName = QuickOutfitConst.DefaultItemName(category);
            AssetCategory otherCategory = category == AssetCategory.Clothes ? AssetCategory.Hair : AssetCategory.Clothes;
            List<string> messages = new List<string>();
            List<GameObject> sceneObjects = new List<GameObject>();
            List<GameObject> assets = new List<GameObject>();
            bool rejected = false;
            _lastProtectedMessage = string.Empty;

            GameObject root = AppearanceStructure.FindAppearanceRoot(_avatar);
            GameObject targetDefault = root != null ? FindDefaultItemFor(root, category) : null;
            GameObject otherDefault = root != null ? FindDefaultItemFor(root, otherCategory) : null;

            foreach (GameObject raw in objects)
            {
                if (raw == null) continue;
                GameObject go = raw;

                // ---------- Project 资源
                if (EditorUtility.IsPersistent(go))
                {
                    if (go.transform.parent != null)
                    {
                        go = go.transform.root.gameObject;
                        messages.Add("「" + raw.name + "」是资源内部对象，已改用其根「" + go.name + "」");
                    }

                    if (go.GetComponentInChildren<VRCAvatarDescriptor>(true) != null)
                    {
                        messages.Add("「" + go.name + "」是角色（含 VRC Avatar Descriptor），不能放入" + containerName);
                        rejected = true;
                        continue;
                    }

                    if (assets.Contains(go)) continue;
                    ProtectedCheck assetCheck = BodyMeshDetector.Check(go, null);
                    if (assetCheck.IsProtected && !ConfirmProtected(go, assetCheck, containerName))
                    {
                        messages.Add("已取消添加「" + go.name + "」");
                        rejected = true;
                        continue;
                    }

                    assets.Add(go);
                    continue;
                }

                // ---------- 场景对象
                if (go == _avatar.gameObject || AvatarLocator.FromGameObject(go) != _avatar)
                {
                    messages.Add("「" + go.name + "」不在当前角色「" + _avatar.gameObject.name + "」下，已拒绝");
                    rejected = true;
                    continue;
                }

                if (root != null && (go == root || go.transform.IsChildOf(root.transform)))
                {
                    Transform parent = go.transform.parent;
                    if (targetDefault != null && parent == targetDefault.transform)
                    {
                        messages.Add("「" + go.name + "」已在「" + containerName + "」中");
                        continue;
                    }

                    if (otherDefault == null || parent != otherDefault.transform)
                    {
                        messages.Add("「" + go.name + "」已在外观设置中（槽位或结构节点），请在主窗口「当前外观」中管理");
                        rejected = true;
                        continue;
                    }
                }

                ProtectedCheck check = BodyMeshDetector.Check(go, _avatar);
                if (check.Kind == ProtectedKind.Bone)
                {
                    string text = BodyMeshDetector.BuildWarningText(go, check);
                    EditorUtility.DisplayDialog("不能移动骨骼", text, "确定");
                    _lastProtectedMessage = "已拒绝：" + text;
                    rejected = true;
                    continue;
                }

                if (check.Kind == ProtectedKind.BodyOrFace && !ConfirmProtected(go, check, containerName))
                {
                    messages.Add("已取消添加「" + go.name + "」");
                    rejected = true;
                    continue;
                }

                bool related = false;
                foreach (GameObject s in sceneObjects)
                {
                    if (go.transform.IsChildOf(s.transform) || s.transform.IsChildOf(go.transform))
                    {
                        related = true;
                        break;
                    }
                }

                if (related)
                {
                    messages.Add("「" + go.name + "」与其它拖入物体存在父子关系，已跳过");
                    continue;
                }

                sceneObjects.Add(go);
            }

            if (sceneObjects.Count == 0 && assets.Count == 0)
            {
                SetMessage(messages.Count > 0 ? string.Join("\n", messages) : "没有可添加的物体",
                    rejected ? MessageType.Warning : MessageType.Info);
                _dirty = true;
                return;
            }

            // 1. 动画路径引用检查（移动前）。
            if (sceneObjects.Count > 0)
            {
                List<string> hits = AnimationPathScanner.FindReferences(_avatar, sceneObjects, 20);
                if (hits.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("以下动画引用了将被移动的物体，移动后这些动画的路径会失效：");
                    foreach (string h in hits) sb.AppendLine(h);
                    if (hits.Count >= 20) sb.AppendLine("……（仅显示前 20 条）");
                    sb.AppendLine();
                    sb.Append("是否继续？");
                    if (!EditorUtility.DisplayDialog("动画引用警告", sb.ToString(), "继续", "取消"))
                    {
                        SetMessage("已取消", MessageType.Info);
                        return;
                    }
                }
            }

            // 2. Prefab 实例内部对象需先解包。
            bool needUnpack = false;
            foreach (GameObject go in sceneObjects)
            {
                if (!AppearanceStructure.CanReparent(go))
                {
                    needUnpack = true;
                    break;
                }
            }

            if (needUnpack)
            {
                GameObject outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(_avatar.gameObject);
                string prefabName = outermost != null ? outermost.name : _avatar.gameObject.name;
                if (!EditorUtility.DisplayDialog("需要解包 Prefab",
                        "所选物体属于 Prefab「" + prefabName + "」内部，Unity 不允许直接移动。\n" +
                        "将解包角色 Prefab（可能多层，例如 Prefab Variant 会逐层解包），解包可撤销。\n是否解包并继续？",
                        "解包并继续", "取消"))
                {
                    SetMessage("已取消", MessageType.Info);
                    return;
                }
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            List<string> logs = new List<string>();
            List<string> warnings = new List<string>();
            try
            {
                if (needUnpack)
                {
                    const int maxUnpack = 8;
                    foreach (GameObject go in sceneObjects)
                    {
                        int attempts = 0;
                        while (!AppearanceStructure.CanReparent(go) && attempts < maxUnpack)
                        {
                            GameObject prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                            if (prefabRoot == null) break;
                            PrefabUtility.UnpackPrefabInstance(prefabRoot, PrefabUnpackMode.OutermostRoot,
                                InteractionMode.UserAction);
                            logs.Add("已解包 Prefab「" + prefabRoot.name + "」");
                            attempts++;
                        }
                    }
                }

                foreach (GameObject go in sceneObjects)
                {
                    if (!AppearanceStructure.CanReparent(go))
                    {
                        throw new InvalidOperationException("「" + go.name + "」解包后仍属于 Prefab 实例内部，无法移动");
                    }
                }

                bool createdAppearance = AppearanceStructure.FindAppearanceRoot(_avatar) == null;
                GameObject appearance = AppearanceStructure.EnsureAppearance(_avatar, logs);
                if (appearance == null) throw new InvalidOperationException("无法创建外观设置");
                AppearanceStructure.EnsureDefaults(appearance, logs);
                GameObject def = FindDefaultItemFor(appearance, category);
                if (def == null) throw new InvalidOperationException("找不到「" + containerName + "」");

                foreach (GameObject go in sceneObjects)
                {
                    Undo.SetTransformParent(go.transform, def.transform, UndoName);
                    logs.Add("「" + go.name + "」→ " + containerName);
                }

                foreach (GameObject asset in assets)
                {
                    GameObject instance = PrefabUtility.InstantiatePrefab(asset, def.transform) as GameObject;
                    if (instance == null)
                    {
                        instance = UnityEngine.Object.Instantiate(asset, def.transform);
                        if (instance != null) instance.name = asset.name;
                    }

                    if (instance == null)
                    {
                        warnings.Add("「" + asset.name + "」实例化失败");
                        continue;
                    }

                    Undo.RegisterCreatedObjectUndo(instance, UndoName);
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    if (!instance.activeSelf)
                    {
                        Undo.RecordObject(instance, UndoName);
                        instance.SetActive(true);
                    }

                    logs.Add("实例化「" + instance.name + "」到 " + containerName);
                    BindingHelper.Bind(instance, category, _avatar, logs, warnings);
                    Undo.SetCurrentGroupName(UndoName);
                }

                if (createdAppearance)
                {
                    // 本次新建的外观设置：与快速应用相同的空槽清理（每类保留 1 个）。
                    ApplyPlanner.CleanupEmptySlots(appearance, logs, warnings);
                }

                AppearanceStructure.RefreshReferences(appearance, _avatar.transform, logs);
                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(group);

                StringBuilder result = new StringBuilder();
                result.Append("完成：已添加 ").Append(sceneObjects.Count + assets.Count).Append(" 个物体到「")
                    .Append(containerName).Append("」（可 Ctrl+Z 撤销）");
                foreach (string m in messages) result.Append("\n").Append(m);
                foreach (string w in warnings) result.Append("\n警告：").Append(w);
                SetMessage(result.ToString(), warnings.Count > 0 || rejected ? MessageType.Warning : MessageType.Info);

                StringBuilder log = new StringBuilder("[快速改模] 设置默认衣服和头发");
                foreach (string l in logs) log.Append("\n  ").Append(l);
                foreach (string w in warnings) log.Append("\n  警告：").Append(w);
                Debug.Log(log.ToString());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                SetMessage("执行失败，已回滚：" + e.Message, MessageType.Error);
            }

            _dirty = true;
            Repaint();
        }

        /// <summary>把默认项中的物体移回角色根（保持世界位姿），一个 Undo 组。</summary>
        private void MoveBackToRoot(GameObject go)
        {
            if (this == null || _avatar == null || go == null) return;
            if (!AppearanceStructure.CanReparent(go))
            {
                SetMessage("「" + go.name + "」属于 Prefab 实例内部，无法移动", MessageType.Warning);
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(MoveBackUndoName);
            try
            {
                Undo.SetTransformParent(go.transform, _avatar.transform, MoveBackUndoName);
                GameObject root = AppearanceStructure.FindAppearanceRoot(_avatar);
                if (root != null) AppearanceStructure.RefreshReferences(root, _avatar.transform, null);
                EditorSceneManager.MarkSceneDirty(_avatar.gameObject.scene);
                Undo.SetCurrentGroupName(MoveBackUndoName);
                Undo.CollapseUndoOperations(group);
                _lastProtectedMessage = string.Empty;
                SetMessage("已将「" + go.name + "」移回角色根（可 Ctrl+Z 撤销）", MessageType.Info);
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

        private void SetMessage(string message, MessageType type)
        {
            _message = message;
            _messageType = type;
            Repaint();
        }
    }

    /// <summary>扫描角色相关 AnimatorController 中的动画剪辑，找出引用了指定物体（或其子物体）路径的绑定。</summary>
    public static class AnimationPathScanner
    {
        private struct ControllerSource
        {
            public RuntimeAnimatorController Controller;
            public Transform PathBase;
            public string Label;
        }

        public static List<string> FindReferences(VRCAvatarDescriptor avatar, List<GameObject> targets, int maxCount)
        {
            List<string> hits = new List<string>();
            if (avatar == null || targets == null || targets.Count == 0) return hits;

            List<ControllerSource> sources = new List<ControllerSource>();
            AddLayers(sources, avatar.baseAnimationLayers, avatar.transform, "Base");
            AddLayers(sources, avatar.specialAnimationLayers, avatar.transform, "Special");
            foreach (ModularAvatarMergeAnimator ma in avatar.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (ma == null || ma.animator == null) continue;
                Transform pathBase = avatar.transform;
                if (ma.pathMode == MergeAnimatorPathMode.Relative)
                {
                    GameObject rel = null;
                    try
                    {
                        rel = ma.relativePathRoot != null ? ma.relativePathRoot.Get(ma) : null;
                    }
                    catch (Exception)
                    {
                        rel = null;
                    }

                    pathBase = rel != null ? rel.transform : ma.transform;
                }

                sources.Add(new ControllerSource
                {
                    Controller = ma.animator, PathBase = pathBase, Label = "MA Merge Animator(" + ma.gameObject.name + ")",
                });
            }

            HashSet<string> reported = new HashSet<string>();
            foreach (ControllerSource src in sources)
            {
                List<string> paths = new List<string>();
                foreach (GameObject t in targets)
                {
                    if (t == null || src.PathBase == null) continue;
                    if (!t.transform.IsChildOf(src.PathBase) || t.transform == src.PathBase) continue;
                    paths.Add(AnimationUtility.CalculateTransformPath(t.transform, src.PathBase));
                }

                if (paths.Count == 0) continue;

                foreach (AnimationClip clip in CollectClips(src.Controller))
                {
                    if (clip == null) continue;
                    List<EditorCurveBinding> bindings = new List<EditorCurveBinding>();
                    bindings.AddRange(AnimationUtility.GetCurveBindings(clip));
                    bindings.AddRange(AnimationUtility.GetObjectReferenceCurveBindings(clip));
                    foreach (EditorCurveBinding b in bindings)
                    {
                        string bp = b.path ?? string.Empty;
                        foreach (string p in paths)
                        {
                            if (bp == p || bp.StartsWith(p + "/", StringComparison.Ordinal))
                            {
                                string line = src.Label + " / " + clip.name + "：" + bp;
                                if (reported.Add(line)) hits.Add(line);
                                if (hits.Count >= maxCount) return hits;
                                break;
                            }
                        }
                    }
                }
            }

            return hits;
        }

        private static void AddLayers(List<ControllerSource> sources, VRCAvatarDescriptor.CustomAnimLayer[] layers,
            Transform avatarRoot, string label)
        {
            if (layers == null) return;
            foreach (VRCAvatarDescriptor.CustomAnimLayer layer in layers)
            {
                if (layer.animatorController == null) continue;
                sources.Add(new ControllerSource
                {
                    Controller = layer.animatorController, PathBase = avatarRoot, Label = label + "(" + layer.type + ")",
                });
            }
        }

        /// <summary>收集控制器的全部剪辑（含 BlendTree 子剪辑与子状态机）。</summary>
        public static List<AnimationClip> CollectClips(RuntimeAnimatorController controller)
        {
            List<AnimationClip> result = new List<AnimationClip>();
            HashSet<AnimationClip> set = new HashSet<AnimationClip>();
            if (controller == null) return result;

            AnimatorController ac = controller as AnimatorController;
            if (ac != null)
            {
                foreach (AnimatorControllerLayer layer in ac.layers)
                {
                    if (layer == null) continue;
                    CollectFromStateMachine(layer.stateMachine, set, result, new HashSet<AnimatorStateMachine>());
                }
            }

            AnimationClip[] all = controller.animationClips;
            if (all != null)
            {
                foreach (AnimationClip clip in all)
                {
                    if (clip != null && set.Add(clip)) result.Add(clip);
                }
            }

            return result;
        }

        private static void CollectFromStateMachine(AnimatorStateMachine sm, HashSet<AnimationClip> set,
            List<AnimationClip> result, HashSet<AnimatorStateMachine> visited)
        {
            if (sm == null || !visited.Add(sm)) return;
            foreach (ChildAnimatorState cs in sm.states)
            {
                if (cs.state == null) continue;
                CollectFromMotion(cs.state.motion, set, result, new HashSet<BlendTree>());
            }

            foreach (ChildAnimatorStateMachine child in sm.stateMachines)
            {
                CollectFromStateMachine(child.stateMachine, set, result, visited);
            }
        }

        private static void CollectFromMotion(Motion motion, HashSet<AnimationClip> set, List<AnimationClip> result,
            HashSet<BlendTree> visited)
        {
            if (motion == null) return;
            AnimationClip clip = motion as AnimationClip;
            if (clip != null)
            {
                if (set.Add(clip)) result.Add(clip);
                return;
            }

            BlendTree tree = motion as BlendTree;
            if (tree == null || !visited.Add(tree)) return;
            foreach (ChildMotion child in tree.children)
            {
                CollectFromMotion(child.motion, set, result, visited);
            }
        }
    }
}
