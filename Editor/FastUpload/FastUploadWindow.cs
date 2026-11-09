// FastUploadWindow.cs
// 焦糖工具箱 · 快速上传 —— 跳过 SDK 面板的校验/Alerts 步骤，直接填名称与封面并上传。
// 放置路径：Assets/CaramelToolbox/Editor/FastUpload/FastUploadWindow.cs
//
// 上传走 VRChat 官方对外接口（Packages/com.vrchat.base .../Public SDK API）：
//   VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>() 取 Builder，
//   IVRCSdkAvatarBuilderApi.Build() 只负责构建，上传部分用 VRCApi 自己拼。
// 该接口要求 SDK 控制面板处于打开状态（Builder 内部的 _builder 不能为空），因此上传前会自动打开面板。
//
// 为什么不直接用 BuildAndUpload：它会两次调用 VRCCopyrightAgreement.CheckCopyrightAgreement，
// 弹出「Copyright ownership agreement」确认框。本插件改为 Build + VRCApi 手动上传，全程不经过该校验。
// 想恢复 SDK 原生行为（含协议框）时，取消勾选界面上的「跳过版权协议确认弹窗」即可。
//
// 行为：
//   1. 角色身上已有 PipelineManager.blueprintId → 调 VRCApi.GetAvatar 读取名称与封面，两者都可手动改；
//   2. 没有 blueprintId → 先用 VRCApi.CreateAvatarRecord 建号，名称必须手动输入，封面默认用视口截图；
//   3. 不改动封面时不调 UpdateAvatarImage，线上封面保持不变；
//   4. 不显示任何 Alerts / 校验结果；成功与失败都用仿 SDK 的弹窗呈现。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.Api;

namespace CaramelToolbox
{
    /// <summary>快速上传窗口：读取 Builder 信息 → 确认名称与封面 → 一键上传。</summary>
    public class FastUploadWindow : EditorWindow
    {
        private const string MenuPath = "焦糖工具箱/快速上传";
        private const int CoverWidth = 800;
        private const int CoverHeight = 600;
        private const string SkipAgreementPrefKey = "CaramelToolbox.FastUpload.SkipCopyrightAgreement";

        // ---- 角色 ----
        private readonly List<VRCAvatarDescriptor> _avatars = new List<VRCAvatarDescriptor>();
        private string[] _avatarNames = Array.Empty<string>();
        private int _avatarIndex;
        private VRCAvatarDescriptor _avatar;

        // ---- 线上信息 ----
        private string _blueprintId = string.Empty;
        private bool _hasRemote;
        private VRCAvatar _remote;
        private Texture2D _remoteCover;

        // ---- 可编辑内容 ----
        private string _nameInput = string.Empty;
        private Texture2D _coverPreview;
        private string _thumbnailPath;
        private bool _coverChanged;

        // ---- 蓝图 ID 手动编辑 ----
        private bool _editingBlueprintId;
        private string _blueprintIdInput = string.Empty;

        /// <summary>勾选时不走 SDK 的版权协议确认弹窗（默认开）。</summary>
        private bool _skipCopyrightAgreement = true;

        // ---- 状态 ----
        private string _status = string.Empty;
        private MessageType _statusType = MessageType.None;
        private bool _busy;
        private bool _infoLoading;
        private CancellationTokenSource _cts;

        // ---- 进度条 ----
        private float _progress;
        private bool _progressVisible;

        // ---- 封面预览宽度缓存（用于按 4:3 联动高度） ----
        private float _lastCoverWidth;

        // ---- 自定义截图相机（对应 SDK 的 Use Custom Camera） ----
        private Camera _customCamera;

        // ---- 自定义相机持久化键（按场景路径 + 层级路径存 EditorPrefs） ----
        private const string CustomCameraPrefKey = "CaramelToolbox.FastUpload.CustomCameraPath";

        // ---- 上传结果（显示在窗口底部，不再弹独立窗口） ----
        private bool _hasResult;
        private bool _resultSuccess;
        private string _resultAvatarId = string.Empty;
        private string _resultMessage = string.Empty;

        /// <summary>本次上传预建的模型记录 ID，构建/上传失败时要删掉。</summary>
        private string _reservedId;

        [MenuItem(MenuPath, false, 100)]
        private static void Open()
        {
            FastUploadWindow window = GetWindow<FastUploadWindow>(false, "快速上传", true);
            window.minSize = new Vector2(360f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("快速上传");
            _skipCopyrightAgreement = EditorPrefs.GetBool(SkipAgreementPrefKey, true);
            RefreshAvatarList();
            VRCAvatarDescriptor fromSelection = FromSelection();
            if (fromSelection != null)
            {
                SelectAvatar(fromSelection);
            }
            else if (_avatars.Count > 0)
            {
                SelectAvatar(_avatars[0]);
            }

            RestoreCustomCamera();

            // 停靠窗口在场景重载时不会重新 OnEnable，所以监听场景/层级变化，触发后再恢复一次。
            EditorApplication.hierarchyChanged += OnHierarchyChangedForCamera;
            EditorSceneManager.sceneOpened += OnSceneOpenedForCamera;
        }

        private void OnDisable()
        {
            if (_cts != null)
            {
                // 只取消不 Dispose：仍在跑的上传协程还会读这个 Token。
                _cts.Cancel();
            }

            EditorApplication.hierarchyChanged -= OnHierarchyChangedForCamera;
            EditorSceneManager.sceneOpened -= OnSceneOpenedForCamera;
        }

        private void OnHierarchyChangedForCamera()
        {
            // 相机引用已断（missing）时才重试，避免覆盖用户手动改的值。
            if (_customCamera == null)
            {
                RestoreCustomCamera();
                Repaint();
            }
        }

        private void OnSceneOpenedForCamera(Scene scene, OpenSceneMode mode)
        {
            RestoreCustomCamera();
            Repaint();
        }

        // --------------------------------------------------------------------
        // 界面
        // --------------------------------------------------------------------

        private void OnGUI()
        {
            CaramelUI.DrawTitle("快速上传", null);
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawAvatarPicker();
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("模型名称", EditorStyles.boldLabel);
                _nameInput = EditorGUILayout.TextField(_nameInput);
                EditorGUILayout.Space(6);
                DrawBlueprintIdArea();
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawCoverArea();
            }

            EditorGUILayout.Space(8);

            // 进度条（在上传状态字上方，仅上传中显示）。
            if (_progressVisible)
            {
                Rect progressRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, Mathf.Clamp01(_progress), "");
                EditorGUILayout.Space(4);
            }

            DrawStatusBox();
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool skip = EditorGUILayout.ToggleLeft("跳过版权协议确认弹窗", _skipCopyrightAgreement);
                if (skip != _skipCopyrightAgreement)
                {
                    _skipCopyrightAgreement = skip;
                    EditorPrefs.SetBool(SkipAgreementPrefKey, skip);
                }

                EditorGUILayout.Space(4);

                EditorGUI.BeginDisabledGroup(_busy || _infoLoading);
                if (CaramelUI.CaramelButton(_busy ? "上传中…" : "上传", 34))
                {
                    OnUploadClicked();
                }

                EditorGUI.EndDisabledGroup();

                if (_busy && _cts != null)
                {
                    EditorGUILayout.Space(4);
                    if (GUILayout.Button("取消上传", GUILayout.Height(24)))
                    {
                        _cts.Cancel();
                    }
                }
            }

            DrawUploadResult();
        }

        /// <summary>状态区：带浅色底框，状态字按类型上色。</summary>
        private void DrawStatusBox()
        {
            if (string.IsNullOrEmpty(_status)) return;

            Color bg;
            switch (_statusType)
            {
                case MessageType.Error: bg = new Color(0.86f, 0.33f, 0.28f, 0.15f); break;
                case MessageType.Warning: bg = new Color(0.90f, 0.70f, 0.20f, 0.15f); break;
                case MessageType.Info: bg = new Color(0.30f, 0.78f, 0.42f, 0.15f); break;
                default: bg = new Color(0.5f, 0.5f, 0.5f, 0.12f); break;
            }

            Rect rect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, bg);
            Color prev = GUI.contentColor;
            switch (_statusType)
            {
                case MessageType.Error: GUI.contentColor = CaramelUI.Error; break;
                case MessageType.Warning: GUI.contentColor = CaramelUI.Warning; break;
                case MessageType.Info: GUI.contentColor = CaramelUI.Success; break;
            }

            GUI.Label(new Rect(rect.x + 8, rect.y + 3, rect.width - 16, rect.height - 6), _status, EditorStyles.miniLabel);
            GUI.contentColor = prev;
        }

        /// <summary>在窗口底部显示上次上传结果（成功带跳转链接，失败带错误信息）。</summary>
        private void DrawUploadResult()
        {
            if (!_hasResult) return;

            EditorGUILayout.Space(10);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_resultSuccess)
                {
                    EditorGUILayout.LabelField("上传成功！", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("模型 ID：" + _resultAvatarId, EditorStyles.miniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("在 VRChat 网页中打开", GUILayout.Height(24)))
                        {
                            Application.OpenURL("https://vrchat.com/home/avatar/" + _resultAvatarId);
                        }

                        if (GUILayout.Button("复制链接", GUILayout.Width(80), GUILayout.Height(24)))
                        {
                            EditorGUIUtility.systemCopyBuffer =
                                "https://vrchat.com/home/avatar/" + _resultAvatarId;
                            SetStatus("已复制模型链接到剪贴板", MessageType.Info);
                        }
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("上传失败", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(_resultMessage, EditorStyles.wordWrappedLabel);
                }
            }
        }

        /// <summary>
        /// 蓝图 ID 区：默认只读显示 PipelineManager 上的蓝图 ID；
        /// 点「Detach」后变为输入框，输入 ID 读取对应模型信息，留空则作为新模型由 VRC 分配。
        /// </summary>
        private void DrawBlueprintIdArea()
        {
            EditorGUILayout.LabelField("蓝图 ID（Blueprint ID）", EditorStyles.boldLabel);

            if (_editingBlueprintId)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _blueprintIdInput = EditorGUILayout.TextField(_blueprintIdInput);

                    if (GUILayout.Button("读取", GUILayout.Width(52)))
                    {
                        ApplyBlueprintIdInput();
                    }

                    if (GUILayout.Button("取消", GUILayout.Width(52)))
                    {
                        _editingBlueprintId = false;
                        _blueprintIdInput = string.Empty;
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = false;
                    EditorGUILayout.TextField(string.IsNullOrEmpty(_blueprintId) ? "（未绑定，将作为新模型）" : _blueprintId);
                    GUI.enabled = true;

                    if (GUILayout.Button("Detach", GUILayout.Width(64)))
                    {
                        DetachBlueprint();
                    }
                }
            }
        }

        /// <summary>Detach：清空角色的蓝图 ID（等价于 SDK 面板里的 Detach (Optional)），并切换到手动输入模式。</summary>
        private void DetachBlueprint()
        {
            if (_avatar == null)
            {
                SetStatus("请先选择角色", MessageType.Warning);
                return;
            }

            PipelineManager pipeline = _avatar.GetComponent<PipelineManager>();
            if (pipeline == null)
            {
                SetStatus("角色身上没有 PipelineManager，无法 Detach", MessageType.Error);
                return;
            }

            Undo.RecordObject(pipeline, "快速上传：Detach 蓝图 ID");
            pipeline.blueprintId = string.Empty;
            EditorUtility.SetDirty(pipeline);
            PrefabUtility.RecordPrefabInstancePropertyModifications(pipeline);

            _editingBlueprintId = true;
            _blueprintIdInput = string.Empty;
            _hasRemote = false;
            _remote = default;
            _blueprintId = string.Empty;
            _nameInput = string.Empty;
            _thumbnailPath = null;
            _coverChanged = false;
            _coverPreview = null;
            _remoteCover = null;
            SetStatus("已 Detach：角色不再绑定蓝图 ID。可输入 ID 读取已有模型，或留空作为新模型上传", MessageType.Info);
        }

        /// <summary>应用手动输入的蓝图 ID：非空则读取对应模型信息，空则按新模型处理。</summary>
        private async void ApplyBlueprintIdInput()
        {
            string id = (_blueprintIdInput ?? string.Empty).Trim();
            _editingBlueprintId = false;

            if (_avatar == null)
            {
                SetStatus("请先选择角色", MessageType.Warning);
                return;
            }

            if (string.IsNullOrEmpty(id))
            {
                // 留空：作为新模型上传。
                _blueprintId = string.Empty;
                _hasRemote = false;
                _remote = default;
                _nameInput = string.Empty;
                _thumbnailPath = null;
                _coverChanged = false;
                _coverPreview = null;
                _remoteCover = null;

                PipelineManager pipeline = _avatar.GetComponent<PipelineManager>();
                if (pipeline != null)
                {
                    Undo.RecordObject(pipeline, "快速上传：清空蓝图 ID");
                    pipeline.blueprintId = string.Empty;
                    EditorUtility.SetDirty(pipeline);
                }

                SetStatus("已设为新模型：上传时由 VRChat 分配蓝图 ID，请填写名称并生成封面", MessageType.Info);
                Repaint();
                return;
            }

            // 有 ID：写入角色并读取对应模型信息。
            PipelineManager pm = _avatar.GetComponent<PipelineManager>();
            if (pm != null)
            {
                Undo.RecordObject(pm, "快速上传：设置蓝图 ID");
                pm.blueprintId = id;
                EditorUtility.SetDirty(pm);
                PrefabUtility.RecordPrefabInstancePropertyModifications(pm);
            }

            _blueprintId = id;
            _infoLoading = true;
            SetStatus("正在读取蓝图 ID " + id + " 的模型信息…", MessageType.Info);
            Repaint();

            try
            {
                VRCAvatar data = await VRCApi.GetAvatar(id, true);
                _remote = data;
                _hasRemote = true;
                _nameInput = data.Name ?? string.Empty;

                string url = !string.IsNullOrWhiteSpace(data.ThumbnailImageUrl)
                    ? data.ThumbnailImageUrl
                    : data.ImageUrl;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    Texture2D tex = await VRCApi.GetImage(url, true);
                    _remoteCover = tex;
                    _coverPreview = tex;
                }

                SetStatus(_coverPreview != null
                    ? "已读取蓝图 ID 对应的模型信息，可直接上传，也可手动修改"
                    : "已读取模型名称，但该模型线上没有封面，建议补一张",
                    _coverPreview != null ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception e)
            {
                _hasRemote = false;
                _nameInput = _avatar.gameObject.name;
                SetStatus("读取蓝图 ID " + id + " 失败：" + e.Message, MessageType.Warning);
            }
            finally
            {
                _infoLoading = false;
                Repaint();
            }
        }

        private void DrawAvatarPicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("当前角色", GUILayout.Width(60));

                if (_avatars.Count == 0)
                {
                    EditorGUILayout.LabelField("场景中没有找到角色");
                }
                else
                {
                    int picked = EditorGUILayout.Popup(_avatarIndex, _avatarNames);
                    if (picked != _avatarIndex)
                    {
                        _avatarIndex = picked;
                        SelectAvatar(_avatars[picked]);
                    }
                }

                if (GUILayout.Button(CaramelUI.UseSelectionLabel, GUILayout.Width(92)))
                {
                    VRCAvatarDescriptor selected = FromSelection();
                    if (selected == null)
                    {
                        SetStatus("选中的物体不属于任何角色（VRCAvatarDescriptor）", MessageType.Warning);
                    }
                    else
                    {
                        RefreshAvatarList();
                        SelectAvatar(selected);
                    }
                }

                if (GUILayout.Button("刷新", GUILayout.Width(44)))
                {
                    RefreshAvatarList();

                    // 重新读取当前角色的蓝图 ID 与线上名称/封面，并清掉上次的上传结果提示。
                    _hasResult = false;
                    _resultSuccess = false;
                    _resultAvatarId = string.Empty;
                    _resultMessage = string.Empty;

                    if (_avatar != null)
                    {
                        int index = _avatars.IndexOf(_avatar);
                        if (index >= 0) _avatarIndex = index;
                        LoadRemoteInfo();
                    }
                }
            }
        }

        private void DrawCoverArea()
        {
            EditorGUILayout.LabelField("封面", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            // 封面预览：铺满区块宽度，高度按 4:3（封面标准 800×600）联动，图片完整显示不变形。
            float aspect = CoverWidth / (float)CoverHeight; // 4:3
            float cachedWidth = _lastCoverWidth > 0f ? _lastCoverWidth : 200f;
            float previewHeight = Mathf.Clamp(cachedWidth / aspect, 60f, 260f);
            Rect previewRect = GUILayoutUtility.GetRect(cachedWidth, previewHeight,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(false));
            _lastCoverWidth = previewRect.width; // 下一帧用真实宽度，首帧后稳定

            if (_coverPreview != null)
            {
                GUI.DrawTexture(previewRect, _coverPreview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                GUI.Box(previewRect, "无封面", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.Space(6);

            // 截图按钮 + 自定义相机输入框（对应 SDK 的 Use Custom Camera）。
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("截取视口", GUILayout.Height(24)))
                {
                    CaptureViewportAsCover();
                }

                if (GUILayout.Button("选择图片…", GUILayout.Height(24)))
                {
                    PickCoverFile();
                }

                Camera pickedCamera = (Camera)EditorGUILayout.ObjectField(_customCamera, typeof(Camera), true,
                    GUILayout.Width(120));
                if (pickedCamera != _customCamera)
                {
                    _customCamera = pickedCamera;
                    SaveCustomCamera();
                }
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                _coverChanged ? "本次上传会更新封面" : "沿用线上封面",
                EditorStyles.miniLabel);
        }

        // --------------------------------------------------------------------
        // 角色定位
        // --------------------------------------------------------------------

        private void RefreshAvatarList()
        {
            _avatars.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (VRCAvatarDescriptor descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(false))
                    {
                        if (descriptor == null) continue;
                        if (!descriptor.gameObject.activeInHierarchy) continue;
                        if (_avatars.Contains(descriptor)) continue;
                        _avatars.Add(descriptor);
                    }
                }
            }

            _avatarNames = new string[_avatars.Count];
            for (int i = 0; i < _avatars.Count; i++)
            {
                _avatarNames[i] = _avatars[i].gameObject.name;
            }

            if (_avatar != null)
            {
                int index = _avatars.IndexOf(_avatar);
                if (index >= 0) _avatarIndex = index;
            }
            else
            {
                _avatarIndex = 0;
            }
        }

        private static VRCAvatarDescriptor FromSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null) return null;
            if (EditorUtility.IsPersistent(go)) return null;
            return go.GetComponentInParent<VRCAvatarDescriptor>(true);
        }

        private void SelectAvatar(VRCAvatarDescriptor descriptor)
        {
            _avatar = descriptor;
            int index = _avatars.IndexOf(descriptor);
            if (index >= 0) _avatarIndex = index;
            LoadRemoteInfo();
        }

        // --------------------------------------------------------------------
        // 读取 Builder（线上）信息
        // --------------------------------------------------------------------

        private async void LoadRemoteInfo()
        {
            _hasRemote = false;
            _remote = default;
            _blueprintId = string.Empty;
            _nameInput = string.Empty;
            _thumbnailPath = null;
            _coverChanged = false;
            _coverPreview = null;
            _remoteCover = null;
            _editingBlueprintId = false;
            _blueprintIdInput = string.Empty;

            if (_avatar == null)
            {
                _status = string.Empty;
                _statusType = MessageType.None;
                return;
            }

            PipelineManager pipeline = _avatar.GetComponent<PipelineManager>();
            string id = pipeline != null ? pipeline.blueprintId : null;

            if (string.IsNullOrWhiteSpace(id))
            {
                // 没有上传记录：名称由用户输入，封面默认走视口截图。
                _nameInput = string.Empty;
                SetStatus("该角色还没有上传记录：请填写名称，并点「截取视口」生成封面", MessageType.Warning);
                Repaint();
                return;
            }

            _blueprintId = id.Trim();
            _infoLoading = true;
            SetStatus("正在读取 Builder 中的模型信息…", MessageType.Info);
            Repaint();

            try
            {
                VRCAvatar data = await VRCApi.GetAvatar(_blueprintId, true);
                _remote = data;
                _hasRemote = true;
                _nameInput = data.Name ?? string.Empty;

                string url = !string.IsNullOrWhiteSpace(data.ThumbnailImageUrl)
                    ? data.ThumbnailImageUrl
                    : data.ImageUrl;

                if (!string.IsNullOrWhiteSpace(url))
                {
                    Texture2D tex = await VRCApi.GetImage(url, true);
                    _remoteCover = tex;
                    _coverPreview = tex;
                }

                SetStatus(_coverPreview != null
                    ? "已读取 Builder 中的名称与封面，可直接上传，也可手动修改"
                    : "已读取 Builder 中的名称，但该模型线上没有封面，建议补一张",
                    _coverPreview != null ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception e)
            {
                _nameInput = _avatar.gameObject.name;
                SetStatus("读取线上信息失败：" + e.Message, MessageType.Warning);
            }
            finally
            {
                _infoLoading = false;
                Repaint();
            }
        }

        // --------------------------------------------------------------------
        // 封面
        // --------------------------------------------------------------------

        /// <summary>
        /// 截图作为封面：有自定义相机则截该相机视角（对应 SDK 的 Use Custom Camera），
        /// 否则截当前场景视口。不改动角色与场景。
        /// </summary>
        private void CaptureViewportAsCover()
        {
            Camera camera;
            string source;

            if (_customCamera != null)
            {
                camera = _customCamera;
                source = "自定义相机「" + _customCamera.name + "」";
            }
            else
            {
                SceneView view = SceneView.lastActiveSceneView;
                camera = view != null ? view.camera : Camera.main;
                source = "当前视口";
            }

            Texture2D shot = RenderCamera(camera, CoverWidth, CoverHeight);
            if (shot == null)
            {
                SetStatus("截取失败：没有可用的相机或场景视图，请改用「选择图片…」", MessageType.Error);
                return;
            }

            ApplyCover(shot);
            SetStatus("已用" + source + "截图作为封面", MessageType.Info);
        }

        /// <summary>把自定义相机按「场景路径 | 层级路径」存到 EditorPrefs，重开场景后能找回。</summary>
        private void SaveCustomCamera()
        {
            if (_customCamera == null)
            {
                EditorPrefs.DeleteKey(CustomCameraPrefKey);
                Debug.Log("[快速上传] 相机已清空，删除持久化记录");
                return;
            }

            Scene scene = _customCamera.gameObject.scene;
            // 未保存的场景没有稳定路径，只能存层级路径，跨场景不保证能找回。
            string scenePath = string.IsNullOrEmpty(scene.path) ? string.Empty : scene.path;
            string hierarchyPath = GetHierarchyPath(_customCamera.transform);
            EditorPrefs.SetString(CustomCameraPrefKey, scenePath + "|" + hierarchyPath);
            Debug.Log("[快速上传] 已保存自定义相机路径：" + scenePath + " | " + hierarchyPath);
        }

        /// <summary>从 EditorPrefs 读取相机路径，在当前已加载的场景里找回对应相机。</summary>
        private void RestoreCustomCamera()
        {
            _customCamera = null;

            string saved = EditorPrefs.GetString(CustomCameraPrefKey, string.Empty);
            Debug.Log("[快速上传] 恢复自定义相机：读取到记录「" + saved + "」");
            if (string.IsNullOrEmpty(saved)) return;

            int sep = saved.IndexOf('|');
            if (sep < 0) return;

            string scenePath = saved.Substring(0, sep);
            string hierarchyPath = saved.Substring(sep + 1);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                // 场景路径为空（未保存场景）时按层级路径全局匹配，否则要求场景路径一致。
                if (!string.IsNullOrEmpty(scenePath) && scene.path != scenePath)
                {
                    Debug.Log("[快速上传] 跳过场景「" + scene.path + "」（与保存路径不一致）");
                    continue;
                }

                Transform found = FindByHierarchyPath(scene, hierarchyPath);
                if (found != null)
                {
                    _customCamera = found.GetComponent<Camera>();
                    Debug.Log("[快速上传] 恢复自定义相机" + (_customCamera != null
                        ? "成功：" + found.name
                        : "失败：找到物体但无 Camera 组件"));
                    if (_customCamera != null) return;
                }
                else
                {
                    Debug.Log("[快速上传] 场景「" + scene.path + "」中未找到层级路径 " + hierarchyPath);
                }
            }
        }

        private static string GetHierarchyPath(Transform t)
        {
            List<string> parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static Transform FindByHierarchyPath(Scene scene, string hierarchyPath)
        {
            string[] parts = hierarchyPath.Split('/');
            if (parts.Length == 0) return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;

                Transform current = root.transform;
                bool matched = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    Transform next = current.Find(parts[i]);
                    if (next == null)
                    {
                        matched = false;
                        break;
                    }

                    current = next;
                }

                if (matched) return current;
            }

            return null;
        }

        private void PickCoverFile()
        {
            string path = EditorUtility.OpenFilePanel("选择封面图片", string.Empty, "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                SetStatus("读取图片失败：" + e.Message, MessageType.Error);
                return;
            }

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(bytes))
            {
                DestroyImmediate(tex);
                SetStatus("无法解析该图片，请选择 PNG / JPG", MessageType.Error);
                return;
            }

            ApplyCover(tex);
            SetStatus("已用「" + Path.GetFileName(path) + "」作为封面", MessageType.Info);
        }

        /// <summary>应用一张新封面：统一转成 PNG 存到临时目录，交给 SDK 上传。</summary>
        private void ApplyCover(Texture2D texture)
        {
            _coverPreview = texture;
            _coverChanged = true;

            try
            {
                string dir = Path.Combine(Application.temporaryCachePath, "CaramelFastUpload");
                Directory.CreateDirectory(dir);
                _thumbnailPath = Path.Combine(dir, "cover_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                File.WriteAllBytes(_thumbnailPath, texture.EncodeToPNG());
            }
            catch (Exception e)
            {
                _thumbnailPath = null;
                SetStatus("保存封面失败：" + e.Message, MessageType.Error);
            }
        }

        private static Texture2D RenderCamera(Camera camera, int width, int height)
        {
            if (camera == null) return null;

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);

            try
            {
                RenderTexture previousTarget = camera.targetTexture;
                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = previousTarget;

                RenderTexture.active = rt;
                Texture2D result = new Texture2D(width, height, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // --------------------------------------------------------------------
        // 上传
        // --------------------------------------------------------------------

        private void OnUploadClicked()
        {
            if (_busy) return;

            if (_avatar == null)
            {
                SetStatus("请先选择要上传的角色", MessageType.Warning);
                return;
            }

            _nameInput = (_nameInput ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(_nameInput))
            {
                SetStatus("请先填写模型名称", MessageType.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_blueprintId) && string.IsNullOrWhiteSpace(_thumbnailPath))
            {
                SetStatus("首次上传必须指定封面：请点「截取视口」或「选择图片…」", MessageType.Warning);
                return;
            }

            if (!APIUser.IsLoggedIn)
            {
                ApiCredentials.Load();
            }

            if (!APIUser.IsLoggedIn)
            {
                SetStatus("尚未登录，请先在 VRChat SDK 面板登录后再上传", MessageType.Warning);
                return;
            }

            _busy = true;
            _cts = new CancellationTokenSource();
            _progressVisible = true;
            _progress = 0f;
            SetStatus("正在准备上传…", MessageType.Info);
            try
            {
                EnsureSdkPanelThenUpload();
            }
            catch (Exception e)
            {
                _busy = false;
                _progressVisible = false;
                Debug.LogException(e);
                SetStatus("启动上传失败：" + e.Message, MessageType.Error);
                Repaint();
            }
        }

        /// <summary>
        /// 官方接口要求 SDK 控制面板已打开（VRCSdkControlPanel.window 非空）。
        /// window 是在面板 CreateGUI 里赋值的，GetWindow 之后要等一次布局，所以用 delayCall 轮询。
        /// </summary>
        private void EnsureSdkPanelThenUpload()
        {
            Debug.Log("[快速上传] 准备打开 VRChat SDK 控制面板…");
            EditorWindow.GetWindow(typeof(VRCSdkControlPanel));

            int attempts = 0;
            EditorApplication.CallbackFunction waitForPanel = null;
            waitForPanel = () =>
            {
                if (this == null) return; // 窗口已关闭，停止轮询

                if (VRCSdkControlPanel.window != null)
                {
                    Debug.Log("[快速上传] SDK 面板已就绪，开始上传");
                    _ = RunUploadAsync();
                    return;
                }

                if (++attempts > 40)
                {
                    _busy = false;
                    _progressVisible = false;
                    Debug.LogWarning("[快速上传] 等待 SDK 面板超时（window 一直为空）");
                    SetStatus("无法初始化 VRChat SDK 控制面板，请手动打开 VRChat SDK > Show Control Panel 后重试",
                        MessageType.Error);
                    Repaint();
                    return;
                }

                EditorApplication.delayCall += waitForPanel;
            };

            EditorApplication.delayCall += waitForPanel;
        }

        private async Task RunUploadAsync()
        {
            GameObject target = _avatar != null ? _avatar.gameObject : null;
            PipelineManager pipeline = target != null ? target.GetComponent<PipelineManager>() : null;
            _reservedId = null; // 本次预建的记录：失败时要删掉，避免留下空记录
            Debug.Log("[快速上传] RunUploadAsync 启动，blueprintId=" + (_blueprintId ?? "(空)") +
                      "，skipAgreement=" + _skipCopyrightAgreement);

            try
            {
                if (target == null || pipeline == null)
                {
                    SetStatus("角色身上没有 PipelineManager，无法上传", MessageType.Error);
                    return;
                }

                if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out IVRCSdkAvatarBuilderApi builder))
                {
                    SetStatus("未能获取 VRChat SDK 的 Avatar Builder，请打开 SDK 控制面板后重试", MessageType.Error);
                    return;
                }

                // 以线上数据为底，只覆盖名称；其余字段原样保留，避免把描述 / 标签 / 可见性改掉。
                VRCAvatar data = _hasRemote ? _remote : new VRCAvatar();
                data.ID = _blueprintId ?? string.Empty;
                data.Name = _nameInput;
                if (data.Description == null) data.Description = string.Empty;
                if (data.Tags == null) data.Tags = new List<string>();
                if (string.IsNullOrEmpty(data.ReleaseStatus)) data.ReleaseStatus = "private";

                // ---- 取消勾选时：走 SDK 原生流程（会弹版权协议框） ----
                if (!_skipCopyrightAgreement)
                {
                    SetStatus("正在构建并上传（SDK 原生流程）…", MessageType.Info);
                    await builder.BuildAndUpload(target, data, _thumbnailPath, _cts.Token);
                    FinishUpload(pipeline, data);
                    return;
                }

                // ---- 跳过协议框：Build 只构建，上传自己拼 ----
                bool isNewRecord = string.IsNullOrWhiteSpace(_blueprintId);

                if (isNewRecord)
                {
                    if (string.IsNullOrWhiteSpace(_thumbnailPath))
                    {
                        SetStatus("首次上传必须指定封面：请点「截取视口」或「选择图片…」", MessageType.Warning);
                        return;
                    }

                    SetStatus("正在创建模型记录…", MessageType.Info);
                    VRCAvatar created = await VRCApi.CreateAvatarRecord(data, null, _cts.Token);
                    if (string.IsNullOrWhiteSpace(created.ID))
                    {
                        throw new Exception("创建模型记录失败：服务端没有返回 ID");
                    }

                    Undo.RecordObject(pipeline, "快速上传：分配 Blueprint ID");
                    pipeline.blueprintId = created.ID;
                    EditorUtility.SetDirty(pipeline);

                    data.ID = created.ID;
                    _blueprintId = created.ID;
                    _reservedId = created.ID;
                }

                SetStatus("正在构建 AssetBundle…", MessageType.Info);
                string bundlePath = await builder.Build(target);
                if (string.IsNullOrWhiteSpace(bundlePath))
                {
                    throw new Exception("构建失败：没有拿到 AssetBundle 路径");
                }

                if (isNewRecord)
                {
                    // 新建记录走 CreateNewAvatar：它内部对图片用的是空 fileId，正是新建上传该走的路径。
                    SetStatus("正在上传模型与封面…", MessageType.Info);
                    await VRCApi.CreateNewAvatar(pipeline.blueprintId, data, bundlePath, _thumbnailPath,
                        ReportProgress, _cts.Token);
                }
                else
                {
                    bool infoChanged;
                    try
                    {
                        infoChanged = !_remote.ContentInfoEqual(data);
                    }
                    catch
                    {
                        // 线上数据缺字段时 ContentInfoEqual 可能抛异常，保守起见提交一次。
                        infoChanged = true;
                    }

                    if (infoChanged)
                    {
                        SetStatus("正在更新模型信息…", MessageType.Info);
                        await VRCApi.UpdateAvatarInfo(pipeline.blueprintId, data, _cts.Token);
                    }

                    // 没换封面就不调 UpdateAvatarImage，线上封面保持不变。
                    if (!string.IsNullOrWhiteSpace(_thumbnailPath))
                    {
                        SetStatus("正在上传封面…", MessageType.Info);
                        await VRCApi.UpdateAvatarImage(pipeline.blueprintId, data, _thumbnailPath,
                            ReportProgress, _cts.Token);
                    }

                    SetStatus("正在上传 AssetBundle…", MessageType.Info);
                    await VRCApi.UpdateAvatarBundle(pipeline.blueprintId, data, bundlePath,
                        ReportProgress, _cts.Token);
                }

                _reservedId = null;
                FinishUpload(pipeline, data);
            }
            catch (OperationCanceledException)
            {
                await CleanupReservedRecord(pipeline);
                SetStatus("上传已取消", MessageType.Warning);
            }
            catch (Exception e)
            {
                await CleanupReservedRecord(pipeline);
                Debug.LogException(e);
                string message = string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message;
                SetStatus("上传失败：" + message, MessageType.Error);

                _hasResult = true;
                _resultSuccess = false;
                _resultAvatarId = string.Empty;
                _resultMessage = message;
            }
            finally
            {
                _busy = false;
                _progressVisible = false;
                Repaint();
            }
        }

        private void FinishUpload(PipelineManager pipeline, VRCAvatar data)
        {
            string uploadedId = pipeline != null ? pipeline.blueprintId : null;
            if (string.IsNullOrWhiteSpace(uploadedId)) uploadedId = data.ID;

            _blueprintId = uploadedId;
            _thumbnailPath = null;
            _coverChanged = false;
            SetStatus("上传成功", MessageType.Info);

            _hasResult = true;
            _resultSuccess = true;
            _resultAvatarId = uploadedId ?? string.Empty;
            _resultMessage = string.Empty;

            // 重新拉一次，让下次上传读到最新的线上封面与信息。
            LoadRemoteInfo();
        }

        /// <summary>构建或上传失败时，删掉本次预建的记录，避免留下一条空模型。</summary>
        private async Task CleanupReservedRecord(PipelineManager pipeline)
        {
            if (string.IsNullOrWhiteSpace(_reservedId)) return;

            string id = _reservedId;
            _reservedId = null;

            try
            {
                await VRCApi.DeleteAvatar(id);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[快速上传] 清理预建记录失败：" + e.Message);
            }

            if (pipeline != null && pipeline.blueprintId == id)
            {
                Undo.RecordObject(pipeline, "快速上传：清理 Blueprint ID");
                pipeline.blueprintId = string.Empty;
                EditorUtility.SetDirty(pipeline);
                _blueprintId = string.Empty;
            }
        }

        private void ReportProgress(string status, float percentage)
        {
            _progressVisible = true;
            _progress = Mathf.Clamp01(percentage);
            if (!string.IsNullOrEmpty(status))
            {
                _status = status;
                _statusType = MessageType.Info;
            }

            EditorApplication.delayCall += Repaint;
        }

        // --------------------------------------------------------------------

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }
    }
}
#endif
