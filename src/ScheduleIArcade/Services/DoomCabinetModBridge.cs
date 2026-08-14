using System.Reflection;
using MelonLoader.Utils;

namespace ScheduleIArcade.Services;

internal enum DoomCabinetEdition
{
    Doom1,
    Doom2
}

/// <summary>
/// Optional, reflection-only bridge to the separately installed Doom TV mods. No Doom binaries
/// or WADs are bundled here: this only uses a matching mod after it is loaded by MelonLoader and
/// its own WAD/native runtime files pass validation.
/// </summary>
internal static class DoomCabinetModBridge
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 400;
    private const int FrameBytes = FrameWidth * FrameHeight * 4;

    public static bool IsReady(DoomCabinetEdition edition, out string detail)
        => TryResolve(edition, out _, out _, out detail);

    public static bool TryCreate(DoomCabinetEdition edition, out Session? session, out string detail)
    {
        session = null;
        if (!TryResolve(edition, out Assembly? assembly, out Type? runtimeType, out detail) || assembly == null || runtimeType == null)
            return false;

        try
        {
            MethodInfo? start = runtimeType.GetMethod("Start", InstanceFlags, null, Type.EmptyTypes, null);
            MethodInfo? tickAndCapture = runtimeType.GetMethod("TickAndCapture", InstanceFlags, null, Type.EmptyTypes, null);
            MethodInfo? dispose = runtimeType.GetMethod("Dispose", InstanceFlags, null, Type.EmptyTypes, null);
            PropertyInfo? frame = runtimeType.GetProperty("Frame", InstanceFlags);
            PropertyInfo? running = runtimeType.GetProperty("IsRunning", InstanceFlags);
            PropertyInfo? lastError = runtimeType.GetProperty("LastError", InstanceFlags);
            Type? inputType = assembly.GetType("ScheduleIDoomTV.DoomInputService", throwOnError: false);
            MethodInfo? updateInput = inputType?.GetMethod("Update", StaticFlags, null, new[] { runtimeType }, null);
            if (start == null || tickAndCapture == null || dispose == null || frame == null || running == null || updateInput == null)
            {
                detail = "The Doom TV mod version does not expose its compatible native runtime API.";
                return false;
            }

            object? runtime = Activator.CreateInstance(runtimeType, nonPublic: true);
            if (runtime == null)
            {
                detail = "The Doom TV runtime could not be created.";
                return false;
            }

            session = new Session(edition, runtime, start, tickAndCapture, dispose, frame, running, lastError, updateInput);
            detail = edition == DoomCabinetEdition.Doom1 ? "Doom 1 TV mod and WAD are ready." : "Doom 2 TV mod and WAD are ready.";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Could not prepare the Doom TV runtime: " + ex.Message;
            return false;
        }
    }

    private static bool TryResolve(DoomCabinetEdition edition, out Assembly? assembly, out Type? runtimeType, out string detail)
    {
        (string dll, string assemblyName, string folder, string wad) = edition == DoomCabinetEdition.Doom1
            ? ("ScheduleIDoomTV.dll", "ScheduleIDoomTV", "SchedualDoomTv", "Doom1.WAD")
            : ("ScheduleIDoom2TV.dll", "ScheduleIDoom2TV", "SchedualDoom2Tv", "Doom2.wad");

        string dllPath = Path.Combine(MelonEnvironment.ModsDirectory, dll);
        if (!File.Exists(dllPath) || new FileInfo(dllPath).Length < 8192)
        {
            assembly = null;
            runtimeType = null;
            detail = dll + " is missing or incomplete.";
            return false;
        }

        string wadPath = Path.Combine(MelonEnvironment.ModsDirectory, folder, "WAD", wad);
        if (!HasValidWadHeader(wadPath))
        {
            assembly = null;
            runtimeType = null;
            detail = "The required " + wad + " is missing or is not a valid WAD file.";
            return false;
        }

        string runtimePath = Path.Combine(MelonEnvironment.ModsDirectory, folder, "Runtime", "doomgeneric_s1.dll");
        if (!File.Exists(runtimePath) || new FileInfo(runtimePath).Length < 4096)
        {
            assembly = null;
            runtimeType = null;
            detail = "The Doom native runtime is missing for " + wad + ".";
            return false;
        }

        assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (assembly == null)
        {
            runtimeType = null;
            detail = dll + " exists but is not loaded by MelonLoader.";
            return false;
        }

        runtimeType = assembly.GetType("ScheduleIDoomTV.DoomNativeRuntime", throwOnError: false);
        if (runtimeType == null)
        {
            detail = dll + " does not contain its expected Doom runtime.";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    private static bool HasValidWadHeader(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 4096)
                return false;

            using var stream = File.OpenRead(path);
            byte[] header = new byte[4];
            if (stream.Read(header, 0, header.Length) != header.Length)
                return false;
            string magic = System.Text.Encoding.ASCII.GetString(header);
            return magic is "IWAD" or "PWAD";
        }
        catch
        {
            return false;
        }
    }

    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal sealed class Session : IDisposable
    {
        private readonly DoomCabinetEdition _edition;
        private readonly object _runtime;
        private readonly MethodInfo _start;
        private readonly MethodInfo _tickAndCapture;
        private readonly MethodInfo _dispose;
        private readonly PropertyInfo _frame;
        private readonly PropertyInfo _running;
        private readonly PropertyInfo? _lastError;
        private readonly MethodInfo _updateInput;
        private bool _disposed;

        internal Session(
            DoomCabinetEdition edition,
            object runtime,
            MethodInfo start,
            MethodInfo tickAndCapture,
            MethodInfo dispose,
            PropertyInfo frame,
            PropertyInfo running,
            PropertyInfo? lastError,
            MethodInfo updateInput)
        {
            _edition = edition;
            _runtime = runtime;
            _start = start;
            _tickAndCapture = tickAndCapture;
            _dispose = dispose;
            _frame = frame;
            _running = running;
            _lastError = lastError;
            _updateInput = updateInput;
        }

        public bool Start(out string detail)
        {
            try
            {
                bool started = _start.Invoke(_runtime, null) is true;
                detail = started ? string.Empty : ReadRuntimeError();
                return started;
            }
            catch (Exception ex)
            {
                detail = "The " + Name + " runtime could not start: " + ex.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>Ticks the other mod's native runtime and returns its newest RGBA frame, if any.</summary>
        public bool TryTick(out byte[]? frame, out string detail)
        {
            frame = null;
            detail = string.Empty;
            try
            {
                _updateInput.Invoke(null, new[] { _runtime });
                bool captured = _tickAndCapture.Invoke(_runtime, null) is true;
                bool isRunning = _running.GetValue(_runtime) is true;
                if (!isRunning)
                {
                    detail = ReadRuntimeError();
                    return false;
                }

                if (!captured)
                    return true;

                frame = _frame.GetValue(_runtime) as byte[];
                if (frame == null || frame.Length != FrameBytes)
                {
                    detail = Name + " returned an invalid framebuffer.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                detail = Name + " stopped unexpectedly: " + ex.GetBaseException().Message;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _dispose.Invoke(_runtime, null); }
            catch { }
        }

        private string Name => _edition == DoomCabinetEdition.Doom1 ? "Doom I" : "Doom II";

        private string ReadRuntimeError()
        {
            string error = _lastError?.GetValue(_runtime)?.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(error) ? Name + " did not return a frame." : error;
        }
    }
}
