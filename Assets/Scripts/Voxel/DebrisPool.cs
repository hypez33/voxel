using UnityEngine;

namespace Voxels
{
    public sealed class DebrisPool : MonoBehaviour
    {
        [SerializeField] private Material debrisMaterial;
        [SerializeField] private float gravity = -9.81f;
        [SerializeField] private float minLifetime = 0.6f;
        [SerializeField] private float maxLifetime = 1.4f;
        [SerializeField] private int capacity = 512;

        private DebrisInstance[] _instances;
        private Matrix4x4[] _matrices;
        private int _count;
        private Mesh _mesh;

        public Material DebrisMaterial
        {
            get => debrisMaterial;
            set
            {
                debrisMaterial = value;
                if (debrisMaterial != null && !debrisMaterial.enableInstancing)
                {
                    debrisMaterial.enableInstancing = true;
                }
            }
        }

        private void Awake()
        {
            if (capacity < 8)
            {
                capacity = 8;
            }

            _instances = new DebrisInstance[capacity];
            _matrices = new Matrix4x4[Mathf.Min(1023, capacity)];
            _mesh = CreateCubeMesh();

            if (debrisMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                debrisMaterial = new Material(shader);
            }

            if (debrisMaterial != null && !debrisMaterial.enableInstancing)
            {
                debrisMaterial.enableInstancing = true;
            }
        }

        private void LateUpdate()
        {
            if (_count == 0 || debrisMaterial == null)
            {
                return;
            }

            float delta = Time.deltaTime;
            int alive = 0;

            for (int i = 0; i < _count; i++)
            {
                var inst = _instances[i];
                inst.velocity.y += gravity * delta;
                inst.position += inst.velocity * delta;
                inst.rotation = (Quaternion.Euler(inst.angularVelocity * delta) * inst.rotation).normalized;
                inst.life += delta;

                if (inst.life < inst.maxLife)
                {
                    _instances[alive] = inst;
                    alive++;
                }
            }

            _count = alive;

            int offset = 0;
            while (offset < _count)
            {
                int batch = Mathf.Min(_count - offset, _matrices.Length);
                for (int i = 0; i < batch; i++)
                {
                    var inst = _instances[offset + i];
                    _matrices[i] = Matrix4x4.TRS(inst.position, inst.rotation, Vector3.one * inst.scale);
                }

                Graphics.DrawMeshInstanced(_mesh, 0, debrisMaterial, _matrices, batch);
                offset += batch;
            }
        }

        public void SpawnBurst(Vector3 center, int amount, float radius)
        {
            if (_instances == null)
            {
                return;
            }

            for (int i = 0; i < amount && _count < capacity; i++)
            {
                ref var inst = ref _instances[_count];
                inst.position = center + Random.insideUnitSphere * radius;
                inst.velocity = Random.insideUnitSphere * 3.5f;
                inst.velocity.y += 3f;
                inst.angularVelocity = Random.insideUnitSphere * 180f;
                inst.rotation = Random.rotation;
                inst.life = 0f;
                inst.maxLife = Random.Range(minLifetime, maxLifetime);
                inst.scale = Mathf.Lerp(VoxelMetrics.VOXEL_SIZE * 0.25f, VoxelMetrics.VOXEL_SIZE * 0.6f, Random.value);
                _count++;
            }
        }

        private static Mesh CreateCubeMesh()
        {
            var mesh = new Mesh { name = "DebrisCube" };
            var vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f)
            };

            var triangles = new[]
            {
                0,2,1,0,3,2,
                4,5,6,4,6,7,
                0,1,5,0,5,4,
                2,3,7,2,7,6,
                0,4,7,0,7,3,
                1,2,6,1,6,5
            };

            var normals = new[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward
            };

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private struct DebrisInstance
        {
            public Vector3 position;
            public Vector3 velocity;
            public Vector3 angularVelocity;
            public Quaternion rotation;
            public float life;
            public float maxLife;
            public float scale;
        }
    }
}


