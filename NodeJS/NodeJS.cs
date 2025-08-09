using System;
using System.Collections.Generic;
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
        private static string _inlineScript = "";
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
                string scriptContent = "";
                bool isInlineScript = false;

                // Check if we have inline script or file script
                if (!string.IsNullOrWhiteSpace(_inlineScript))
                {
                    scriptContent = _inlineScript;
                    isInlineScript = true;
                }
                else if (!string.IsNullOrWhiteSpace(_scriptFile))
                {
                    string scriptFull = Path.GetFullPath(_scriptFile);
                    string escapedScriptPath = scriptFull.Replace("\\", "\\\\").Replace("'", "\\'");
                    scriptContent = $"require('{escapedScriptPath}')";
                    isInlineScript = false;
                }
                else
                {
                    return;
                }

                // *** EXTENDED RM CONTEXT CODE ***
                // Added new functions to the RM object to expose more of the Rainmeter API to JavaScript.
                string rmContextCode = $@"
  // Helper function for synchronous stdin read.
  const fs = require('fs');
  function readFromHost() {{
    const BUFSIZE = 4096; // Increased buffer size for potentially larger values
    let buf = Buffer.alloc(BUFSIZE);
    let bytesRead = 0;
    try {{
      bytesRead = fs.readSync(process.stdin.fd, buf, 0, BUFSIZE, null);
    }} catch (e) {{
      return '';
    }}
    if (bytesRead === 0) {{
      return '';
    }}
    return buf.toString('utf8', 0, bytesRead).trim();
  }}

  // RM object for Rainmeter integration
  global.RM = {{
    Execute: function(command) {{
      process.stdout.write('@@RM_EXECUTE ' + command + '\n');
      process.stdout.flush?.();
    }},
    GetVariable: function(variableName, defaultValue = '') {{
      process.stdout.write('@@RM_GETVARIABLE ' + variableName + '|' + defaultValue + '\n');
      process.stdout.flush?.();
      return readFromHost();
    }},
    ReadString: function(option, defValue = '') {{
        process.stdout.write('@@RM_READSTRING ' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return readFromHost();
    }},
    ReadStringFromSection: function(section, option, defValue = '') {{
        process.stdout.write('@@RM_READSTRINGFROMSECTION ' + section + '|' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return readFromHost();
    }},
    ReadDouble: function(option, defValue = 0.0) {{
        process.stdout.write('@@RM_READDOUBLE ' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return parseFloat(readFromHost());
    }},
    ReadDoubleFromSection: function(section, option, defValue = 0.0) {{
        process.stdout.write('@@RM_READDOUBLEFROMSECTION ' + section + '|' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return parseFloat(readFromHost());
    }},
    ReadInt: function(option, defValue = 0) {{
        process.stdout.write('@@RM_READINT ' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return parseInt(readFromHost(), 10);
    }},
    ReadIntFromSection: function(section, option, defValue = 0) {{
        process.stdout.write('@@RM_READINTFROMSECTION ' + section + '|' + option + '|' + defValue + '\n');
        process.stdout.flush?.();
        return parseInt(readFromHost(), 10);
    }},
    GetMeasureName: function() {{
        process.stdout.write('@@RM_GETMEASURENAME\n');
        process.stdout.flush?.();
        return readFromHost();
    }},
    GetSkinName: function() {{
        process.stdout.write('@@RM_GETSKINNAME\n');
        process.stdout.flush?.();
        return readFromHost();
    }},
    GetSkin: function() {{
        process.stdout.write('@@RM_GETSKIN\n');
        process.stdout.flush?.();
        return readFromHost();
    }},
    GetSkinWindow: function() {{
        process.stdout.write('@@RM_GETSKINWINDOW\n');
        process.stdout.flush?.();
        return readFromHost();
    }}
  }};
  
  // Also make RM available as a local variable
  const RM = global.RM;
";

                string scriptModuleCode = isInlineScript ?
                    $@"
  // Inline script execution
  let scriptModule = null;
  try {{
    // Create a module-like object for inline scripts
    scriptModule = {{}};
    
    // Execute the inline script in a context where it can define functions
    const scriptFunction = new Function('module', 'exports', 'RM', `
      {scriptContent}
      
      // Export functions that might have been defined
      if (typeof initialize !== 'undefined') module.exports.initialize = initialize;
      if (typeof update !== 'undefined') module.exports.update = update;
      
      // Export any other functions defined in global scope
      for (let key in this) {{
        if (typeof this[key] === 'function' && key !== 'initialize' && key !== 'update') {{
          module.exports[key] = this[key];
        }}
      }}
    `);
    
    const moduleObj = {{ exports: {{}} }};
    scriptFunction.call({{}}, moduleObj, moduleObj.exports, RM);
    scriptModule = moduleObj.exports;
  }} catch (e) {{
    process.stderr.write('@@LOG_ERROR Inline script compilation error: ' + (e && e.stack ? e.stack : e) + '\n');
    scriptModule = {{}};
  }}" :
                    $@"
  // File-based script loading
  let scriptModule = null;
  try {{
    scriptModule = {scriptContent};
  }} catch (e) {{
    process.stderr.write('@@LOG_ERROR Script loading error: ' + (e && e.stack ? e.stack : e) + '\n');
    scriptModule = {{}};
  }}";

                string wrapper = $@"
(function(){{
  console.log = (...args) => {{ process.stdout.write('@@LOG_NOTICE ' + args.join(' ') + '\n'); process.stdout.flush?.(); }};
  console.warn = (...args) => {{ process.stdout.write('@@LOG_WARNING ' + args.join(' ') + '\n'); process.stdout.flush?.(); }};
  console.debug = (...args) => {{ process.stdout.write('@@LOG_DEBUG ' + args.join(' ') + '\n'); process.stdout.flush?.(); }};
  console.error = (...args) => {{ process.stderr.write('@@LOG_ERROR ' + args.join(' ') + '\n'); process.stderr.flush?.(); }};

{rmContextCode}

{scriptModuleCode}

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

        private static void SendToNode(Process proc, string message)
        {
            proc.StandardInput.WriteLine(message);
            proc.StandardInput.Flush();
        }

        // *** EXTENDED RunNodeSynchronous METHOD ***
        // This method now handles a wide range of API calls from the Node.js script.
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
                    RedirectStandardInput = true, // <-- Required to send data back to Node
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = !string.IsNullOrEmpty(_scriptFile) ?
                        (Path.GetDirectoryName(Path.GetFullPath(_scriptFile)) ?? Environment.CurrentDirectory) :
                        Environment.CurrentDirectory,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    API.Log(_rmHandle, API.LogType.Error, $"Failed to start Node.js process for {mode}.");
                    return (null, "");
                }

                var errors = new StringBuilder();
                proc.ErrorDataReceived += (sender, args) => {
                    if (args.Data != null) errors.AppendLine(args.Data);
                };
                proc.BeginErrorReadLine();

                double? numResult = null;
                string strResult = "";
                string resultPrefix = mode == "custom" ? "@@CUSTOM_RESULT " : (mode == "init" ? "@@INIT_RESULT " : "@@UPDATE_RESULT ");

                var api = new API(_rmHandle);
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("@@LOG_NOTICE ")) { API.Log(_rmHandle, API.LogType.Notice, trimmed.Substring(13)); }
                    else if (trimmed.StartsWith("@@LOG_WARNING ")) { API.Log(_rmHandle, API.LogType.Warning, trimmed.Substring(14)); }
                    else if (trimmed.StartsWith("@@LOG_DEBUG ")) { API.Log(_rmHandle, API.LogType.Debug, trimmed.Substring(12)); }
                    else if (trimmed.StartsWith("@@RM_EXECUTE "))
                    {
                        api.Execute(trimmed.Substring(13));
                    }
                    else if (trimmed.StartsWith("@@RM_GETVARIABLE "))
                    {
                        string[] parts = trimmed.Substring(17).Split(new[] { '|' }, 2);
                        string varValue = api.ReplaceVariables($"#{parts[0]}#");
                        if (varValue == $"#{parts[0]}#") varValue = parts.Length > 1 ? parts[1] : "";
                        SendToNode(proc, varValue);
                    }
                    else if (trimmed.StartsWith("@@RM_READSTRINGFROMSECTION "))
                    {
                        string[] parts = trimmed.Substring(27).Split(new[] { '|' }, 3);
                        SendToNode(proc, api.ReadStringFromSection(parts[0], parts[1], parts[2]));
                    }
                    else if (trimmed.StartsWith("@@RM_READSTRING "))
                    {
                        string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                        SendToNode(proc, api.ReadString(parts[0], parts[1]));
                    }
                    else if (trimmed.StartsWith("@@RM_READDOUBLEFROMSECTION "))
                    {
                        string[] parts = trimmed.Substring(27).Split(new[] { '|' }, 3);
                        SendToNode(proc, api.ReadDoubleFromSection(parts[0], parts[1], double.Parse(parts[2], CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                    }
                    else if (trimmed.StartsWith("@@RM_READDOUBLE "))
                    {
                        string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                        SendToNode(proc, api.ReadDouble(parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
                    }
                    else if (trimmed.StartsWith("@@RM_READINTFROMSECTION "))
                    {
                        string[] parts = trimmed.Substring(24).Split(new[] { '|' }, 3);
                        SendToNode(proc, api.ReadIntFromSection(parts[0], parts[1], int.Parse(parts[2])).ToString());
                    }
                    else if (trimmed.StartsWith("@@RM_READINT "))
                    {
                        string[] parts = trimmed.Substring(13).Split(new[] { '|' }, 2);
                        SendToNode(proc, api.ReadInt(parts[0], int.Parse(parts[1])).ToString());
                    }
                    else if (trimmed.StartsWith("@@RM_GETMEASURENAME"))
                    {
                        SendToNode(proc, api.GetMeasureName());
                    }
                    else if (trimmed.StartsWith("@@RM_GETSKINNAME"))
                    {
                        SendToNode(proc, api.GetSkinName());
                    }
                    else if (trimmed.StartsWith("@@RM_GETSKIN"))
                    {
                        SendToNode(proc, api.GetSkin().ToString());
                    }
                    else if (trimmed.StartsWith("@@RM_GETSKINWINDOW"))
                    {
                        SendToNode(proc, api.GetSkinWindow().ToString());
                    }
                    else if (trimmed.StartsWith(resultPrefix))
                    {
                        string payload = trimmed.Substring(resultPrefix.Length);
                        strResult = payload;

                        if (!string.IsNullOrEmpty(payload) && double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                        {
                            numResult = v;
                        }
                    }
                    else { API.Log(_rmHandle, API.LogType.Notice, line); }
                }

                proc.WaitForExit();

                string allErrors = errors.ToString();
                if (!string.IsNullOrWhiteSpace(allErrors))
                {
                    foreach (string errorLine in allErrors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        API.Log(_rmHandle, API.LogType.Error, errorLine.StartsWith("@@LOG_ERROR ") ? errorLine.Substring(12) : errorLine);
                    }
                }

                if (proc.ExitCode != 0)
                {
                    API.Log(_rmHandle, API.LogType.Warning, $"Node.js '{mode}' process exited with code {proc.ExitCode}.");
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
                        WorkingDirectory = !string.IsNullOrEmpty(currentScriptFile) ?
                            (Path.GetDirectoryName(Path.GetFullPath(currentScriptFile)) ?? Environment.CurrentDirectory) :
                            Environment.CurrentDirectory
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
                            else if (line.StartsWith("@@RM_EXECUTE "))
                            {
                                new API(currentRmHandle).Execute(line.Substring(13));
                            }
                            else if (line.StartsWith("@@RM_"))
                            {
                                API.Log(currentRmHandle, API.LogType.Warning, "RM API calls are not supported in asynchronous 'update' calls. Use bangs or initialize for this functionality.");
                            }
                            else if (line.StartsWith("@@UPDATE_RESULT "))
                            {
                                string payload = line.Substring(16);
                                lock (_valueLock)
                                {
                                    _lastStringValue = payload;
                                    _hasStringValue = !string.IsNullOrEmpty(payload);

                                    if (!string.IsNullOrEmpty(payload) && double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                                    {
                                        _lastValue = v;
                                    }
                                }
                            }
                            else { API.Log(currentRmHandle, API.LogType.Notice, line); }
                        }
                        catch (Exception ex) { API.Log(currentRmHandle, API.LogType.Error, "Output handler exception: " + ex.Message); }
                    };

                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            API.Log(currentRmHandle, API.LogType.Error, e.Data.Trim().StartsWith("@@LOG_ERROR ") ? e.Data.Trim().Substring(12) : e.Data.Trim());
                        }
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        API.Log(currentRmHandle, API.LogType.Warning, $"Node.js 'update' process exited with code {proc.ExitCode}.");
                    }
                }
                catch (OperationCanceledException) { }
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
                        try { File.Delete(tempExecutionPath); } catch { }
                    }
                }
            }, token);
        }

        private static string BuildInlineScript(API api)
        {
            var scriptLines = new List<string>();
            int lineIndex = 1;

            string firstLine = api.ReadString("Line", "");
            if (!string.IsNullOrEmpty(firstLine))
            {
                scriptLines.Add(firstLine);
                lineIndex = 2;
            }

            while (true)
            {
                string line = api.ReadString($"Line{lineIndex}", "");
                if (string.IsNullOrEmpty(line))
                    break;

                scriptLines.Add(line);
                lineIndex++;
            }

            return scriptLines.Count > 0 ? string.Join("\n", scriptLines) : "";
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
                catch { }

                try
                {
                    if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath))
                    {
                        File.Delete(_wrapperPath);
                    }
                }
                catch { }

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
            string newInlineScript = BuildInlineScript(api);

            lock (_stateLock)
            {
                if (_pluginState == PluginState.Initialized &&
                    newScriptFile == _scriptFile &&
                    newInlineScript == _inlineScript &&
                    !_forceReload)
                {
                    return;
                }

                _forceReload = false;

                if (newScriptFile != _scriptFile || newInlineScript != _inlineScript)
                {
                    API.Log(_rmHandle, API.LogType.Notice, "Script changed: Cleaning up previous state...");
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
                    catch { }

                    lock (_valueLock)
                    {
                        _lastValue = 0.0;
                        _lastStringValue = "";
                        _hasStringValue = false;
                    }
                }

                _pluginState = PluginState.Initializing;
                _scriptFile = newScriptFile;
                _inlineScript = newInlineScript;

                _cancellationTokenSource = new CancellationTokenSource();
            }

            if (string.IsNullOrWhiteSpace(_scriptFile) && string.IsNullOrWhiteSpace(_inlineScript))
            {
                API.Log(_rmHandle, API.LogType.Error, "Neither ScriptFile nor Line parameters are set for NodeJS measure.");
                lock (_stateLock) { _pluginState = PluginState.Uninitialized; }
                return;
            }

            if (!string.IsNullOrWhiteSpace(_inlineScript))
            {
                API.Log(_rmHandle, API.LogType.Notice, "Using inline script with " + _inlineScript.Split('\n').Length + " lines.");
            }
            else
            {
                API.Log(_rmHandle, API.LogType.Notice, "Using script file: " + _scriptFile);
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
            }
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            if (_pluginState == PluginState.Initialized &&
                (!string.IsNullOrWhiteSpace(_scriptFile) || !string.IsNullOrWhiteSpace(_inlineScript)))
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
            if (string.IsNullOrWhiteSpace(args)) return;

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
                    }
                    _lastStringValue = resultString ?? "";
                    _hasStringValue = !string.IsNullOrEmpty(_lastStringValue);
                }
            }
            catch (Exception ex)
            {
                API.Log(_rmHandle, API.LogType.Error, $"ExecuteBang exception: {ex.Message}");
            }
        }

        [DllExport]
        public static IntPtr Call(IntPtr data, int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            if (argc == 0 || string.IsNullOrWhiteSpace(argv[0]))
            {
                return IntPtr.Zero;
            }

            if (_pluginState != PluginState.Initialized)
            {
                API.Log(_rmHandle, API.LogType.Warning, "Cannot execute inline call: Plugin not initialized.");
                return IntPtr.Zero;
            }

            string functionCall = argv[0];

            try
            {
                var (_, resultString) = RunNodeSynchronous("custom", functionCall.Trim());

                if (resultString != null)
                {
                    return Marshal.StringToHGlobalUni(resultString);
                }
            }
            catch (Exception ex)
            {
                API.Log(_rmHandle, API.LogType.Error, $"Inline call '{functionCall}' exception: {ex.Message}");
            }

            return IntPtr.Zero;
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
