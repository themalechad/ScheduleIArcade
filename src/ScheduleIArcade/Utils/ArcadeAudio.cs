using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.Core.Audio;
using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>Unity side of the arcade volume path (see <see cref="ArcadeAudioMath"/>).</summary>
internal static class ArcadeAudio
{
    /// <summary>Schedule I's own volume for a category (master x Ambient/FX slider), or 1 when the
    /// game's AudioManager is not available yet.</summary>
    public static float GameVolume(EAudioType type)
    {
        try
        {
            AudioManager? manager = AudioManager.Instance;
            if (manager != null)
                return ArcadeAudioMath.Clamp01(manager.GetVolume(type, true));
        }
        catch
        {
        }
        return 1f;
    }

    /// <summary>Full 3D source with Unity Linear rolloff: silent beyond <paramref name="maxDistance"/>.</summary>
    public static void ConfigureSpatial(AudioSource source, float minDistance, float maxDistance)
    {
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.dopplerLevel = 0f;
        source.spread = 0f;
        source.bypassReverbZones = true;
        source.priority = 160;
    }
}
