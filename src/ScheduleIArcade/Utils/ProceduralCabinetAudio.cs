using ScheduleIArcade.Configuration;
using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Small pool of quiet procedural one-shot sounds (tone + noise bursts) played from a cabinet through
/// the arcade's cabinet volume path: MasterVolume x CabinetVolume x the game's own FX volume.
/// </summary>
internal sealed class ProceduralCabinetAudio : IDisposable
{
    private readonly ArcadePreferences _preferences;
    private readonly GameObject _root;
    private readonly AudioSource[] _voices = new AudioSource[4];
    private readonly AudioClip?[] _clips;
    private int _next;

    public ProceduralCabinetAudio(Transform speaker, ArcadePreferences preferences, string name, params (float seconds, float frequency, float decay)[] sounds)
    {
        _preferences = preferences;
        _root = new GameObject("ScheduleIArcade_" + name + "Audio");
        _root.transform.SetParent(speaker, false);
        for (int i = 0; i < _voices.Length; i++)
        {
            AudioSource source = _root.AddComponent<AudioSource>();
            ArcadeAudio.ConfigureSpatial(source, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance);
            source.spatialBlend = 0.7f;
            _voices[i] = source;
        }
        _clips = new AudioClip?[sounds.Length];
        for (int i = 0; i < sounds.Length; i++)
            _clips[i] = MakeClip($"ScheduleIArcade_{name}Sfx{i}", sounds[i].seconds, sounds[i].frequency, sounds[i].decay);
    }

    private static AudioClip MakeClip(string name, float seconds, float frequency, float decay)
    {
        const int rate = 22050;
        int count = Mathf.Max(1, (int)(seconds * rate));
        var samples = new float[count];
        var random = new System.Random(name.Length * 7919);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float envelope = Mathf.Exp(-decay * t);
            float tone = Mathf.Sin(2f * Mathf.PI * frequency * t);
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            samples[i] = envelope * (0.6f * tone + 0.4f * noise) * 0.5f;
        }
        AudioClip clip = AudioClip.Create(name, count, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public void Play(int sound, float gain)
    {
        if (sound < 0 || sound >= _clips.Length)
            return;
        AudioClip? clip = _clips[sound];
        AudioSource voice = _voices[_next];
        _next = (_next + 1) % _voices.Length;
        if (clip == null || voice == null)
            return;
        voice.Stop();
        voice.clip = clip;
        voice.volume = ArcadeAudioMath.CabinetEffectGain(Mathf.Clamp01(gain) * 0.6f, _preferences.MasterVolume, _preferences.CabinetVolume,
            ArcadeAudio.GameVolume(Il2CppScheduleOne.Core.Audio.EAudioType.FX));
        voice.Play();
    }

    public void Dispose()
    {
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
        foreach (AudioClip? clip in _clips)
            if (clip != null)
                UnityEngine.Object.Destroy(clip);
    }
}
