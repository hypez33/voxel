So startest du
--------------
1. Öffne Unity, rufe im Menü `Tools/Voxels/Run Auto-Setup` auf. Das Skript richtet URP (Forward+), Material, Volume-Profile und die Szene `Assets/Scenes/Main.unity` automatisch ein.
2. Szene `Assets/Scenes/Main.unity` öffnen und per Play starten. Steuerung: WASD bewegen, Shift sprint, Space springen, Maus zum Umschauen, 1–9 Blocktyp wählen, LKM abbauen, RMK setzen, G sprengen.
3. Die globale Voxelgröße ist in `Assets/Scripts/Voxel/VoxelTypes.cs` als Konstante `VoxelMetrics.VOXEL_SIZE` definiert. Änderungen wirken sich auf Chunk-Auflösung, Player-Near-Clip und Tools aus.
4. Vor Builds sicherstellen, dass URP-Assets zugewiesen bleiben und die Szene in den Build Settings gelistet ist. Optional Sichtweite in `World` anpassen, um Performance-/Qualitäts-Ziele zu treffen.
