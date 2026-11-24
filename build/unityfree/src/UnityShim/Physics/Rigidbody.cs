namespace UnityEngine
{
    public class Rigidbody : Component
    {
        public Vector3 velocity { get; set; }
        public Vector3 angularVelocity { get; set; }
        public float mass { get; set; } = 1f;
        public float drag { get; set; }
        public float angularDrag { get; set; } = 0.05f;
        public bool useGravity { get; set; } = true;
        public bool isKinematic { get; set; }
        public RigidbodyConstraints constraints { get; set; }

        public Vector3 position
        {
            get => transform.position;
            set => transform.position = value;
        }

        public Quaternion rotation
        {
            get => transform.rotation;
            set => transform.rotation = value;
        }

        public void MovePosition(Vector3 position) => transform.position = position;
        public void MoveRotation(Quaternion rot) => transform.rotation = rot;

        public void AddForce(Vector3 force)
        {
            float step = Mathf.Max(Time.fixedDeltaTime, 0.02f);
            velocity += (force / Mathf.Max(0.01f, mass)) * step;
        }
    }
}
