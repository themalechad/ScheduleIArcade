using MelonLoader;
using ScheduleIArcade.Core;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

internal sealed class AssetBundleService : IDisposable
{
    private AssetBundle? _bundle;
    private bool _loadAttempted;
    private bool _missingLogged;

    public bool IsLoaded => _bundle != null;

    public bool Load()
    {
        if (_bundle != null)
            return true;
        if (_loadAttempted)
            return false;

        _loadAttempted = true;
        if (!File.Exists(ModPaths.BundlePath))
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                MelonLogger.Warning($"Arcade AssetBundle not found: {ModPaths.BundlePath}. Trying the packaged raw FBX scene next.");
            }
            return false;
        }

        _bundle = AssetBundle.LoadFromFile(ModPaths.BundlePath);
        if (_bundle == null)
        {
            MelonLogger.Error("Unity failed to load the Schedule I Arcade AssetBundle.");
            return false;
        }

        MelonLogger.Msg($"Loaded arcade AssetBundle with {_bundle.GetAllAssetNames().Length} assets.");
        return true;
    }

    public GameObject? LoadRoomPrefab()
    {
        if (_bundle == null && !Load())
            return null;

        var prefab = _bundle!.LoadAsset<GameObject>(Constants.RoomPrefabAssetName);
        if (prefab != null)
            return prefab;

        MelonLogger.Error($"Room prefab '{Constants.RoomPrefabAssetName}' was not found in the bundle.");
        foreach (string name in _bundle.GetAllAssetNames())
            MelonLogger.Warning($"Bundle asset: {name}");
        return null;
    }

    public void Dispose()
    {
        if (_bundle != null)
        {
            _bundle.Unload(false);
            _bundle = null;
        }
        _loadAttempted = false;
        _missingLogged = false;
    }
}
