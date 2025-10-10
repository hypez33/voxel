using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Voxels
{
    public static class BlockPalette
    {
        private static readonly Color32[] Colors =
        {
            new Color32(0, 0, 0, 0),               // Air
            new Color32(96, 160, 72, 255),         // Grass
            new Color32(120, 85, 60, 255),         // Dirt
            new Color32(110, 110, 115, 255),       // Stone
            new Color32(138, 108, 80, 255),        // Wood
            new Color32(86, 142, 64, 220),         // Leaves
            new Color32(214, 190, 125, 255),       // Sand
            new Color32(230, 235, 245, 255),       // Snow
            new Color32(64, 120, 210, 200),        // Water
            new Color32(150, 60, 60, 255),         // Brick
            new Color32(190, 190, 198, 255),       // Metal
            new Color32(170, 220, 255, 90)         // Glass
        };

        private static NativeArray<Color32> _nativePalette;

        public static Color32 GetColor(byte id)
        {
            return Colors[Mathf.Clamp(id, 0, Colors.Length - 1)];
        }

        public static Color32 GetColorWithAO(byte id, byte aoLevel)
        {
            var baseColor = GetColor(id);
            if (id == (byte)VoxelType.Water || id == (byte)VoxelType.Glass)
            {
                return baseColor;
            }

            if (aoLevel == 0)
            {
                return baseColor;
            }

            // Greedy meshing AO uses 0-3 steps; transfer into a linear darkening factor.
            const float step = 0.18f;
            var color = (Color)baseColor;
            var linear = color.linear;
            linear *= Mathf.Clamp01(1f - step * aoLevel);
            var gamma = linear.gamma;
            return (Color32)gamma;
        }

        public static NativeArray<Color32> GetNativePalette(Allocator allocator)
        {
            if (_nativePalette.IsCreated)
            {
                var copy = new NativeArray<Color32>(_nativePalette, allocator);
                return copy;
            }

            _nativePalette = new NativeArray<Color32>(Colors.Length, Allocator.Persistent);
            for (var i = 0; i < Colors.Length; i++)
            {
                _nativePalette[i] = Colors[i];
            }

            var slice = new NativeArray<Color32>(_nativePalette, allocator);
            return slice;
        }

        public static IReadOnlyList<Color32> ColorsList => Colors;
    }
}
