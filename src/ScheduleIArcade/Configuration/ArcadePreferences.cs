using MelonLoader;
using ScheduleIArcade.Utils;
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
    private readonly MelonPreferences_Entry<float> _masterVolume;
    private readonly MelonPreferences_Entry<float> _cabinetVolume;

    public ArcadePreferences()
    {
        _category = MelonPreferences.CreateCategory("ScheduleIArcade");
        _debug = _category.CreateEntry("DebugLogs", false, "Debug Logs");
        _roomX = _category.CreateEntry("RoomX", -49.1f, "Custom arcade X");
        _roomY = _category.CreateEntry("RoomY", -2.9f, "Custom arcade Y");
        _roomZ = _category.CreateEntry("RoomZ", 144.0f, "Custom arcade Z");
        _ambienceEnabled = _category.CreateEntry("AmbienceEnabled", true, "Arcade Ambience Enabled");
        _ambienceVolume = _category.CreateEntry("AmbienceVolume", ArcadeAudioMath.DefaultAmbienceVolume, "Arcade Ambience Volume", "Room ambience level (0-1), multiplied by MasterVolume and the game's own Ambient volume.");
        _ambienceFadeSeconds = _category.CreateEntry("AmbienceFadeSeconds", 1.25f, "Arcade Ambience Fade Seconds");
        _masterVolume = _category.CreateEntry("MasterVolume", ArcadeAudioMath.DefaultMasterVolume, "Arcade Master Volume", "Scales every arcade sound (0-1). Final volume = this x category volume x the game's own master/Ambient/FX sliders.");
        _cabinetVolume = _category.CreateEntry("CabinetVolume", ArcadeAudioMath.DefaultCabinetVolume, "Arcade Cabinet Volume", "Game audio from cabinets, including Doom sound effects (0-1).");
        _category.SaveToFile(false);
    }

    public bool DebugLogs => _debug.Value;
    public Vector3 RoomPosition => new(_roomX.Value, _roomY.Value, _roomZ.Value);
    public bool AmbienceEnabled => _ambienceEnabled.Value;
    public float AmbienceVolume => Mathf.Clamp01(_ambienceVolume.Value);
    public float AmbienceFadeSeconds => Mathf.Max(0f, _ambienceFadeSeconds.Value);
    public float MasterVolume => Mathf.Clamp01(_masterVolume.Value);
    public float CabinetVolume => Mathf.Clamp01(_cabinetVolume.Value);
}
