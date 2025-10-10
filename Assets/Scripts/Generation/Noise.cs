using UnityEngine;

namespace Voxels.Generation
{
    public static class Noise
    {
        public static float Fractal2D(float x, float z, float scale, int octaves, float persistence, float lacunarity, int seed)
        {
            if (scale <= 0.0001f)
            {
                scale = 0.0001f;
            }

            var amplitude = 1f;
            var frequency = 1f;
            var value = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float sampleX = (x + seed * 13.37f) / scale * frequency;
                float sampleZ = (z + seed * 19.91f) / scale * frequency;
                float perlin = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f;
                value += perlin * amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return value;
        }

        public static float Ridged2D(float x, float z, float scale, int octaves, float persistence, float lacunarity, int seed)
        {
            float accum = 0f;
            float weight = 1f;
            float frequency = 1f;

            for (int i = 0; i < octaves; i++)
            {
                float sampleX = (x + 57.1f * seed) / scale * frequency;
                float sampleZ = (z + 93.3f * seed) / scale * frequency;
                float noise = Mathf.PerlinNoise(sampleX, sampleZ);
                noise = 1f - Mathf.Abs(noise * 2f - 1f);
                accum += noise * noise * weight;
                weight *= Mathf.Clamp01(noise * persistence);
                frequency *= lacunarity;
            }

            return accum;
        }

        public static float Perlin3D(float x, float y, float z, float scale, int seed)
        {
            if (scale <= 0.0001f)
            {
                scale = 0.0001f;
            }

            float sampleX = (x + seed * 0.131f) * scale;
            float sampleY = (y + seed * 0.197f) * scale;
            float sampleZ = (z + seed * 0.271f) * scale;

            float xy = Mathf.PerlinNoise(sampleX, sampleY);
            float yz = Mathf.PerlinNoise(sampleY, sampleZ);
            float xz = Mathf.PerlinNoise(sampleX, sampleZ);
            float yx = Mathf.PerlinNoise(sampleY, sampleX);
            float zy = Mathf.PerlinNoise(sampleZ, sampleY);
            float zx = Mathf.PerlinNoise(sampleZ, sampleX);

            return (xy + yz + xz + yx + zy + zx) / 6f;
        }
    }
}
