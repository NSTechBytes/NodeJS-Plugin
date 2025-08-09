using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Rainmeter;

namespace NodeJSPlugin
{
    public static class Plugin
    {
        internal enum PluginState { Uninitialized, Initializing, Initialized }

        internal static string _scriptFile = "";
        internal static string _inlineScript = "";
        internal static string _wrapperPath = "";
        internal static IntPtr _rmHandle = IntPtr.Zero;
        internal static double _lastValue = 0.0;
        internal static string _lastStringValue = "";
        internal static bool _hasStringValue = false;
        internal static readonly object _valueLock = new object();
        internal static CancellationTokenSource _cancellationTokenSource;

        internal static volatile PluginState _pluginState = PluginState.Uninitialized;
        internal static readonly object _stateLock = new object();
        private static bool _forceReload = false;

        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            _rmHandle = rm;

            lock (_stateLock)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();

                lock (_valueLock)
                {
                    _lastValue = 0.0;
                    _lastStringValue = "";
                    _hasStringValue = false;
                }
            }
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            CleanupAndReset();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            _rmHandle = rm;
            var api = new API(rm);
            string newScriptFile = api.ReadPath("ScriptFile", "").Trim();
            string newInlineScript = BuildInlineScript(api);

            lock (_stateLock)
            {
                // Skip reload if nothing changed
                if (_pluginState == PluginState.Initialized &&
                    newScriptFile == _scriptFile &&
                    newInlineScript == _inlineScript &&
                    !_forceReload)
                {
                    return;
                }

                _forceReload = false;

                // Clean up previous state if needed
                if (_pluginState == PluginState.Initialized || _pluginState == PluginState.Initializing)
                {
                    CleanupResources();
                }

                _pluginState = PluginState.Initializing;
                _scriptFile = newScriptFile;
                _inlineScript = newInlineScript;
                _cancellationTokenSource = new CancellationTokenSource();
            }

            // Validate script configuration
            if (string.IsNullOrWhiteSpace(_scriptFile) && string.IsNullOrWhiteSpace(_inlineScript))
            {
                LogError("Neither ScriptFile nor Line parameters are configured.");
                lock (_stateLock) { _pluginState = PluginState.Uninitialized; }
                return;
            }

            try
            {
                Wrapper.CreateWrapper();
                var (initialValue, initialStringValue) = NodeProcess.RunNodeSynchronous("init");

                lock (_stateLock)
                {
                    lock (_valueLock)
                    {
                        if (initialValue.HasValue)
                            _lastValue = initialValue.Value;

                        _lastStringValue = initialStringValue ?? "";
                        _hasStringValue = !string.IsNullOrEmpty(_lastStringValue);
                    }

                    _pluginState = PluginState.Initialized;
                }
            }
            catch (Exception ex)
            {
                LogError($"Script initialization failed: {GetSimpleErrorMessage(ex)}");
                lock (_stateLock) { _pluginState = PluginState.Uninitialized; }
            }
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            if (_pluginState == PluginState.Initialized &&
                (!string.IsNullOrWhiteSpace(_scriptFile) || !string.IsNullOrWhiteSpace(_inlineScript)))
            {
                NodeProcess.RunNodeAsync();
            }

            lock (_valueLock)
            {
                return _lastValue;
            }
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, [MarshalAs(UnmanagedType.LPWStr)] string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return;

            if (_pluginState != PluginState.Initialized)
            {
                LogError("Cannot execute function: Plugin not initialized.");
                return;
            }

            try
            {
                var (resultValue, resultString) = NodeProcess.RunNodeSynchronous("custom", args.Trim());

                lock (_valueLock)
                {
                    if (resultValue.HasValue)
                        _lastValue = resultValue.Value;

                    _lastStringValue = resultString ?? "";
                    _hasStringValue = !string.IsNullOrEmpty(_lastStringValue);
                }
            }
            catch (Exception ex)
            {
                LogError($"ExecuteBang failed: {GetSimpleErrorMessage(ex)}");
            }
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            lock (_valueLock)
            {
                if (_hasStringValue && !string.IsNullOrEmpty(_lastStringValue))
                {
                    return Marshal.StringToHGlobalUni(_lastStringValue);
                }
            }
            return IntPtr.Zero;
        }

        [DllExport]
        public static IntPtr Call(IntPtr data, int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            if (argc == 0 || string.IsNullOrWhiteSpace(argv[0]))
                return IntPtr.Zero;

            if (_pluginState != PluginState.Initialized)
            {
                LogError("Cannot execute call: Plugin not initialized.");
                return IntPtr.Zero;
            }

            try
            {
                var (_, resultString) = NodeProcess.RunNodeSynchronous("custom", argv[0].Trim());
                return resultString != null ? Marshal.StringToHGlobalUni(resultString) : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LogError($"Call '{argv[0]}' failed: {GetSimpleErrorMessage(ex)}");
                return IntPtr.Zero;
            }
        }

        private static void CleanupAndReset()
        {
            lock (_stateLock)
            {
                _forceReload = true;
                CleanupResources();
                ResetState();
            }
        }

        private static void CleanupResources()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath))
                    File.Delete(_wrapperPath);
            }
            catch { }
        }

        private static void ResetState()
        {
            _scriptFile = "";
            _inlineScript = "";
            _wrapperPath = "";
            _pluginState = PluginState.Uninitialized;

            lock (_valueLock)
            {
                _lastValue = 0.0;
                _lastStringValue = "";
                _hasStringValue = false;
            }
        }

        private static string BuildInlineScript(API api)
        {
            var scriptLines = new List<string>();

            string firstLine = api.ReadString("Line", "");
            if (!string.IsNullOrEmpty(firstLine))
            {
                scriptLines.Add(firstLine);

                for (int lineIndex = 2; ; lineIndex++)
                {
                    string line = api.ReadString($"Line{lineIndex}", "");
                    if (string.IsNullOrEmpty(line)) break;
                    scriptLines.Add(line);
                }
            }

            return scriptLines.Count > 0 ? string.Join("\n", scriptLines) : "";
        }

        private static void LogError(string message)
        {
            if (_rmHandle != IntPtr.Zero)
                API.Log(_rmHandle, API.LogType.Error, message);
        }

        private static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }
}