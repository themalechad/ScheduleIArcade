using ScheduleIArcade.Utils;

namespace ScheduleIArcade.Tests;

/// <summary>
/// Measurable evidence for the arcade volume path (src/ScheduleIArcade/Utils/ArcadeAudioMath.cs).
/// Gains are linear amplitude; dB = 20 log10(gain).
/// </summary>
internal static class AudioTests
{
    public static void DefaultsAreQuieter()
    {
        float legacy = ArcadeAudioMath.LegacyAmbienceGain; // v0.3.0: flat 2D 0.25, game sliders ignored
        // An existing install keeps its saved AmbienceVolume=0.25; a fresh install gets the new default.
        float upgraded = ArcadeAudioMath.AmbienceGain(ArcadeAudioMath.DefaultMasterVolume, 0.25f, 1f);
        float fresh = ArcadeAudioMath.AmbienceGain(ArcadeAudioMath.DefaultMasterVolume, ArcadeAudioMath.DefaultAmbienceVolume, 1f);
        float halfGameSlider = ArcadeAudioMath.AmbienceGain(ArcadeAudioMath.DefaultMasterVolume, ArcadeAudioMath.DefaultAmbienceVolume, 0.5f);
        float doomShot = ArcadeAudioMath.CabinetEffectGain(1f, ArcadeAudioMath.DefaultMasterVolume, ArcadeAudioMath.DefaultCabinetVolume, 1f);

        Console.WriteLine($"      ambience v0.3.0            gain {legacy:0.000} ({ArcadeAudioMath.ToDecibels(legacy):0.0} dB), ignores game volume, 2D everywhere in the room");
        Console.WriteLine($"      ambience upgraded (0.25)   gain {upgraded:0.000} ({ArcadeAudioMath.ToDecibels(upgraded):0.0} dB) = {ArcadeAudioMath.ToDecibels(upgraded) - ArcadeAudioMath.ToDecibels(legacy):0.0} dB vs v0.3.0");
        Console.WriteLine($"      ambience fresh default     gain {fresh:0.000} ({ArcadeAudioMath.ToDecibels(fresh):0.0} dB) = {ArcadeAudioMath.ToDecibels(fresh) - ArcadeAudioMath.ToDecibels(legacy):0.0} dB vs v0.3.0");
        Console.WriteLine($"      ambience, game Ambient 50% gain {halfGameSlider:0.000} ({ArcadeAudioMath.ToDecibels(halfGameSlider):0.0} dB)");
        Console.WriteLine($"      loudest Doom effect        gain {doomShot:0.000} ({ArcadeAudioMath.ToDecibels(doomShot):0.0} dB) before distance falloff");

        Assert.True(ArcadeAudioMath.ToDecibels(upgraded) - ArcadeAudioMath.ToDecibels(legacy) <= -7.5f, "upgraded installs must be at least 7.5 dB quieter");
        Assert.True(ArcadeAudioMath.ToDecibels(fresh) - ArcadeAudioMath.ToDecibels(legacy) <= -9.5f, "fresh installs must be at least 9.5 dB quieter");
        Assert.True(Math.Abs(halfGameSlider - fresh * 0.5f) < 1e-6f, "the game's own volume slider must scale arcade audio linearly");
        Assert.True(ArcadeAudioMath.AmbienceGain(1f, 1f, 0f) == 0f && ArcadeAudioMath.CabinetEffectGain(1f, 1f, 1f, 0f) == 0f, "muting the game category must silence the arcade");
        Assert.True(doomShot <= legacy, "the loudest Doom effect must not exceed the old ambience level");
    }

    public static void FalloffStopsAtStreet()
    {
        // Positions in metres, measured from level1 / the room layout (see tests/ArcadeGeometryCheck):
        // 0.6.0 room centre = building centre (-48.00, 141.50); building glass doorway (-45.11, 137.11);
        // street point 2.5 m outside the doorway (-43.34, 135.34); nearest Doom cabinet ~(-44.80, 146.94).
        static float D(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
        float centreToCorner = D(0, 0, 4.2f, 5.7f);
        float centreToDoorway = D(-48.00f, 141.50f, -45.11f, 137.11f);
        float centreToStreet = D(-48.00f, 141.50f, -43.34f, 135.34f);
        float cabinetToStreet = D(-44.80f, 146.94f, -43.34f, 135.34f);

        Console.WriteLine("      distance m : ambience gain | cabinet gain   (Unity Linear rolloff)");
        foreach (float d in new[] { 0.5f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 7.5f, 8f, 10f })
            Console.WriteLine($"      {d,10:0.0} : {ArcadeAudioMath.LinearRolloff(d, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance),13:0.00} | {ArcadeAudioMath.LinearRolloff(d, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance),12:0.00}");
        Console.WriteLine($"      room corner {centreToCorner:0.00} m -> ambience x{ArcadeAudioMath.LinearRolloff(centreToCorner, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance):0.00}; glass doorway {centreToDoorway:0.00} m -> x{ArcadeAudioMath.LinearRolloff(centreToDoorway, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance):0.00}; street {centreToStreet:0.00} m -> x{ArcadeAudioMath.LinearRolloff(centreToStreet, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance):0.00}; Doom cabinet to street {cabinetToStreet:0.00} m -> x{ArcadeAudioMath.LinearRolloff(cabinetToStreet, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance):0.00}");

        Assert.True(ArcadeAudioMath.LinearRolloff(ArcadeAudioMath.AmbienceMaxDistance, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance) == 0f, "ambience must be exactly silent at its max distance");
        Assert.True(ArcadeAudioMath.LinearRolloff(centreToStreet, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance) == 0f, "ambience must not reach the street outside the door");
        Assert.True(ArcadeAudioMath.LinearRolloff(cabinetToStreet, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance) == 0f, "Doom cabinet audio must not reach the street");
        Assert.True(ArcadeAudioMath.LinearRolloff(1.1f, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance) > 0.95f, "the player at the cabinet hears it at full (already quiet) level");
        float previous = 2f;
        for (float d = 0f; d < 12f; d += 0.25f)
        {
            float g = ArcadeAudioMath.LinearRolloff(d, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance);
            Assert.True(g <= previous, "rolloff must never increase with distance");
            previous = g;
        }
    }

    public static void VoicesDoNotStack()
    {
        float single = ArcadeAudioMath.CabinetEffectGain(1f, ArcadeAudioMath.DefaultMasterVolume, ArcadeAudioMath.DefaultCabinetVolume, 1f);
        for (int voices = 1; voices <= ArcadeAudioMath.MaxCabinetVoices; voices++)
        {
            float each = single * ArcadeAudioMath.OverlapScale(voices);
            float power = voices * each * each;
            Console.WriteLine($"      {voices} voice(s): each {each:0.000}, summed power {power:0.0000} (single {single * single:0.0000})");
            Assert.True(Math.Abs(power - single * single) < 1e-5f, "equal-power scaling keeps overlapping voices at one voice's loudness");
        }
        Assert.True(ArcadeAudioMath.MaxCabinetVoices == 4, "cabinet voices are capped at 4");
    }
}
