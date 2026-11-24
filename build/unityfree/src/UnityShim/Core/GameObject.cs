using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public class Transform : Component
    {
        private readonly List<Transform> _children = new List<Transform>();
        private Transform _parent;
        public Vector3 localPosition { get; set; } = Vector3.zero;
        public Quaternion localRotation { get; set; } = Quaternion.identity;
        public Vector3 localScale { get; set; } = Vector3.one;
        internal int siblingOrder;

        public Transform parent
        {
            get => _parent;
            set => SetParent(value, worldPositionStays: true);
        }

        public int childCount => _children.Count;
        public Transform GetChild(int index) => _children[index];

        public void SetParent(Transform newParent, bool worldPositionStays = true)
        {
            if (_parent == newParent) return;
            Vector3 wPos = worldPositionStays ? position : Vector3.zero;
            Quaternion wRot = worldPositionStays ? rotation : Quaternion.identity;
            _parent?._children.Remove(this);
            _parent = newParent;
            if (_parent != null)
            {
                _parent._children.Add(this);
                _parent._children.Sort((a, b) => a.siblingOrder.CompareTo(b.siblingOrder));
            }
            if (worldPositionStays)
            {
                position = wPos;
                rotation = wRot;
            }
            gameObject?.NotifyHierarchyActivationChanged();
        }

        public Vector3 position
        {
            get => _parent == null ? localPosition : _parent.localToWorldMatrix.MultiplyPoint3x4(localPosition);
            set => localPosition = _parent == null ? value : _parent.worldToLocalMatrix.MultiplyPoint3x4(value);
        }

        public Quaternion rotation
        {
            get => _parent == null ? localRotation : (_parent.rotation * localRotation).normalized;
            set => localRotation = _parent == null ? value : (Quaternion.Inverse(_parent.rotation) * value).normalized;
        }

        public Vector3 eulerAngles
        {
            get => rotation.eulerAngles;
            set => rotation = Quaternion.Euler(value);
        }

        public Vector3 localEulerAngles
        {
            get => localRotation.eulerAngles;
            set => localRotation = Quaternion.Euler(value);
        }

        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 right => rotation * Vector3.right;
        public Vector3 up => rotation * Vector3.up;

        public Vector3 lossyScale => _parent == null
            ? localScale
            : new Vector3(_parent.lossyScale.x * localScale.x, _parent.lossyScale.y * localScale.y, _parent.lossyScale.z * localScale.z);

        public Matrix4x4 localToWorldMatrix
        {
            get
            {
                Matrix4x4 local = Matrix4x4.TRS(localPosition, localRotation, localScale);
                return _parent == null ? local : _parent.localToWorldMatrix * local;
            }
        }

        public Matrix4x4 worldToLocalMatrix
        {
            get
            {
                // Exact inverse for TRS chains
                Vector3 invScale = new Vector3(
                    MathF.Abs(localScale.x) > 1e-6f ? 1f / localScale.x : 0f,
                    MathF.Abs(localScale.y) > 1e-6f ? 1f / localScale.y : 0f,
                    MathF.Abs(localScale.z) > 1e-6f ? 1f / localScale.z : 0f);
                Quaternion invRot = Quaternion.Inverse(localRotation);
                Vector3 rotatedNegPos = invRot * (-localPosition);
                Vector3 invPos = Vector3.Scale(rotatedNegPos, invScale);
                Matrix4x4 invLocal = Matrix4x4.TRS(invPos, invRot, invScale);
                // Careful: TRS applies scale then rot, whereas inv(T*R*S) = S^-1 * R^-1 * T^-1
                Matrix4x4 sm = Matrix4x4.identity;
                sm.m00 = invScale.x; sm.m11 = invScale.y; sm.m22 = invScale.z;
                Matrix4x4 rm = Matrix4x4.TRS(Vector3.zero, invRot, Vector3.one);
                Matrix4x4 tm = Matrix4x4.TRS(-localPosition, Quaternion.identity, Vector3.one);
                invLocal = sm * rm * tm;
                return _parent == null ? invLocal : invLocal * _parent.worldToLocalMatrix;
            }
        }

        public Vector3 TransformPoint(Vector3 localPt) => localToWorldMatrix.MultiplyPoint3x4(localPt);
        public Vector3 InverseTransformPoint(Vector3 worldPt) => worldToLocalMatrix.MultiplyPoint3x4(worldPt);
        public Vector3 TransformDirection(Vector3 dir) => rotation * dir;
        public Vector3 InverseTransformDirection(Vector3 dir) => Quaternion.Inverse(rotation) * dir;
    }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; } = new Vector2(0.5f, 0.5f);
        public Vector2 anchorMax { get; set; } = new Vector2(0.5f, 0.5f);
        public Vector2 anchoredPosition { get; set; } = Vector2.zero;
        public Vector2 sizeDelta { get; set; } = new Vector2(100f, 100f);
        public Vector2 pivot { get; set; } = new Vector2(0.5f, 0.5f);

        // Computes top-left screen-space pixel rect from bottom-up Unity RectTransform math
        public Rect ComputeScreenRect(Rect parentScreenRect, float uiScale)
        {
            // Unity local UI coordinates: y goes UP, parent rect bottom-left = (x, y)
            float anchorLeft = parentScreenRect.x + anchorMin.x * parentScreenRect.width;
            float anchorRight = parentScreenRect.x + anchorMax.x * parentScreenRect.width;
            float anchorBottom = parentScreenRect.y + anchorMin.y * parentScreenRect.height;
            float anchorTop = parentScreenRect.y + anchorMax.y * parentScreenRect.height;

            float width = (anchorRight - anchorLeft) + sizeDelta.x * uiScale * MathF.Abs(localScale.x);
            float height = (anchorTop - anchorBottom) + sizeDelta.y * uiScale * MathF.Abs(localScale.y);
            float anchorPivotX = (anchorLeft + anchorRight) * 0.5f;
            float anchorPivotY = (anchorBottom + anchorTop) * 0.5f;
            float posX = anchorPivotX + anchoredPosition.x * uiScale - pivot.x * width;
            float posYBottom = anchorPivotY + anchoredPosition.y * uiScale - pivot.y * height;
            return new Rect(posX, posYBottom, width, height);
        }
    }

    public class GameObject : Object
    {
        private readonly List<Component> _components = new List<Component>();
        private bool _activeSelf = true;

        public Transform transform { get; internal set; }
        public bool activeSelf => _activeSelf;
        public bool activeInHierarchy => !_destroyed && _activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public string tag { get; set; } = "Untagged";
        public int layer { get; set; }

        public GameObject() : this("GameObject", false) { }
        public GameObject(string name) : this(name, false) { }
        internal GameObject(string name, bool isRectTransform)
        {
            this.name = name ?? "GameObject";
            transform = isRectTransform ? new RectTransform() : new Transform();
            transform.gameObject = this;
            _components.Add(transform);
            UnityShim.Backend.SceneRegistry.RegisterGameObject(this);
        }

        internal void ReplaceWithRectTransform()
        {
            if (transform is RectTransform) return;
            var rt = new RectTransform { gameObject = this };
            _components[0] = rt;
            transform = rt;
        }

        internal void SetInitialActiveSelf(bool value) => _activeSelf = value;

        public void SetActive(bool value)
        {
            if (_activeSelf == value) return;
            _activeSelf = value;
            NotifyHierarchyActivationChanged();
        }

        internal void NotifyHierarchyActivationChanged()
        {
            foreach (var c in _components)
            {
                if (c is MonoBehaviour mb) mb.InternalSyncActivation();
            }
            for (int i = 0; i < transform.childCount; i++)
            {
                transform.GetChild(i).gameObject.NotifyHierarchyActivationChanged();
            }
        }

        public bool CompareTag(string t) => string.Equals(tag, t, StringComparison.Ordinal);

        public T AddComponent<T>() where T : Component, new()
        {
            var comp = new T();
            AttachComponent(comp);
            return comp;
        }

        public Component AddComponent(Type type)
        {
            if (Activator.CreateInstance(type) is Component comp)
            {
                AttachComponent(comp);
                return comp;
            }
            throw new InvalidOperationException($"Type {type.FullName} is not a Component");
        }

        internal void AttachComponent(Component comp, bool triggerLifecycleNow = true)
        {
            comp.gameObject = this;
            _components.Add(comp);
            UnityShim.Backend.SceneRegistry.RegisterComponent(comp);
            if (triggerLifecycleNow && comp is MonoBehaviour mb)
            {
                mb.InternalSyncActivation();
            }
        }

        public T GetComponent<T>() where T : class
        {
            foreach (var c in _components) if (c is T matched && !c._destroyed) return matched;
            return null;
        }

        public Component GetComponent(Type type)
        {
            foreach (var c in _components) if (type.IsInstanceOfType(c) && !c._destroyed) return c;
            return null;
        }

        public T[] GetComponents<T>() where T : class
        {
            var list = new List<T>();
            foreach (var c in _components) if (c is T matched && !c._destroyed) list.Add(matched);
            return list.ToArray();
        }

        public T GetComponentInChildren<T>(bool includeInactive = false) where T : class
        {
            if (includeInactive || _activeSelf)
            {
                var hit = GetComponent<T>();
                if (hit != null) return hit;
            }
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i).gameObject;
                if (!includeInactive && !child.activeSelf) continue;
                var hit = child.GetComponentInChildren<T>(includeInactive);
                if (hit != null) return hit;
            }
            return null;
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class
        {
            var results = new List<T>();
            CollectInChildren(this, includeInactive, results);
            return results.ToArray();
        }

        private static void CollectInChildren<T>(GameObject go, bool includeInactive, List<T> results) where T : class
        {
            if (!includeInactive && !go.activeSelf) return;
            foreach (var c in go._components) if (c is T matched && !c._destroyed) results.Add(matched);
            for (int i = 0; i < go.transform.childCount; i++)
            {
                CollectInChildren(go.transform.GetChild(i).gameObject, includeInactive, results);
            }
        }

        public T GetComponentInParent<T>() where T : class
        {
            for (Transform t = transform; t != null; t = t.parent)
            {
                var hit = t.gameObject.GetComponent<T>();
                if (hit != null) return hit;
            }
            return null;
        }
    }
}

namespace UnityShim.Backend
{
    public static class SceneRegistry
    {
        public static int SceneGeneration { get; private set; }
        public static readonly List<GameObject> GameObjects = new List<GameObject>();
        public static readonly List<Component> AllComponents = new List<Component>();
        public static readonly List<MonoBehaviour> MonoBehaviours = new List<MonoBehaviour>();
        private static readonly List<MonoBehaviour> _pendingStart = new List<MonoBehaviour>();

        public static void Clear()
        {
            SceneGeneration++;
            GameObjects.Clear();
            AllComponents.Clear();
            MonoBehaviours.Clear();
            _pendingStart.Clear();
        }

        public static void RegisterGameObject(GameObject go)
        {
            GameObjects.Add(go);
            if (go.transform != null) RegisterComponent(go.transform);
        }

        public static void RegisterComponent(Component comp)
        {
            if (comp is Transform && AllComponents.Contains(comp)) return;
            AllComponents.Add(comp);
            if (comp is MonoBehaviour mb) MonoBehaviours.Add(mb);
        }

        internal static void EnqueueStart(MonoBehaviour mb)
        {
            if (!mb._started && !_pendingStart.Contains(mb)) _pendingStart.Add(mb);
        }

        public static T FindObjectOfType<T>() where T : UnityEngine.Object
        {
            if (typeof(T) == typeof(GameObject))
            {
                foreach (var go in GameObjects) if (!go._destroyed && go.activeInHierarchy) return go as T;
            }
            foreach (var c in AllComponents)
            {
                if (!c._destroyed && c is T matched && c.gameObject != null && c.gameObject.activeInHierarchy) return matched;
            }
            return null;
        }

        public static T[] FindObjectsOfType<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            foreach (var c in AllComponents)
            {
                if (!c._destroyed && c is T matched && c.gameObject != null && c.gameObject.activeInHierarchy) list.Add(matched);
            }
            return list.ToArray();
        }

        public static List<T> GetAllComponents<T>() where T : class
        {
            var list = new List<T>();
            for (int i = 0; i < AllComponents.Count; i++)
            {
                var c = AllComponents[i];
                if (!c._destroyed && c is T t) list.Add(t);
            }
            return list;
        }

        public static void ActivateSceneAndDispatchLifecycle()
        {
            var snapshot = new List<GameObject>(GameObjects);
            foreach (var go in snapshot)
            {
                if (go.activeInHierarchy) go.NotifyHierarchyActivationChanged();
            }
            DispatchPendingStarts();
        }

        public static void DispatchPendingStarts()
        {
            while (_pendingStart.Count > 0)
            {
                var batch = _pendingStart.ToArray();
                _pendingStart.Clear();
                foreach (var mb in batch) mb.InternalStart();
            }
        }

        public static void DispatchFixedUpdate()
        {
            var snap = MonoBehaviours.ToArray();
            foreach (var mb in snap) mb.InternalFixedUpdate();
        }

        public static void DispatchUpdate()
        {
            DispatchPendingStarts();
            var snap = MonoBehaviours.ToArray();
            foreach (var mb in snap) mb.InternalUpdate();
        }

        public static void DispatchLateUpdate()
        {
            var snap = MonoBehaviours.ToArray();
            foreach (var mb in snap) mb.InternalLateUpdate();
        }

        public static void DispatchTriggerEnter(GameObject receiver, Collider other)
        {
            foreach (var mb in receiver.GetComponents<MonoBehaviour>()) mb.InternalOnTriggerEnter(other);
        }

        public static void DispatchTriggerExit(GameObject receiver, Collider other)
        {
            foreach (var mb in receiver.GetComponents<MonoBehaviour>()) mb.InternalOnTriggerExit(other);
        }
    }
}
