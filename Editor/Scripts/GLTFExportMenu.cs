using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace UnityGLTF
{
    public static class GLTFExportMenu
    {
        private const string MenuPrefix = "Assets/UnityGLTF/";
        private const string MenuPrefixGameObject = "GameObject/UnityGLTF/";

        private const string ExportGltf = "Export selected as glTF";
        private const string ExportGlb = "Export selected as GLB";
        private const string QuickExportGlb = "Quick Export GLB";

        private static string GetLastGlbPath(GameObject gameObject)
        {
            return EditorPrefs.GetString($"UnityGLTF.LastGlbPath.{gameObject.GetInstanceID()}", "");
        }

        private static void SetLastGlbPath(GameObject gameObject, string path)
        {
            EditorPrefs.SetString($"UnityGLTF.LastGlbPath.{gameObject.GetInstanceID()}", path);
        }

        public static string RetrieveTexturePath(UnityEngine.Texture texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(Texture2D))
            {
                var ext = System.IO.Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(ext)) return texture.name + ".png";
                path = path.Replace(ext, "-" + texture.name + ext);
            }
            return path;
        }

        private static bool TryGetExportNameAndRootTransformsFromSelection(out string sceneName, out Transform[] rootTransforms, out Object[] rootResources, Object context = null)
        {
            // If we have a context object (from right-click menu), use that instead of selection
            if (context is GameObject contextGO)
            {
                sceneName = contextGO.name;
                rootTransforms = new[] { contextGO.transform };
                rootResources = null;
                return true;
            }

            if (Selection.transforms.Length > 1)
            {
                sceneName = SceneManager.GetActiveScene().name;
                rootTransforms = Selection.transforms;
                rootResources = null;
                return true;
            }
            if (Selection.transforms.Length == 1)
            {
                sceneName = Selection.activeGameObject.name;
                rootTransforms = Selection.transforms;
                rootResources = null;
                return true;
            }
            if (Selection.objects.Any() && Selection.objects.All(x => x is GameObject))
            {
                sceneName = Selection.objects.First().name;
                rootTransforms = Selection.objects.Select(x => (x as GameObject).transform).ToArray();
                rootResources = null;
                return true;
            }
            if (Selection.objects.Any() && Selection.objects.All(x => x is Material))
            {
                sceneName = "Material Library";
                rootTransforms = null;
                rootResources = Selection.objects;
                return true;
            }

            sceneName = null;
            rootTransforms = null;
            rootResources = null;
            return false;
        }

        [MenuItem(MenuPrefix + ExportGltf, true)]
        [MenuItem(MenuPrefixGameObject + ExportGltf, true)]
        private static bool ExportSelectedValidate()
        {
            return TryGetExportNameAndRootTransformsFromSelection(out _, out _, out _);
        }

        [MenuItem(MenuPrefix + ExportGltf)]
        [MenuItem(MenuPrefixGameObject + ExportGltf, false, 33)]
        private static void ExportSelected(MenuCommand command)
        {
            if (!TryGetExportNameAndRootTransformsFromSelection(out var sceneName, out var rootTransforms, out var rootResources, command.context))
            {
                Debug.LogError("Can't export: selection is empty");
                return;
            }

            ExportToFolder(rootTransforms, rootResources, false, sceneName);
        }

        [MenuItem(MenuPrefix + ExportGlb, true)]
        [MenuItem(MenuPrefixGameObject + ExportGlb, true)]
        private static bool ExportGLBSelectedValidate()
        {
            return TryGetExportNameAndRootTransformsFromSelection(out _, out _, out _);
        }

        [MenuItem(MenuPrefix + ExportGlb)]
        [MenuItem(MenuPrefixGameObject + ExportGlb, false, 34)]
        private static void ExportGLBSelected(MenuCommand command)
        {
            if (!TryGetExportNameAndRootTransformsFromSelection(out var sceneName, out var rootTransforms, out var rootResources, command.context))
            {
                Debug.LogError("Can't export: selection is empty");
                return;
            }

            var invokedByShortcut = Event.current?.type == EventType.KeyDown;
            var selectedObject = command.context as GameObject ?? Selection.activeGameObject;
            var lastPath = GetLastGlbPath(selectedObject);
            var initialPath = !string.IsNullOrEmpty(lastPath) ? lastPath : Path.Combine(GLTFSettings.GetOrCreateSettings().SaveFolderPath, sceneName + ".glb");
            
            var path = invokedByShortcut && File.Exists(Path.GetDirectoryName(initialPath)) 
                ? initialPath 
                : EditorUtility.SaveFilePanel("Save GLB", Path.GetDirectoryName(initialPath), Path.GetFileName(initialPath), "glb");

            if (!string.IsNullOrEmpty(path))
            {
                SetLastGlbPath(selectedObject, path);
                Export(rootTransforms, rootResources, path);
            }
        }

        [MenuItem(MenuPrefix + QuickExportGlb, true)]
        [MenuItem(MenuPrefixGameObject + QuickExportGlb, true)]
        private static bool QuickExportGLBValidate()
        {
            if (!TryGetExportNameAndRootTransformsFromSelection(out _, out _, out _))
                return false;

            var selectedObject = Selection.activeGameObject;
            var hasPath = selectedObject != null && !string.IsNullOrEmpty(GetLastGlbPath(selectedObject));
            
            // Show message if invoked by shortcut and no path exists
            var invokedByShortcut = Event.current?.type == EventType.KeyDown;
            if (invokedByShortcut && !hasPath && selectedObject != null)
            {
                EditorUtility.DisplayDialog("Quick Export GLB", 
                    "This game object hasn't been exported yet.", 
                    "OK");
            }

            return hasPath;
        }

        [MenuItem(MenuPrefix + QuickExportGlb)]
        [MenuItem(MenuPrefixGameObject + QuickExportGlb, false, 35)]
        private static void QuickExportGLB(MenuCommand command)
        {
            if (!TryGetExportNameAndRootTransformsFromSelection(out _, out var rootTransforms, out var rootResources, command.context))
            {
                Debug.LogError("Can't export: selection is empty");
                return;
            }

            var selectedObject = command.context as GameObject ?? Selection.activeGameObject;
            var path = GetLastGlbPath(selectedObject);
            
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("No previous export path found for this object");
                return;
            }

            Export(rootTransforms, rootResources, path);
        }

        private static void ExportToFolder(Transform[] transforms, Object[] resources, bool binary, string sceneName)
        {
            var settings = GLTFSettings.GetOrCreateSettings();
            var path = EditorUtility.SaveFolderPanel("glTF Export Path", settings.SaveFolderPath, "");
            
            if (!string.IsNullOrEmpty(path))
            {
                settings.SaveFolderPath = path;
                var fullPath = Path.Combine(path, sceneName + (binary ? ".glb" : ".gltf"));
                Export(transforms, resources, fullPath);
            }
        }

        private static void Export(Transform[] transforms, Object[] resources, string fullPath)
        {
            var settings = GLTFSettings.GetOrCreateSettings();
            var exportOptions = new ExportContext(settings) { TexturePathRetriever = RetrieveTexturePath };
            var exporter = new GLTFSceneExporter(transforms, exportOptions);

            if (resources != null)
            {
                exportOptions.AfterSceneExport += (sceneExporter, _) =>
                {
                    foreach (var resource in resources)
                    {
                        if (resource is Material material)
                            sceneExporter.ExportMaterial(material);
                        if (resource is Texture2D texture)
                            sceneExporter.ExportTexture(texture, "unknown");
                        if (resource is Mesh mesh)
                            sceneExporter.ExportMesh(mesh);
                    }
                };
            }

            var binary = Path.GetExtension(fullPath).ToLowerInvariant() == ".glb";
            var path = Path.GetDirectoryName(fullPath);
            var sceneName = Path.GetFileNameWithoutExtension(fullPath);

            if (binary)
                exporter.SaveGLB(path, sceneName);
            else
                exporter.SaveGLTFandBin(path, sceneName);

            Debug.Log("Exported to " + fullPath);
        }
    }

	internal static class GLTFCreateMenu
	{
		[MenuItem("Assets/Create/UnityGLTF/Material", false)]
		private static void CreateNewAsset()
		{
			var filename = "glTF Material Library.gltf";
			var content = @"{
	""asset"": {
		""generator"": ""UnityGLTF"",
		""version"": ""2.0""
	},
	""materials"": [
		{
			""name"": ""Material"",
			""pbrMetallicRoughness"": {
				""metallicFactor"": 0.0
			}
		}
	]
}";

			var importAction = ScriptableObject.CreateInstance<AdjustImporterAction>();
			importAction.fileContent = content;
			ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, importAction, filename, null, (string) null);
		}

		// Based on DoCreateAssetWithContent.cs
		private class AdjustImporterAction : EndNameEditAction
		{
			public string fileContent;
			public override void Action(int instanceId, string pathName, string resourceFile)
			{
				var templateContent = SetLineEndings(fileContent, EditorSettings.lineEndingsForNewScripts);
				File.WriteAllText(Path.GetFullPath(pathName), templateContent);
				AssetDatabase.ImportAsset(pathName);
				// This is why we're not using ProjectWindowUtil.CreateAssetWithContent directly:
				// We want glTF materials created with UnityGLTF to also use UnityGLTF for importing.
				AssetDatabase.SetImporterOverride<GLTFImporter>(pathName);
				var asset = AssetDatabase.LoadAssetAtPath(pathName, typeof (UnityEngine.Object));
				ProjectWindowUtil.ShowCreatedAsset(asset);
			}
		}
		
		// Unmodified from ProjectWindowUtil.cs:SetLineEndings (internal)
		private static string SetLineEndings(string content, LineEndingsMode lineEndingsMode)
		{
			string replacement;
			switch (lineEndingsMode)
			{
				case LineEndingsMode.OSNative:
					replacement = Application.platform != RuntimePlatform.WindowsEditor ? "\n" : "\r\n";
					break;
				case LineEndingsMode.Unix:
					replacement = "\n";
					break;
				case LineEndingsMode.Windows:
					replacement = "\r\n";
					break;
				default:
					replacement = "\n";
					break;
			}
			content = Regex.Replace(content, "\\r\\n?|\\n", replacement);
			return content;
		}
	}
}
