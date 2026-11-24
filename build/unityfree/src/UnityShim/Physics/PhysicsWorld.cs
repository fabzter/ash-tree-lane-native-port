using System;
using System.Collections.Generic;
using UnityEngine;
using UnityShim.Backend;

namespace UnityShim.Physics
{
    public sealed class PhysicsWorld : IPhysicsBackend
    {
        private readonly HashSet<(Collider dyn, Collider trig)> _activeTriggers = new();
        private readonly List<(Collider dyn, Collider trig)> _scratchRemove = new();
        private readonly List<int> _triIndices = new();
        private readonly HashSet<int> _triDedup = new();
        private int _lastGeneration = -1;

        public void Step(float dt)
        {
            if (_lastGeneration != SceneRegistry.SceneGeneration)
            {
                _activeTriggers.Clear();
                _lastGeneration = SceneRegistry.SceneGeneration;
            }

            var rigidbodies = SceneRegistry.GetAllComponents<Rigidbody>();
            var meshCols = SceneRegistry.GetAllComponents<MeshCollider>();
            var boxCols = SceneRegistry.GetAllComponents<BoxCollider>();
            var capsCols = SceneRegistry.GetAllComponents<CapsuleCollider>();

            // 1. Integrate non-kinematic rigidbodies & resolve capsule collisions
            foreach (var rb in rigidbodies)
            {
                if (!rb.gameObject.activeInHierarchy || rb.isKinematic) continue;
                Vector3 v = rb.velocity;
                if (rb.useGravity) v.y -= 9.81f * dt;
                if (rb.drag > 0f) v *= MathF.Max(0f, 1f - rb.drag * dt);

                rb.transform.position += v * dt;

                var cap = rb.gameObject.GetComponentInChildren<CapsuleCollider>();
                if (cap != null && cap.isActiveAndEnabled && !cap.isTrigger)
                {
                    ResolveCapsule(rb, cap, ref v, meshCols, boxCols);
                }
                rb.velocity = v;
            }

            // 2. Update triggers
            UpdateTriggers(capsCols, boxCols, meshCols);
        }

        private void ResolveCapsule(Rigidbody rb, CapsuleCollider cap, ref Vector3 vel,
                                    List<MeshCollider> meshCols, List<BoxCollider> boxCols)
        {
            for (int iter = 0; iter < 3; iter++)
            {
                cap.GetWorldSegment(out Vector3 pA, out Vector3 pB, out float r);
                Bounds capBounds = cap.WorldBounds;
                Bounds queryBounds = new Bounds(capBounds.center, capBounds.size + new Vector3(0.25f, 0.25f, 0.25f));
                Vector3 totalPush = Vector3.zero;

                // Mesh colliders
                for (int mci = 0; mci < meshCols.Count; mci++)
                {
                    var mc = meshCols[mci];
                    if (!mc.isActiveAndEnabled || mc.isTrigger) continue;
                    _triIndices.Clear();
                    mc.QueryAabb(queryBounds, _triIndices, _triDedup);
                    if (_triIndices.Count == 0) continue;
                    var tris = mc.Triangles;

                    for (int k = 0; k < _triIndices.Count; k++)
                    {
                        ref readonly WorldTri t = ref tris[_triIndices[k]];
                        if (t.MaxX < queryBounds.min.x || t.MinX > queryBounds.max.x ||
                            t.MaxY < queryBounds.min.y || t.MinY > queryBounds.max.y ||
                            t.MaxZ < queryBounds.min.z || t.MinZ > queryBounds.max.z) continue;

                        ClosestCapsuleTri(pA, pB, in t, out Vector3 segPt, out Vector3 triPt);
                        Vector3 diff = segPt - triPt;
                        float distSq = diff.sqrMagnitude;
                        if (distSq < r * r)
                        {
                            float dist = MathF.Sqrt(distSq);
                            Vector3 n = dist > 1e-5f ? diff / dist : (t.Normal.sqrMagnitude > 0.5f ? t.Normal : Vector3.up);
                            // Gentle step-up support on low lips <= 0.30m
                            if (n.y >= 0f && n.y < 0.45f && (triPt.y - (pA.y - r)) <= 0.28f && (triPt.y - (pA.y - r)) > 0.01f)
                            {
                                n = new Vector3(n.x * 0.25f, 0.9f, n.z * 0.25f).normalized;
                            }
                            float pen = r - dist;
                            Vector3 push = n * pen;
                            if (push.sqrMagnitude > totalPush.sqrMagnitude) totalPush = push;
                            rb.transform.position += push * 0.6f;
                            cap.GetWorldSegment(out pA, out pB, out r);
                            float vn = Vector3.Dot(vel, n);
                            if (vn < 0f) vel -= n * vn;
                        }
                    }
                }

                // Solid BoxColliders
                for (int bi = 0; bi < boxCols.Count; bi++)
                {
                    var bc = boxCols[bi];
                    if (!bc.isActiveAndEnabled || bc.isTrigger || bc.gameObject == rb.gameObject) continue;
                    if (!bc.WorldBounds.Intersects(queryBounds)) continue;
                    ClosestCapsuleBox(pA, pB, bc, out Vector3 segPt, out Vector3 boxPt);
                    Vector3 diff = segPt - boxPt;
                    float distSq = diff.sqrMagnitude;
                    if (distSq < r * r)
                    {
                        float dist = MathF.Sqrt(distSq);
                        Vector3 n = dist > 1e-5f ? diff / dist : Vector3.up;
                        rb.transform.position += n * (r - dist);
                        float vn = Vector3.Dot(vel, n);
                        if (vn < 0f) vel -= n * vn;
                    }
                }
            }
        }

        private void UpdateTriggers(List<CapsuleCollider> capsCols, List<BoxCollider> boxCols, List<MeshCollider> meshCols)
        {
            var current = new HashSet<(Collider dyn, Collider trig)>();
            foreach (var cap in capsCols)
            {
                if (!cap.isActiveAndEnabled || cap.isTrigger) continue;
                cap.GetWorldSegment(out Vector3 pA, out Vector3 pB, out float r);
                Bounds capBounds = cap.WorldBounds;

                foreach (var bc in boxCols)
                {
                    if (!bc.isActiveAndEnabled || !bc.isTrigger || bc.gameObject == cap.gameObject) continue;
                    if (!bc.WorldBounds.Intersects(capBounds)) continue;
                    if (OverlapsCapsuleBox(pA, pB, r, bc))
                    {
                        current.Add((cap, bc));
                    }
                }

                foreach (var mc in meshCols)
                {
                    if (!mc.isActiveAndEnabled || !mc.isTrigger || mc.gameObject == cap.gameObject) continue;
                    if (mc.WorldBounds.Intersects(capBounds)) current.Add((cap, mc));
                }
            }

            // Exits first (including disabled/inactive/destroyed triggers or players)
            _scratchRemove.Clear();
            foreach (var pair in _activeTriggers)
            {
                bool valid = !pair.dyn._destroyed && !pair.trig._destroyed &&
                             pair.dyn.isActiveAndEnabled && pair.trig.isActiveAndEnabled &&
                             current.Contains(pair);
                if (!valid) _scratchRemove.Add(pair);
            }
            foreach (var pair in _scratchRemove)
            {
                _activeTriggers.Remove(pair);
                if (!pair.trig._destroyed && !pair.dyn._destroyed)
                {
                    SceneRegistry.DispatchTriggerExit(pair.trig.gameObject, pair.dyn);
                    SceneRegistry.DispatchTriggerExit(pair.dyn.gameObject, pair.trig);
                }
            }

            // Enters
            foreach (var pair in current)
            {
                if (_activeTriggers.Add(pair))
                {
                    SceneRegistry.DispatchTriggerEnter(pair.trig.gameObject, pair.dyn);
                    SceneRegistry.DispatchTriggerEnter(pair.dyn.gameObject, pair.trig);
                }
            }
        }

        private static bool OverlapsCapsuleBox(Vector3 pA, Vector3 pB, float r, BoxCollider bc)
        {
            ClosestCapsuleBox(pA, pB, bc, out Vector3 segPt, out Vector3 boxPt);
            return (segPt - boxPt).sqrMagnitude <= (r * 1.05f) * (r * 1.05f) || bc.ContainsWorldPoint((pA + pB) * 0.5f, r);
        }

        private static void ClosestCapsuleBox(Vector3 pA, Vector3 pB, BoxCollider bc, out Vector3 segPt, out Vector3 boxPt)
        {
            float best = float.MaxValue;
            segPt = pA; boxPt = pA;
            for (int i = 0; i <= 4; i++)
            {
                Vector3 sp = Vector3.Lerp(pA, pB, i * 0.25f);
                Vector3 bp = bc.ClosestPointWorld(sp);
                float d = (sp - bp).sqrMagnitude;
                if (d < best) { best = d; segPt = sp; boxPt = bp; }
            }
        }

        private static void ClosestCapsuleTri(Vector3 pA, Vector3 pB, in WorldTri tri, out Vector3 segPt, out Vector3 triPt)
        {
            Vector3 segDir = pB - pA;
            float segLenSq = segDir.sqrMagnitude;
            float sCent = segLenSq > 1e-6f
                ? Math.Clamp(Vector3.Dot(((tri.A + tri.B + tri.C) * (1f / 3f)) - pA, segDir) / segLenSq, 0f, 1f)
                : 0f;

            segPt = pA; triPt = tri.A;
            float best = float.MaxValue;
            Span<float> samples = stackalloc float[4] { 0f, 0.5f, 1f, sCent };
            for (int i = 0; i < 4; i++)
            {
                Vector3 sp = pA + segDir * samples[i];
                Vector3 tp = ClosestPointOnTriangle(sp, tri.A, tri.B, tri.C);
                float d = (sp - tp).sqrMagnitude;
                if (d < best) { best = d; segPt = sp; triPt = tp; }
            }
        }

        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

            float denom = 1f / (va + vb + vc);
            float v = vb * denom, w = vc * denom;
            return a + ab * v + ac * w;
        }

        public bool Raycast(float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance)
        {
            Vector3 origin = new Vector3(ox, oy, oz);
            Vector3 dir = new Vector3(dx, dy, dz).normalized;
            if (dir.sqrMagnitude < 1e-8f) return false;
            float maxD = float.IsInfinity(maxDistance) ? 10000f : maxDistance;
            Bounds rayBounds = new Bounds(origin + dir * (maxD * 0.5f),
                new Vector3(MathF.Abs(dir.x) * maxD + 0.2f, MathF.Abs(dir.y) * maxD + 0.2f, MathF.Abs(dir.z) * maxD + 0.2f));

            var meshCols = SceneRegistry.GetAllComponents<MeshCollider>();
            for (int i = 0; i < meshCols.Count; i++)
            {
                var mc = meshCols[i];
                if (!mc.isActiveAndEnabled || mc.isTrigger) continue;
                if (!RayIntersectsAabb(origin, dir, maxD, mc.WorldBounds)) continue;
                _triIndices.Clear();
                mc.QueryAabb(rayBounds, _triIndices, _triDedup);
                var tris = mc.Triangles;
                for (int k = 0; k < _triIndices.Count; k++)
                {
                    ref readonly WorldTri t = ref tris[_triIndices[k]];
                    if (RayIntersectsTriangle(origin, dir, maxD, t.A, t.B, t.C)) return true;
                }
            }

            var boxCols = SceneRegistry.GetAllComponents<BoxCollider>();
            for (int i = 0; i < boxCols.Count; i++)
            {
                var bc = boxCols[i];
                if (!bc.isActiveAndEnabled || bc.isTrigger || bc.attachedRigidbody != null) continue;
                if (RayIntersectsBoxObb(origin, dir, maxD, bc)) return true;
            }
            return false;
        }

        private static bool RayIntersectsAabb(Vector3 ro, Vector3 rd, float maxD, Bounds b)
        {
            Vector3 mn = b.min, mx = b.max;
            float tmin = 0f, tmax = maxD;
            for (int a = 0; a < 3; a++)
            {
                float o = a == 0 ? ro.x : (a == 1 ? ro.y : ro.z);
                float d = a == 0 ? rd.x : (a == 1 ? rd.y : rd.z);
                float b0 = a == 0 ? mn.x : (a == 1 ? mn.y : mn.z);
                float b1 = a == 0 ? mx.x : (a == 1 ? mx.y : mx.z);
                if (MathF.Abs(d) < 1e-8f) { if (o < b0 || o > b1) return false; }
                else
                {
                    float inv = 1f / d;
                    float t0 = (b0 - o) * inv, t1 = (b1 - o) * inv;
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    tmin = MathF.Max(tmin, t0); tmax = MathF.Min(tmax, t1);
                    if (tmax < tmin) return false;
                }
            }
            return true;
        }

        private static bool RayIntersectsBoxObb(Vector3 ro, Vector3 rd, float maxD, BoxCollider bc)
        {
            Vector3 lro = bc.transform.InverseTransformPoint(ro) - bc.center;
            Vector3 lrd = bc.transform.InverseTransformDirection(rd);
            return RayIntersectsAabb(lro, lrd, maxD, new Bounds(Vector3.zero, bc.size));
        }

        private static bool RayIntersectsTriangle(Vector3 orig, Vector3 dir, float maxD, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 pvec = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pvec);
            if (MathF.Abs(det) < 1e-7f) return false;
            float invDet = 1f / det;
            Vector3 tvec = orig - v0;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 qvec = Vector3.Cross(tvec, e1);
            float v = Vector3.Dot(dir, qvec) * invDet;
            if (v < 0f || u + v > 1f) return false;
            float t = Vector3.Dot(e2, qvec) * invDet;
            return t >= 0f && t <= maxD;
        }
    }
}
