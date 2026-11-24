using System;
using System.Collections.Concurrent;
using System.Reflection;
using UnityShim.Backend;

namespace UnityEngine
{
    public class Object
    {
        internal bool _destroyed;
        public string name { get; set; } = string.Empty;
        public HideFlags hideFlags { get; set; }

        public static implicit operator bool(Object exists) => exists is not null && !exists._destroyed;
        public static bool operator ==(Object x, Object y)
        {
            bool xNull = x is null || x._destroyed;
            bool yNull = y is null || y._destroyed;
            if (xNull && yNull) return true;
            if (xNull || yNull) return false;
            return ReferenceEquals(x, y);
        }
        public static bool operator !=(Object x, Object y) => !(x == y);
        public override bool Equals(object other) => other is Object o && this == o;
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        public override string ToString() => name;

        public static void Destroy(Object obj)
        {
            if (obj == null) return;
            obj._destroyed = true;
        }
        public static void DestroyImmediate(Object obj) => Destroy(obj);
        public static T FindObjectOfType<T>() where T : Object => SceneRegistry.FindObjectOfType<T>();
        public static T[] FindObjectsOfType<T>() where T : Object => SceneRegistry.FindObjectsOfType<T>();
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
        public Transform transform => gameObject?.transform;

        public new string name
        {
            get => gameObject != null ? gameObject.name : base.name;
            set { if (gameObject != null) gameObject.name = value; else base.name = value; }
        }

        public string tag
        {
            get => gameObject != null ? gameObject.tag : "Untagged";
            set { if (gameObject != null) gameObject.tag = value; }
        }

        public bool CompareTag(string t) => gameObject != null && gameObject.CompareTag(t);
        public T GetComponent<T>() where T : class => gameObject?.GetComponent<T>();
        public Component GetComponent(Type type) => gameObject?.GetComponent(type);
        public T GetComponentInChildren<T>(bool includeInactive = false) where T : class => gameObject?.GetComponentInChildren<T>(includeInactive);
        public T[] GetComponents<T>() where T : class => gameObject != null ? gameObject.GetComponents<T>() : Array.Empty<T>();
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : class => gameObject != null ? gameObject.GetComponentsInChildren<T>(includeInactive) : Array.Empty<T>();
        public T GetComponentInParent<T>() where T : class => gameObject?.GetComponentInParent<T>();
    }

    public class Behaviour : Component
    {
        private bool _enabled = true;
        public bool enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnEnabledStateChanged();
            }
        }
        public bool isActiveAndEnabled => !_destroyed && _enabled && gameObject != null && gameObject.activeInHierarchy;
        internal void SetEnabledDirect(bool value) => _enabled = value;
        internal virtual void OnEnabledStateChanged() { }
    }

    internal sealed class MonoMethodTable
    {
        public MethodInfo Awake;
        public MethodInfo OnEnable;
        public MethodInfo Start;
        public MethodInfo FixedUpdate;
        public MethodInfo Update;
        public MethodInfo LateUpdate;
        public MethodInfo OnDisable;
        public MethodInfo OnDestroy;
        public MethodInfo OnTriggerEnter;
        public MethodInfo OnTriggerExit;
        public MethodInfo OnRenderImage;
        public MethodInfo Reset;
    }

    public class MonoBehaviour : Behaviour
    {
        private static readonly ConcurrentDictionary<Type, MonoMethodTable> _cache = new();
        private MonoMethodTable _methods;
        internal bool _awoken;
        internal bool _started;
        internal bool _isEnabledActive;

        private MonoMethodTable Methods => _methods ??= _cache.GetOrAdd(GetType(), BuildTable);

        private static MonoMethodTable BuildTable(Type t)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo Find(string name)
            {
                for (Type cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                {
                    var m = cur.GetMethod(name, F);
                    if (m != null) return m;
                }
                return null;
            }
            return new MonoMethodTable
            {
                Awake = Find("Awake"),
                OnEnable = Find("OnEnable"),
                Start = Find("Start"),
                FixedUpdate = Find("FixedUpdate"),
                Update = Find("Update"),
                LateUpdate = Find("LateUpdate"),
                OnDisable = Find("OnDisable"),
                OnDestroy = Find("OnDestroy"),
                OnTriggerEnter = Find("OnTriggerEnter"),
                OnTriggerExit = Find("OnTriggerExit"),
                OnRenderImage = Find("OnRenderImage"),
                Reset = Find("Reset"),
            };
        }

        internal override void OnEnabledStateChanged() => InternalSyncActivation();

        internal void InternalSyncActivation()
        {
            if (_destroyed) return;
            bool nowActive = isActiveAndEnabled;
            if (nowActive && !_awoken)
            {
                _awoken = true;
                try { Methods.Awake?.Invoke(this, null); }
                catch (Exception ex) { Debug.LogError($"Awake error in {GetType().Name}: {ex.InnerException ?? ex}"); }
            }
            if (nowActive && !_isEnabledActive)
            {
                _isEnabledActive = true;
                try { Methods.OnEnable?.Invoke(this, null); }
                catch (Exception ex) { Debug.LogError($"OnEnable error in {GetType().Name}: {ex.InnerException ?? ex}"); }
                if (!_started) SceneRegistry.EnqueueStart(this);
            }
            else if (!nowActive && _isEnabledActive)
            {
                _isEnabledActive = false;
                try { Methods.OnDisable?.Invoke(this, null); }
                catch (Exception ex) { Debug.LogError($"OnDisable error in {GetType().Name}: {ex.InnerException ?? ex}"); }
            }
        }

        internal void InternalStart()
        {
            if (_started || !isActiveAndEnabled) return;
            _started = true;
            try { Methods.Start?.Invoke(this, null); }
            catch (Exception ex) { Debug.LogError($"Start error in {GetType().Name}: {ex.InnerException ?? ex}"); }
        }

        internal void InternalFixedUpdate()
        {
            if (!isActiveAndEnabled) return;
            Methods.FixedUpdate?.Invoke(this, null);
        }

        internal void InternalUpdate()
        {
            if (!isActiveAndEnabled) return;
            Methods.Update?.Invoke(this, null);
        }

        internal void InternalLateUpdate()
        {
            if (!isActiveAndEnabled) return;
            Methods.LateUpdate?.Invoke(this, null);
        }

        internal void InternalOnTriggerEnter(Collider other)
        {
            if (!isActiveAndEnabled || Methods.OnTriggerEnter == null) return;
            Methods.OnTriggerEnter.Invoke(this, new object[] { other });
        }

        internal void InternalOnTriggerExit(Collider other)
        {
            if (!isActiveAndEnabled || Methods.OnTriggerExit == null) return;
            Methods.OnTriggerExit.Invoke(this, new object[] { other });
        }

        public bool InternalHasOnRenderImage => Methods.OnRenderImage != null;

        public void InternalOnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (!isActiveAndEnabled || Methods.OnRenderImage == null) return;
            Methods.OnRenderImage.Invoke(this, new object[] { src, dst });
        }
    }
}
