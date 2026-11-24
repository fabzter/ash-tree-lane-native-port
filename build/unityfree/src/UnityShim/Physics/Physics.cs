using UnityShim.Backend;

namespace UnityEngine
{
    public static class Physics
    {
        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.PositiveInfinity)
        {
            return EngineHost.Physics?.Raycast(origin.x, origin.y, origin.z, direction.x, direction.y, direction.z, maxDistance) ?? false;
        }
    }
}
