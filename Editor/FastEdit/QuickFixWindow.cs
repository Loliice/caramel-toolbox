using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Milfy.QuickOutfit.Editor
{
    /// <summary>
    /// 快速修复胸/脚穿模：
    /// · 自动检测当前场景角色；读取「外观设置-服装切换」下的服装列表（默认服装 + 服装_N）。
    /// · 每项右侧「预览」按钮：复用快速改模的服装预览（临时勾上内容 Toggle 的反转条件）。
    /// · 每项下方「形态键调整」区（默认收起）：展开后列出用户在「指定胸脚形态键」弹窗里选定的身体形态键，
    ///   每条一个滑条（临时改身体权重预览穿模，不进 Undo）+「锁定」按钮。
    /// · 点「锁定」：在该服装节点（服装_N / 默认服装）下加 MA ShapeChanger（Set 模式，身体形态键=滑条值）。
    ///   未点锁定的形态键不添加。
    /// </summary>
    public class QuickFixWindow : EditorWindow
    {
        private QuickFixSession _session = new QuickFixSession();
        private Vector2 _scroll;
        private string _status = "请选择或拖入角色。";
        private MessageType _statusType = MessageType.None;

        [MenuItem("焦糖工具箱/快速修复胸、脚穿模")]
        public static void ShowWindow()
        {
            QuickFixWindow window = GetWindow<QuickFixWindow>("快速修复胸、脚穿模");
            window.minSize = new Vector2(460f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            QuickFixPreviewController.Changed += OnPreviewChanged;
            PreviewController.Changed += OnPreviewChanged;
            AutoDetect();
        }

        private void OnDisable()
        {
            QuickFixPreviewController.Changed -= OnPreviewChanged;
            PreviewController.Changed -= OnPreviewChanged;
            QuickFixPreviewController.RestoreAll();
            PreviewController.ExitAll(null);
        }

        private void OnSelectionChange()
        {
            // 不强制刷新，避免打断操作；仅在点「使用当前选中」时应用。
        }

        private void OnPreviewChanged()
        {
            // 预览状态变化时同步形态键区展开：预览中则展开，关闭预览则同步收起。
            // 覆盖所有途径：再次点击、切到别的槽、运行/保存/撤销等自动退出。
            foreach (FixClothRow row in _session.ClothRows)
            {
                if (row == null || row.SlotObject == null) continue;
                row.Expanded = IsRowPreviewing(row);
            }

            Repaint();
        }

        /// <summary>某服装行是否处于预览态（默认服装=同类没有其它槽在预览，即展示默认态）。</summary>
        private bool IsRowPreviewing(FixClothRow row)
        {
            if (row == null || row.SlotObject == null) return false;
            if (row.IsDefault)
            {
                return !PreviewController.IsCategoryPreviewing(AssetCategory.Clothes);
            }

            return PreviewController.IsSlotPreviewing(row.SlotObject);
        }

        // ================================================================== 角色

        private void AutoDetect()
        {
            string notice;
            VRCAvatarDescriptor avatar = AvatarLocator.AutoDetect(out notice);
            if (avatar != null)
            {
                SetAvatar(avatar);
                if (!string.IsNullOrEmpty(notice)) _status = notice;
            }
            else
            {
                _session.Avatar = null;
                _status = "场景中未检测到角色，请在层级中选中角色后点「使用当前选中」。";
                _statusType = MessageType.Warning;
            }

            Repaint();
        }

        private void SetAvatar(VRCAvatarDescriptor avatar)
        {
            if (_session.Avatar != null && _session.Avatar != avatar)
            {
                PreviewController.ExitAll("切换角色");
                QuickFixPreviewController.RestoreAll();
            }

            _session.Avatar = avatar;

            // 加载该角色上次指定的形态键（按名字重新匹配身体 mesh）。
            if (avatar != null)
            {
                GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
                List<FixShape> saved = BodyShapeLibrary.LoadDesignated(avatar, root);
                if (saved.Count > 0)
                {
                    _session.DesignatedShapes = saved;
                    _status = "已恢复上次指定的 " + saved.Count + " 个形态键。";
                    _statusType = MessageType.Info;
                }
                else
                {
                    _session.DesignatedShapes.Clear();
                }
            }
            else
            {
                _session.DesignatedShapes.Clear();
            }

            BuildClothRows();
        }

        private void BuildClothRows()
        {
            _session.ClothRows.Clear();
            VRCAvatarDescriptor avatar = _session.Avatar;
            if (avatar == null) return;

            GameObject root = AppearanceStructure.FindAppearanceRoot(avatar);
            if (root == null) return;

            AppearanceInfo info = AppearanceStructure.Analyze(root, false);
            CategoryInfo ci = info.Get(AssetCategory.Clothes);
            if (ci == null) return;

            // 默认服装在前，其后依次服装槽。
            if (ci.DefaultItem != null)
            {
                _session.ClothRows.Add(new FixClothRow
                {
                    SlotObject = ci.DefaultItem,
                    Name = ci.DefaultItem.name,
                    IsDefault = true,
                });
            }

            foreach (SlotInfo slot in ci.Slots)
            {
                if (slot.GameObject == null) continue;
                _session.ClothRows.Add(new FixClothRow
                {
                    SlotObject = slot.GameObject,
                    Name = slot.Name,
                    IsDefault = false,
                });
            }

            // 若已指定形态键，为每行补上调整条目。
            if (_session.DesignatedShapes.Count > 0)
            {
                foreach (FixClothRow row in _session.ClothRows)
                {
                    if (row.Shapes.Count == 0) FillRowShapes(row);
                }
            }
        }

        private void FillRowShapes(FixClothRow row)
        {
            row.Shapes.Clear();

            // 读取该服装节点上现有的 MA ShapeChanger，用于还原已锁定形态键的值与状态。
            ModularAvatarShapeChanger changer = row.SlotObject != null
                ? row.SlotObject.GetComponent<ModularAvatarShapeChanger>()
                : null;

            foreach (FixShape d in _session.DesignatedShapes)
            {
                // 只反映之前指定过的形态键；从 changer 中找同名同身体 mesh 的已锁定项，还原其值。
                // changer 里若有额外的、未指定过的形态键，这里不会遍历到，因此不显示。
                float value = 0f;
                bool locked = false;
                if (changer != null && changer.Shapes != null)
                {
                    SkinnedMeshRenderer bodySmr = d.BodyMesh != null ? d.BodyMesh.GetComponent<SkinnedMeshRenderer>() : null;
                    string bodyPath = bodySmr != null ? new AvatarObjectReference(bodySmr.gameObject).referencePath : string.Empty;
                    foreach (ChangedShape cs in changer.Shapes)
                    {
                        if (cs.Object != null && cs.Object.referencePath == bodyPath && cs.ShapeName == d.Name)
                        {
                            value = cs.Value;
                            locked = true;
                            break;
                        }
                    }
                }

                row.Shapes.Add(new FixShape
                {
                    Name = d.Name,
                    BodyMesh = d.BodyMesh,
                    Recommended = d.Recommended,
                    Value = value,
                    Locked = locked,
                });
            }
        }

        // ================================================================== 指定形态键

        private void OpenPicker()
        {
            if (_session.Avatar == null)
            {
                _status = "请先选择角色。";
                _statusType = MessageType.Warning;
                Repaint();
                return;
            }

            GameObject root = AppearanceStructure.FindAppearanceRoot(_session.Avatar);
            Dictionary<string, GameObject> bodyShapes = BodyShapeLibrary.Collect(_session.Avatar, root);
            if (bodyShapes.Count == 0)
            {
                _status = "该角色身体上没有可用的形态键。";
                _statusType = MessageType.Warning;
                Repaint();
                return;
            }

            List<FixShape> source = BodyShapeLibrary.ToFixShapes(bodyShapes);
            ChestHeelsPickerWindow.Open(source, OnDesignated);
        }

        private void OnDesignated(List<FixShape> selected)
        {
            _session.DesignatedShapes = selected;
            // 为每行填充调整条目。
            foreach (FixClothRow row in _session.ClothRows)
            {
                FillRowShapes(row);
                row.Expanded = false;
            }

            // 持久化保存（下次打开同一角色自动恢复）。
            BodyShapeLibrary.SaveDesignated(_session.Avatar, selected);

            QuickFixPreviewController.RestoreAll();
            _status = "已指定 " + selected.Count + " 个胸/脚形态键（已记录）。";
            _statusType = MessageType.Info;
            Repaint();
        }

        // ================================================================== GUI

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            DrawTitle();
            DrawAvatarBar();

            if (_session.Avatar == null)
            {
                EditorGUILayout.HelpBox("未选择角色。", MessageType.Warning);
                return;
            }

            if (_session.DesignatedShapes.Count == 0)
            {
                EditorGUILayout.HelpBox("请先点上方「指定胸脚形态键」，选择要处理的 Breast / heels 等形态键。", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("服装列表（" + _session.ClothRows.Count + "）", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _session.ClothRows.Count; i++)
            {
                DrawClothRow(_session.ClothRows[i]);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>统一标题栏（焦糖工具箱 · 快速修复胸、脚穿模）。</summary>
        private void DrawTitle()
        {
            EditorGUILayout.LabelField(
                "<size=18><color=#E8A24A>焦糖工具箱 · 快速修复胸、脚穿模</color></size>",
                new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.MiddleCenter },
                GUILayout.ExpandWidth(true));
        }

        private void DrawAvatarBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("当前角色", GUILayout.Width(56f));
                _session.Avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(_session.Avatar, typeof(VRCAvatarDescriptor), true);
                if (GUILayout.Button("使用当前选中", GUILayout.Width(96f), GUILayout.Height(22f)))
                {
                    VRCAvatarDescriptor sel = AvatarLocator.FromSelection();
                    if (sel != null)
                    {
                        SetAvatar(sel);
                        _status = "已切换到「" + sel.gameObject.name + "」。";
                        _statusType = MessageType.Info;
                    }
                    else
                    {
                        _status = "层级中未选中角色。";
                        _statusType = MessageType.Warning;
                    }

                    Repaint();
                }

                if (GUILayout.Button("指定胸脚形态键", GUILayout.Width(120f), GUILayout.Height(22f)))
                {
                    OpenPicker();
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                Color prev = GUI.contentColor;
                if (_statusType == MessageType.Error) GUI.contentColor = new Color(0.86f, 0.33f, 0.28f);
                else if (_statusType == MessageType.Warning) GUI.contentColor = new Color(0.90f, 0.70f, 0.20f);
                else if (_statusType == MessageType.Info) GUI.contentColor = new Color(0.30f, 0.78f, 0.42f);
                EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
                GUI.contentColor = prev;
            }
        }

        private void DrawClothRow(FixClothRow row)
        {
            if (row == null || row.SlotObject == null) return;

            EditorGUILayout.Space(3f);
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                // 名称（放宽到 140，减少长名/「（默认）」被截断的情况）
                string defaultTag = row.IsDefault ? "（默认）" : string.Empty;
                EditorGUILayout.LabelField(row.Name + defaultTag, GUILayout.Width(140f));

                GUILayout.FlexibleSpace();

                // 预览按钮（复用快速改模的服装预览）——也是形态键区的唯一展开/收起触发。
                bool previewing = PreviewController.IsSlotPreviewing(row.SlotObject);
                Color prev = GUI.backgroundColor;
                if (previewing) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 1f);
                if (GUILayout.Button(previewing ? "● 预览" : "预览", GUILayout.Width(70f), GUILayout.Height(22f)))
                {
                    TogglePreview(row);
                }

                GUI.backgroundColor = prev;
            }

            // 默认服装行：无可退出预览时给出灰显提示，避免「点了没反应」。
            if (row.IsDefault && !PreviewController.IsCategoryPreviewing(AssetCategory.Clothes))
            {
                EditorGUILayout.LabelField("    当前已是默认服装展示状态", EditorStyles.miniLabel);
            }

            if (row.Expanded)
            {
                DrawShapeAdjust(row);
            }
        }

        private void DrawShapeAdjust(FixClothRow row)
        {
            if (row.Shapes.Count == 0)
            {
                EditorGUILayout.LabelField("    （无形态键）", EditorStyles.miniLabel);
                return;
            }

            for (int i = 0; i < row.Shapes.Count; i++)
            {
                FixShape shape = row.Shapes[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(shape.Name, GUILayout.Width(130f));
                    shape.Value = EditorGUILayout.Slider(shape.Value, QuickFixConst.ShapeMin, QuickFixConst.ShapeMax);

                    // 锁定按钮
                    Color prev = GUI.backgroundColor;
                    if (shape.Locked) GUI.backgroundColor = new Color(0.4f, 0.8f, 0.5f, 1f);
                    string lockLabel = shape.Locked ? "● 已锁定" : "锁定";
                    if (GUILayout.Button(lockLabel, GUILayout.Width(76f), GUILayout.Height(20f)))
                    {
                        LockShape(row, shape);
                    }

                    GUI.backgroundColor = prev;
                }

                // 滑条拖动时实时预览身体形态键（不进 Undo）。
                if (shape.Value >= 0f)
                {
                    QuickFixPreviewController.SetValue(shape);
                }
            }
        }

        // ================================================================== 预览 / 锁定

        private void TogglePreview(FixClothRow row)
        {
            string message;
            bool ok;

            if (row.IsDefault)
            {
                // 默认服装没有内容 Toggle：预览 = 退出同类其它槽的预览，回到默认状态。
                if (PreviewController.IsCategoryPreviewing(AssetCategory.Clothes))
                {
                    PreviewController.ExitCategory(AssetCategory.Clothes);
                    ok = true;
                    message = "已回到默认服装预览";
                }
                else
                {
                    ok = false;
                    message = "当前没有可退出的服装预览";
                }
            }
            else
            {
                ok = PreviewController.ToggleSlot(_session.Avatar, row.SlotObject, out message);
            }

            _status = message;
            _statusType = ok ? MessageType.Info : MessageType.Warning;
            // 进入该项预览时，把当前滑条值应用到身体，便于看到穿模效果。
            // 形态键区的展开/收起由 OnPreviewChanged 根据预览状态统一同步。
            if (ok)
            {
                QuickFixPreviewController.ApplyShapes(row.Shapes);
            }

            Repaint();
        }

        private void LockShape(FixClothRow row, FixShape shape)
        {
            if (_session.Avatar == null || row == null || row.SlotObject == null || shape == null) return;

            // 锁定/解锁是带 Undo 的写入：先暂停身体预览（恢复原权重），避免把预览值写进 Undo。
            QuickFixPreviewController.Suspend();

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(QuickFixConst.UndoName);
            try
            {
                if (shape.Locked)
                {
                    RemoveLockedShape(row, shape);
                }
                else
                {
                    AddLockedShape(row, shape);
                }

                EditorSceneManager.MarkSceneDirty(_session.Avatar.gameObject.scene);
                Undo.SetCurrentGroupName(QuickFixConst.UndoName);
                Undo.CollapseUndoOperations(group);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                _status = "操作失败，已回滚：" + e.Message;
                _statusType = MessageType.Error;
            }

            Repaint();
        }

        /// <summary>添加锁定：在该服装节点加 MA ShapeChanger（Set，身体形态键=滑条值）。</summary>
        private void AddLockedShape(FixClothRow row, FixShape shape)
        {
            ModularAvatarShapeChanger changer = row.SlotObject.GetComponent<ModularAvatarShapeChanger>();
            bool created = false;
            if (changer == null)
            {
                changer = Undo.AddComponent<ModularAvatarShapeChanger>(row.SlotObject);
                created = true;
            }

            Undo.RecordObject(changer, QuickFixConst.UndoName);

            SkinnedMeshRenderer bodySmr = shape.BodyMesh != null ? shape.BodyMesh.GetComponent<SkinnedMeshRenderer>() : null;
            if (bodySmr == null)
            {
                throw new System.InvalidOperationException("身体网格「" + shape.Name + "」丢失，无法锁定");
            }

            AvatarObjectReference bodyRef = new AvatarObjectReference(bodySmr.gameObject);
            string bodyPath = bodyRef.referencePath;

            List<ChangedShape> shapes = CollectChangedShapes(changer, bodyPath, shape.Name);
            shapes.Add(new ChangedShape
            {
                Object = bodyRef,
                ShapeName = shape.Name,
                ChangeType = ShapeChangeType.Set,
                Value = shape.Value,
            });

            changer.Shapes = shapes;
            PrefabUtility.RecordPrefabInstancePropertyModifications(changer);
            EditorUtility.SetDirty(changer);

            shape.Locked = true;
            _status = (created ? "新增" : "更新") + "锁定：" + shape.Name + " → " + shape.Value.ToString("F0") +
                      "（" + row.Name + "，MA ShapeChanger / Set）";
            _statusType = MessageType.Info;
        }

        /// <summary>解锁：从该服装节点的 ShapeChanger 移除该形态键；组件空了则删除组件。</summary>
        private void RemoveLockedShape(FixClothRow row, FixShape shape)
        {
            ModularAvatarShapeChanger changer = row.SlotObject.GetComponent<ModularAvatarShapeChanger>();
            if (changer == null)
            {
                // 没有组件却标记为已锁定：仅复位标记。
                shape.Locked = false;
                _status = "「" + shape.Name + "」已解锁";
                return;
            }

            Undo.RecordObject(changer, QuickFixConst.UndoName);

            SkinnedMeshRenderer bodySmr = shape.BodyMesh != null ? shape.BodyMesh.GetComponent<SkinnedMeshRenderer>() : null;
            string bodyPath = bodySmr != null ? new AvatarObjectReference(bodySmr.gameObject).referencePath : string.Empty;

            List<ChangedShape> shapes = CollectChangedShapes(changer, bodyPath, shape.Name);
            bool removed = shapes.Count < (changer.Shapes != null ? changer.Shapes.Count : 0);

            if (shapes.Count == 0)
            {
                // 空了：删除整个组件。
                Undo.DestroyObjectImmediate(changer);
            }
            else
            {
                changer.Shapes = shapes;
                PrefabUtility.RecordPrefabInstancePropertyModifications(changer);
                EditorUtility.SetDirty(changer);
            }

            shape.Locked = false;
            _status = removed
                ? "已删除锁定：" + shape.Name + "（" + row.Name + "）"
                : "「" + shape.Name + "」已解锁";
            _statusType = MessageType.Info;
        }

        /// <summary>读取现有 ChangedShape，排除目标 (身体 mesh, 形态键) 的旧项。</summary>
        private List<ChangedShape> CollectChangedShapes(ModularAvatarShapeChanger changer, string bodyPath, string shapeName)
        {
            List<ChangedShape> shapes = new List<ChangedShape>();
            if (changer.Shapes == null) return shapes;
            foreach (ChangedShape cs in changer.Shapes)
            {
                if (cs.Object != null && cs.Object.referencePath == bodyPath && cs.ShapeName == shapeName)
                    continue;
                shapes.Add(cs);
            }

            return shapes;
        }
    }
}
