// MegaScansURPImporter.cs
// Place in Assets/Editor. Requires Unity 2022.3+ with URP (tested against Unity 6 APIs).
// Processes FBX + source textures under Assets/Models/MegaScans recursively.
// Generated outputs are written to each model's _URPGenerated directory.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityLibrary.EditorTools
{
    public sealed class MegaScansURPImporterWindow : EditorWindow
    {
        internal const string Root = "Assets/Models/MegaScans";
        internal const string OutputFolder = "_URPGenerated";

        private const string Prefix = "UnityLibrary.MegaScansURP.";
        private const string AutoKey = Prefix + "Auto";
        private const string FlipKey = Prefix + "FlipGreen";
        private const string CutoffKey = Prefix + "Cutoff";
        private const string SmoothnessKey = Prefix + "Smoothness";
        private const string MaxSizeKey = Prefix + "MaxSize";

        internal static bool AutoProcess { get { return EditorPrefs.GetBool(AutoKey, true); } }
        internal static bool FlipUnknownNormals { get { return EditorPrefs.GetBool(FlipKey, false); } }
        internal static float Cutoff { get { return EditorPrefs.GetFloat(CutoffKey, 0.5f); } }
        internal static float DefaultSmoothness { get { return EditorPrefs.GetFloat(SmoothnessKey, 0.45f); } }
        internal static int MaxPackedSize { get { return EditorPrefs.GetInt(MaxSizeKey, 4096); } }

        [MenuItem("Tools/MegaScans URP/Importer Settings")]
        private static void OpenWindow()
        {
            GetWindow<MegaScansURPImporterWindow>("MegaScans URP");
        }

        [MenuItem("Tools/MegaScans URP/Process All Models")]
        private static void ProcessAllMenu()
        {
            MegaScansURPProcessor.ProcessAll(true);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Fab / Quixel MegaScans to URP Lit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Only processes FBX models beneath " + Root + ". Output materials and generated PNGs are stored in each model folder's " + OutputFolder + " subfolder.", MessageType.Info);
            EditorGUILayout.Space();

            bool autoProcess = EditorGUILayout.Toggle(new GUIContent("Auto-process imports", "Run when a source FBX or texture is imported/changed."), AutoProcess);
            bool flip = EditorGUILayout.Toggle(new GUIContent("Flip ambiguous normal green", "Enable for DirectX-format files named simply Normal. NormalDX and NormalGL are detected automatically."), FlipUnknownNormals);
            float cutoff = EditorGUILayout.Slider(new GUIContent("Opacity alpha cutoff"), Cutoff, 0.01f, 0.99f);
            float smoothness = EditorGUILayout.Slider(new GUIContent("Fallback smoothness"), DefaultSmoothness, 0f, 1f);
            int size = EditorGUILayout.IntPopup(
                new GUIContent("Maximum packed size"),
                MaxPackedSize,
                new[] { new GUIContent("1024"), new GUIContent("2048"), new GUIContent("4096"), new GUIContent("8192") },
                new[] { 1024, 2048, 4096, 8192 });

            bool changed = autoProcess != AutoProcess || flip != FlipUnknownNormals || !Mathf.Approximately(cutoff, Cutoff) || !Mathf.Approximately(smoothness, DefaultSmoothness) || size != MaxPackedSize;
            if (changed)
            {
                EditorPrefs.SetBool(AutoKey, autoProcess);
                EditorPrefs.SetBool(FlipKey, flip);
                EditorPrefs.SetFloat(CutoffKey, cutoff);
                EditorPrefs.SetFloat(SmoothnessKey, smoothness);
                EditorPrefs.SetInt(MaxSizeKey, size);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Process All MegaScans FBX Models", GUILayout.Height(34))) MegaScansURPProcessor.ProcessAll(true);
            EditorGUILayout.HelpBox("Supported: BaseColor/Albedo, Normal/NormalDX/NormalGL, Roughness, AO, Metallic/Metalness, Opacity, and ORM/ARM/RMA/ORD packed maps. JPG and PNG are supported as channel-packing input. Other Unity-importable texture formats work for BaseColor and Normal.", MessageType.None);
            EditorGUILayout.HelpBox("The processor assumes one texture set per FBX. If a model contains several different material surfaces, its FBX material slots will all point to the same generated material.", MessageType.Warning);
        }
    }

    internal static class MegaScansURPProcessor
    {
        private sealed class TextureSource
        {
            internal string Path;
            internal string Kind;
            internal int Score;
        }

        private sealed class TextureSet
        {
            internal string BaseColor;
            internal string Normal;
            internal string Roughness;
            internal string AO;
            internal string Metalness;
            internal string Opacity;
            internal string Packed;
            internal string PackedKind;
        }

        private sealed class ImageData
        {
            internal int Width;
            internal int Height;
            internal Color32[] Pixels;
            internal byte Sample(float u, float v, int channel)
            {
                if (Pixels == null || Pixels.Length == 0) return 255;
                int x = Mathf.Clamp(Mathf.RoundToInt(u * (Width - 1)), 0, Width - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(v * (Height - 1)), 0, Height - 1);
                Color32 c = Pixels[y * Width + x];
                if (channel == 0) return c.r;
                if (channel == 1) return c.g;
                if (channel == 2) return c.b;
                return c.a;
            }
        }

        private static readonly HashSet<string> TextureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tga", ".tif", ".tiff", ".psd", ".exr" };
        private static readonly HashSet<string> PendingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool processing;
        private static bool pendingScheduled;

        internal static bool IsInsideRoot(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && assetPath.StartsWith(MegaScansURPImporterWindow.Root + "/", StringComparison.OrdinalIgnoreCase) && assetPath.IndexOf("/" + MegaScansURPImporterWindow.OutputFolder + "/", StringComparison.OrdinalIgnoreCase) < 0;
        }

        internal static bool IsSourceTexture(string path)
        {
            return IsInsideRoot(path) && TextureExtensions.Contains(System.IO.Path.GetExtension(path));
        }

        internal static bool IsSourceModel(string path)
        {
            return IsInsideRoot(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        internal static void EnqueueChangedAssets(IEnumerable<string> paths)
        {
            if (!MegaScansURPImporterWindow.AutoProcess || processing) return;
            bool any = false;
            foreach (string path in paths)
            {
                if (IsSourceModel(path))
                {
                    any |= PendingModels.Add(path);
                    continue;
                }
                if (!IsSourceTexture(path)) continue;
                string directory = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
                string parent = System.IO.Path.GetDirectoryName(directory).Replace('\\', '/');
                string folderName = System.IO.Path.GetFileName(directory);
                bool usesTextureSubfolder = folderName.Equals("Textures", StringComparison.OrdinalIgnoreCase) || folderName.Equals("Texture", StringComparison.OrdinalIgnoreCase) || folderName.Equals("Maps", StringComparison.OrdinalIgnoreCase);
                foreach (string folder in usesTextureSubfolder ? new[] { directory, parent } : new[] { directory })
                {
                    if (folder == null || !folder.StartsWith(MegaScansURPImporterWindow.Root, StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsValidFolder(folder)) continue;
                    foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
                    {
                        string model = AssetDatabase.GUIDToAssetPath(guid);
                        if (IsSourceModel(model)) any |= PendingModels.Add(model);
                    }
                }
            }
            if (!any || pendingScheduled) return;
            pendingScheduled = true;
            EditorApplication.delayCall += ProcessPending;
        }

        private static void ProcessPending()
        {
            pendingScheduled = false;
            if (processing || PendingModels.Count == 0) return;
            List<string> models = PendingModels.ToList();
            PendingModels.Clear();
            ProcessModels(models, false);
        }

        internal static void ProcessAll(bool showProgress)
        {
            if (!AssetDatabase.IsValidFolder(MegaScansURPImporterWindow.Root))
            {
                Debug.LogWarning("MegaScans URP: Folder not found: " + MegaScansURPImporterWindow.Root);
                return;
            }
            List<string> models = AssetDatabase.FindAssets("t:Model", new[] { MegaScansURPImporterWindow.Root }).Select(AssetDatabase.GUIDToAssetPath).Where(IsSourceModel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            ProcessModels(models, showProgress);
        }

        private static void ProcessModels(IList<string> models, bool showProgress)
        {
            if (processing) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("MegaScans URP: Universal Render Pipeline/Lit shader not found. Install and configure URP first.");
                return;
            }
            processing = true;
            int succeeded = 0;
            int skipped = 0;
            int failed = 0;
            try
            {
                for (int i = 0; i < models.Count; i++)
                {
                    string model = models[i];
                    if (showProgress && EditorUtility.DisplayCancelableProgressBar("Processing MegaScans", model, models.Count > 0 ? (float)i / models.Count : 1f)) break;
                    if (!IsSourceModel(model) || AssetDatabase.LoadAssetAtPath<GameObject>(model) == null) { skipped++; continue; }
                    try
                    {
                        if (ProcessModel(model, shader)) succeeded++;
                        else skipped++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.LogError("MegaScans URP: Failed to process " + model + "\n" + ex);
                    }
                }
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (showProgress) EditorUtility.ClearProgressBar();
                processing = false;
            }
            Debug.Log("MegaScans URP: Processed " + succeeded + " FBX models; skipped " + skipped + "; errors " + failed + ".");
        }

        private static bool ProcessModel(string modelPath, Shader shader)
        {
            TextureSet set = DiscoverTextureSet(modelPath);
            if (string.IsNullOrEmpty(set.BaseColor) && string.IsNullOrEmpty(set.Normal) && string.IsNullOrEmpty(set.Roughness) && string.IsNullOrEmpty(set.AO) && string.IsNullOrEmpty(set.Packed))
            {
                Debug.LogWarning("MegaScans URP: No matching texture set for " + modelPath);
                return false;
            }

            string dir = System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
            string name = System.IO.Path.GetFileNameWithoutExtension(modelPath);
            string outputDir = dir + "/" + MegaScansURPImporterWindow.OutputFolder;
            if (!AssetDatabase.IsValidFolder(outputDir)) AssetDatabase.CreateFolder(dir, MegaScansURPImporterWindow.OutputFolder);

            string baseMapPath = set.BaseColor;
            bool alphaClip = !string.IsNullOrEmpty(set.Opacity) && !string.IsNullOrEmpty(set.BaseColor);
            if (alphaClip)
            {
                string generatedBase = outputDir + "/" + name + "_URP_BaseColor.png";
                if (GenerateBaseColorAlpha(set.BaseColor, set.Opacity, generatedBase)) baseMapPath = generatedBase;
                else { alphaClip = false; Debug.LogWarning("MegaScans URP: Could not combine opacity map for " + modelPath); }
            }

            Texture2D baseMap = LoadTexture(baseMapPath);
            if (!string.IsNullOrEmpty(set.Normal)) ConfigureNormal(set.Normal);
            Texture2D normal = LoadTexture(set.Normal);

            Texture2D packed = null;
            bool hasPackedInputs = !string.IsNullOrEmpty(set.Roughness) || !string.IsNullOrEmpty(set.AO) || !string.IsNullOrEmpty(set.Metalness) || !string.IsNullOrEmpty(set.Packed);
            if (hasPackedInputs)
            {
                string packedPath = outputDir + "/" + name + "_URP_Mask.png";
                if (GeneratePackedMask(set, packedPath))
                {
                    ConfigureDataTexture(packedPath);
                    packed = LoadTexture(packedPath);
                }
            }

            string materialPath = outputDir + "/" + name + "_URP.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else if (material.shader != shader) material.shader = shader;

            material.SetFloat("_WorkflowMode", 1f);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
            material.SetFloat("_Cutoff", MegaScansURPImporterWindow.Cutoff);
            material.SetFloat("_Cull", alphaClip ? 0f : 2f);
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            material.SetTexture("_MetallicGlossMap", packed);
            material.SetTexture("_OcclusionMap", packed);
            material.SetFloat("_Metallic", string.IsNullOrEmpty(set.Metalness) && string.IsNullOrEmpty(set.Packed) ? 0f : 1f);
            material.SetFloat("_Smoothness", packed != null ? 1f : MegaScansURPImporterWindow.DefaultSmoothness);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            material.SetFloat("_OcclusionStrength", 1f);
            SetKeyword(material, "_NORMALMAP", normal != null);
            SetKeyword(material, "_METALLICSPECGLOSSMAP", packed != null);
            SetKeyword(material, "_OCCLUSIONMAP", packed != null);
            SetKeyword(material, "_ALPHATEST_ON", alphaClip);
            SetKeyword(material, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", false);
            material.renderQueue = alphaClip ? (int)RenderQueue.AlphaTest : -1;
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);

            RemapModelMaterials(modelPath, material);
            return true;
        }

        private static void RemapModelMaterials(string modelPath, Material material)
        {
            ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null) return;
            bool changed = false;
            if (importer.materialImportMode == ModelImporterMaterialImportMode.None)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                changed = true;
            }

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in importer.GetExternalObjectMap())
            {
                if (pair.Key.type == typeof(Material) && !string.IsNullOrEmpty(pair.Key.name)) names.Add(pair.Key.name);
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (prefab != null)
            {
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material m in renderer.sharedMaterials)
                    {
                        if (m != null && !string.IsNullOrEmpty(m.name)) names.Add(m.name);
                    }
                }
            }
            foreach (Material m in AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>())
            {
                if (!string.IsNullOrEmpty(m.name)) names.Add(m.name);
            }
            if (names.Count == 0)
            {
                Debug.LogWarning("MegaScans URP: No material slots found in " + modelPath + ". You may need to enable Import Materials on this FBX and run the processor again.");
            }

            var existingMap = importer.GetExternalObjectMap();
            foreach (string name in names)
            {
                var key = new AssetImporter.SourceAssetIdentifier(typeof(Material), name);
                UnityEngine.Object existing;
                if (existingMap.TryGetValue(key, out existing) && existing == material) continue;
                importer.AddRemap(key, material);
                changed = true;
            }
            if (!changed) return;
            AssetDatabase.WriteImportSettingsIfDirty(modelPath);
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
        }

        private static TextureSet DiscoverTextureSet(string modelPath)
        {
            string directory = System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
            string filename = System.IO.Path.GetFileNameWithoutExtension(modelPath);
            string normalized = StripLodSuffix(filename);
            string id = ExtractMegascansId(filename);
            List<TextureSource> sources = new List<TextureSource>();

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { directory }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsSourceTexture(path)) continue;
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                string kind = ClassifyTexture(stem);
                if (kind == null) continue;
                string textureDir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
                bool sameFolder = string.Equals(textureDir, directory, StringComparison.OrdinalIgnoreCase);
                bool fullMatch = stem.StartsWith(filename + "_", StringComparison.OrdinalIgnoreCase) || string.Equals(stem, filename, StringComparison.OrdinalIgnoreCase);
                bool lodMatch = stem.StartsWith(normalized + "_", StringComparison.OrdinalIgnoreCase);
                bool idMatch = id != null && stem.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!fullMatch && !lodMatch && !idMatch) continue;
                int score = (fullMatch ? 1000 : lodMatch ? 800 : 500) + (sameFolder ? 100 : 0) + ResolutionScore(stem);
                if (kind == "NormalGL") score += 30;
                sources.Add(new TextureSource { Path = path, Kind = kind, Score = score });
            }

            // Some Fab downloads use generic texture names, but only fall back when
            // there is exactly one FBX in that folder to avoid cross-asset mixing.
            if (sources.Count == 0)
            {
                int fbxCount = AssetDatabase.FindAssets("t:Model", new[] { directory }).Select(AssetDatabase.GUIDToAssetPath).Count(p => IsSourceModel(p) && string.Equals(System.IO.Path.GetDirectoryName(p).Replace('\\', '/'), directory, StringComparison.OrdinalIgnoreCase));
                if (fbxCount == 1)
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { directory }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!IsSourceTexture(path) || !string.Equals(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'), directory, StringComparison.OrdinalIgnoreCase)) continue;
                        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                        string kind = ClassifyTexture(stem);
                        if (kind != null) sources.Add(new TextureSource { Path = path, Kind = kind, Score = ResolutionScore(stem) });
                    }
                }
            }

            TextureSet result = new TextureSet();
            result.BaseColor = Best(sources, "BaseColor");
            result.Normal = Best(sources, "NormalGL") ?? Best(sources, "Normal") ?? Best(sources, "NormalDX");
            result.Roughness = Best(sources, "Roughness");
            result.AO = Best(sources, "AO");
            result.Metalness = Best(sources, "Metalness");
            result.Opacity = Best(sources, "Opacity");
            TextureSource packed = sources.Where(s => s.Kind == "ORM" || s.Kind == "ARM" || s.Kind == "RMA" || s.Kind == "ORD" || s.Kind == "MRA").OrderByDescending(s => s.Score).FirstOrDefault();
            if (packed != null) { result.Packed = packed.Path; result.PackedKind = packed.Kind; }
            return result;
        }

        private static string Best(List<TextureSource> sources, string kind)
        {
            TextureSource source = sources.Where(s => s.Kind == kind).OrderByDescending(s => s.Score).FirstOrDefault();
            return source != null ? source.Path : null;
        }

        private static string StripLodSuffix(string name)
        {
            return Regex.Replace(name, @"(?i)(?:[_-](?:low|high|mid|nanite|lod[0-9]+))+$", "");
        }

        private static string ExtractMegascansId(string name)
        {
            MatchCollection matches = Regex.Matches(name, @"(?i)(?:^|[_-])([a-z0-9]{7,9})(?=[_-]|$)");
            return matches.Count > 0 ? matches[matches.Count - 1].Groups[1].Value : null;
        }

        private static int ResolutionScore(string name)
        {
            Match match = Regex.Match(name, @"(?i)(?:^|[_-])([0-9]{1,2})k(?:[_-]|$)");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) ? value * 4 : 0;
        }

        private static string ClassifyTexture(string stem)
        {
            string[] tokens = Regex.Split(stem.ToLowerInvariant(), @"[_\-. ]+");
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string t = tokens[i];
                if (t == "basecolor" || t == "albedo" || t == "diffuse" || t == "diff" || t == "basecolour") return "BaseColor";
                if (t == "normalgl" || t == "normalogl" || t == "normalopengl") return "NormalGL";
                if (t == "normaldx" || t == "normaldirectx") return "NormalDX";
                if (t == "normal" || t == "normals" || t == "nrm") return i + 1 < tokens.Length && tokens[i + 1] == "dx" ? "NormalDX" : i + 1 < tokens.Length && (tokens[i + 1] == "gl" || tokens[i + 1] == "opengl") ? "NormalGL" : "Normal";
                if (t == "roughness" || t == "rough") return "Roughness";
                if (t == "ao" || t == "ambientocclusion" || t == "occlusion") return "AO";
                if (t == "metalness" || t == "metallic" || t == "metal") return "Metalness";
                if (t == "opacity" || t == "alpha" || t == "opacitymask" || t == "mask") return "Opacity";
                if (t == "orm") return "ORM";
                if (t == "arm") return "ARM";
                if (t == "rma" || t == "rmetal") return "RMA";
                if (t == "mra") return "MRA";
                if (t == "ord" || t == "ordp") return "ORD";
            }
            return null;
        }

        private static Texture2D LoadTexture(string path)
        {
            return !string.IsNullOrEmpty(path) ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
        }

        private static void ConfigureNormal(string path)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            bool flip = path.IndexOf("normaldx", StringComparison.OrdinalIgnoreCase) >= 0 || (path.IndexOf("normalgl", StringComparison.OrdinalIgnoreCase) < 0 && MegaScansURPImporterWindow.FlipUnknownNormals);
            if (importer.textureType == TextureImporterType.NormalMap && importer.flipGreenChannel == flip) return;
            importer.textureType = TextureImporterType.NormalMap;
            importer.flipGreenChannel = flip;
            importer.SaveAndReimport();
        }

        private static void ConfigureDataTexture(string path)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            if (importer.textureType == TextureImporterType.Default && !importer.sRGBTexture && importer.mipmapEnabled && !importer.isReadable) return;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        private static bool GeneratePackedMask(TextureSet source, string outputPath)
        {
            ImageData rough = ReadImage(source.Roughness);
            ImageData ao = ReadImage(source.AO);
            ImageData metallic = ReadImage(source.Metalness);
            ImageData packed = ReadImage(source.Packed);
            if (rough == null && ao == null && metallic == null && packed == null) return false;

            int width = 1;
            int height = 1;
            foreach (ImageData img in new[] { rough, ao, metallic, packed })
            {
                if (img == null) continue;
                width = Math.Max(width, img.Width);
                height = Math.Max(height, img.Height);
            }
            float scale = Mathf.Min(1f, (float)MegaScansURPImporterWindow.MaxPackedSize / Math.Max(width, height));
            width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(height * scale));

            Color32[] pixels = new Color32[width * height];
            byte fallbackSmooth = (byte)Mathf.RoundToInt(MegaScansURPImporterWindow.DefaultSmoothness * 255f);
            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    byte metal = metallic != null ? metallic.Sample(u, v, 0) : PackedMetal(packed, source.PackedKind, u, v);
                    byte occlusion = ao != null ? ao.Sample(u, v, 0) : PackedAO(packed, source.PackedKind, u, v);
                    byte roughness = rough != null ? rough.Sample(u, v, 0) : PackedRoughness(packed, source.PackedKind, u, v);
                    byte smoothness = rough != null || packed != null ? (byte)(255 - roughness) : fallbackSmooth;
                    pixels[y * width + x] = new Color32(metal, occlusion, 0, smoothness);
                }
            }
            return WritePng(outputPath, width, height, pixels);
        }

        private static byte PackedAO(ImageData packed, string kind, float u, float v)
        {
            if (packed == null) return 255;
            if (kind == "RMA" || kind == "MRA") return packed.Sample(u, v, 2);
            return packed.Sample(u, v, 0);
        }

        private static byte PackedMetal(ImageData packed, string kind, float u, float v)
        {
            if (packed == null || kind == "ORD") return 0;
            if (kind == "RMA") return packed.Sample(u, v, 1);
            if (kind == "MRA") return packed.Sample(u, v, 0);
            return packed.Sample(u, v, 2);
        }

        private static byte PackedRoughness(ImageData packed, string kind, float u, float v)
        {
            if (packed == null) return (byte)Mathf.RoundToInt((1f - MegaScansURPImporterWindow.DefaultSmoothness) * 255f);
            return packed.Sample(u, v, kind == "RMA" ? 0 : kind == "MRA" ? 1 : 1);
        }

        private static bool GenerateBaseColorAlpha(string baseColor, string opacity, string outputPath)
        {
            ImageData color = ReadImage(baseColor);
            ImageData alpha = ReadImage(opacity);
            if (color == null || alpha == null) return false;
            Color32[] pixels = new Color32[color.Pixels.Length];
            for (int y = 0; y < color.Height; y++)
            {
                float v = (y + 0.5f) / color.Height;
                for (int x = 0; x < color.Width; x++)
                {
                    float u = (x + 0.5f) / color.Width;
                    Color32 c = color.Pixels[y * color.Width + x];
                    c.a = (byte)((c.a * alpha.Sample(u, v, 0) + 127) / 255);
                    pixels[y * color.Width + x] = c;
                }
            }
            bool saved = WritePng(outputPath, color.Width, color.Height, pixels);
            TextureImporter importer = AssetImporter.GetAtPath(outputPath) as TextureImporter;
            if (importer != null && (!importer.sRGBTexture || !importer.alphaIsTransparency || importer.textureType != TextureImporterType.Default))
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }
            return saved;
        }

        private static ImageData ReadImage(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string extension = System.IO.Path.GetExtension(assetPath);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("MegaScans URP: Channel packing currently supports JPG/PNG inputs. Unsupported: " + assetPath);
                return null;
            }
            string filePath = System.IO.Path.GetFullPath(assetPath);
            if (!File.Exists(filePath)) return null;
            Texture2D temporary = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(temporary, File.ReadAllBytes(filePath), false)) return null;
                return new ImageData { Width = temporary.width, Height = temporary.height, Pixels = temporary.GetPixels32() };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        private static bool WritePng(string assetPath, int width, int height, Color32[] pixels)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            byte[] png;
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                png = ImageConversion.EncodeToPNG(texture);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
            string filePath = System.IO.Path.GetFullPath(assetPath);
            if (File.Exists(filePath) && File.ReadAllBytes(filePath).SequenceEqual(png)) return true;
            File.WriteAllBytes(filePath, png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }
    }

    public sealed class MegaScansURPPostprocessor : AssetPostprocessor
    {
        public Material OnAssignMaterialModel(Material sourceMaterial, Renderer renderer)
        {
            if (!MegaScansURPProcessor.IsSourceModel(assetPath)) return null;
            string directory = System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            string materialPath = directory + "/" + MegaScansURPImporterWindow.OutputFolder + "/" + name + "_URP.mat";
            return AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        }

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!MegaScansURPImporterWindow.AutoProcess) return;
            MegaScansURPProcessor.EnqueueChangedAssets(importedAssets.Concat(movedAssets));
        }
    }
}
#endif
