using System.Reflection;
using ScheduleIArcade.Core;

namespace ScheduleIArcade.Services;

internal enum DoomCabinetEdition
{
    Doom1,
    Doom2
}

/// <summary>
/// Reflection-only bridge to the bundled Doom engine, Mods/ScheduleIArcade/Doom/ScheduleIArcade.Doom.dll
/// (Managed Doom 2.1a, GPL-2.0, shipped as a separate assembly with its complete source). The mod
/// never references the engine at compile time; it binds the small primitive-typed API of
/// ScheduleIArcade.Doom.EmbeddedDoomRuntime (ApiVersion 1).
///
/// Game data: cabinet 1 plays Freedoom: Phase 1 and cabinet 2 Freedoom: Phase 2 (BSD-licensed, bundled).
/// A player's own DOOM.WAD/DOOM1.WAD or DOOM2.WAD dropped into the same folder is preferred.
/// </summary>
internal static class EmbeddedDoomBridge
{
    private const int ExpectedApiVersion = 1;
    private static Type? _runtimeType;
    private static string _loadError = string.Empty;
    private static bool _loadAttempted;

    public static bool IsReady(DoomCabinetEdition edition, out string detail)
    {
        if (!ModPaths.Exists(ModPaths.DoomEnginePath))
        {
            detail = "The bundled Doom engine is missing: Mods/ScheduleIArcade/Doom/ScheduleIArcade.Doom.dll (reinstall the complete ZIP).";
            return false;
        }

        if (ResolveWad(edition, out _) == null)
        {
            detail = edition == DoomCabinetEdition.Doom1
                ? "No Doom data: Mods/ScheduleIArcade/Doom/freedoom1.wad is missing (reinstall the complete ZIP)."
                : "No Doom data: Mods/ScheduleIArcade/Doom/freedoom2.wad is missing (reinstall the complete ZIP).";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    /// <summary>The IWAD this cabinet will play and a display label, or null if none is valid.</summary>
    public static string? ResolveWad(DoomCabinetEdition edition, out string label)
    {
        string[] own = edition == DoomCabinetEdition.Doom1 ? new[] { "DOOM.WAD", "DOOM1.WAD" } : new[] { "DOOM2.WAD" };
        foreach (string name in own)
        {
            string path = ModPaths.Find(Path.Combine(ModPaths.DoomRoot, name));
            if (IsIwad(path))
            {
                label = edition == DoomCabinetEdition.Doom1 ? "Doom (your WAD)" : "Doom II (your WAD)";
                return path;
            }
        }

        string free = ModPaths.Find(Path.Combine(ModPaths.DoomRoot, edition == DoomCabinetEdition.Doom1 ? "freedoom1.wad" : "freedoom2.wad"));
        label = edition == DoomCabinetEdition.Doom1 ? "Freedoom: Phase 1" : "Freedoom: Phase 2";
        return IsIwad(free) ? free : null;
    }

    /// <summary>Loads the engine assembly once (main thread) and returns its runtime type.</summary>
    public static Type? GetRuntimeType(out string detail)
    {
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            try
            {
                Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "ScheduleIArcade.Doom", StringComparison.OrdinalIgnoreCase))
                    ?? LoadEngineAssembly(ModPaths.Find(ModPaths.DoomEnginePath));
                Type? type = assembly.GetType("ScheduleIArcade.Doom.EmbeddedDoomRuntime", throwOnError: false);
                object? api = type?.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (type == null || api is not int version || version != ExpectedApiVersion)
                    _loadError = "The bundled Doom engine does not expose EmbeddedDoomRuntime API v" + ExpectedApiVersion + ".";
                else
                    _runtimeType = type;
            }
            catch (Exception ex)
            {
                _loadError = "The bundled Doom engine could not be loaded: " + ex.GetBaseException().Message;
            }
        }

        detail = _loadError;
        return _runtimeType;
    }

    /// <summary>
    /// Loads the engine from bytes so a Vortex symlink (FileInfo.Length == 0, reparse point) can never
    /// confuse the loader; falls back to LoadFrom if the bytes cannot be read.
    /// </summary>
    private static Assembly LoadEngineAssembly(string path)
    {
        byte[]? bytes = Utils.ModFiles.ReadAllBytes(path);
        if (bytes != null && bytes.Length > 0)
            return Assembly.Load(bytes);
        return Assembly.LoadFrom(path);
    }

    /// <summary>
    /// Symlink-safe IWAD check: opens the file and validates the stream length and the "IWAD" header.
    /// Never uses FileInfo.Length (0 for Vortex-deployed SymbolicLink reparse points).
    /// </summary>
    internal static bool IsIwad(string path) => Utils.ModFiles.IsDoomWad(path, requireIwad: true);

    /// <summary>Thin typed wrapper over an EmbeddedDoomRuntime instance. Main thread only.</summary>
    internal sealed class Session : IDisposable
    {
        private readonly object _runtime;
        private readonly PropertyInfo _width, _height, _isRunning, _lastError;
        private readonly MethodInfo _advance, _renderInto, _setKey, _releaseAll, _dequeueSound, _getSamples;
        private readonly object?[] _soundArgs = new object?[3];
        private readonly object?[] _advanceArgs = new object?[1];
        private readonly object?[] _renderArgs = new object?[1];
        private bool _disposed;

        public Session(object runtime)
        {
            _runtime = runtime;
            Type t = runtime.GetType();
            _width = t.GetProperty("Width")!;
            _height = t.GetProperty("Height")!;
            _isRunning = t.GetProperty("IsRunning")!;
            _lastError = t.GetProperty("LastError")!;
            _advance = t.GetMethod("Advance")!;
            _renderInto = t.GetMethod("RenderInto")!;
            _setKey = t.GetMethod("SetKey")!;
            _releaseAll = t.GetMethod("ReleaseAllKeys")!;
            _dequeueSound = t.GetMethod("TryDequeueSound")!;
            _getSamples = t.GetMethod("GetSfxSamples")!;
        }

        public int Width => (int)_width.GetValue(_runtime)!;
        public int Height => (int)_height.GetValue(_runtime)!;
        public bool IsRunning => !_disposed && (bool)_isRunning.GetValue(_runtime)!;
        public string LastError => (string?)_lastError.GetValue(_runtime) ?? string.Empty;

        public int Advance(double seconds)
        {
            _advanceArgs[0] = seconds;
            return (int)_advance.Invoke(_runtime, _advanceArgs)!;
        }

        public bool RenderInto(byte[] rgbaBottomUp)
        {
            _renderArgs[0] = rgbaBottomUp;
            return (bool)_renderInto.Invoke(_runtime, _renderArgs)!;
        }

        public void SetKey(string doomKey, bool down) => _setKey.Invoke(_runtime, new object[] { doomKey, down });

        public void ReleaseAllKeys() => _releaseAll.Invoke(_runtime, null);

        public bool TryDequeueSound(out int sfx, out float gain, out float pan)
        {
            bool ok = (bool)_dequeueSound.Invoke(_runtime, _soundArgs)!;
            sfx = ok ? (int)_soundArgs[0]! : -1;
            gain = ok ? (float)_soundArgs[1]! : 0f;
            pan = ok ? (float)_soundArgs[2]! : 0f;
            return ok;
        }

        public float[]? GetSfxSamples(int sfx, out int sampleRate)
        {
            var args = new object?[] { sfx, 0 };
            var samples = (float[]?)_getSamples.Invoke(_runtime, args);
            sampleRate = (int)args[1]!;
            return samples;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { ((IDisposable)_runtime).Dispose(); }
            catch { }
        }
    }
}
