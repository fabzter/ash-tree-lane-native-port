using System;

namespace UnityEngine
{
    public struct Vector2 : IEquatable<Vector2>
    {
        public float x;
        public float y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 up => new Vector2(0f, 1f);
        public static Vector2 down => new Vector2(0f, -1f);
        public static Vector2 left => new Vector2(-1f, 0f);
        public static Vector2 right => new Vector2(1f, 0f);

        public float magnitude => MathF.Sqrt(x * x + y * y);
        public float sqrMagnitude => x * x + y * y;
        public Vector2 normalized { get { float m = magnitude; return m > 1e-6f ? this / m : zero; } }

        public static Vector2 Scale(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        }
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);
        public static bool operator ==(Vector2 a, Vector2 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);

        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);
        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
        public static implicit operator Vector4(Vector2 v) => new Vector4(v.x, v.y, 0f, 0f);

        public bool Equals(Vector2 other) => x.Equals(other.x) && y.Equals(other.y);
        public override bool Equals(object obj) => obj is Vector2 o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(x, y);
        public override string ToString() => $"({x:F2}, {y:F2})";
    }

    public struct Vector3 : IEquatable<Vector3>
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) : this(x, y, 0f) { }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);

        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized { get { float m = magnitude; return m > 1e-6f ? this / m : zero; } }
        public void Normalize() { float m = magnitude; if (m > 1e-6f) { x /= m; y /= m; z /= m; } else { x = y = z = 0f; } }

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vector3 Normalize(Vector3 value) => value.normalized;
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { t = Mathf.Clamp01(t); return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
        public static Vector3 Min(Vector3 a, Vector3 b) => new Vector3(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));
        public static Vector3 Max(Vector3 a, Vector3 b) => new Vector3(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
        public static Vector3 MoveTowards(Vector3 cur, Vector3 target, float maxDelta)
        {
            Vector3 diff = target - cur;
            float dist = diff.magnitude;
            return dist <= maxDelta || dist < 1e-6f ? target : cur + diff / dist * maxDelta;
        }
        public static Vector3 ClampMagnitude(Vector3 v, float maxLength)
        {
            float sq = v.sqrMagnitude;
            return sq > maxLength * maxLength ? v.normalized * maxLength : v;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 9.99999944E-11f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public static implicit operator Vector4(Vector3 v) => new Vector4(v.x, v.y, v.z, 0f);

        public bool Equals(Vector3 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z);
        public override bool Equals(object obj) => obj is Vector3 o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    public struct Vector4 : IEquatable<Vector4>
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public Vector4(float x, float y, float z) : this(x, y, z, 0f) { }
        public Vector4(float x, float y) : this(x, y, 0f, 0f) { }
        public static Vector4 zero => new Vector4(0f, 0f, 0f, 0f);
        public static Vector4 one => new Vector4(1f, 1f, 1f, 1f);

        public float this[int i]
        {
            get => i switch { 0 => x, 1 => y, 2 => z, 3 => w, _ => throw new IndexOutOfRangeException() };
            set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; case 3: w = value; break; default: throw new IndexOutOfRangeException(); } }
        }

        public static Vector4 operator +(Vector4 a, Vector4 b) => new Vector4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        public static Vector4 operator -(Vector4 a, Vector4 b) => new Vector4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        public static Vector4 operator *(Vector4 a, float d) => new Vector4(a.x * d, a.y * d, a.z * d, a.w * d);
        public static Vector4 operator *(float d, Vector4 a) => a * d;
        public static bool operator ==(Vector4 a, Vector4 b) => (a - b).SqrMag() < 9.99999944E-11f;
        public static bool operator !=(Vector4 a, Vector4 b) => !(a == b);
        private float SqrMag() => x * x + y * y + z * z + w * w;
        public bool Equals(Vector4 other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
        public override bool Equals(object obj) => obj is Vector4 o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
    }

    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public Quaternion normalized
        {
            get
            {
                float m = MathF.Sqrt(x * x + y * y + z * z + w * w);
                return m > 1e-6f ? new Quaternion(x / m, y / m, z / m, w / m) : identity;
            }
        }

        // Unity intrinsic Y * X * Z left-handed Euler angles (degrees)
        public static Quaternion Euler(float xDeg, float yDeg, float zDeg)
        {
            float hx = xDeg * Mathf.Deg2Rad * 0.5f;
            float hy = yDeg * Mathf.Deg2Rad * 0.5f;
            float hz = zDeg * Mathf.Deg2Rad * 0.5f;
            Quaternion qx = new Quaternion(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
            Quaternion qy = new Quaternion(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
            Quaternion qz = new Quaternion(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));
            return (qy * qx * qz).normalized;
        }
        public static Quaternion Euler(Vector3 euler) => Euler(euler.x, euler.y, euler.z);

        public Vector3 eulerAngles
        {
            get => ToEulerDegrees(this);
            set => this = Euler(value);
        }

        public static Quaternion AngleAxis(float angleDeg, Vector3 axis)
        {
            Vector3 n = axis.normalized;
            if (n.sqrMagnitude < 1e-8f) return identity;
            float half = angleDeg * Mathf.Deg2Rad * 0.5f;
            float s = MathF.Sin(half);
            return new Quaternion(n.x * s, n.y * s, n.z * s, MathF.Cos(half)).normalized;
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float lenSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (lenSq < 1e-8f) return identity;
            float inv = 1f / lenSq;
            return new Quaternion(-q.x * inv, -q.y * inv, -q.z * inv, q.w * inv);
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 zAxis = forward.normalized;
            if (zAxis.sqrMagnitude < 1e-6f) zAxis = Vector3.forward;
            Vector3 xAxis = Vector3.Cross(upwards, zAxis).normalized;
            if (xAxis.sqrMagnitude < 1e-6f)
            {
                Vector3 alt = MathF.Abs(zAxis.y) < 0.99f ? Vector3.up : Vector3.right;
                xAxis = Vector3.Cross(alt, zAxis).normalized;
            }
            Vector3 yAxis = Vector3.Cross(zAxis, xAxis);
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = xAxis.x; m.m01 = yAxis.x; m.m02 = zAxis.x;
            m.m10 = xAxis.y; m.m11 = yAxis.y; m.m12 = zAxis.y;
            m.m20 = xAxis.z; m.m21 = yAxis.z; m.m22 = zAxis.z;
            return FromMatrix(m);
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            t = Mathf.Clamp01(t);
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f) { b = new Quaternion(-b.x, -b.y, -b.z, -b.w); dot = -dot; }
            if (dot > 0.9995f)
            {
                return new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t).normalized;
            }
            float theta0 = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            float theta = theta0 * t;
            float sin0 = MathF.Sin(theta0);
            float s0 = MathF.Cos(theta) - dot * MathF.Sin(theta) / sin0;
            float s1 = MathF.Sin(theta) / sin0;
            return new Quaternion(a.x * s0 + b.x * s1, a.y * s0 + b.y * s1, a.z * s0 + b.z * s1, a.w * s0 + b.w * s1).normalized;
        }

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => new Quaternion(
            lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
            lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
            lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
            lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float num = rotation.x * 2f;
            float num2 = rotation.y * 2f;
            float num3 = rotation.z * 2f;
            float num4 = rotation.x * num;
            float num5 = rotation.y * num2;
            float num6 = rotation.z * num3;
            float num7 = rotation.x * num2;
            float num8 = rotation.x * num3;
            float num9 = rotation.y * num3;
            float num10 = rotation.w * num;
            float num11 = rotation.w * num2;
            float num12 = rotation.w * num3;
            return new Vector3(
                (1f - (num5 + num6)) * point.x + (num7 - num12) * point.y + (num8 + num11) * point.z,
                (num7 + num12) * point.x + (1f - (num4 + num6)) * point.y + (num9 - num10) * point.z,
                (num8 - num11) * point.x + (num9 + num10) * point.y + (1f - (num4 + num5)) * point.z);
        }

        public static bool operator ==(Quaternion lhs, Quaternion rhs) => MathF.Abs(lhs.x * rhs.x + lhs.y * rhs.y + lhs.z * rhs.z + lhs.w * rhs.w) > 0.999999f;
        public static bool operator !=(Quaternion lhs, Quaternion rhs) => !(lhs == rhs);

        private static Vector3 ToEulerDegrees(Quaternion q)
        {
            q = q.normalized;
            // Forward vector rotated by q tells pitch (X) and yaw (Y); local Z roll from rotated right/up
            float sinX = 2f * (q.w * q.x - q.y * q.z);
            float rx, ry, rz;
            if (MathF.Abs(sinX) >= 0.9999f)
            {
                rx = MathF.CopySign(MathF.PI * 0.5f, sinX);
                ry = MathF.Atan2(2f * (q.x * q.z + q.w * q.y), 1f - 2f * (q.y * q.y + q.z * q.z));
                rz = 0f;
            }
            else
            {
                rx = MathF.Asin(Math.Clamp(sinX, -1f, 1f));
                ry = MathF.Atan2(2f * (q.x * q.z + q.w * q.y), 1f - 2f * (q.x * q.x + q.y * q.y));
                rz = MathF.Atan2(2f * (q.x * q.y + q.w * q.z), 1f - 2f * (q.x * q.x + q.z * q.z));
            }
            static float Norm(float deg) { deg %= 360f; return deg < 0f ? deg + 360f : deg; }
            return new Vector3(Norm(rx * Mathf.Rad2Deg), Norm(ry * Mathf.Rad2Deg), Norm(rz * Mathf.Rad2Deg));
        }

        private static Quaternion FromMatrix(Matrix4x4 m)
        {
            float tr = m.m00 + m.m11 + m.m22;
            Quaternion q;
            if (tr > 0f)
            {
                float s = MathF.Sqrt(tr + 1f) * 2f;
                q = new Quaternion((m.m21 - m.m12) / s, (m.m02 - m.m20) / s, (m.m10 - m.m01) / s, 0.25f * s);
            }
            else if (m.m00 > m.m11 && m.m00 > m.m22)
            {
                float s = MathF.Sqrt(1f + m.m00 - m.m11 - m.m22) * 2f;
                q = new Quaternion(0.25f * s, (m.m01 + m.m10) / s, (m.m02 + m.m20) / s, (m.m21 - m.m12) / s);
            }
            else if (m.m11 > m.m22)
            {
                float s = MathF.Sqrt(1f + m.m11 - m.m00 - m.m22) * 2f;
                q = new Quaternion((m.m01 + m.m10) / s, 0.25f * s, (m.m12 + m.m21) / s, (m.m02 - m.m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m.m22 - m.m00 - m.m11) * 2f;
                q = new Quaternion((m.m02 + m.m20) / s, (m.m12 + m.m21) / s, 0.25f * s, (m.m10 - m.m01) / s);
            }
            return q.normalized;
        }

        public bool Equals(Quaternion other) => x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);
        public override bool Equals(object obj) => obj is Quaternion o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
    }

    public struct Matrix4x4
    {
        public float m00, m10, m20, m30;
        public float m01, m11, m21, m31;
        public float m02, m12, m22, m32;
        public float m03, m13, m23, m33;

        public static Matrix4x4 identity => new Matrix4x4 { m00 = 1f, m11 = 1f, m22 = 1f, m33 = 1f };

        public static Matrix4x4 TRS(Vector3 pos, Quaternion q, Vector3 s)
        {
            float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
            float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
            float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;

            Matrix4x4 m = default;
            m.m00 = (1f - 2f * (yy + zz)) * s.x; m.m10 = 2f * (xy + wz) * s.x;        m.m20 = 2f * (xz - wy) * s.x;        m.m30 = 0f;
            m.m01 = 2f * (xy - wz) * s.y;        m.m11 = (1f - 2f * (xx + zz)) * s.y; m.m21 = 2f * (yz + wx) * s.y;        m.m31 = 0f;
            m.m02 = 2f * (xz + wy) * s.z;        m.m12 = 2f * (yz - wx) * s.z;        m.m22 = (1f - 2f * (xx + yy)) * s.z; m.m32 = 0f;
            m.m03 = pos.x;                       m.m13 = pos.y;                       m.m23 = pos.z;                       m.m33 = 1f;
            return m;
        }

        // Left-handed camera view matrix (+Z forward, +Y up, +X right)
        public static Matrix4x4 LookToLH(Vector3 eye, Vector3 forward, Vector3 up, Vector3 right)
        {
            Matrix4x4 m = identity;
            m.m00 = right.x;   m.m01 = right.y;   m.m02 = right.z;   m.m03 = -Vector3.Dot(right, eye);
            m.m10 = up.x;      m.m11 = up.y;      m.m12 = up.z;      m.m13 = -Vector3.Dot(up, eye);
            m.m20 = forward.x; m.m21 = forward.y; m.m22 = forward.z; m.m23 = -Vector3.Dot(forward, eye);
            m.m30 = 0f;        m.m31 = 0f;        m.m32 = 0f;        m.m33 = 1f;
            return m;
        }

        // Left-handed Metal [0, 1] depth perspective projection
        public static Matrix4x4 PerspectiveLHZeroToOne(float fovDeg, float aspect, float nearZ, float farZ)
        {
            float yScale = 1f / MathF.Tan(fovDeg * Mathf.Deg2Rad * 0.5f);
            float xScale = yScale / MathF.Max(0.0001f, aspect);
            float range = farZ / (farZ - nearZ);
            Matrix4x4 m = default;
            m.m00 = xScale;
            m.m11 = yScale;
            m.m22 = range;
            m.m32 = 1f;
            m.m23 = -nearZ * range;
            return m;
        }

        public Vector3 MultiplyPoint(Vector3 p)
        {
            float w = m30 * p.x + m31 * p.y + m32 * p.z + m33;
            float inv = MathF.Abs(w) > 1e-7f ? 1f / w : 1f;
            return new Vector3((m00 * p.x + m01 * p.y + m02 * p.z + m03) * inv,
                               (m10 * p.x + m11 * p.y + m12 * p.z + m13) * inv,
                               (m20 * p.x + m21 * p.y + m22 * p.z + m23) * inv);
        }

        public Vector3 MultiplyPoint3x4(Vector3 p) => new Vector3(
            m00 * p.x + m01 * p.y + m02 * p.z + m03,
            m10 * p.x + m11 * p.y + m12 * p.z + m13,
            m20 * p.x + m21 * p.y + m22 * p.z + m23);

        public Vector3 MultiplyVector(Vector3 v) => new Vector3(
            m00 * v.x + m01 * v.y + m02 * v.z,
            m10 * v.x + m11 * v.y + m12 * v.z,
            m20 * v.x + m21 * v.y + m22 * v.z);

        public static Matrix4x4 operator *(Matrix4x4 lhs, Matrix4x4 rhs)
        {
            Matrix4x4 r = default;
            r.m00 = lhs.m00 * rhs.m00 + lhs.m01 * rhs.m10 + lhs.m02 * rhs.m20 + lhs.m03 * rhs.m30;
            r.m10 = lhs.m10 * rhs.m00 + lhs.m11 * rhs.m10 + lhs.m12 * rhs.m20 + lhs.m13 * rhs.m30;
            r.m20 = lhs.m20 * rhs.m00 + lhs.m21 * rhs.m10 + lhs.m22 * rhs.m20 + lhs.m23 * rhs.m30;
            r.m30 = lhs.m30 * rhs.m00 + lhs.m31 * rhs.m10 + lhs.m32 * rhs.m20 + lhs.m33 * rhs.m30;

            r.m01 = lhs.m00 * rhs.m01 + lhs.m01 * rhs.m11 + lhs.m02 * rhs.m21 + lhs.m03 * rhs.m31;
            r.m11 = lhs.m10 * rhs.m01 + lhs.m11 * rhs.m11 + lhs.m12 * rhs.m21 + lhs.m13 * rhs.m31;
            r.m21 = lhs.m20 * rhs.m01 + lhs.m21 * rhs.m11 + lhs.m22 * rhs.m21 + lhs.m23 * rhs.m31;
            r.m31 = lhs.m30 * rhs.m01 + lhs.m31 * rhs.m11 + lhs.m32 * rhs.m21 + lhs.m33 * rhs.m31;

            r.m02 = lhs.m00 * rhs.m02 + lhs.m01 * rhs.m12 + lhs.m02 * rhs.m22 + lhs.m03 * rhs.m32;
            r.m12 = lhs.m10 * rhs.m02 + lhs.m11 * rhs.m12 + lhs.m12 * rhs.m22 + lhs.m13 * rhs.m32;
            r.m22 = lhs.m20 * rhs.m02 + lhs.m21 * rhs.m12 + lhs.m22 * rhs.m22 + lhs.m23 * rhs.m32;
            r.m32 = lhs.m30 * rhs.m02 + lhs.m31 * rhs.m12 + lhs.m32 * rhs.m22 + lhs.m33 * rhs.m32;

            r.m03 = lhs.m00 * rhs.m03 + lhs.m01 * rhs.m13 + lhs.m02 * rhs.m23 + lhs.m03 * rhs.m33;
            r.m13 = lhs.m10 * rhs.m03 + lhs.m11 * rhs.m13 + lhs.m12 * rhs.m23 + lhs.m13 * rhs.m33;
            r.m23 = lhs.m20 * rhs.m03 + lhs.m21 * rhs.m13 + lhs.m22 * rhs.m23 + lhs.m23 * rhs.m33;
            r.m33 = lhs.m30 * rhs.m03 + lhs.m31 * rhs.m13 + lhs.m32 * rhs.m23 + lhs.m33 * rhs.m33;
            return r;
        }

        public void CopyColumnMajor(Span<float> dst)
        {
            dst[0] = m00; dst[1] = m10; dst[2] = m20; dst[3] = m30;
            dst[4] = m01; dst[5] = m11; dst[6] = m21; dst[7] = m31;
            dst[8] = m02; dst[9] = m12; dst[10] = m22; dst[11] = m32;
            dst[12] = m03; dst[13] = m13; dst[14] = m23; dst[15] = m33;
        }
    }

    public struct Color : IEquatable<Color>
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) : this(r, g, b, 1f) { }

        public static Color white => new Color(1f, 1f, 1f, 1f);
        public static Color black => new Color(0f, 0f, 0f, 1f);
        public static Color red => new Color(1f, 0f, 0f, 1f);
        public static Color green => new Color(0f, 1f, 0f, 1f);
        public static Color blue => new Color(0f, 0f, 1f, 1f);
        public static Color clear => new Color(0f, 0f, 0f, 0f);
        public static Color gray => new Color(0.5f, 0.5f, 0.5f, 1f);

        public static Color Lerp(Color a, Color b, float t) { t = Mathf.Clamp01(t); return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t); }
        public static Color operator *(Color a, float b) => new Color(a.r * b, a.g * b, a.b * b, a.a * b);
        public static Color operator *(Color a, Color b) => new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
        public static Color operator +(Color a, Color b) => new Color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
        public static bool operator ==(Color lhs, Color rhs) => (Vector4)lhs == (Vector4)rhs;
        public static bool operator !=(Color lhs, Color rhs) => !(lhs == rhs);

        public static implicit operator Vector4(Color c) => new Vector4(c.r, c.g, c.b, c.a);
        public static implicit operator Color(Vector4 v) => new Color(v.x, v.y, v.z, v.w);

        public bool Equals(Color other) => r.Equals(other.r) && g.Equals(other.g) && b.Equals(other.b) && a.Equals(other.a);
        public override bool Equals(object obj) => obj is Color o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
        public override string ToString() => $"RGBA({r:F3}, {g:F3}, {b:F3}, {a:F3})";
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public Vector2 min => new Vector2(x, y);
        public Vector2 max => new Vector2(x + width, y + height);
        public Vector2 center => new Vector2(x + width * 0.5f, y + height * 0.5f);
        public Vector2 size => new Vector2(width, height);
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 extents;
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.extents = size * 0.5f; }
        public Vector3 size { get => extents * 2f; set => extents = value * 0.5f; }
        public Vector3 min => center - extents;
        public Vector3 max => center + extents;

        public void Encapsulate(Vector3 point)
        {
            Vector3 mn = Vector3.Min(min, point);
            Vector3 mx = Vector3.Max(max, point);
            center = (mn + mx) * 0.5f;
            extents = (mx - mn) * 0.5f;
        }

        public bool Intersects(Bounds other) =>
            min.x <= other.max.x && max.x >= other.min.x &&
            min.y <= other.max.y && max.y >= other.min.y &&
            min.z <= other.max.z && max.z >= other.min.z;

        public bool Contains(Vector3 p) =>
            p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && p.z >= min.z && p.z <= max.z;
    }

    public static class Mathf
    {
        public const float PI = MathF.PI;
        public const float Deg2Rad = MathF.PI / 180f;
        public const float Rad2Deg = 180f / MathF.PI;
        public static float Clamp(float val, float min, float max) => Math.Clamp(val, min, max);
        public static int Clamp(int val, int min, int max) => Math.Clamp(val, min, max);
        public static float Clamp01(float val) => Math.Clamp(val, 0f, 1f);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Abs(float f) => MathF.Abs(f);
        public static int Abs(int v) => Math.Abs(v);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static float Sqrt(float f) => MathF.Sqrt(f);
        public static float Pow(float f, float p) => MathF.Pow(f, p);
        public static float Sin(float f) => MathF.Sin(f);
        public static float Cos(float f) => MathF.Cos(f);
        public static float Tan(float f) => MathF.Tan(f);
        public static float Asin(float f) => MathF.Asin(Math.Clamp(f, -1f, 1f));
        public static float Acos(float f) => MathF.Acos(Math.Clamp(f, -1f, 1f));
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Floor(float f) => MathF.Floor(f);
        public static int FloorToInt(float f) => (int)MathF.Floor(f);
        public static float Ceil(float f) => MathF.Ceiling(f);
        public static int CeilToInt(float f) => (int)MathF.Ceiling(f);
        public static float Round(float f) => MathF.Round(f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
        public static float MoveTowards(float cur, float target, float maxDelta) => MathF.Abs(target - cur) <= maxDelta ? target : cur + Sign(target - cur) * maxDelta;
        public static bool Approximately(float a, float b) => MathF.Abs(b - a) < MathF.Max(1e-6f * MathF.Max(MathF.Abs(a), MathF.Abs(b)), 1e-43f * 8f);
    }

    public static class Random
    {
        private static readonly System.Random _rng = new System.Random();
        public static int Range(int minInclusive, int maxExclusive) => maxExclusive <= minInclusive ? minInclusive : _rng.Next(minInclusive, maxExclusive);
        public static float Range(float minInclusive, float maxInclusive) => minInclusive + (float)_rng.NextDouble() * (maxInclusive - minInclusive);
    }
}
