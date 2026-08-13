using MelonLoader;
using UnityEngine;

namespace ScheduleIArcade.Configuration;

internal sealed class ArcadePreferences
{
    private readonly MelonPreferences_Category _category;
    private readonly MelonPreferences_Entry<bool> _debug;
    private readonly MelonPreferences_Entry<float> _roomX;
    private readonly MelonPreferences_Entry<float> _roomY;
    private readonly MelonPreferences_Entry<float> _roomZ;
    private readonly MelonPreferences_Entry<bool> _ambienceEnabled;
    private readonly MelonPreferences_Entry<float> _ambienceVolume;
    private readonly MelonPreferences_Entry<float> _ambienceFadeSeconds;

    public ArcadePreferences()
    {
        _category = MelonPreferences.CreateCategory("ScheduleIArcade");
        _debug = _category.CreateEntry("DebugLogs", false, "Debug Logs");
        _roomX = _category.CreateEntry("RoomX", 0f, "Custom arcade X");
        _roomY = _category.CreateEntry("RoomY", 300f, "Custom arcade Y");
        _roomZ = _category.CreateEntry("RoomZ", 0f, "Custom arcade Z");
        _ambienceEnabled = _category.CreateEntry("AmbienceEnabled", true, "Arcade Ambience Enabled");
        _ambienceVolume = _category.CreateEntry("AmbienceVolume", 0.35f, "Arcade Ambience Volume");
        _ambienceFadeSeconds = _category.CreateEntry("AmbienceFadeSeconds", 1.25f, "Arcade Ambience Fade Seconds");
        _category.SaveToFile(false);
    }

    public bool DebugLogs => _debug.Value;
    public Vector3 RoomPosition => new(_roomX.Value, _roomY.Value, _roomZ.Value);
    public bool AmbienceEnabled => _ambienceEnabled.Value;
    public float AmbienceVolume => Mathf.Clamp01(_ambienceVolume.Value);
    public float AmbienceFadeSeconds => Mathf.Max(0f, _ambienceFadeSeconds.Value);
}
