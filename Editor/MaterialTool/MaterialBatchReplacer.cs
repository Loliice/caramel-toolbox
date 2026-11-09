// 焦糖工具箱 - 材质批量替换
// 放置路径：Assets/任意目录/Editor/MaterialBatchReplacer.cs
// 入口：菜单栏 焦糖工具箱 > 材质批量替换
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace CaramelToolbox
{
 public class MaterialBatchReplacer : EditorWindow
 {
 private class SlotRef
 {
 public Renderer renderer;
 public int index;
 }
 private class MatEntry
 {
 public Material original; // 可能为 null（空材质槽）
 public Material replacement;
 public bool selected = true;
 public bool foldout;
 public readonly List<SlotRef> slots = new List<SlotRef>();
 public int RendererCount => slots.Select(s => s.renderer).Distinct().Count();

}
private GameObject target;
private bool includeInactive = true;
private bool followSelection = false;
private bool preview = true; // 预览：勾选后把已指定的新材质临时套到视图里
private readonly Dictionary<Renderer, Material[]> previewBackup = new Dictionary<Renderer, Material[]>();
private Material globalMaterial;
private string search = "";
private Vector2 scroll;
private readonly List<MatEntry> entries = new List<MatEntry>();
private int rendererTotal;

[MenuItem("焦糖工具箱/材质批量替换")]
private static void Open()
{
    var win = GetWindow<MaterialBatchReplacer>("材质批量替换");
    win.minSize = new Vector2(460, 380);
    if (Selection.activeGameObject != null)
    {
        win.target = Selection.activeGameObject;
        win.Scan();
    }
    win.Show();
}

private void OnSelectionChange(){
    if (!followSelection) return;
    var go = Selection.activeGameObject;
    if (go != null && go != target)
    {
        target = go;
        Scan();
        Repaint();
    }
}

private void OnEnable()
{
    EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    AssemblyReloadEvents.beforeAssemblyReload += RestorePreview;
}

private void OnDisable()
{
    EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
    AssemblyReloadEvents.beforeAssemblyReload -= RestorePreview;
    RestorePreview(); // 关窗口时必须还原，否则临时改动会残留
}

private void OnPlayModeStateChanged(PlayModeStateChange state)
{
    // 进入播放模式前还原，避免把临时材质带进运行态
    if (state == PlayModeStateChange.ExitingEditMode) RestorePreview();
}

private void OnHierarchyChange()
{
    if (target == null && entries.Count > 0)
    {
        RestorePreview();
        entries.Clear();
        Repaint();
    }
}

// ---------------- 预览 ----------------
private void RefreshPreview()
{
    if (preview) ApplyPreview();
    else RestorePreview();
}

private void ApplyPreview()
{
    RestorePreview(); // 先还原到原始状态，保证备份的永远是真身
    if (target == null) return;

    var perRenderer = new Dictionary<Renderer, Material[]>();
    foreach (var e in entries)
    {
        if (!e.selected || e.replacement == null || e.replacement == e.original) continue;
        foreach (var s in e.slots)
        {
            if (s.renderer == null) continue;
            // 只预览场景/预制体编辑模式里的实例，绝不改动 Project 里的预制体资源
            if (!s.renderer.gameObject.scene.IsValid()) continue;
            if (!perRenderer.TryGetValue(s.renderer, out var mats))
            {
                mats = s.renderer.sharedMaterials;
                perRenderer[s.renderer] = mats;
            }
            if (s.index < mats.Length) mats[s.index] = e.replacement;
        }
    }
    if (perRenderer.Count == 0) return;

    foreach (var kv in perRenderer)
    {
        previewBackup[kv.Key] = kv.Key.sharedMaterials; // 备份原始材质数组
        kv.Key.sharedMaterials = kv.Value;              // 只改内存，不记 Undo、不标脏
    }
    RepaintViews();
}

private void RestorePreview()
{
    if (previewBackup.Count == 0) return;
    foreach (var kv in previewBackup)
        if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
    previewBackup.Clear();
    RepaintViews();
}

private static void RepaintViews()
{
    SceneView.RepaintAll();
    EditorApplication.QueuePlayerLoopUpdate();
}

// ---------------- 扫描 ----------------
private void Scan()
{
    RestorePreview(); // 扫描依赖 sharedMaterials，必须先看真身，否则会把预览材质误判为原始材质

    var oldReplace = new Dictionary<Material, Material>();
    Material oldNullReplace = null;
    foreach (var e in entries)
    {
        if (e.original == null) oldNullReplace = e.replacement;
        else oldReplace[e.original] = e.replacement;
    }

    entries.Clear();
    rendererTotal = 0;
    if (target == null) return;

    var map = new Dictionary<Material, MatEntry>();
    MatEntry nullEntry = null;

    var renderers = target.GetComponentsInChildren<Renderer>(includeInactive);
    rendererTotal = renderers.Length;

    foreach (var r in renderers)
    {
        var mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            MatEntry entry;
            if (m == null)
            {
                if (nullEntry == null)
                    nullEntry = new MatEntry { original = null,replacement = oldNullReplace };
                entry = nullEntry;
            }
            else if (!map.TryGetValue(m, out entry))
            {
                entry = new MatEntry { original = m };
                oldReplace.TryGetValue(m, out entry.replacement);
                map[m] = entry;
            }
            entry.slots.Add(new SlotRef { renderer = r, index = i });
        }
    }

    entries.AddRange(map.Values.OrderBy(e => e.original.name));
    if (nullEntry != null) entries.Add(nullEntry);

    if (preview) ApplyPreview(); // 扫描后把已指定的新材质重新套上
}
// ---------------- GUI ----------------
private void OnGUI()
{
    DrawHeader();
    EditorGUILayout.Space(4);

    if (target == null)
    {
        EditorGUILayout.HelpBox("请拖入或在场景中选中一个物体，然后点击【扫描】。", MessageType.Info);
        return;
    }

    DrawGlobalTools();
    EditorGUILayout.Space(4);
    DrawList();
    EditorGUILayout.Space(4);
    DrawFooter();
}

private void DrawHeader()
{
    EditorGUILayout.LabelField("焦糖工具箱 · 材质批量替换", EditorStyles.boldLabel);

    EditorGUI.BeginChangeCheck();
    target = (GameObject)EditorGUILayout.ObjectField("目标物体", target, typeof(GameObject), true);
    if (EditorGUI.EndChangeCheck()) Scan();

    using (new EditorGUILayout.HorizontalScope())
    {
        EditorGUI.BeginChangeCheck();
        preview = EditorGUILayout.ToggleLeft("预览", preview, GUILayout.Width(60));
        if (EditorGUI.EndChangeCheck()) RefreshPreview();

        GUILayout.FlexibleSpace();
        string tip = !preview ? "未启用"
            : previewBackup.Count > 0 ? "生效中：视图已临时套用新材质"
            : "指定新材质后将立刻在视图中显示";
        GUILayout.Label(tip, EditorStyles.miniLabel);
    }

    using (new EditorGUILayout.HorizontalScope())
    {
        EditorGUI.BeginChangeCheck();
        includeInactive = EditorGUILayout.ToggleLeft("包含隐藏子物体", includeInactive, GUILayout.Width(120));
        if (EditorGUI.EndChangeCheck()) Scan();

        followSelection = EditorGUILayout.ToggleLeft("跟随选中", followSelection, GUILayout.Width(80));
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("使用当前选中", GUILayout.Width(90)) && Selection.activeGameObject != null)
        {
            target = Selection.activeGameObject;
            Scan();
        }
        if (GUILayout.Button("扫描", GUILayout.Width(60))) Scan();
    }
}

private void DrawGlobalTools()
{
    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
    {
        EditorGUILayout.LabelField(
            $"共 {rendererTotal} 个 Renderer，{entries.Count} 种材质，{entries.Sum(e => e.slots.Count)} 个材质槽",
            EditorStyles.miniBoldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            globalMaterial = (Material)EditorGUILayout.ObjectField("统一新材质", globalMaterial, typeof(Material), false);
            GUI.enabled = globalMaterial != null;
            if (GUILayout.Button("填入勾选项", GUILayout.Width(80)))
            {
                foreach (var e in entries.Where(e => e.selected)) e.replacement = globalMaterial;
                RefreshPreview();
            }
            GUI.enabled = true;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("全选")) { entries.ForEach(e => e.selected = true); RefreshPreview(); }
            if (GUILayout.Button("全不选")) { entries.ForEach(e => e.selected = false); RefreshPreview(); }
            if (GUILayout.Button("反选")) { entries.ForEach(e => e.selected = !e.selected); RefreshPreview(); }
            if (GUILayout.Button("清空新材质")) { entries.ForEach(e => e.replacement = null); RefreshPreview(); }
        }

        if (preview && previewBackup.Count > 0)
            EditorGUILayout.HelpBox("预览仅作用于视图，不写入资源；取消勾选或关闭窗口会自动还原。", MessageType.None);
        search = EditorGUILayout.TextField("搜索材质名", search);
    }
}
private void DrawList()
{
    scroll = EditorGUILayout.BeginScrollView(scroll);

    foreach (var e in entries)
    {
        string name = e.original != null ? e.original.name : "(空材质槽 / Missing)";
        if (!string.IsNullOrEmpty(search) &&
            name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0)
            continue;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)){
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(18));
                if (EditorGUI.EndChangeCheck()) RefreshPreview();

                Texture thumb = e.original != null
                    ? (AssetPreview.GetAssetPreview(e.original) ?? AssetPreview.GetMiniThumbnail(e.original))
                    : null;
                GUILayout.Label(thumb, GUILayout.Width(36), GUILayout.Height(36));

                using (new EditorGUILayout.VerticalScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUI.enabled = false;
                        EditorGUILayout.ObjectField(e.original,typeof(Material), false);
                        GUI.enabled = true;
                        GUILayout.Label("→", GUILayout.Width(16));
                        EditorGUI.BeginChangeCheck();
                        e.replacement = (Material)EditorGUILayout.ObjectField(e.replacement, typeof(Material), false);
                        if (EditorGUI.EndChangeCheck()) RefreshPreview(); // 一指定就立刻在视图里看到
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        e.foldout = EditorGUILayout.Foldout(e.foldout,
                            $"{name}  ·  {e.RendererCount} 个物体 / {e.slots.Count} 个槽", true);
                        if (e.original != null && GUILayout.Button("定位材质", EditorStyles.miniButton, GUILayout.Width(60)))
                            EditorGUIUtility.PingObject(e.original);
                        if (GUILayout.Button("选中物体", EditorStyles.miniButton, GUILayout.Width(60)))
                            Selection.objects = e.slots.Where(s => s.renderer != null)
                                .Select(s => (Object)s.renderer.gameObject).Distinct().ToArray();
                    }
                }
            }

            if (e.foldout)
            {
                EditorGUI.indentLevel++;
                foreach (var s in e.slots)
                {
                    if (s.renderer == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{GetPath(s.renderer.transform)}[槽 {s.index}]", EditorStyles.miniLabel);
                        if (GUILayout.Button("选中", EditorStyles.miniButton, GUILayout.Width(40)))
                        {
                            Selection.activeGameObject = s.renderer.gameObject;
                            EditorGUIUtility.PingObject(s.renderer.gameObject);
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }
    }

    EditorGUILayout.EndScrollView();
}

private void DrawFooter()
{
    int pending = entries.Count(e => e.selected && e.replacement != null && e.replacement != e.original);
    GUI.enabled = pending > 0;
    if (CaramelToolbox.CaramelUI.CaramelButton($"执行替换（{pending} 种材质）", 32f))
        ApplyReplace();
    GUI.enabled = true;
    EditorGUILayout.LabelField("提示：支持 Ctrl+Z 撤销；仅替换已勾选且指定了新材质的条目；预览不会写入资源。", EditorStyles.miniLabel);
}

// ---------------- 替换 ----------------
private void ApplyReplace()
{
    RestorePreview(); // 必须先还原：否则 Undo 记录到的"原始状态"是预览中的新材质，撤销就失效了

    var todo = entries.Where(e => e.selected && e.replacement != null && e.replacement != e.original).ToList();
    if (todo.Count == 0) return;

    var perRenderer = new Dictionary<Renderer, Material[]>();
    foreach (var e in todo)
    {
        foreach (var s in e.slots)
        {
            if (s.renderer == null) continue;
            if (!perRenderer.TryGetValue(s.renderer, out var mats))
            {
                mats = s.renderer.sharedMaterials;
                perRenderer[s.renderer] = mats;
            }
            if (s.index < mats.Length) mats[s.index] = e.replacement;
        }
    }

    Undo.IncrementCurrentGroup();
    int group = Undo.GetCurrentGroup();
    Undo.SetCurrentGroupName("材质批量替换");

    foreach (var kv in perRenderer)
    {
        Undo.RecordObject(kv.Key, "材质批量替换");
        kv.Key.sharedMaterials = kv.Value;
        if (PrefabUtility.IsPartOfPrefabInstance(kv.Key))
            PrefabUtility.RecordPrefabInstancePropertyModifications(kv.Key);
        EditorUtility.SetDirty(kv.Key);
    }

    Undo.CollapseUndoOperations(group);

    if (target != null && target.scene.IsValid())
        EditorSceneManager.MarkSceneDirty(target.scene);

    Debug.Log($"[焦糖工具箱] 材质批量替换完成：{todo.Count} 种材质，影响 {perRenderer.Count} 个 Renderer。");

    foreach (var e in todo) e.replacement = null;
    Scan();
    ShowNotification(new GUIContent($"已替换 {perRenderer.Count} 个物体"));
}

private string GetPath(Transform t)
{
    if (target == null) return t.name;
    var root = target.transform;
    var names = new List<string>();
    while (t != null)
    {
        names.Add(t.name);
        if (t == root) break;
        t = t.parent;
    }

    names.Reverse();
    return string.Join("/", names);
}
}
}
#endif