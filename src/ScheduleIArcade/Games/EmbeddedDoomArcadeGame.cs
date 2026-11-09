using System.Threading.Tasks;
using System.Runtime.InteropServices;
using MelonLoader;
using ScheduleIArcade.Configuration;
using ScheduleIArcade.Core;
using ScheduleIArcade.Services;
using ScheduleIArcade.Utils;
using UnityEngine;

namespace ScheduleIArcade.Games;

/// <summary>
/// A real, playable Doom on the cabinet screen. The bundled engine (Managed Doom, separate GPL-2.0
/// assembly) runs in-process: its software renderer's RGBA frame is uploaded to a Unity Texture2D
/// every frame, cabinet keys become Doom key events, and Doom's sound effects play from the
/// cabinet through the arcade volume path. The IWAD is loaded on a worker thread so starting a
/// cabinet does not hitch the game.
///
/// If the engine or data is missing or the engine stops with an error, the cabinet drops to its
/// test screen; the session then refuses or refunds the $10 (see ArcadeSessionService).
/// </summary>
internal sealed class EmbeddedDoomArcadeGame : IArcadeGame
{
    // Unity key -> Doom key name. Escape is reserved for leaving the cabinet, so Tab opens Doom's menu.
    private static readonly (KeyCode unity, string doom)[] KeyMap =
    {
        (KeyCode.UpArrow, "Up"), (KeyCode.DownArrow, "Down"), (KeyCode.LeftArrow, "Left"), (KeyCode.RightArrow, "Right"),
        (KeyCode.W, "Up"), (KeyCode.S, "Down"), (KeyCode.A, "A"), (KeyCode.D, "D"),
        (KeyCode.LeftControl, "LControl"), (KeyCode.RightControl, "RControl"), (KeyCode.Mouse0, "LControl"),
        (KeyCode.Space, "Space"), (KeyCode.E, "Space"), (KeyCode.Mouse1, "Space"),
        (KeyCode.LeftShift, "LShift"), (KeyCode.RightShift, "RShift"), (KeyCode.LeftAlt, "LAlt"),
        (KeyCode.Return, "Enter"), (KeyCode.KeypadEnter, "Enter"), (KeyCode.Tab, "Escape"), (KeyCode.Backspace, "Backspace"),
        (KeyCode.Y, "Y"), (KeyCode.N, "N"),
        (KeyCode.Alpha1, "Num1"), (KeyCode.Alpha2, "Num2"), (KeyCode.Alpha3, "Num3"), (KeyCode.Alpha4, "Num4"),
        (KeyCode.Alpha5, "Num5"), (KeyCode.Alpha6, "Num6"), (KeyCode.Alpha7, "Num7"),
    };

    private readonly DoomCabinetEdition _edition;
    private readonly Dictionary<string, bool> _doomKeyState = new(StringComparer.Ordinal);
    private Task<object>? _loading;
    private EmbeddedDoomBridge.Session? _doom;
    private TestPatternArcadeGame? _fallback;
    private Texture2D? _texture;
    private byte[]? _frame;
    private GCHandle _framePin;
    private DoomCabinetAudio? _audio;
    private string _label;
    private bool _usingFallback;
    private bool _disposed;

    public EmbeddedDoomArcadeGame(DoomCabinetEdition edition)
    {
        _edition = edition;
        _label = edition == DoomCabinetEdition.Doom1 ? "Freedoom: Phase 1" : "Freedoom: Phase 2";
    }

    public string Id => _edition == DoomCabinetEdition.Doom1 ? "doom1" : "doom2";
    public string DisplayName => _usingFallback ? _label + " Cabinet — Out of Order" : _label + " Cabinet";
    public Texture? ScreenTexture => _usingFallback ? _fallback?.ScreenTexture : _texture;

    /// <summary>True once the cabinet has dropped to its test screen (dependency missing or the
    /// engine failed). ArcadeSessionService refuses/refunds the $10 play.</summary>
    public bool IsUsingFallback => _usingFallback;
    public string FallbackReason { get; private set; } = string.Empty;

    /// <summary>The player chose "Quit Game" in Doom's own menu: end the session normally.</summary>
    public bool QuitRequested { get; private set; }

    public bool IsLoaded => _doom != null;

    public void Initialize()
    {
        if (_texture != null || _fallback != null)
            return;

        if (!EmbeddedDoomBridge.IsReady(_edition, out string detail))
        {
            StartFallback(detail);
            return;
        }

        Type? runtimeType = EmbeddedDoomBridge.GetRuntimeType(out detail);
        string? wad = EmbeddedDoomBridge.ResolveWad(_edition, out _label);
        if (runtimeType == null || wad == null)
        {
            StartFallback(string.IsNullOrEmpty(detail) ? "The Doom engine could not be prepared." : detail);
            return;
        }

        _texture = new Texture2D(640, 400, TextureFormat.RGBA32, false)
        {
            name = "ScheduleIArcade_" + Id + "_Frame",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        Color32[] loading = Enumerable.Repeat(new Color32(8, 6, 6, 255), 640 * 400).ToArray();
        _texture.SetPixels32(loading);
        _texture.Apply(false, false);

        // Pure managed work (WAD parse, texture/sprite tables): safe off the Unity main thread.
        string dataDir = ModPaths.DoomDataRoot;
        _loading = Task.Run(() => Activator.CreateInstance(runtimeType, wad, dataDir)!);
        MelonLogger.Msg($"{_label} cabinet: loading the bundled Doom engine with {Path.GetFileName(wad)}.");
    }

    public void Start()
    {
        if (_usingFallback)
            _fallback?.Start();
    }

    public void AttachAudio(Transform speaker, ArcadePreferences preferences)
    {
        _audio ??= new DoomCabinetAudio(speaker, preferences);
    }

    public void Tick(float deltaTime)
    {
        if (_disposed)
            return;

        if (_usingFallback)
        {
            _fallback?.Tick(deltaTime);
            return;
        }

        if (_doom == null)
        {
            if (_loading == null || !_loading.IsCompleted)
                return;
            if (_loading.IsFaulted || _loading.IsCanceled)
            {
                StartFallback("The Doom engine failed to start: " + (_loading.Exception?.GetBaseException().Message ?? "cancelled"));
                return;
            }

            _doom = new EmbeddedDoomBridge.Session(_loading.Result);
            _loading = null;
            int width = _doom.Width, height = _doom.Height;
            if (_texture == null || _texture.width != width || _texture.height != height)
            {
                if (_texture != null)
                    UnityEngine.Object.Destroy(_texture);
                _texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            }
            _frame = new byte[width * height * 4];
            _framePin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
            MelonLogger.Msg($"{_label} cabinet: Doom engine running ({width}x{height}). Tab opens the Doom menu, Esc leaves the cabinet.");
        }

        UpdateKeys(_doom);

        // Real time, not game time: the cabinet keeps its 35 Hz pace regardless of Time.timeScale.
        float frameSeconds = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : deltaTime;
        _doom.Advance(Math.Min(frameSeconds, 0.25f));

        if (!_doom.IsRunning)
        {
            string error = _doom.LastError;
            if (string.IsNullOrEmpty(error))
                QuitRequested = true;
            else
                StartFallback("Doom stopped: " + error);
            return;
        }

        if (_frame != null && _texture != null && _doom.RenderInto(_frame))
        {
            // Upload straight from the pinned managed buffer (no per-frame IL2CPP array copy).
            _texture.LoadRawTextureData(_framePin.AddrOfPinnedObject(), _frame.Length);
            _texture.Apply(false, false);
        }

        int played = 0;
        while (_doom.TryDequeueSound(out int sfx, out float gain, out float pan))
        {
            if (played++ < ArcadeAudioMath.MaxCabinetVoices)
                _audio?.Play(_doom, sfx, gain, pan);
        }
    }

    private void UpdateKeys(EmbeddedDoomBridge.Session doom)
    {
        foreach (string key in _doomKeyState.Keys.ToArray())
            _scratch[key] = false;
        foreach (var (unity, doomKey) in KeyMap)
        {
            bool held = Input.GetKey(unity);
            _scratch[doomKey] = (_scratch.TryGetValue(doomKey, out bool already) && already) || held;
        }
        foreach (var pair in _scratch)
        {
            _doomKeyState.TryGetValue(pair.Key, out bool previous);
            if (pair.Value != previous)
            {
                doom.SetKey(pair.Key, pair.Value);
                _doomKeyState[pair.Key] = pair.Value;
            }
        }
        _scratch.Clear();
    }

    private readonly Dictionary<string, bool> _scratch = new(StringComparer.Ordinal);

    public void Stop()
    {
        try { _doom?.ReleaseAllKeys(); }
        catch { }
        _doomKeyState.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _doom?.Dispose();
        _doom = null;
        if (_loading != null)
        {
            // Still loading in the background: dispose the instance when it arrives.
            _loading.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    (t.Result as IDisposable)?.Dispose();
            });
            _loading = null;
        }
        if (_framePin.IsAllocated)
            _framePin.Free();
        _frame = null;
        _audio?.Dispose();
        _audio = null;
        _fallback?.Dispose();
        _fallback = null;
        if (_texture != null)
        {
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
    }

    private void StartFallback(string reason)
    {
        FallbackReason = reason;
        if (!_usingFallback)
            MelonLogger.Warning(_label + " cabinet is out of order: " + reason);

        _doom?.Dispose();
        _doom = null;
        _usingFallback = true;
        _fallback ??= new TestPatternArcadeGame();
        _fallback.Initialize();
        _fallback.Start();
    }

    /// <summary>Doom sound effects from the cabinet: 3D, short range, voice-capped, and scaled by
    /// MasterVolume x CabinetVolume x the game's own FX volume.</summary>
    private sealed class DoomCabinetAudio : IDisposable
    {
        private readonly ArcadePreferences _preferences;
        private readonly GameObject _root;
        private readonly AudioSource[] _voices = new AudioSource[ArcadeAudioMath.MaxCabinetVoices];
        private readonly Dictionary<int, AudioClip> _clips = new();
        private int _next;

        public DoomCabinetAudio(Transform speaker, ArcadePreferences preferences)
        {
            _preferences = preferences;
            _root = new GameObject("ScheduleIArcade_DoomCabinetAudio");
            _root.transform.SetParent(speaker, false);
            for (int i = 0; i < _voices.Length; i++)
            {
                AudioSource source = _root.AddComponent<AudioSource>();
                ArcadeAudio.ConfigureSpatial(source, ArcadeAudioMath.CabinetMinDistance, ArcadeAudioMath.CabinetMaxDistance);
                // A little 2D share keeps Doom's left/right cues for the player at the cabinet.
                source.spatialBlend = 0.85f;
                _voices[i] = source;
            }
        }

        public void Play(EmbeddedDoomBridge.Session doom, int sfx, float gain, float pan)
        {
            if (!_clips.TryGetValue(sfx, out AudioClip? clip))
            {
                float[]? samples = doom.GetSfxSamples(sfx, out int rate);
                if (samples == null || samples.Length == 0 || rate <= 0)
                    return;
                clip = AudioClip.Create("ScheduleIArcade_DoomSfx" + sfx, samples.Length, 1, rate, false);
                clip.SetData(samples, 0);
                _clips[sfx] = clip;
            }

            int active = 1;
            foreach (AudioSource v in _voices)
                if (v != null && v.isPlaying) active++;

            AudioSource voice = _voices[_next];
            _next = (_next + 1) % _voices.Length;
            if (voice == null)
                return;
            voice.Stop();
            voice.clip = clip;
            voice.panStereo = Mathf.Clamp(pan, -1f, 1f) * 0.5f;
            voice.volume = ArcadeAudioMath.CabinetEffectGain(gain, _preferences.MasterVolume, _preferences.CabinetVolume,
                ArcadeAudio.GameVolume(Il2CppScheduleOne.Core.Audio.EAudioType.FX)) * ArcadeAudioMath.OverlapScale(Math.Min(active, _voices.Length));
            voice.Play();
        }

        public void Dispose()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            foreach (AudioClip clip in _clips.Values)
                if (clip != null)
                    UnityEngine.Object.Destroy(clip);
            _clips.Clear();
        }
    }
}
