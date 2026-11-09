// Version: v1
// CaramelToolbox - QuickDuplicateScene
// Path: Assets/CaramelToolbox/Editor/QuickDuplicateScene.cs
// Menu: 焦糖工具箱 / 快速复制场景
// Compatible: Unity 5.3 ~ Unity 6 (C# 4 syntax, no lambda / no generics)
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CaramelToolbox
{
    public class QuickDuplicateScene : EditorWindow
    {
        private const string PrefAddToBuild = "CaramelToolbox.QuickDuplicateScene.AddToBuild";
        private const string FieldName = "CaramelToolbox_NewSceneName";

        private string newName = "";
        private string sourcePath = "";
        private bool addToBuild = false;
        private bool focusField = true;

        [MenuItem("焦糖工具箱/快速复制场景")]
        private static void Open()
        {
            QuickDuplicateScene win = (QuickDuplicateScene)EditorWindow.GetWindow(typeof(QuickDuplicateScene), true, "快速复制场景");
            win.minSize = new Vector2(400, 150);
            win.LoadCurrent();
            win.Show();
        }

        private void OnEnable()
        {
            addToBuild = EditorPrefs.GetBool(PrefAddToBuild, false);
            if (string.IsNullOrEmpty(sourcePath)) LoadCurrent();
        }

        private void LoadCurrent()
        {
            Scene s = EditorSceneManager.GetActiveScene();
            sourcePath = s.path;
            newName = s.name;
            focusField = true;
        }

        private string GetTargetPath(string name)
        {
            string dir = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
            return dir + "/" + name + ".unity";
        }

        // returns an error message, or null when the name is valid
        private string Validate(string name)
        {
            if (string.IsNullOrEmpty(name)) return "名称不能为空。";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称包含非法字符（如 \\ / : * ? \" < > |）。";
            if (name.StartsWith(".") || name.EndsWith(".")) return "名称不能以 .开头或结尾。";
            if (string.Compare(GetTargetPath(name), sourcePath, System.StringComparison.OrdinalIgnoreCase) == 0)
                return "新名称不能与当前场景相同。";
            return null;
        }

        private static bool AssetExists(string path)
        {
            return File.Exists(path) || AssetDatabase.LoadAssetAtPath(path, typeof(Object)) != null;
        }

        private static bool IsSceneLoaded(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (string.Compare(s.path, path, System.StringComparison.OrdinalIgnoreCase) == 0) return true;
            }
            return false;
        }

        // ================= GUI =================
        private void OnGUI(){
            // follow the active scene if the user switched scenes
            Scene active = EditorSceneManager.GetActiveScene();
            if (active.path != sourcePath) LoadCurrent();

            Event ev = Event.current;
            bool enterPressed = ev.type == EventType.KeyDown &&
                                (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);

            EditorGUILayout.LabelField("焦糖工具箱 · 快速复制场景", EditorStyles.boldLabel);
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorGUILayout.HelpBox("当前场景尚未保存，请先保存场景后再复制。", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("当前场景", sourcePath, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();GUI.SetNextControlName(FieldName);
            newName = EditorGUILayout.TextField("新场景名称", newName);
            if (GUILayout.Button("重置", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                newName = active.name;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            if (focusField)
            {
                EditorGUI.FocusTextInControl(FieldName);
                focusField = false;
            }

            string name = newName != null ? newName.Trim() : "";
            string error = Validate(name);
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            else
            {
                string target = GetTargetPath(name);EditorGUILayout.LabelField("将创建", target, EditorStyles.miniLabel);
                if (AssetExists(target))
                    EditorGUILayout.HelpBox("已存在同名场景，创建时会询问覆盖还是自动重命名。", MessageType.Warning);
            }

            EditorGUI.BeginChangeCheck();
            addToBuild = EditorGUILayout.ToggleLeft("同时加入 Build Settings", addToBuild);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefAddToBuild, addToBuild);

            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            if (playing) EditorGUILayout.HelpBox("播放模式下无法复制场景，请先退出播放模式。", MessageType.Info);

            GUILayout.Space(4);
            GUI.enabled = error == null && !playing;
            bool clicked = CaramelToolbox.CaramelUI.CaramelButton(new GUIContent("创建"), 30f);
            GUI.enabled = true;

            if (enterPressed && error == null && !playing)
            {
                ev.Use();
                clicked = true;
            }

            if (clicked) Create(name);
        }

        // ================= Create =================
        private void Create(string name)
        {
            if (Validate(name) != null) return;

            Scene source = EditorSceneManager.GetActiveScene();
            string target = GetTargetPath(name);
            if (AssetExists(target))
            {
                int choice = EditorUtility.DisplayDialogComplex("快速复制场景",
                    "已存在同名场景：\n" + target + "\n\n要怎么处理？","覆盖", "取消", "自动重命名");
                if (choice == 1) return;
                if (choice == 2)
                {
                    target = AssetDatabase.GenerateUniqueAssetPath(target);
                }
                else if (IsSceneLoaded(target))
                {
                    EditorUtility.DisplayDialog("快速复制场景", "目标场景当前已在编辑器中打开，无法覆盖：\n" + target, "确定");
                    return;
                }
            }

            // Ask about unsaved changes of the open scenes (Save / Don't Save / Cancel).
            // The copy itself is taken from what is currently shown in the editor.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            // save the in-memory scene as a copy; the original file is not changed
            if(!EditorSceneManager.SaveScene(source, target, true))
            {
                EditorUtility.DisplayDialog("快速复制场景", "场景复制失败：\n" + target, "确定");
                return;
            }
            AssetDatabase.Refresh();

            if (addToBuild) AddToBuildSettings(target);

            EditorSceneManager.OpenScene(target, OpenSceneMode.Single);

            Object asset = AssetDatabase.LoadAssetAtPath(target, typeof(Object));
            if (asset != null) EditorGUIUtility.PingObject(asset);

            Debug.Log("[焦糖工具箱] 已复制场景：" + sourcePath + "  →  " + target);
            Close();
        }

        private static void AddToBuildSettings(string path)
        {
            EditorBuildSettingsScene[] old = EditorBuildSettings.scenes;
            for (int i = 0; i < old.Length; i++){
                if (string.Compare(old[i].path, path, System.StringComparison.OrdinalIgnoreCase) == 0) return;
            }
            EditorBuildSettingsScene[] arr = new EditorBuildSettingsScene[old.Length + 1];
            for (int i = 0; i < old.Length; i++) arr[i] = old[i];
            arr[old.Length] = new EditorBuildSettingsScene(path, true);
            EditorBuildSettings.scenes = arr;
        }
    }
}
#endif