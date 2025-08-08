using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rainmeter;

namespace NodeJSPlugin
{
    public static class Plugin
    {

        private enum PluginState { Uninitialized, Initializing, Initialized }

        private static string _scriptFile = "";
        private static string _wrapperPath = "";
        private static IntPtr _rmHandle = IntPtr.Zero;
        private static double _lastValue = 0.0;
        private static string _lastStringValue = "";
        private static bool _hasStringValue = false;
        private static readonly object _valueLock = new object();
        private static CancellationTokenSource _cancellationTokenSource;

        private static volatile PluginState _pluginState = PluginState.Uninitialized;
        private static readonly object _stateLock = new object(); 

        private static void CreateWrapper()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_scriptFile)) return;

                string scriptFull = Path.GetFullPath(_scriptFile);
                string escapedScriptPath = scriptFull.Replace("\\", "\\\\").Replace("'", "\\'");

                string wrapper = $@"
(function(){{
  console.log = (...args) => {{ process.stdout.write('@@LOG_NOTICE ' + args.join(' ') + '\n'); }};
  console.warn = (...args) => {{ process.stdout.write('@@LOG_WARNING ' + args.join(' ') + '\n'); }};
  console.debug = (...args) => {{ process.stdout.write('@@LOG_DEBUG ' + args.join(' ') + '\n'); }};
  console.error = (...args) => {{ process.stderr.write('@@LOG_ERROR ' + args.join(' ') + '\n'); }};

  let scriptModule = null;

  try {{
    scriptModule = require('{escapedScriptPath}');

    async function runInit() {{
      try {{
        if (scriptModule && typeof scriptModule.initialize === 'function') {{
          const res = await scriptModule.initialize();
          process.stdout.write('@@INIT_RESULT ' + (res === undefined ? '' : String(res)) + '\n');
        }} else {{
          process.stdout.write('@@INIT_RESULT \n');
        }}
      }} catch (e) {{
        process.stderr.write('@@LOG_ERROR Initialize function error: ' + (e && e.stack ? e.stack : e) + '\n');
      }}
    }}

    async function runUpdate() {{
      try {{
        if (scriptModule && typeof scriptModule.update === 'function') {{
          const res = await scriptModule.update();
          process.stdout.write('@@UPDATE_RESULT ' + (res === undefined ? '' : String(res)) + '\n');
        }} else {{
          process.stdout.write('@@UPDATE_RESULT \n');
        }}
      }} catch (e) {{
        process.stderr.write('@@LOG_ERROR Update function error: ' + (e && e.stack ? e.stack : e) + '\n');
      }}
    }}

    async function runCustom(functionCall) {{
      try {{
        if (!scriptModule) {{
          process.stderr.write('@@LOG_ERROR Script module not loaded\n');
          return;
        }}

        const result = eval('scriptModule.' + functionCall);
        const resolvedResult = await Promise.resolve(result);
        process.stdout.write('@@CUSTOM_RESULT ' + (resolvedResult === undefined ? '' : String(resolvedResult)) + '\n');
      }} catch (e) {{
        process.stderr.write('@@LOG_ERROR Custom function error: ' + (e && e.stack ? e.stack : e) + '\n');
      }}
    }}

    const mode = process.argv[2] || 'update';
    const customCall = process.argv[3] || '';

    if (mode === 'init') {{
      runInit();
    }} else if (mode === 'custom' && customCall) {{
      runCustom(customCall);
    }} else {{
      runUpdate();
    }}
  }} catch (e) {{
    process.stderr.write('@@LOG_ERROR Script loading error: ' + (e && e.stack ? e.stack : e) + '\n');
  }}
}})();
";

                string tempDir = Path.GetTempPath();
                string name = "RainNodeWrapper_" + Guid.NewGuid().ToString("N") + ".js";
                string path = Path.Combine(tempDir, name);
                File.WriteAllText(path, wrapper);

                try { if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath)) File.Delete(_wrapperPath); } catch { }
                _wrapperPath = path;
            }
            catch (Exception ex)
            {
                if (_rmHandle != IntPtr.Zero)
                    API.Log(_rmHandle, API.LogType.Error, "CreateWrapper exception: " + ex.Message);
            }
        }

        private static (double?, string) RunNodeSynchronous(string mode = "init", string customCall = "")
        {
            if (string.IsNullOrWhiteSpace(_wrapperPath) || !File.Exists(_wrapperPath))
            {
                API.Log(_rmHandle, API.LogType.Error, "Operation failed: Wrapper file does not exist.");
                return (null, "");
            }

            try
            {
                string arguments = $"\"{_wrapperPath}\" {mode}";
                if (!string.IsNullOrEmpty(customCall))
                {
                    arguments += $" \"{customCall}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_scriptFile)) ?? Environment.CurrentDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    API.Log(_rmHandle, API.LogType.Error, $"Failed to start Node.js process for {mode}.");
                    return (null, "");
                }

                string output = proc.StandardOutput.ReadToEnd();
                string errors = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    foreach (string line in errors.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("@@LOG_ERROR ")) API.Log(_rmHandle, API.LogType.Error, trimmed.Substring(12));
                        else API.Log(_rmHandle, API.LogType.Error, trimmed);
                    }
                }

                if (proc.ExitCode != 0)
                {
                    API.Log(_rmHandle, API.LogType.Warning, $"Node.js '{mode}' process exited with code {proc.ExitCode}.");
                    return (null, "");
                }

                double? numResult = null;
                string strResult = "";
                string resultPrefix = mode == "custom" ? "@@CUSTOM_RESULT " : (mode == "init" ? "@@INIT_RESULT " : "@@UPDATE_RESULT ");

                foreach (string line in output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("@@LOG_NOTICE ")) { API.Log(_rmHandle, API.LogType.Notice, trimmed.Substring(13)); }
                    else if (trimmed.StartsWith("@@LOG_WARNING ")) { API.Log(_rmHandle, API.LogType.Warning, trimmed.Substring(14)); }
                    else if (trimmed.StartsWith("@@LOG_DEBUG ")) { API.Log(_rmHandle, API.LogType.Debug, trimmed.Substring(12)); }
                    else if (trimmed.StartsWith(resultPrefix))
                    {
                        string payload = trimmed.Substring(resultPrefix.Length);
                        strResult = payload; 

                        if (!string.IsNullOrEmpty(payload))
                        {
                            if (double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                            {
                                numResult = v;
                            }
                            else
                            {
                                API.Log(_rmHandle, API.LogType.Notice, $"'{mode}' result (non-numeric): {payload}");
                            }
                        }
                    }
                    else { API.Log(_rmHandle, API.LogType.Notice, line); }
                }
                return (numResult, strResult);
            }
            catch (Exception ex)
            {
                API.Log(_rmHandle, API.LogType.Error, $"RunNodeSynchronous exception: {ex.Message}");
                return (null, "");
            }
        }

        private static void RunNodeAsync()
        {
            string currentWrapperPath = _wrapperPath;
            string currentScriptFile = _scriptFile;
            IntPtr currentRmHandle = _rmHandle;
            CancellationToken token = _cancellationTokenSource.Token;

            Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;

                string tempExecutionPath = "";
                try
                {

                    lock (_stateLock)
                    {
                        if (string.IsNullOrWhiteSpace(currentWrapperPath) || !File.Exists(currentWrapperPath)) return;
                        tempExecutionPath = Path.GetTempFileName();
                        File.Copy(currentWrapperPath, tempExecutionPath, true);
                    }

                    token.ThrowIfCancellationRequested();

                    var psi = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{tempExecutionPath}\" update",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(currentScriptFile)) ?? Environment.CurrentDirectory
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        API.Log(currentRmHandle, API.LogType.Error, "Failed to start Node.js process for update.");
                        return;
                    }

                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        try
                        {
                            string line = e.Data.Trim();
                            if (line.StartsWith("@@LOG_NOTICE ")) { API.Log(currentRmHandle, API.LogType.Notice, line.Substring(13)); }
                            else if (line.StartsWith("@@LOG_WARNING ")) { API.Log(currentRmHandle, API.LogType.Warning, line.Substring(14)); }
                            else if (line.StartsWith("@@LOG_DEBUG ")) { API.Log(currentRmHandle, API.LogType.Debug, line.Substring(12)); }
                            else if (line.StartsWith("@@LOG_ERROR ")) { API.Log(currentRmHandle, API.LogType.Error, line.Substring(12)); }
                            else if (line.StartsWith("@@UPDATE_RESULT "))
                            {
                                string payload = line.Substring(16);
                                lock (_valueLock)
                                {
                                    _lastStringValue = payload;
                                    _hasStringValue = !string.IsNullOrEmpty(payload);

                                    if (!string.IsNullOrEmpty(payload))
                                    {
                                        if (double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                                        {
                                            _lastValue = v;
                                        }
                                        else
                                        {
                                            API.Log(currentRmHandle, API.LogType.Notice, $"'update' result (non-numeric): {payload}");
                                        }
                                    }
                                }
                            }
                            else { API.Log(currentRmHandle, API.LogType.Notice, line); }
                        }
                        catch (Exception ex) { API.Log(currentRmHandle, API.LogType.Error, "Output handler exception: " + ex.Message); }
                    };

                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        string line = e.Data.Trim();
                        if (line.StartsWith("@@LOG_ERROR ")) API.Log(currentRmHandle, API.LogType.Error, line.Substring(12));
                        else API.Log(currentRmHandle, API.LogType.Error, line);
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        API.Log(currentRmHandle, API.LogType.Warning, $"Node.js 'update' process exited with code {proc.ExitCode}.");
                    }
                }
                catch (OperationCanceledException) {  }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        API.Log(currentRmHandle, API.LogType.Error, $"RunNodeAsync exception: {ex.Message}");
                    }
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempExecutionPath) && File.Exists(tempExecutionPath))
                    {
                        try { File.Delete(tempExecutionPath); } catch {  }
                    }
                }
            }, token);
        }

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

            API.Log(_rmHandle, API.LogType.Notice, "NodeJS plugin initialized with fresh state.");
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            CleanupAndReset();
        }

        private static bool _forceReload = false;

        private static void CleanupAndReset()
        {
            lock (_stateLock)
            {

                _forceReload = true;

                try
                {
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
                catch (Exception ex)
                {
                    if (_rmHandle != IntPtr.Zero)
                        API.Log(_rmHandle, API.LogType.Warning, "Error canceling tasks: " + ex.Message);
                }

                try
                {
                    if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath))
                    {
                        File.Delete(_wrapperPath);
                    }
                }
                catch (Exception ex)
                {
                    if (_rmHandle != IntPtr.Zero)
                        API.Log(_rmHandle, API.LogType.Warning, "Error deleting wrapper file: " + ex.Message);
                }

                _scriptFile = "";
                _wrapperPath = "";
                _pluginState = PluginState.Uninitialized;

                lock (_valueLock)
                {
                    _lastValue = 0.0;
                    _lastStringValue = "";
                    _hasStringValue = false;
                }

                if (_rmHandle != IntPtr.Zero)
                    API.Log(_rmHandle, API.LogType.Notice, "NodeJS plugin cleaned up and reset.");
            }
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            _rmHandle = rm;
            var api = new API(rm);
            string newScriptFile = api.ReadPath("ScriptFile", "").Trim();

            lock (_stateLock)
            {

                if (_pluginState == PluginState.Initialized &&
                    newScriptFile == _scriptFile &&
                    !_forceReload)
                {

                    return;
                }

                if (_forceReload)
                {
                    _forceReload = false;
                    API.Log(_rmHandle, API.LogType.Notice, "Forced reload: Cleaning up previous state...");
                }
                else if (newScriptFile != _scriptFile)
                {
                    API.Log(_rmHandle, API.LogType.Notice, "Script file changed: Cleaning up previous state...");
                }

                if (_pluginState == PluginState.Initialized || _pluginState == PluginState.Initializing)
                {

                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();

                    try
                    {
                        if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath))
                        {
                            File.Delete(_wrapperPath);
                        }
                    }
                    catch {  }

                    lock (_valueLock)
                    {
                        _lastValue = 0.0;
                        _lastStringValue = "";
                        _hasStringValue = false;
                    }
                }

                _pluginState = PluginState.Initializing;
                _scriptFile = newScriptFile;

                _cancellationTokenSource = new CancellationTokenSource();
            }

            if (string.IsNullOrWhiteSpace(_scriptFile))
            {
                API.Log(_rmHandle, API.LogType.Error, "ScriptFile not set for NodeJS measure.");
                lock (_stateLock) { _pluginState = PluginState.Uninitialized; }
                return;
            }

            CreateWrapper();

            var (initialValue, initialStringValue) = RunNodeSynchronous("init");

            lock (_stateLock)
            {
                lock (_valueLock)
                {
                    if (initialValue.HasValue)
                    {
                        _lastValue = initialValue.Value;
                    }
                    _lastStringValue = initialStringValue ?? "";
                    _hasStringValue = !string.IsNullOrEmpty(_lastStringValue);
                }

                _pluginState = PluginState.Initialized;
                API.Log(_rmHandle, API.LogType.Notice, $"NodeJS script initialized successfully.");

                if (initialValue.HasValue)
                {
                    API.Log(_rmHandle, API.LogType.Notice, $"Initial numeric value: {_lastValue}");
                }
                if (_hasStringValue)
                {
                    API.Log(_rmHandle, API.LogType.Notice, $"Initial string value: {_lastStringValue}");
                }
            }
        }

        [DllExport]
        public static double Update(IntPtr data)
        {

            if (_pluginState == PluginState.Initialized && !string.IsNullOrWhiteSpace(_scriptFile))
            {
                RunNodeAsync();
            }

            lock (_valueLock)
            {
                return _lastValue;
            }
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, [MarshalAs(UnmanagedType.LPWStr)] string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                API.Log(_rmHandle, API.LogType.Warning, "ExecuteBang called with empty arguments.");
                return;
            }

            if (_pluginState != PluginState.Initialized)
            {
                API.Log(_rmHandle, API.LogType.Warning, "Cannot execute custom function: Plugin not initialized.");
                return;
            }

            try
            {
                var (resultValue, resultString) = RunNodeSynchronous("custom", args.Trim());

                lock (_valueLock)
                {
                    if (resultValue.HasValue)
                    {
                        _lastValue = resultValue.Value;
                        API.Log(_rmHandle, API.LogType.Notice, $"Custom function result (numeric): {_lastValue}");
                    }

                    _lastStringValue = resultString ?? "";
                    _hasStringValue = !string.IsNullOrEmpty(_lastStringValue);

                    if (_hasStringValue)
                    {
                        API.Log(_rmHandle, API.LogType.Notice, $"Custom function result (string): {_lastStringValue}");
                    }
                }
            }
            catch (Exception ex)
            {
                API.Log(_rmHandle, API.LogType.Error, $"ExecuteBang exception: {ex.Message}");
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
    }
}