using MelonLoader;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Core;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Services;

/// <summary>
/// Owns one non-spatial looping ambience source for the custom arcade interior.
///
/// MelonLoader 0.7.3's IL2CPP UnityWebRequest audio wrapper is not ABI-compatible with this
/// Schedule I build (DownloadHandlerAudioClip constructor is missing at runtime), so this service
/// deliberately does not use UnityWebRequest/DownloadHandlerAudioClip. The packaged ambience is
/// ordinary 16-bit PCM WAV and is decoded directly into an AudioClip with Unity's stable
/// AudioClip.Create/SetData API.
/// </summary>
internal sealed class ArcadeAmbienceService : IDisposable
{
    private const string AudioObjectName = "ARCADE_AMBIENCE_AUDIO";

    private readonly ArcadeLocationService _location;
    private readonly ArcadePreferences _preferences;
    private readonly Func<bool> _isInsideArcade;

    private GameObject? _audioObject;
    private AudioSource? _source;
    private AudioClip? _clip;
    private bool _loadAttempted;
    private bool _disposed;
    private bool _lastShouldPlay;
    private float _fadeGain;

    public ArcadeAmbienceService(
        ArcadeLocationService location,
        ArcadePreferences preferences,
        Func<bool> isInsideArcade)
    {
        _location = location;
        _preferences = preferences;
        _isInsideArcade = isInsideArcade;
    }

    public void Initialize()
    {
        if (_disposed || _location.RoomInstance == null)
            return;

        Transform room = _location.RoomInstance.transform;
        Transform existing = room.Find(AudioObjectName);
        if (existing != null)
            UnityEngine.Object.Destroy(existing.gameObject);

        _audioObject = new GameObject(AudioObjectName);
        _audioObject.transform.SetParent(room, false);
        _audioObject.transform.localPosition = Vector3.zero;

        _source = _audioObject.AddComponent<AudioSource>();
        _source.loop = true;
        _source.volume = 0f;
        // 3D at the room centre with Linear rolloff: quieter towards the walls and exactly silent
        // beyond AmbienceMaxDistance, so it never carries out of the building.
        ArcadeAudio.ConfigureSpatial(_source, ArcadeAudioMath.AmbienceMinDistance, ArcadeAudioMath.AmbienceMaxDistance);
        _source.priority = 128;

        LoadClipIfNeeded();
    }

    public void Tick()
    {
        if (_disposed || _source == null)
            return;

        if (!_loadAttempted)
            LoadClipIfNeeded();

        bool shouldPlay = _preferences.AmbienceEnabled && _isInsideArcade() && _clip != null;

        // Same volume path as every arcade sound: base x MasterVolume x AmbienceVolume x the
        // game's own Ambient volume (re-read every frame so slider changes apply immediately).
        float target = shouldPlay
            ? ArcadeAudioMath.AmbienceGain(_preferences.MasterVolume, _preferences.AmbienceVolume, ArcadeAudio.GameVolume(Il2CppScheduleOne.Core.Audio.EAudioType.Ambient))
            : 0f;
        float fade = _preferences.AmbienceFadeSeconds;
        _fadeGain = fade <= 0.01f ? (shouldPlay ? 1f : 0f) : Mathf.MoveTowards(_fadeGain, shouldPlay ? 1f : 0f, Time.unscaledDeltaTime / fade);
        _source.volume = target * _fadeGain;

        if (shouldPlay && !_source.isPlaying)
            _source.Play();
        else if (!shouldPlay && _fadeGain <= 0f && _source.isPlaying)
            _source.Stop();

        if (shouldPlay != _lastShouldPlay)
        {
            _lastShouldPlay = shouldPlay;
            MelonLogger.Msg(shouldPlay
                ? $"Arcade ambience fading in (volume {target:0.000} = master {_preferences.MasterVolume:0.00} x ambience {_preferences.AmbienceVolume:0.00} x game ambient {ArcadeAudio.GameVolume(Il2CppScheduleOne.Core.Audio.EAudioType.Ambient):0.00}, 3D {ArcadeAudioMath.AmbienceMinDistance:0.#}-{ArcadeAudioMath.AmbienceMaxDistance:0.#} m)."
                : "Arcade ambience fading out.");
        }
    }

    private void LoadClipIfNeeded()
    {
        if (_disposed || _loadAttempted)
            return;

        _loadAttempted = true;

        if (!File.Exists(ModPaths.AmbiencePath))
        {
            MelonLogger.Warning($"Arcade ambience file is missing: {ModPaths.AmbiencePath}");
            return;
        }

        try
        {
            MelonLogger.Msg($"Loading arcade ambience with direct PCM WAV decoder: {ModPaths.AmbiencePath}");
            _clip = LoadPcm16Wave(ModPaths.AmbiencePath);
            if (_clip == null)
            {
                MelonLogger.Warning("Arcade ambience WAV decoder returned no AudioClip.");
                return;
            }

            if (_source != null)
            {
                _source.clip = _clip;
                _source.loop = true;

                // Tick() starts it with the fade and the full volume path.
            }

            MelonLogger.Msg($"Arcade ambience loaded with direct WAV decoder: '{Path.GetFileName(ModPaths.AmbiencePath)}' (90-second loop, 3D, routed through the arcade volume path).");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Could not load arcade ambience WAV: {ex}");
        }
    }

    private static AudioClip LoadPcm16Wave(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44)
            throw new InvalidDataException("WAV file is too short.");

        if (!MatchesAscii(bytes, 0, "RIFF") || !MatchesAscii(bytes, 8, "WAVE"))
            throw new InvalidDataException("Ambience file is not a RIFF/WAVE file.");

        int offset = 12;
        ushort audioFormat = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        while (offset + 8 <= bytes.Length)
        {
            string chunkId = ReadAscii4(bytes, offset);
            int chunkSize = ReadI32LE(bytes, offset + 4);
            if (chunkSize < 0)
                throw new InvalidDataException($"WAV chunk '{chunkId}' has invalid size {chunkSize}.");

            int chunkData = offset + 8;
            if (chunkData > bytes.Length - chunkSize)
                throw new EndOfStreamException($"WAV chunk '{chunkId}' extends beyond the file.");

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("WAV fmt chunk is too small.");

                audioFormat = ReadU16LE(bytes, chunkData);
                channels = ReadU16LE(bytes, chunkData + 2);
                sampleRate = ReadI32LE(bytes, chunkData + 4);
                bitsPerSample = ReadU16LE(bytes, chunkData + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkData;
                dataLength = chunkSize;
                break;
            }

            offset = chunkData + chunkSize + (chunkSize & 1);
        }

        if (audioFormat != 1)
            throw new InvalidDataException($"Only PCM WAV is supported; format={audioFormat}.");
        if (channels < 1 || channels > 2)
            throw new InvalidDataException($"Unsupported WAV channel count {channels}.");
        if (sampleRate <= 0)
            throw new InvalidDataException($"Invalid WAV sample rate {sampleRate}.");
        if (bitsPerSample != 16)
            throw new InvalidDataException($"Only 16-bit PCM WAV is supported; bits={bitsPerSample}.");
        if (dataOffset < 0 || dataLength <= 0)
            throw new InvalidDataException("WAV data chunk was not found.");

        int sampleCount = dataLength / 2;
        int frameCount = sampleCount / channels;
        if (frameCount <= 0)
            throw new InvalidDataException("WAV contains no PCM frames.");

        var samples = new float[frameCount * channels];
        int bytePos = dataOffset;
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = unchecked((short)(bytes[bytePos] | (bytes[bytePos + 1] << 8)));
            samples[i] = pcm / 32768f;
            bytePos += 2;
        }

        AudioClip clip = AudioClip.Create(
            "ScheduleIArcade_Ambience",
            frameCount,
            channels,
            sampleRate,
            false);

        if (clip == null)
            throw new InvalidOperationException("Unity AudioClip.Create returned null.");

        if (!clip.SetData(samples, 0))
        {
            UnityEngine.Object.Destroy(clip);
            throw new InvalidOperationException("Unity AudioClip.SetData rejected decoded WAV samples.");
        }

        return clip;
    }

    private static bool MatchesAscii(byte[] bytes, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > bytes.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (bytes[offset + i] != (byte)value[i])
                return false;
        }
        return true;
    }

    private static string ReadAscii4(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new EndOfStreamException("Attempted to read past the WAV buffer.");

        return new string(new[]
        {
            (char)bytes[offset],
            (char)bytes[offset + 1],
            (char)bytes[offset + 2],
            (char)bytes[offset + 3]
        });
    }

    private static ushort ReadU16LE(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
            throw new EndOfStreamException("Attempted to read past the WAV buffer.");
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static int ReadI32LE(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new EndOfStreamException("Attempted to read past the WAV buffer.");

        uint value = (uint)bytes[offset]
            | ((uint)bytes[offset + 1] << 8)
            | ((uint)bytes[offset + 2] << 16)
            | ((uint)bytes[offset + 3] << 24);
        return unchecked((int)value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
        }

        if (_audioObject != null)
            UnityEngine.Object.Destroy(_audioObject);

        if (_clip != null)
            UnityEngine.Object.Destroy(_clip);

        _clip = null;
        _source = null;
        _audioObject = null;
    }
}
