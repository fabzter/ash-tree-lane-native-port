using System;
using System.Collections.Generic;

namespace UnityEngine
{
    [Flags]
    public enum RigidbodyConstraints
    {
        None = 0,
        FreezePositionX = 2,
        FreezePositionY = 4,
        FreezePositionZ = 8,
        FreezeRotationX = 16,
        FreezeRotationY = 32,
        FreezeRotationZ = 64,
        FreezePosition = 14,
        FreezeRotation = 112,
        FreezeAll = 126
    }

    public class Collider : Behaviour
    {
        public bool isTrigger { get; set; }
        public Rigidbody attachedRigidbody => GetComponentInParent<Rigidbody>();
        public virtual Bounds WorldBounds => new Bounds(transform.position, Vector3.one);
    }

    public class BoxCollider : Collider
    {
        public Vector3 center { get; set; }
        public Vector3 size { get; set; } = Vector3.one;

        public override Bounds WorldBounds
        {
            get
            {
                Vector3 half = size * 0.5f;
                Matrix4x4 m = transform.localToWorldMatrix;
                Bounds b = new Bounds(m.MultiplyPoint3x4(center), Vector3.zero);
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    b.Encapsulate(m.MultiplyPoint3x4(center + new Vector3(half.x * sx, half.y * sy, half.z * sz)));
                }
                return b;
            }
        }

        public Vector3 ClosestPointWorld(Vector3 worldPt)
        {
            Vector3 local = transform.InverseTransformPoint(worldPt) - center;
            Vector3 half = size * 0.5f;
            Vector3 clamped = new Vector3(
                Mathf.Clamp(local.x, -half.x, half.x),
                Mathf.Clamp(local.y, -half.y, half.y),
                Mathf.Clamp(local.z, -half.z, half.z));
            return transform.TransformPoint(clamped + center);
        }

        public bool ContainsWorldPoint(Vector3 worldPt, float expand = 0f)
        {
            Vector3 local = transform.InverseTransformPoint(worldPt) - center;
            Vector3 half = size * 0.5f + new Vector3(expand, expand, expand);
            return MathF.Abs(local.x) <= half.x && MathF.Abs(local.y) <= half.y && MathF.Abs(local.z) <= half.z;
        }
    }

    public class CapsuleCollider : Collider
    {
        public Vector3 center { get; set; }
        public float radius { get; set; } = 0.5f;
        public float height { get; set; } = 2f;
        public int direction { get; set; } = 1;

        public void GetWorldSegment(out Vector3 pA, out Vector3 pB, out float worldRadius)
        {
            Vector3 scale = transform.lossyScale;
            float sXZ = MathF.Max(MathF.Abs(scale.x), MathF.Abs(scale.z));
            float sY = MathF.Abs(scale.y);
            worldRadius = radius * sXZ;
            float halfTotal = MathF.Max(height * 0.5f * sY, worldRadius);
            float halfSeg = MathF.Max(0f, halfTotal - worldRadius);
            Vector3 worldCenter = transform.TransformPoint(center);
            Vector3 axis = direction == 0 ? transform.right : (direction == 2 ? transform.forward : transform.up);
            pA = worldCenter - axis * halfSeg;
            pB = worldCenter + axis * halfSeg;
        }

        public override Bounds WorldBounds
        {
            get
            {
                GetWorldSegment(out var a, out var b, out float r);
                Vector3 mn = Vector3.Min(a, b) - new Vector3(r, r, r);
                Vector3 mx = Vector3.Max(a, b) + new Vector3(r, r, r);
                return new Bounds((mn + mx) * 0.5f, mx - mn);
            }
        }
    }

    public readonly struct WorldTri
    {
        public readonly Vector3 A, B, C;
        public readonly Vector3 Normal;
        public readonly float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

        public WorldTri(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a; B = b; C = c;
            Normal = Vector3.Cross(b - a, c - a).normalized;
            MinX = MathF.Min(a.x, MathF.Min(b.x, c.x));
            MaxX = MathF.Max(a.x, MathF.Max(b.x, c.x));
            MinY = MathF.Min(a.y, MathF.Min(b.y, c.y));
            MaxY = MathF.Max(a.y, MathF.Max(b.y, c.y));
            MinZ = MathF.Min(a.z, MathF.Min(b.z, c.z));
            MaxZ = MathF.Max(a.z, MathF.Max(b.z, c.z));
        }
    }

    public class MeshCollider : Collider
    {
        public Mesh sharedMesh { get; set; }
        public bool convex { get; set; }

        private bool _built;
        private Bounds _cachedBounds;
        private WorldTri[] _tris = Array.Empty<WorldTri>();
        private const int GridN = 16;
        private List<int>[] _grid;
        private float _gx0, _gz0, _invCellX, _invCellZ;

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            if (sharedMesh == null || sharedMesh.Indices == null || sharedMesh.Indices.Length < 3 || sharedMesh.InterleavedVertices == null)
            {
                _cachedBounds = new Bounds(transform.position, Vector3.zero);
                return;
            }

            float[] v = sharedMesh.InterleavedVertices;
            uint[] idx = sharedMesh.Indices;
            Matrix4x4 m = transform.localToWorldMatrix;
            int triCount = idx.Length / 3;
            var list = new List<WorldTri>(triCount);
            Vector3 bMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 bMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                int i0 = (int)idx[i] * 14;
                int i1 = (int)idx[i + 1] * 14;
                int i2 = (int)idx[i + 2] * 14;
                if (i0 + 2 >= v.Length || i1 + 2 >= v.Length || i2 + 2 >= v.Length) continue;
                Vector3 a = m.MultiplyPoint3x4(new Vector3(v[i0], v[i0 + 1], v[i0 + 2]));
                Vector3 b = m.MultiplyPoint3x4(new Vector3(v[i1], v[i1 + 1], v[i1 + 2]));
                Vector3 c = m.MultiplyPoint3x4(new Vector3(v[i2], v[i2 + 1], v[i2 + 2]));
                var tri = new WorldTri(a, b, c);
                list.Add(tri);
                bMin.x = MathF.Min(bMin.x, tri.MinX); bMin.y = MathF.Min(bMin.y, tri.MinY); bMin.z = MathF.Min(bMin.z, tri.MinZ);
                bMax.x = MathF.Max(bMax.x, tri.MaxX); bMax.y = MathF.Max(bMax.y, tri.MaxY); bMax.z = MathF.Max(bMax.z, tri.MaxZ);
            }

            _tris = list.ToArray();
            if (_tris.Length == 0)
            {
                _cachedBounds = new Bounds(transform.position, Vector3.zero);
                return;
            }
            _cachedBounds = new Bounds((bMin + bMax) * 0.5f, bMax - bMin);

            if (_tris.Length > 48)
            {
                float wx = MathF.Max(0.1f, bMax.x - bMin.x);
                float wz = MathF.Max(0.1f, bMax.z - bMin.z);
                _gx0 = bMin.x; _gz0 = bMin.z;
                _invCellX = GridN / wx; _invCellZ = GridN / wz;
                _grid = new List<int>[GridN * GridN];
                for (int ti = 0; ti < _tris.Length; ti++)
                {
                    ref readonly WorldTri t = ref _tris[ti];
                    int cx0 = Math.Clamp((int)((t.MinX - _gx0) * _invCellX), 0, GridN - 1);
                    int cx1 = Math.Clamp((int)((t.MaxX - _gx0) * _invCellX), 0, GridN - 1);
                    int cz0 = Math.Clamp((int)((t.MinZ - _gz0) * _invCellZ), 0, GridN - 1);
                    int cz1 = Math.Clamp((int)((t.MaxZ - _gz0) * _invCellZ), 0, GridN - 1);
                    for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int cell = cz * GridN + cx;
                        (_grid[cell] ??= new List<int>(4)).Add(ti);
                    }
                }
            }
        }

        public override Bounds WorldBounds { get { EnsureBuilt(); return _cachedBounds; } }
        internal WorldTri[] Triangles { get { EnsureBuilt(); return _tris; } }

        internal void QueryAabb(Bounds box, List<int> outIndices, HashSet<int> dedup)
        {
            EnsureBuilt();
            if (_tris.Length == 0 || !_cachedBounds.Intersects(box)) return;
            if (_grid == null)
            {
                for (int i = 0; i < _tris.Length; i++) outIndices.Add(i);
                return;
            }
            dedup.Clear();
            Vector3 mn = box.min, mx = box.max;
            int cx0 = Math.Clamp((int)((mn.x - _gx0) * _invCellX), 0, GridN - 1);
            int cx1 = Math.Clamp((int)((mx.x - _gx0) * _invCellX), 0, GridN - 1);
            int cz0 = Math.Clamp((int)((mn.z - _gz0) * _invCellZ), 0, GridN - 1);
            int cz1 = Math.Clamp((int)((mx.z - _gz0) * _invCellZ), 0, GridN - 1);
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                var cell = _grid[cz * GridN + cx];
                if (cell == null) continue;
                for (int k = 0; k < cell.Count; k++)
                {
                    int ti = cell[k];
                    if (dedup.Add(ti)) outIndices.Add(ti);
                }
            }
        }
    }
}
