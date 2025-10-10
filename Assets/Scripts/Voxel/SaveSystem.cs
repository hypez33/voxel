using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Voxels
{
    public sealed class SaveSystem : MonoBehaviour
    {
        [SerializeField] private World world;
        [SerializeField] private string fileName = "voxel_world.vxl";

        private readonly List<Vector3Int> _chunkBuffer = new List<Vector3Int>(32);

        private string SavePath => Path.Combine(Application.persistentDataPath, fileName);

        private void Awake()
        {
            if (world == null)
            {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
                world = UnityEngine.Object.FindFirstObjectByType<World>();
#else
                world = FindObjectOfType<World>();
#endif
            }
        }

        private void OnGUI()
        {
            const float width = 140f;
            const float height = 28f;
            var rect = new Rect(20f, Screen.height - height * 2 - 30f, width, height);
            if (GUI.Button(rect, "Speichern"))
            {
                Save();
            }

            rect.y += height + 6f;
            if (GUI.Button(rect, "Laden"))
            {
                Load();
            }
        }

        public void Save()
        {
            if (world == null)
            {
                Debug.LogWarning("SaveSystem: Keine Welt referenziert.");
                return;
            }

            world.CollectDirtyChunks(_chunkBuffer);
            if (_chunkBuffer.Count == 0)
            {
                Debug.Log("Keine Aenderungen zu speichern.");
                return;
            }

            var directory = Path.GetDirectoryName(SavePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(SavePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream);

            writer.Write(0x56584C31); // VXL1
            writer.Write(world.Seed);
            int writtenChunks = 0;
            long chunkCountPosition = writer.BaseStream.Position;
            writer.Write(writtenChunks);

            foreach (var coord in _chunkBuffer)
            {
                using var tempStream = new MemoryStream();
                bool hasChanges;
                using (var tempWriter = new BinaryWriter(tempStream, System.Text.Encoding.UTF8, true))
                {
                    hasChanges = world.WriteChunkDiff(coord, tempWriter);
                }

                if (!hasChanges)
                {
                    world.ClearSaveDirtyFlag(coord);
                    continue;
                }

                writer.Write(coord.x);
                writer.Write(coord.y);
                writer.Write(coord.z);
                var diff = tempStream.ToArray();
                writer.Write(diff.Length);
                writer.Write(diff);
                world.ClearSaveDirtyFlag(coord);
                writtenChunks++;
            }

            long endPos = writer.BaseStream.Position;
            writer.BaseStream.Position = chunkCountPosition;
            writer.Write(writtenChunks);
            writer.BaseStream.Position = endPos;

            Debug.Log($"Welt gespeichert: {SavePath}");
        }

        public void Load()
        {
            if (world == null)
            {
                Debug.LogWarning("SaveSystem: Keine Welt referenziert.");
                return;
            }

            var path = SavePath;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"Keine Speicherdatei vorhanden: {path}");
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            int magic = reader.ReadInt32();
            if (magic != 0x56584C31)
            {
                Debug.LogWarning("Ungueltiges Speicherformat.");
                return;
            }

            int fileSeed = reader.ReadInt32();
            world.SetSeed(fileSeed);

            int chunkCount = reader.ReadInt32();
            for (int i = 0; i < chunkCount; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                int length = reader.ReadInt32();
                var data = reader.ReadBytes(length);
                world.ApplyChunkDiff(new Vector3Int(x, y, z), data);
            }

            Debug.Log("Welt geladen.");
        }
    }
}

