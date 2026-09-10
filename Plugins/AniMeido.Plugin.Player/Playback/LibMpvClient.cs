using AniMeido.Plugin.Player.Sources;
using AniMeido.Plugin.Player.Sources.Web;
using AniMeido.Plugin.Player.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Player.Playback;

/// <summary>
/// Minimal managed owner for the official libmpv client API.
/// </summary>
internal sealed class LibMpvClient : IDisposable
{
    private const string DefaultUserAgent = "AniMeido/1.0";
    private readonly nint _nativeLibrary;
    private readonly NativeApi _api;
    private readonly PlaybackDiagnosticRecorder _diagnostics;
    private nint _handle;
    private bool _disposed;
    private volatile bool _stopEventLoop;
    private Thread? _eventThread;

    public event EventHandler? FileLoaded;

    public event EventHandler? PlaybackEnded;

    public event EventHandler<MpvPlaybackFailedEventArgs>? PlaybackFailed;

    private LibMpvClient(
        nint nativeLibrary,
        NativeApi api,
        nint windowHandle,
        PlaybackDiagnosticRecorder diagnostics)
    {
        _nativeLibrary = nativeLibrary;
        _api = api;
        _diagnostics = diagnostics;
        _handle = _api.Create();
        if (_handle == 0)
        {
            throw new InvalidOperationException("libmpv 无法创建播放实例。");
        }

        try
        {
            var windowId = unchecked((uint)windowHandle.ToInt64());
            SetOption(
                "wid",
                windowId.ToString(CultureInfo.InvariantCulture));
            SetOption("terminal", "no");
            SetOption("input-default-bindings", "yes");
            SetOption("input-vo-keyboard", "yes");
            SetOption("keep-open", "yes");
            SetOption("hwdec", "auto-safe");
            ThrowIfError(_api.Initialize(_handle), "初始化 libmpv");
            _ = _api.RequestLogMessages(_handle, "warn");
            _diagnostics.Record(
                "libmpv",
                "initialized",
                data: new Dictionary<string, object?>
                {
                    ["windowHandleSet"] = windowHandle != 0,
                });
            _eventThread = new Thread(PumpEvents)
            {
                IsBackground = true,
                Name = "AniMeido libmpv events",
            };
            _eventThread.Start();
        }
        catch
        {
            _api.Destroy(_handle);
            _handle = 0;
            throw;
        }
    }

    public static bool TryCreate(
        nint windowHandle,
        PlaybackDiagnosticRecorder diagnostics,
        out LibMpvClient? client,
        out string? error)
    {
        client = null;
        error = null;
        var libraryPath = FindNativeLibrary();
        if (libraryPath is null)
        {
            error =
                "未找到 libmpv。请将 x64 的 libmpv-2.dll、mpv-2.dll 或 mpv-1.dll "
                + "放入 PlayerPlugin 安装目录。";
            return false;
        }

        nint nativeLibrary = 0;
        try
        {
            nativeLibrary = NativeLibrary.Load(
                libraryPath,
                Assembly.GetExecutingAssembly(),
                DllImportSearchPath.SafeDirectories
                | DllImportSearchPath.AssemblyDirectory
                | DllImportSearchPath.UseDllDirectoryForDependencies);
            var api = new NativeApi(nativeLibrary);
            client = new LibMpvClient(
                nativeLibrary,
                api,
                windowHandle,
                diagnostics);
            return true;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or InvalidOperationException)
        {
            if (nativeLibrary != 0)
            {
                NativeLibrary.Free(nativeLibrary);
            }

            error = $"libmpv 加载失败：{ex.Message}";
            return false;
        }
    }

    public void Load(ResolvedMedia media)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(media);

        _diagnostics.Record(
            "libmpv",
            "load-started",
            uri: media.Uri,
            data: new Dictionary<string, object?>
            {
                ["headerNames"] = string.Join(
                    ",",
                    media.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase)),
                ["subtitleCount"] = media.Subtitles?.Count ?? 0,
            });
        try
        {
            var headerPlan = CreateHttpHeaderPlan(media.Headers);
            ThrowIfError(
                _api.SetPropertyString(
                    _handle,
                    "user-agent",
                    headerPlan.UserAgent ?? DefaultUserAgent),
                "设置媒体 User-Agent");
            ThrowIfError(
                _api.SetPropertyString(
                    _handle,
                    "referrer",
                    headerPlan.Referrer ?? string.Empty),
                "设置媒体 Referer");
            Command("change-list", "http-header-fields", "clr", string.Empty);
            foreach (var field in headerPlan.AdditionalFields)
            {
                Command("change-list", "http-header-fields", "append", field);
            }

            Command("loadfile", media.Uri.AbsoluteUri, "replace");
            foreach (var subtitle in media.Subtitles ?? [])
            {
                Command(
                    "sub-add",
                    subtitle.Uri.AbsoluteUri,
                    "auto",
                    subtitle.Title,
                    subtitle.Language ?? string.Empty);
            }

            _diagnostics.Record(
                "libmpv",
                "load-command-accepted",
                uri: media.Uri);
        }
#pragma warning disable CA1031 // Native failures must be captured before surfacing.
        catch (Exception ex)
        {
            _diagnostics.Record(
                "libmpv",
                "load-failed",
                uri: media.Uri,
                data: new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                });
            throw;
        }
#pragma warning restore CA1031
    }

    internal static MpvHttpHeaderPlan CreateHttpHeaderPlan(
        IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var normalized = HeaderNormalizer.Merge(headers);
        normalized.TryGetValue("User-Agent", out var userAgent);
        normalized.TryGetValue("Referer", out var referrer);
        var additionalFields = normalized
            .Where(pair =>
                !string.Equals(
                    pair.Key,
                    "User-Agent",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    pair.Key,
                    "Referer",
                    StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .ToArray();
        return new MpvHttpHeaderPlan(userAgent, referrer, additionalFields);
    }

    public void TogglePause() => Command("cycle", "pause");

    public void Stop() => Command("stop");

    public void SeekRelative(double seconds)
        => Command(
            "seek",
            seconds.ToString(CultureInfo.InvariantCulture),
            "relative");

    public void SeekAbsolute(double seconds)
        => Command(
            "seek",
            Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture),
            "absolute+exact");

    public void SetVolume(double percentage)
        => SetDoubleProperty("volume", Math.Clamp(percentage, 0, 100));

    public void SetSpeed(double speed)
        => Command(
            "set",
            "speed",
            Math.Clamp(speed, 0.5, 2)
                .ToString(CultureInfo.InvariantCulture));

    public void SetMuted(bool muted)
        => Command("set", "mute", muted ? "yes" : "no");

    public bool TryGetPlaybackFlags(out bool muted, out bool reachedEnd)
    {
        var muteAvailable = TryGetFlagProperty("mute", out muted);
        var endAvailable = TryGetFlagProperty("eof-reached", out reachedEnd);
        return muteAvailable & endAvailable;
    }

    public bool TryGetPlaybackState(
        out double position,
        out double duration,
        out double volume,
        out bool paused)
    {
        position = 0;
        duration = 0;
        volume = 100;
        paused = false;
        return TryGetDoubleProperty("time-pos", out position)
            & TryGetDoubleProperty("duration", out duration)
            & TryGetDoubleProperty("volume", out volume)
            & TryGetFlagProperty("pause", out paused);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopEventLoop = true;
        _eventThread?.Join(TimeSpan.FromSeconds(1));
        _eventThread = null;
        if (_handle != 0)
        {
            _api.TerminateDestroy(_handle);
            _handle = 0;
        }

        NativeLibrary.Free(_nativeLibrary);
    }

    private void PumpEvents()
    {
        while (!_stopEventLoop)
        {
            var eventPointer = _api.WaitEvent(_handle, 0.1);
            if (eventPointer == 0)
            {
                continue;
            }

            try
            {
                var nativeEvent = Marshal.PtrToStructure<MpvEvent>(
                    eventPointer);
                if (nativeEvent.EventId == 0)
                {
                    continue;
                }

                var data = new Dictionary<string, object?>
                {
                    ["eventId"] = nativeEvent.EventId,
                    ["eventName"] = GetEventName(nativeEvent.EventId),
                    ["error"] = nativeEvent.Error,
                };
                if (nativeEvent.EventId == 2 && nativeEvent.Data != 0)
                {
                    var message = Marshal.PtrToStructure<MpvLogMessage>(
                        nativeEvent.Data);
                    data["prefix"] = Marshal.PtrToStringUTF8(message.Prefix);
                    data["level"] = Marshal.PtrToStringUTF8(message.Level);
                    data["message"] = Marshal.PtrToStringUTF8(message.Text);
                }
                else if (nativeEvent.EventId == 7 && nativeEvent.Data != 0)
                {
                    var endFile = Marshal.PtrToStructure<MpvEndFileEvent>(
                        nativeEvent.Data);
                    data["reason"] = endFile.Reason;
                    data["playbackError"] = endFile.Error;
                    if (endFile.Error < 0)
                    {
                        PlaybackFailed?.Invoke(
                            this,
                            new MpvPlaybackFailedEventArgs(
                                endFile.Error,
                                GetErrorMessage(endFile.Error)));
                    }
                    else if (endFile.Reason == 0)
                    {
                        PlaybackEnded?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (nativeEvent.EventId == 8)
                {
                    FileLoaded?.Invoke(this, EventArgs.Empty);
                }

                _diagnostics.Record("libmpv", "event", data: data);
            }
#pragma warning disable CA1031 // Native event diagnostics must not terminate the pump.
            catch (Exception ex)
            {
                _diagnostics.Record(
                    "libmpv",
                    "event-read-failed",
                    data: new Dictionary<string, object?>
                    {
                        ["exception"] = ex.GetType().Name,
                        ["message"] = ex.Message,
                    });
            }
#pragma warning restore CA1031
        }
    }

    private static string GetEventName(int eventId)
        => eventId switch
        {
            1 => "shutdown",
            2 => "log-message",
            5 => "command-reply",
            6 => "start-file",
            7 => "end-file",
            8 => "file-loaded",
            11 => "idle",
            17 => "video-reconfig",
            18 => "audio-reconfig",
            20 => "seek",
            21 => "playback-restart",
            _ => "other",
        };

    private static string? FindNativeLibrary()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            return null;
        }

        string[] names = ["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"];
        string[] directories =
        [
            assemblyDirectory,
            Path.Combine(assemblyDirectory, "runtimes", "win-x64", "native"),
        ];
        return directories
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }

    private void SetOption(string name, string value)
        => ThrowIfError(
            _api.SetOptionString(_handle, name, value),
            $"设置 libmpv 选项 {name}");

    private bool TryGetDoubleProperty(string name, out double value)
    {
        value = 0;
        if (_disposed || _handle == 0)
        {
            return false;
        }

        return _api.GetDoubleProperty(
            _handle,
            name,
            MpvFormat.Double,
            ref value) >= 0;
    }

    private bool TryGetFlagProperty(string name, out bool value)
    {
        var flag = 0;
        if (_disposed
            || _handle == 0
            || _api.GetFlagProperty(
                _handle,
                name,
                MpvFormat.Flag,
                ref flag) < 0)
        {
            value = false;
            return false;
        }

        value = flag != 0;
        return true;
    }

    private void SetDoubleProperty(string name, double value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfError(
            _api.SetProperty(
                _handle,
                name,
                MpvFormat.Double,
                ref value),
            $"设置 libmpv 属性 {name}");
    }

    private void Command(params string[] arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strings = new nint[arguments.Length];
        var array = Marshal.AllocHGlobal(
            checked((arguments.Length + 1) * IntPtr.Size));
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(array, index * IntPtr.Size, strings[index]);
            }

            Marshal.WriteIntPtr(
                array,
                arguments.Length * IntPtr.Size,
                IntPtr.Zero);
            ThrowIfError(_api.Command(_handle, array), "执行 libmpv 命令");
        }
        finally
        {
            foreach (var value in strings)
            {
                if (value != 0)
                {
                    Marshal.FreeCoTaskMem(value);
                }
            }

            Marshal.FreeHGlobal(array);
        }
    }

    private void ThrowIfError(int errorCode, string operation)
    {
        if (errorCode >= 0)
        {
            return;
        }

        var errorPointer = _api.ErrorString(errorCode);
        var message = Marshal.PtrToStringUTF8(errorPointer) ?? $"错误 {errorCode}";
        throw new InvalidOperationException($"{operation}失败：{message}");
    }

    private string GetErrorMessage(int errorCode)
    {
        var errorPointer = _api.ErrorString(errorCode);
        return Marshal.PtrToStringUTF8(errorPointer) ?? $"错误 {errorCode}";
    }

    private sealed class NativeApi
    {
        public NativeApi(nint library)
        {
            Create = Load<MpvCreate>(library, "mpv_create");
            Initialize = Load<MpvInitialize>(library, "mpv_initialize");
            Destroy = Load<MpvDestroy>(library, "mpv_destroy");
            TerminateDestroy = Load<MpvTerminateDestroy>(
                library,
                "mpv_terminate_destroy");
            SetOptionString = Load<MpvSetOptionString>(
                library,
                "mpv_set_option_string");
            SetPropertyString = Load<MpvSetPropertyString>(
                library,
                "mpv_set_property_string");
            GetDoubleProperty = Load<MpvGetPropertyDouble>(
                library,
                "mpv_get_property");
            GetFlagProperty = Load<MpvGetPropertyFlag>(
                library,
                "mpv_get_property");
            SetProperty = Load<MpvSetProperty>(
                library,
                "mpv_set_property");
            Command = Load<MpvCommand>(library, "mpv_command");
            WaitEvent = Load<MpvWaitEvent>(library, "mpv_wait_event");
            RequestLogMessages = Load<MpvRequestLogMessages>(
                library,
                "mpv_request_log_messages");
            ErrorString = Load<MpvErrorString>(library, "mpv_error_string");
        }

        public MpvCreate Create { get; }

        public MpvInitialize Initialize { get; }

        public MpvDestroy Destroy { get; }

        public MpvTerminateDestroy TerminateDestroy { get; }

        public MpvSetOptionString SetOptionString { get; }

        public MpvSetPropertyString SetPropertyString { get; }

        public MpvGetPropertyDouble GetDoubleProperty { get; }

        public MpvGetPropertyFlag GetFlagProperty { get; }

        public MpvSetProperty SetProperty { get; }

        public MpvCommand Command { get; }

        public MpvWaitEvent WaitEvent { get; }

        public MpvRequestLogMessages RequestLogMessages { get; }

        public MpvErrorString ErrorString { get; }

        private static T Load<T>(nint library, string name)
            where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(
                NativeLibrary.GetExport(library, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvCreate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvInitialize(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvDestroy(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvTerminateDestroy(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetOptionString(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetPropertyString(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvGetPropertyDouble(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        ref double value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvGetPropertyFlag(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        ref int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetProperty(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format,
        ref double value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvCommand(nint handle, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvWaitEvent(nint handle, double timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvRequestLogMessages(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minimumLevel);

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int EventId;

        public int Error;

        public ulong ReplyUserdata;

        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvLogMessage
    {
        public nint Prefix;

        public nint Level;

        public nint Text;

        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEndFileEvent
    {
        public int Reason;

        public int Error;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvErrorString(int errorCode);

    private enum MpvFormat
    {
        Flag = 3,
        Double = 5,
    }
}

internal sealed record MpvHttpHeaderPlan(
    string? UserAgent,
    string? Referrer,
    IReadOnlyList<string> AdditionalFields);

internal sealed class MpvPlaybackFailedEventArgs(
    int errorCode,
    string message) : EventArgs
{
    public int ErrorCode { get; } = errorCode;

    public string Message { get; } = message;
}
