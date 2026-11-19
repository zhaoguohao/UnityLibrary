// CrossProjectHierarchyClipboard.cs
// Place this file in Assets/Editor in BOTH Unity projects.
// Intended for Unity 2019.4 through Unity 7 (7000.x).
// Cross-project transfer uses the operating system's text clipboard.
// This copies scene GameObjects, not prefab links or asset file contents.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityLibrary.EditorTools
{
    public static class CrossProjectHierarchyClipboard
    {
        private const string ClipboardHeader = "UNITY_CROSS_PROJECT_HIERARCHY_V1\n";
        private const int FormatVersion = 1;
        private const int MaximumObjects = 100000;

        // If an asset's GUID/path cannot be found, allow a unique match by
        // asset name and type. Ambiguous matches are deliberately rejected.
        private const bool AllowUniqueAssetNameFallback = true;

        [Serializable]
        private class Package
        {
            public int version = FormatVersion;
            public string sourceUnityVersion;
            public List<Node> nodes = new List<Node>();
        }

        [Serializable]
        private class Node
        {
            public int id;
            public int parentId = -1;
            public string name;
            public bool active;
            public string tag;
            public string layerName;
            public int layer;
            public int staticFlags;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
            public Vector3 worldPosition;
            public Quaternion worldRotation;
            public Vector3 worldScale;
            public bool hasRectTransform;
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 pivot;
            public Vector2 sizeDelta;
            public Vector2 anchoredPosition;
            public List<Part> parts = new List<Part>();
        }

        [Serializable]
        private class Part
        {
            public int componentIndex;
            public string typeFullName;
            public string assemblyName;
            public bool isTransform;
            public List<Field> fields = new List<Field>();
        }

        [Serializable]
        private class Field
        {
            public string path;
            public string kind;
            public string stringValue;
            public string enumName;
            public long integerValue;
            public double doubleValue;
            public bool boolValue;
            public Color color;
            public Vector2 vector2;
            public Vector3 vector3;
            public Vector4 vector4;
            public Quaternion quaternion;
            public Rect rect;
            public Bounds bounds;
            public Vector2Int vector2Int;
            public Vector3Int vector3Int;
            public RectInt rectInt;
            public BoundsInt boundsInt;
            public AnimationCurve curve;
            public GradientColorKey[] gradientColors;
            public GradientAlphaKey[] gradientAlphas;
            public int gradientMode;
            public Reference reference;
        }

        [Serializable]
        private class Reference
        {
            public string kind;  // null, hierarchy, asset, external
            public int nodeId;
            public int componentIndex = -1; // -1 = GameObject
            public string name;
            public string typeFullName;
            public string assemblyName;
            public string guid;
            public long localFileId;
            public string path;
        }

        private class CaptureContext
        {
            public readonly Dictionary<GameObject, int> nodeIds = new Dictionary<GameObject, int>();
            public readonly List<GameObject> orderedObjects = new List<GameObject>();
            public readonly Dictionary<Component, Reference> componentRefs = new Dictionary<Component, Reference>();
        }

        private class PasteContext
        {
            public readonly Dictionary<int, GameObject> nodes = new Dictionary<int, GameObject>();
            public readonly Dictionary<string, Component> components = new Dictionary<string, Component>();
            public readonly Dictionary<string, Object> assetCache = new Dictionary<string, Object>();
            public readonly HashSet<string> warnings = new HashSet<string>();
            public int componentsAdded;
            public int missingComponents;
            public int missingReferences;
            public int skippedFields;

            public void Warn(string message)
            {
                // Limit log spam for large selections.
                if (warnings.Count < 20)
                    warnings.Add(message);
            }
        }

        [MenuItem("Tools/Cross-Project Hierarchy/Copy Selected %&c", false, 1)]
        public static void CopySelected()
        {
            var selected = Selection.gameObjects.Where(go => go && go.scene.IsValid()).ToArray();
            if (selected.Length == 0)
            {
                Debug.LogWarning("Cross-Project Hierarchy: Select one or more scene GameObjects to copy.");
                return;
            }

            var selectedSet = new HashSet<GameObject>(selected);
            var roots = selected.Where(go => !HasSelectedAncestor(go.transform, selectedSet))
                                .OrderBy(go => GetHierarchySortKey(go.transform), StringComparer.Ordinal)
                                .ToArray();

            var package = new Package { sourceUnityVersion = Application.unityVersion };
            var context = new CaptureContext();

            // Pass 1: collect the complete hierarchy and stable IDs.
            foreach (var root in roots)
                CaptureStructure(root.transform, -1, package, context);

            if (package.nodes.Count > MaximumObjects)
            {
                Debug.LogError("Cross-Project Hierarchy: Selection is too large to copy safely.");
                return;
            }

            // Pass 2: gather component properties and their portable references.
            foreach (var node in package.nodes)
            {
                var sourceGo = context.orderedObjects[node.id];
                var components = sourceGo.GetComponents<Component>();
                for (int index = 0; index < components.Length; ++index)
                {
                    var component = components[index];
                    if (!component) // Missing script in the source project.
                        continue;

                    var type = component.GetType();
                    var part = new Part
                    {
                        componentIndex = index,
                        typeFullName = type.FullName,
                        assemblyName = type.Assembly.GetName().Name,
                        isTransform = component is Transform
                    };

                    if (!part.isTransform)
                        CaptureFields(component, part, context);
                    node.parts.Add(part);
                }
            }

            string json = JsonUtility.ToJson(package);
            EditorGUIUtility.systemCopyBuffer = ClipboardHeader + json;
            Debug.Log(string.Format(
                "Cross-Project Hierarchy: Copied {0} GameObjects ({1} root objects) to the system clipboard, {2:N0} characters.",
                package.nodes.Count, roots.Length, json.Length));
        }

        [MenuItem("Tools/Cross-Project Hierarchy/Copy Selected %&c", true)]
        private static bool ValidateCopy() { return Selection.gameObjects.Any(go => go && go.scene.IsValid()); }

        [MenuItem("Tools/Cross-Project Hierarchy/Paste Under Selected %&v", false, 2)]
        public static void PasteUnderSelected()
        {
            var parent = Selection.activeTransform;
            if (parent && !parent.gameObject.scene.IsValid())
                parent = null;
            Paste(parent);
        }

        [MenuItem("Tools/Cross-Project Hierarchy/Paste At Scene Root", false, 3)]
        public static void PasteAtRoot() { Paste(null); }

        [MenuItem("Tools/Cross-Project Hierarchy/Paste Under Selected %&v", true)]
        [MenuItem("Tools/Cross-Project Hierarchy/Paste At Scene Root", true)]
        private static bool ValidatePaste()
        {
            string buffer = EditorGUIUtility.systemCopyBuffer;
            return !string.IsNullOrEmpty(buffer) && buffer.StartsWith(ClipboardHeader, StringComparison.Ordinal);
        }

        private static bool HasSelectedAncestor(Transform child, HashSet<GameObject> selected)
        {
            for (var parent = child.parent; parent; parent = parent.parent)
                if (selected.Contains(parent.gameObject))
                    return true;
            return false;
        }

        private static string GetHierarchySortKey(Transform t)
        {
            var indices = new List<string>();
            while (t)
            {
                indices.Add(t.GetSiblingIndex().ToString("D8"));
                t = t.parent;
            }
            indices.Reverse();
            return string.Join("/", indices.ToArray());
        }

        private static void CaptureStructure(Transform transform, int parentId,
                                             Package package, CaptureContext context)
        {
            var go = transform.gameObject;
            int id = package.nodes.Count;
            var node = new Node
            {
                id = id,
                parentId = parentId,
                name = go.name,
                active = go.activeSelf,
                tag = go.tag,
                layer = go.layer,
                layerName = LayerMask.LayerToName(go.layer),
                staticFlags = (int)GameObjectUtility.GetStaticEditorFlags(go),
                localPosition = transform.localPosition,
                localRotation = transform.localRotation,
                localScale = transform.localScale,
                worldPosition = transform.position,
                worldRotation = transform.rotation,
                worldScale = transform.lossyScale,
                hasRectTransform = transform is RectTransform
            };

            var rect = transform as RectTransform;
            if (rect)
            {
                node.anchorMin = rect.anchorMin;
                node.anchorMax = rect.anchorMax;
                node.pivot = rect.pivot;
                node.sizeDelta = rect.sizeDelta;
                node.anchoredPosition = rect.anchoredPosition;
            }

            package.nodes.Add(node);
            context.nodeIds.Add(go, id);
            context.orderedObjects.Add(go);

            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; ++i)
            {
                if (!components[i])
                    continue;
                context.componentRefs[components[i]] = new Reference
                {
                    kind = "hierarchy",
                    nodeId = id,
                    componentIndex = i
                };
            }

            for (int child = 0; child < transform.childCount; ++child)
                CaptureStructure(transform.GetChild(child), id, package, context);
        }

        private static void CaptureFields(Component component, Part part, CaptureContext context)
        {
            try
            {
                var so = new SerializedObject(component);
                var iterator = so.GetIterator();
                bool hasMore = iterator.NextVisible(true);
                string ignoredManagedRoot = null;
                while (hasMore)
                {
                    string path = iterator.propertyPath;
                    if (!string.IsNullOrEmpty(ignoredManagedRoot))
                    {
                        if (path.StartsWith(ignoredManagedRoot + ".", StringComparison.Ordinal))
                        {
                            hasMore = iterator.NextVisible(true);
                            continue;
                        }
                        ignoredManagedRoot = null;
                    }

                    if (ShouldSkipProperty(path))
                    {
                        hasMore = iterator.NextVisible(true);
                        continue;
                    }

                    if (iterator.propertyType == SerializedPropertyType.ManagedReference)
                    {
                        // Polymorphic managed references are not portable across arbitrary assemblies.
                        ignoredManagedRoot = path;
                        hasMore = iterator.NextVisible(true);
                        continue;
                    }

                    try
                    {
                        var field = ReadField(iterator, context);
                        if (field != null)
                            part.fields.Add(field);
                    }
                    catch (Exception)
                    {
                        // An unsupported native property should never abort hierarchy copying.
                    }
                    hasMore = iterator.NextVisible(true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Cross-Project Hierarchy: Could not serialize " + component.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ShouldSkipProperty(string path)
        {
            return path == "m_Script" || path == "m_GameObject" ||
                   path == "m_ObjectHideFlags" || path == "m_CorrespondingSourceObject" ||
                   path == "m_PrefabInstance" || path == "m_PrefabAsset" ||
                   path == "m_EditorClassIdentifier" || path == "m_RootOrder";
        }

        private static Field ReadField(SerializedProperty p, CaptureContext context)
        {
            var field = new Field { path = p.propertyPath, kind = p.propertyType.ToString() };
            if (p.isArray && p.propertyType == SerializedPropertyType.Generic)
            {
                field.kind = "PortableArraySize";
                field.integerValue = p.arraySize;
                return field;
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    field.integerValue = p.longValue;
                    break;
                case SerializedPropertyType.Boolean:
                    field.boolValue = p.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    field.doubleValue = p.doubleValue;
                    break;
                case SerializedPropertyType.String:
                    field.stringValue = p.stringValue;
                    break;
                case SerializedPropertyType.Color:
                    field.color = p.colorValue;
                    break;
                case SerializedPropertyType.ObjectReference:
                    field.reference = CaptureReference(p.objectReferenceValue, context);
                    break;
                case SerializedPropertyType.ExposedReference:
                    field.reference = CaptureReference(p.exposedReferenceValue, context);
                    break;
                case SerializedPropertyType.Enum:
                    field.integerValue = p.intValue;
                    int index = p.enumValueIndex;
                    var names = p.enumNames;
                    if (index >= 0 && index < names.Length)
                        field.enumName = names[index];
                    break;
                case SerializedPropertyType.Vector2:
                    field.vector2 = p.vector2Value;
                    break;
                case SerializedPropertyType.Vector3:
                    field.vector3 = p.vector3Value;
                    break;
                case SerializedPropertyType.Vector4:
                    field.vector4 = p.vector4Value;
                    break;
                case SerializedPropertyType.Quaternion:
                    field.quaternion = p.quaternionValue;
                    break;
                case SerializedPropertyType.Rect:
                    field.rect = p.rectValue;
                    break;
                case SerializedPropertyType.Bounds:
                    field.bounds = p.boundsValue;
                    break;
                case SerializedPropertyType.Vector2Int:
                    field.vector2Int = p.vector2IntValue;
                    break;
                case SerializedPropertyType.Vector3Int:
                    field.vector3Int = p.vector3IntValue;
                    break;
                case SerializedPropertyType.RectInt:
                    field.rectInt = p.rectIntValue;
                    break;
                case SerializedPropertyType.BoundsInt:
                    field.boundsInt = p.boundsIntValue;
                    break;
                case SerializedPropertyType.AnimationCurve:
                    field.curve = p.animationCurveValue;
                    break;
                case SerializedPropertyType.Gradient:
                    // In Unity 2019.4, gradientValue is not a public API.
                    // It remains unchanged on the receiving side.
                    return null;
                default:
                    // Generic structs are handled through their children.
                    // Fixed-size buffers, new Unity 7-only types, etc. are skipped.
                    return null;
            }
            return field;
        }

        private static Reference CaptureReference(Object obj, CaptureContext context)
        {
            if (!obj)
                return new Reference { kind = "null" };

            var go = obj as GameObject;
            int nodeId;
            if (go && context.nodeIds.TryGetValue(go, out nodeId))
                return new Reference { kind = "hierarchy", nodeId = nodeId, componentIndex = -1 };

            var component = obj as Component;
            Reference sceneRef;
            if (component && context.componentRefs.TryGetValue(component, out sceneRef))
                return sceneRef;

            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path))
            {
                string guid;
                long localId;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out localId))
                {
                    guid = AssetDatabase.AssetPathToGUID(path);
                    localId = 0;
                }
                var type = obj.GetType();
                return new Reference
                {
                    kind = "asset",
                    name = obj.name,
                    path = path,
                    guid = guid,
                    localFileId = localId,
                    typeFullName = type.FullName,
                    assemblyName = type.Assembly.GetName().Name
                };
            }

            // Unity built-in mesh/materials/shaders sometimes have no asset path.
            if (obj is Mesh || obj is Material || obj is Shader)
            {
                var type = obj.GetType();
                return new Reference
                {
                    kind = "asset",
                    name = obj.name,
                    typeFullName = type.FullName,
                    assemblyName = type.Assembly.GetName().Name
                };
            }

            return new Reference { kind = "external", name = obj.name };
        }

        private static void Paste(Transform targetParent)
        {
            string buffer = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(buffer) || !buffer.StartsWith(ClipboardHeader, StringComparison.Ordinal))
            {
                Debug.LogWarning("Cross-Project Hierarchy: Clipboard has no hierarchy data. Copy in the other Unity Editor first.");
                return;
            }

            Package package;
            try { package = JsonUtility.FromJson<Package>(buffer.Substring(ClipboardHeader.Length)); }
            catch (Exception ex)
            {
                Debug.LogError("Cross-Project Hierarchy: Invalid clipboard JSON: " + ex.Message);
                return;
            }
            if (package == null || package.version != FormatVersion || package.nodes == null ||
                package.nodes.Count == 0 || package.nodes.Count > MaximumObjects)
            {
                Debug.LogError("Cross-Project Hierarchy: Unsupported or invalid clipboard format.");
                return;
            }

            var context = new PasteContext();
            var createdRoots = new List<GameObject>();
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Paste Cross-Project Hierarchy");

            try
            {
                // Pass 1: always recreate every GameObject and parent relationship.
                foreach (var node in package.nodes)
                {
                    if (context.nodes.ContainsKey(node.id))
                        throw new InvalidOperationException("Duplicate node ID in clipboard data.");

                    GameObject go = node.hasRectTransform
                        ? new GameObject(node.name, typeof(RectTransform))
                        : new GameObject(node.name);
                    go.SetActive(false);
                    Undo.RegisterCreatedObjectUndo(go, "Paste Cross-Project Hierarchy");
                    context.nodes.Add(node.id, go);

                    Transform parent;
                    if (node.parentId < 0)
                    {
                        parent = targetParent;
                        createdRoots.Add(go);
                    }
                    else if (context.nodes.ContainsKey(node.parentId))
                        parent = context.nodes[node.parentId].transform;
                    else
                        throw new InvalidOperationException("Invalid parent ID in clipboard data.");

                    if (parent)
                        go.transform.SetParent(parent, false);
                    else if (SceneManager.GetActiveScene().IsValid() &&
                             go.scene != SceneManager.GetActiveScene())
                        SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());

                    go.transform.localPosition = node.localPosition;
                    go.transform.localRotation = node.localRotation;
                    go.transform.localScale = node.localScale;

                    // Pasting a source child at scene root should preserve its world pose.
                    // Under a selected target, preserve the original LOCAL pose instead.
                    if (node.parentId < 0 && !parent)
                    {
                        go.transform.position = node.worldPosition;
                        go.transform.rotation = node.worldRotation;
                        go.transform.localScale = node.worldScale;
                    }

                    var rect = go.transform as RectTransform;
                    if (rect)
                    {
                        rect.anchorMin = node.anchorMin;
                        rect.anchorMax = node.anchorMax;
                        rect.pivot = node.pivot;
                        rect.sizeDelta = node.sizeDelta;
                        rect.anchoredPosition = node.anchoredPosition;
                        rect.localRotation = node.parentId < 0 && !parent
                            ? node.worldRotation : node.localRotation;
                        rect.localScale = node.parentId < 0 && !parent
                            ? node.worldScale : node.localScale;
                    }

                    int targetLayer = string.IsNullOrEmpty(node.layerName)
                        ? (node.layer >= 0 && node.layer < 8 ? node.layer : 0)
                        : LayerMask.NameToLayer(node.layerName);
                    go.layer = targetLayer >= 0 ? targetLayer : 0;
                    if (!string.IsNullOrEmpty(node.tag))
                    {
                        try { go.tag = node.tag; }
                        catch (UnityException) { /* Tag not defined in destination. */ }
                    }
                    try { GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)node.staticFlags); }
                    catch (Exception) { /* Static flags differ between Unity versions. */ }
                }

                // Pass 2: create all components BEFORE resolving scene references.
                foreach (var node in package.nodes)
                {
                    var go = context.nodes[node.id];
                    var reserved = new HashSet<Component>();
                    if (node.parts == null)
                        continue;
                    foreach (var part in node.parts)
                    {
                        if (part == null)
                            continue;
                        Component dest;
                        if (part.isTransform)
                        {
                            dest = go.transform;
                        }
                        else
                        {
                            var type = ResolveType(part.typeFullName, part.assemblyName);
                            if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract ||
                                typeof(Transform).IsAssignableFrom(type))
                            {
                                context.missingComponents++;
                                context.Warn("Missing component type: " + part.typeFullName);
                                continue;
                            }
                            dest = null;
                            foreach (var existing in go.GetComponents(type))
                            {
                                if (existing && existing.GetType() == type && !reserved.Contains(existing))
                                {
                                    dest = existing;
                                    break;
                                }
                            }
                            if (!dest)
                            {
                                try
                                {
                                    dest = go.AddComponent(type);
                                    context.componentsAdded++;
                                }
                                catch (Exception ex)
                                {
                                    context.missingComponents++;
                                    context.Warn("Could not add " + part.typeFullName + ": " + ex.Message);
                                    continue;
                                }
                            }
                        }
                        reserved.Add(dest);
                        context.components[ComponentKey(node.id, part.componentIndex)] = dest;
                    }
                }

                // Pass 3: apply serialized properties, including in-hierarchy links.
                foreach (var node in package.nodes)
                {
                    if (node.parts == null)
                        continue;
                    foreach (var part in node.parts)
                    {
                        if (part == null || part.isTransform)
                            continue;
                        Component dest;
                        if (!context.components.TryGetValue(ComponentKey(node.id, part.componentIndex), out dest))
                            continue;
                        ApplyFields(dest, part.fields, context);
                    }
                }

                // Pass 4: restore activeSelf AFTER all components are wired up.
                // We also set it on inactive parents' children to preserve activeSelf.
                foreach (var node in package.nodes)
                    context.nodes[node.id].SetActive(node.active);

                Selection.objects = createdRoots.Cast<Object>().ToArray();
                if (createdRoots.Count > 0)
                    EditorGUIUtility.PingObject(createdRoots[0]);
            }
            catch (Exception ex)
            {
                Debug.LogError("Cross-Project Hierarchy: Paste failed: " + ex);
                // The created objects are still in the Undo group and can be undone.
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            Debug.Log(string.Format(
                "Cross-Project Hierarchy: {0} GameObjects created. Components added: {1}. " +
                "Missing component types: {2}. Unresolved references: {3}. Skipped fields: {4}. " +
                "Source Unity: {5}. Target Unity: {6}.",
                context.nodes.Count, context.componentsAdded, context.missingComponents,
                context.missingReferences, context.skippedFields,
                package.sourceUnityVersion, Application.unityVersion));
            foreach (var warning in context.warnings)
                Debug.LogWarning("Cross-Project Hierarchy: " + warning);
        }

        private static string ComponentKey(int id, int index)
        {
            return id.ToString() + ":" + index.ToString();
        }

        private static void ApplyFields(Component target, List<Field> fields, PasteContext context)
        {
            if (fields == null || fields.Count == 0)
                return;
            try
            {
                var so = new SerializedObject(target);
                so.Update();

                // Array lengths must be set from outer to inner before assigning elements.
                var arrays = fields.Where(f => f.kind == "PortableArraySize")
                                   .OrderBy(f => f.path.Length);
                foreach (var field in arrays)
                {
                    try
                    {
                        var prop = so.FindProperty(field.path);
                        if (prop != null && prop.isArray)
                            prop.arraySize = Mathf.Clamp((int)field.integerValue, 0, 100000);
                    }
                    catch (Exception) { context.skippedFields++; }
                }
                so.ApplyModifiedProperties();
                so.Update();

                foreach (var field in fields)
                {
                    if (field.kind == "PortableArraySize")
                        continue;
                    try
                    {
                        var prop = so.FindProperty(field.path);
                        if (prop == null || prop.propertyType.ToString() != field.kind)
                        {
                            context.skippedFields++;
                            continue;
                        }
                        WriteField(prop, field, context);
                    }
                    catch (Exception)
                    {
                        context.skippedFields++;
                    }
                }
                so.ApplyModifiedProperties();
            }
            catch (Exception ex)
            {
                context.Warn("Could not restore " + target.GetType().Name + ": " + ex.Message);
            }
        }

        private static void WriteField(SerializedProperty prop, Field field, PasteContext context)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    prop.longValue = field.integerValue;
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = field.boolValue;
                    break;
                case SerializedPropertyType.Float:
                    prop.doubleValue = field.doubleValue;
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = field.stringValue;
                    break;
                case SerializedPropertyType.Color:
                    prop.colorValue = field.color;
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = ResolveReference(field.reference, context);
                    break;
                case SerializedPropertyType.ExposedReference:
                    prop.exposedReferenceValue = ResolveReference(field.reference, context);
                    break;
                case SerializedPropertyType.Enum:
                    if (!string.IsNullOrEmpty(field.enumName))
                    {
                        int index = Array.IndexOf(prop.enumNames, field.enumName);
                        if (index >= 0)
                        {
                            prop.enumValueIndex = index;
                            break;
                        }
                    }
                    prop.intValue = (int)field.integerValue;
                    break;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = field.vector2;
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = field.vector3;
                    break;
                case SerializedPropertyType.Vector4:
                    prop.vector4Value = field.vector4;
                    break;
                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = field.quaternion;
                    break;
                case SerializedPropertyType.Rect:
                    prop.rectValue = field.rect;
                    break;
                case SerializedPropertyType.Bounds:
                    prop.boundsValue = field.bounds;
                    break;
                case SerializedPropertyType.Vector2Int:
                    prop.vector2IntValue = field.vector2Int;
                    break;
                case SerializedPropertyType.Vector3Int:
                    prop.vector3IntValue = field.vector3Int;
                    break;
                case SerializedPropertyType.RectInt:
                    prop.rectIntValue = field.rectInt;
                    break;
                case SerializedPropertyType.BoundsInt:
                    prop.boundsIntValue = field.boundsInt;
                    break;
                case SerializedPropertyType.AnimationCurve:
                    prop.animationCurveValue = field.curve;
                    break;
                default:
                    context.skippedFields++;
                    break;
            }
        }

        private static Object ResolveReference(Reference reference, PasteContext context)
        {
            if (reference == null || reference.kind == "null")
                return null;
            if (reference.kind == "hierarchy")
            {
                Object obj = null;
                if (reference.componentIndex < 0)
                {
                    GameObject go;
                    if (context.nodes.TryGetValue(reference.nodeId, out go))
                        obj = go;
                }
                else
                {
                    Component part;
                    if (context.components.TryGetValue(ComponentKey(reference.nodeId, reference.componentIndex), out part))
                        obj = part;
                }
                if (!obj)
                    context.missingReferences++;
                return obj;
            }
            if (reference.kind != "asset")
            {
                context.missingReferences++;
                return null;
            }

            string key = (reference.guid ?? "") + "|" + reference.localFileId + "|" +
                         (reference.path ?? "") + "|" + (reference.name ?? "") + "|" +
                         (reference.typeFullName ?? "");
            Object cached;
            if (context.assetCache.TryGetValue(key, out cached))
                return cached;

            Type type = ResolveType(reference.typeFullName, reference.assemblyName);
            Object resolved = type == null ? null : FindAsset(reference, type);
            context.assetCache[key] = resolved;
            if (!resolved)
            {
                context.missingReferences++;
                context.Warn("Asset not found: " + reference.name + " (" + reference.path + ")");
            }
            return resolved;
        }

        private static Object FindAsset(Reference reference, Type type)
        {
            // GUID is the best match when the source .meta file was transferred.
            if (!string.IsNullOrEmpty(reference.guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(reference.guid);
                var asset = FindAtPath(path, reference, type);
                if (asset)
                    return asset;
            }

            // Useful when assets were separately imported with different GUIDs.
            if (!string.IsNullOrEmpty(reference.path))
            {
                var asset = FindAtPath(reference.path, reference, type);
                if (asset)
                    return asset;
            }

            if (type == typeof(Shader))
                return Shader.Find(reference.name);

            if (type == typeof(Mesh) && IsBuiltinReference(reference))
            {
                var mesh = FindBuiltinPrimitiveMesh(reference.name);
                if (mesh)
                    return mesh;
            }

            if (type == typeof(Material) && IsBuiltinReference(reference) &&
                (reference.name == "Default-Material" || reference.name == "Default Material"))
            {
                var material = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
                if (material)
                    return material;
            }

            if (!AllowUniqueAssetNameFallback || string.IsNullOrEmpty(reference.name))
                return null;

            // This fallback only accepts an exact, UNIQUE name and type match.
            // Never arbitrarily bind a reference to a different asset.
            string[] guids = AssetDatabase.FindAssets(reference.name + " t:" + type.Name);
            Object onlyMatch = null;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    continue;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!asset || !type.IsInstanceOfType(asset) || asset.name != reference.name)
                        continue;
                    if (onlyMatch && onlyMatch != asset)
                        return null;
                    onlyMatch = asset;
                }
            }
            return onlyMatch;
        }

        private static bool IsBuiltinReference(Reference reference)
        {
            return string.IsNullOrEmpty(reference.path) ||
                   reference.path.IndexOf("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reference.path.IndexOf("unity default resources", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Object FindAtPath(string path, Reference reference, Type type)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            Object nameMatch = null;
            Object idMatch = null;
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    if (!asset || !type.IsInstanceOfType(asset))
                        continue;
                    if (reference.localFileId != 0)
                    {
                        string guid;
                        long fileId;
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out fileId)
                            && fileId == reference.localFileId &&
                            (guid == reference.guid || asset.name == reference.name))
                            idMatch = asset;
                    }
                    if (asset.name == reference.name)
                        nameMatch = asset;
                }
                if (idMatch)
                    return idMatch;
                if (nameMatch)
                    return nameMatch;
                if (assets.Length == 1 && assets[0] && type.IsInstanceOfType(assets[0]) &&
                    (string.IsNullOrEmpty(reference.name) || assets[0].name == reference.name))
                    return assets[0];
            }
            catch (Exception) { /* Some built-in paths are not regular asset paths. */ }
            return null;
        }

        private static Mesh FindBuiltinPrimitiveMesh(string name)
        {
            PrimitiveType primitive;
            switch (name)
            {
                case "Cube": primitive = PrimitiveType.Cube; break;
                case "Sphere": primitive = PrimitiveType.Sphere; break;
                case "Capsule": primitive = PrimitiveType.Capsule; break;
                case "Cylinder": primitive = PrimitiveType.Cylinder; break;
                case "Plane": primitive = PrimitiveType.Plane; break;
                case "Quad": primitive = PrimitiveType.Quad; break;
                default: return null;
            }

            GameObject temp = null;
            try
            {
                temp = GameObject.CreatePrimitive(primitive);
                var filter = temp.GetComponent<MeshFilter>();
                return filter ? filter.sharedMesh : null;
            }
            finally
            {
                if (temp)
                    Object.DestroyImmediate(temp);
            }
        }

        private static Type ResolveType(string fullName, string assemblyName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;
            if (!string.IsNullOrEmpty(assemblyName))
            {
                var type = Type.GetType(fullName + ", " + assemblyName, false);
                if (type != null)
                    return type;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyName) &&
                    assembly.GetName().Name != assemblyName)
                    continue;
                var type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            // Unity built-in type locations sometimes change between versions.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return null;
        }
    }
}
