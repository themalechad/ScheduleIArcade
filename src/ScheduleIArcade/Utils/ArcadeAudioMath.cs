namespace ScheduleIArcade.Utils;

/// <summary>
/// Pure gain arithmetic for every sound the arcade makes (no Unity types, so it is unit-tested by
/// tests/ScheduleIArcade.Tests). Final source volume is always
///   base(category) x MasterVolume (MelonPreferences) x category preference x game volume
/// where "game volume" is Schedule I's own AudioManager.GetVolume(type, scaled: true), i.e. the
/// player's master and Ambient/FX sliders. Distance falloff is Unity's Linear rolloff, which is
/// exactly zero beyond maxDistance, so arcade audio cannot carry into the street.
/// </summary>
internal static class ArcadeAudioMath
{
    /// <summary>v0.2.65 played the ambience as a flat 2D source at 0.25, ignoring game settings.</summary>
    public const float LegacyAmbienceGain = 0.25f;

    public const float DefaultMasterVolume = 0.4f;
    public const float DefaultAmbienceVolume = 0.2f;
    public const float DefaultCabinetVolume = 0.6f;

    /// <summary>Doom's raw sound effects are full-scale 8-bit PCM; this keeps them below the room.</summary>
    public const float CabinetEffectsBase = 0.35f;

    // Ambience: one source at the room centre. The room is ~6.4 x 8.0 m (half-diagonal ~5.1 m).
    public const float AmbienceMinDistance = 3.0f;
    public const float AmbienceMaxDistance = 7.5f;

    // Cabinet game audio: a source at the cabinet screen; the player stands ~1 m away.
    public const float CabinetMinDistance = 1.0f;
    public const float CabinetMaxDistance = 6.0f;

    /// <summary>Concurrent cabinet voices; extra requests steal the oldest voice.</summary>
    public const int MaxCabinetVoices = 4;

    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    public static float FinalGain(float baseVolume, float masterVolume, float categoryVolume, float gameVolume)
        => Clamp01(Clamp01(baseVolume) * Clamp01(masterVolume) * Clamp01(categoryVolume) * Clamp01(gameVolume));

    public static float AmbienceGain(float master, float ambience, float gameAmbient)
        => FinalGain(1f, master, ambience, gameAmbient);

    public static float CabinetEffectGain(float engineGain, float master, float cabinet, float gameFx)
        => FinalGain(CabinetEffectsBase * Clamp01(engineGain), master, cabinet, gameFx);

    /// <summary>Unity AudioRolloffMode.Linear attenuation.</summary>
    public static float LinearRolloff(float distance, float minDistance, float maxDistance)
    {
        if (distance <= minDistance) return 1f;
        if (distance >= maxDistance) return 0f;
        return 1f - (distance - minDistance) / (maxDistance - minDistance);
    }

    /// <summary>When several voices overlap, scale each so the sum does not stack linearly
    /// (equal-power: N voices at 1/sqrt(N) sum to the loudness of about one).</summary>
    public static float OverlapScale(int activeVoices) => activeVoices <= 1 ? 1f : 1f / (float)Math.Sqrt(activeVoices);

    public static float ToDecibels(float gain) => gain <= 0f ? float.NegativeInfinity : 20f * (float)Math.Log10(gain);
}
