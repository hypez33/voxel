#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Voxels.Editor
{
    public static class ProjectAutoSetup
    {
        private const string PipelinePath = "Assets/Settings/VoxelURP.asset";
        private const string VolumeProfilePath = "Assets/Settings/VoxelVolumeProfile.asset";
        private const string MaterialPath = "Assets/Settings/VoxelMaterial.mat";
        private const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Tools/Voxels/Run Auto-Setup", priority = 0)]
        public static void Run()
        {
            try
            {
                EnsureDirectories();
                EnsureUrpPackage();
                var pipelineAsset = CreateOrLoadPipelineAsset();
                var voxelMaterial = CreateOrLoadVoxelMaterial();
                var volumeProfile = CreateOrLoadVolumeProfile();
                ConfigureGraphicsSettings(pipelineAsset);
                BuildScene(voxelMaterial, volumeProfile);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Voxel Setup", "Auto-Setup abgeschlossen.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Voxel Auto-Setup fehlgeschlagen: {ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void EnsureDirectories()
        {
            CreateFolder("Assets", "Scenes");
            CreateFolder("Assets", "Settings");
        }

        private static void CreateFolder(string parent, string name)
        {
            var path = Path.Combine(parent, name).Replace("\\", "/");
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, name);
            }
        }

        private static void EnsureUrpPackage()
        {
            EditorUtility.DisplayProgressBar("Voxel Setup", "Pruefe URP-Paket ...", 0.1f);
            var listRequest = Client.List(true, true);
            while (!listRequest.IsCompleted)
            {
                EditorUtility.DisplayProgressBar("Voxel Setup", "Pruefe URP-Paket ...", 0.2f);
            }

            if (listRequest.Status == StatusCode.Failure)
            {
                throw new InvalidOperationException($"Package-Listing fehlgeschlagen: {listRequest.Error.message}");
            }

            if (listRequest.Result.Any(p => p.name == "com.unity.render-pipelines.universal"))
            {
                return;
            }

            var addRequest = Client.Add("com.unity.render-pipelines.universal");
            while (!addRequest.IsCompleted)
            {
                EditorUtility.DisplayProgressBar("Voxel Setup", "Installiere URP ...", 0.6f);
            }

            if (addRequest.Status == StatusCode.Failure)
            {
                throw new InvalidOperationException($"URP-Installation fehlgeschlagen: {addRequest.Error.message}");
            }
        }

        private static UniversalRenderPipelineAsset CreateOrLoadPipelineAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (existing != null)
            {
                return existing;
            }

            EditorUtility.DisplayProgressBar("Voxel Setup", "Erzeuge URP-Asset ...", 0.75f);
            var pipeline = UniversalRenderPipelineAsset.Create();
            AssetDatabase.CreateAsset(pipeline, PipelinePath);
            pipeline.name = "VoxelURP";
            DuplicateRendererData(pipeline);
            ConfigureForwardPlus(pipeline);
            return pipeline;
        }

        private static void DuplicateRendererData(UniversalRenderPipelineAsset pipeline)
        {
            var serialized = new SerializedObject(pipeline);
            var listProp = serialized.FindProperty("m_RendererDataList");
            if (listProp == null || listProp.arraySize == 0)
            {
                return;
            }

            for (int i = 0; i < listProp.arraySize; i++)
            {
                var element = listProp.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue is ScriptableObject rendererData)
                {
                    var copy = UnityEngine.Object.Instantiate(rendererData);
                    copy.name = rendererData.name;
                    AssetDatabase.AddObjectToAsset(copy, PipelinePath);
                    element.objectReferenceValue = copy;
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        private static void ConfigureForwardPlus(UniversalRenderPipelineAsset pipeline)
        {
            var pipelineSO = new SerializedObject(pipeline);
            var rendererList = pipelineSO.FindProperty("m_RendererDataList");
            if (rendererList == null)
            {
                return;
            }

            for (int i = 0; i < rendererList.arraySize; i++)
            {
                if (rendererList.GetArrayElementAtIndex(i).objectReferenceValue is UniversalRendererData rendererData)
                {
                    var rendererSO = new SerializedObject(rendererData);
                    var modeProp = rendererSO.FindProperty("m_RenderingMode");
                    if (modeProp != null)
                    {
                        modeProp.enumValueIndex = (int)RenderingMode.ForwardPlus;
                    }

                    rendererSO.ApplyModifiedPropertiesWithoutUndo();
                    rendererData.SetDirty();
                    EditorUtility.SetDirty(rendererData);
                    EnsureSsaoFeature(rendererData);
                }
            }

            pipelineSO.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        private static Material CreateOrLoadVoxelMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null)
            {
                return existing;
            }

            var shader = Shader.Find("Custom/VoxelVertexColor");
            if (shader == null)
            {
                throw new InvalidOperationException("Shader Custom/VoxelVertexColor nicht gefunden.");
            }

            var material = new Material(shader)
            {
                name = "VoxelMaterial"
            };
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        private static VolumeProfile CreateOrLoadVolumeProfile()
        {
            var existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (existing != null)
            {
                return existing;
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "VoxelVolumeProfile";

            var bloom = profile.Add<Bloom>(true);
            if (bloom != null)
            {
                bloom.active = true;
                bloom.intensity.overrideState = true;
                bloom.intensity.value = 0.4f;
                bloom.threshold.overrideState = true;
                bloom.threshold.value = 1f;
                bloom.scatter.overrideState = true;
                bloom.scatter.value = 0.7f;
            }

            var tone = profile.Add<Tonemapping>(true);
            if (tone != null)
            {
                tone.active = true;
                tone.mode.overrideState = true;
                tone.mode.value = TonemappingMode.ACES;
            }

            var colorAdjust = profile.Add<ColorAdjustments>(true);
            if (colorAdjust != null)
            {
                colorAdjust.active = true;
                colorAdjust.postExposure.overrideState = true;
                colorAdjust.postExposure.value = 0.2f;
                colorAdjust.colorFilter.overrideState = true;
                colorAdjust.colorFilter.value = Color.white;
            }

            AssetDatabase.CreateAsset(profile, VolumeProfilePath);
            return profile;
        }

        private static void EnsureSsaoFeature(UniversalRendererData rendererData)
        {
            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature is ScreenSpaceAmbientOcclusion existing)
                {
                    ConfigureSsaoFeature(existing);
                    return;
                }
            }

            var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = "Screen Space Ambient Occlusion";
            AssetDatabase.AddObjectToAsset(ssao, rendererData);
            rendererData.rendererFeatures.Add(ssao);
            ConfigureSsaoFeature(ssao);
            SyncRendererFeatureMap(rendererData);
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
        }

        private static void ConfigureSsaoFeature(ScreenSpaceAmbientOcclusion ssao)
        {
            var so = new SerializedObject(ssao);
            var settings = so.FindProperty("m_Settings");
            if (settings != null)
            {
                settings.FindPropertyRelative("AOMethod").enumValueIndex = 1; // Interleaved gradient
                settings.FindPropertyRelative("Downsample").boolValue = false;
                settings.FindPropertyRelative("AfterOpaque").boolValue = false;
                settings.FindPropertyRelative("Source").enumValueIndex = 1; // DepthNormals
                settings.FindPropertyRelative("NormalSamples").enumValueIndex = 2; // High
                settings.FindPropertyRelative("Intensity").floatValue = 0.65f;
                settings.FindPropertyRelative("DirectLightingStrength").floatValue = 0.25f;
                settings.FindPropertyRelative("Radius").floatValue = 0.4f;
                settings.FindPropertyRelative("Samples").enumValueIndex = 0; // High
                settings.FindPropertyRelative("BlurQuality").enumValueIndex = 0; // High
                settings.FindPropertyRelative("Falloff").floatValue = 80f;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            ssao.Create();
            EditorUtility.SetDirty(ssao);
        }

        private static void SyncRendererFeatureMap(UniversalRendererData rendererData)
        {
            var rendererSO = new SerializedObject(rendererData);
            var featuresProp = rendererSO.FindProperty("m_RendererFeatures");
            var mapProp = rendererSO.FindProperty("m_RendererFeatureMap");
            if (featuresProp != null && mapProp != null && mapProp.arraySize != featuresProp.arraySize)
            {
                mapProp.arraySize = featuresProp.arraySize;
                rendererSO.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void ConfigureGraphicsSettings(RenderPipelineAsset pipeline)
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            int currentQuality = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.count; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(currentQuality, true);
        }

        private static void BuildScene(Material voxelMaterial, VolumeProfile profile)
        {
            EditorUtility.DisplayProgressBar("Voxel Setup", "Erzeuge Szene ...", 0.9f);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "Main";

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.008f;
            RenderSettings.fogColor = new Color(0.62f, 0.7f, 0.78f);

            var lightGO = new GameObject("Directional Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.color = new Color(1f, 0.95f, 0.88f);
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            lightGO.AddComponent<UniversalAdditionalLightData>();

            var worldGO = new GameObject("World");
            var world = worldGO.AddComponent<World>();
            var debris = worldGO.AddComponent<DebrisPool>();
            var save = worldGO.AddComponent<SaveSystem>();

            var worldSO = new SerializedObject(world);
            worldSO.FindProperty("voxelMaterial").objectReferenceValue = voxelMaterial;
            worldSO.FindProperty("horizontalViewDistance").intValue = 6;
            worldSO.ApplyModifiedPropertiesWithoutUndo();

            var debrisSO = new SerializedObject(debris);
            debrisSO.FindProperty("debrisMaterial").objectReferenceValue = voxelMaterial;
            debrisSO.ApplyModifiedPropertiesWithoutUndo();

            var playerGO = new GameObject("Player");
            playerGO.transform.position = new Vector3(0f, 40f * VoxelMetrics.VOXEL_SIZE, 0f);
            var controller = playerGO.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = new Vector3(0f, 0.9f, 0f);

            var playerController = playerGO.AddComponent<PlayerController>();
            var buildTool = playerGO.AddComponent<BuildDestroyTool>();

            var cameraGO = new GameObject("Player Camera");
            cameraGO.transform.SetParent(playerGO.transform, false);
            cameraGO.transform.localPosition = new Vector3(0f, 1.62f, 0f);
            var camera = cameraGO.AddComponent<Camera>();
            camera.nearClipPlane = Mathf.Max(0.05f, VoxelMetrics.VOXEL_SIZE * 0.2f);
            cameraGO.AddComponent<AudioListener>();
            var cameraData = cameraGO.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = true;

            var pcSO = new SerializedObject(playerController);
            pcSO.FindProperty("playerCamera").objectReferenceValue = camera;
            pcSO.ApplyModifiedPropertiesWithoutUndo();

            var toolSO = new SerializedObject(buildTool);
            toolSO.FindProperty("world").objectReferenceValue = world;
            toolSO.FindProperty("viewCamera").objectReferenceValue = camera;
            toolSO.FindProperty("debrisPool").objectReferenceValue = debris;
            toolSO.ApplyModifiedPropertiesWithoutUndo();

            worldSO = new SerializedObject(world);
            worldSO.FindProperty("player").objectReferenceValue = playerGO.transform;
            worldSO.ApplyModifiedPropertiesWithoutUndo();

            var saveSO = new SerializedObject(save);
            saveSO.FindProperty("world").objectReferenceValue = world;
            saveSO.ApplyModifiedPropertiesWithoutUndo();

            var volumeGO = new GameObject("Global Volume");
            var volume = volumeGO.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;

            BuildCrosshairUI();

            EditorSceneManager.MarkAllScenesDirty();
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        private static void BuildCrosshairUI()
        {
            var canvasGO = new GameObject("CrosshairCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var crosshair = new GameObject("Crosshair");
            crosshair.transform.SetParent(canvas.transform, false);
            var rect = crosshair.AddComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;

            CreateCrosshairLine(crosshair.transform, new Vector2(14f, 2f));
            CreateCrosshairLine(crosshair.transform, new Vector2(2f, 14f));
        }

        private static void CreateCrosshairLine(Transform parent, Vector2 size)
        {
            var line = new GameObject("Line", typeof(RawImage));
            line.transform.SetParent(parent, false);
            var rect = line.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            var img = line.GetComponent<RawImage>();
            img.texture = Texture2D.whiteTexture;
            img.color = new Color(1f, 1f, 1f, 0.8f);
        }
    }
}
#endif


