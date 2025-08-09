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

                // *** MODIFIED RM CONTEXT CODE ***
                // Added a synchronous read function and updated RM.GetVariable to perform a two-way communication.
                string rmContextCode = $@"
  // Helper function for synchronous stdin read. This is necessary to wait for C# to send the variable value.
  const fs = require('fs');
  function readFromHost() {{
    const BUFSIZE = 1024;
    let buf = Buffer.alloc(BUFSIZE);
    let bytesRead = 0;
    try {{
      bytesRead = fs.readSync(process.stdin.fd, buf, 0, BUFSIZE, null);
    }} catch (e) {{
      // Ignore errors, likely means stream ended
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
      // 1. Tell C# we want a variable
      process.stdout.write('@@RM_GETVARIABLE ' + variableName + '|' + defaultValue + '\n');
      process.stdout.flush?.();
      // 2. Wait for C# to write the value back to our stdin
      const value = readFromHost();
      return value;
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

        // *** MODIFIED RunNodeSynchronous METHOD ***
        // This method now reads output line-by-line and can write back to the Node.js process,
        // enabling two-way communication for RM.GetVariable.
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

                // Read errors asynchronously
                var errors = new StringBuilder();
                proc.ErrorDataReceived += (sender, args) => {
                    if (args.Data != null) errors.AppendLine(args.Data);
                };
                proc.BeginErrorReadLine();


                double? numResult = null;
                string strResult = "";
                string resultPrefix = mode == "custom" ? "@@CUSTOM_RESULT " : (mode == "init" ? "@@INIT_RESULT " : "@@UPDATE_RESULT ");

                // Process output line-by-line instead of all at once
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("@@LOG_NOTICE ")) { API.Log(_rmHandle, API.LogType.Notice, trimmed.Substring(13)); }
                    else if (trimmed.StartsWith("@@LOG_WARNING ")) { API.Log(_rmHandle, API.LogType.Warning, trimmed.Substring(14)); }
                    else if (trimmed.StartsWith("@@LOG_DEBUG ")) { API.Log(_rmHandle, API.LogType.Debug, trimmed.Substring(12)); }
                    else if (trimmed.StartsWith("@@RM_EXECUTE "))
                    {
                        string command = trimmed.Substring(13);
                        try
                        {
                            var api = new API(_rmHandle);
                            api.Execute(command);
                            API.Log(_rmHandle, API.LogType.Notice, $"Executed Rainmeter command: {command}");
                        }
                        catch (Exception ex)
                        {
                            API.Log(_rmHandle, API.LogType.Error, $"Error executing Rainmeter command '{command}': {ex.Message}");
                        }
                    }
                    else if (trimmed.StartsWith("@@RM_GETVARIABLE "))
                    {
                        // Node is asking for a variable.
                        string varRequest = trimmed.Substring(17);
                        string[] parts = varRequest.Split(new[] { '|' }, 2);
                        string varName = parts.Length > 0 ? parts[0] : "";
                        string defaultValue = parts.Length > 1 ? parts[1] : "";
                        string varValue = defaultValue;

                        try
                        {
                            var api = new API(_rmHandle);
                            string replacedValue = api.ReplaceVariables($"#{varName}#");
                            // If the variable doesn't exist, ReplaceVariables returns the original string literal
                            if (replacedValue != $"#{varName}#")
                            {
                                varValue = replacedValue;
                            }
                            API.Log(_rmHandle, API.LogType.Notice, $"Variable {varName} = {varValue}");
                        }
                        catch (Exception ex)
                        {
                            API.Log(_rmHandle, API.LogType.Error, $"Error getting variable '{varName}': {ex.Message}");
                        }

                        // Send the result back to the Node process via its standard input.
                        proc.StandardInput.WriteLine(varValue);
                        proc.StandardInput.Flush();
                    }
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

                proc.WaitForExit();

                string allErrors = errors.ToString();
                if (!string.IsNullOrWhiteSpace(allErrors))
                {
                    foreach (string errorLine in allErrors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = errorLine.Trim();
                        if (trimmed.StartsWith("@@LOG_ERROR ")) API.Log(_rmHandle, API.LogType.Error, trimmed.Substring(12));
                        else API.Log(_rmHandle, API.LogType.Error, trimmed);
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
            // Note: Asynchronous mode does not support GetVariable.
            // This would require a much more complex implementation.
            // For now, only synchronous calls (Initialize, Bangs) support GetVariable.
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
                                string command = line.Substring(13);
                                try
                                {
                                    var api = new API(currentRmHandle);
                                    api.Execute(command);
                                    API.Log(currentRmHandle, API.LogType.Notice, $"Executed Rainmeter command: {command}");
                                }
                                catch (Exception ex)
                                {
                                    API.Log(currentRmHandle, API.LogType.Error, $"Error executing Rainmeter command '{command}': {ex.Message}");
                                }
                            }
                            else if (line.StartsWith("@@RM_GETVARIABLE "))
                            {
                                // This case is intentionally not handled in async mode as it would be very complex.
                                API.Log(currentRmHandle, API.LogType.Warning, "RM.GetVariable is not supported in asynchronous 'update' calls. Use bangs or initialize for this functionality.");
                            }
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

            // Check for Line parameter first
            string firstLine = api.ReadString("Line", "");
            if (!string.IsNullOrEmpty(firstLine))
            {
                scriptLines.Add(firstLine);
                lineIndex = 2;
            }

            // Then check for Line2, Line3, etc.
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
                // Check if anything has changed
                if (_pluginState == PluginState.Initialized &&
                    newScriptFile == _scriptFile &&
                    newInlineScript == _inlineScript &&
                    !_forceReload)
                {
                    return;
                }

                if (_forceReload)
                {
                    _forceReload = false;
                    API.Log(_rmHandle, API.LogType.Notice, "Forced reload: Cleaning up previous state...");
                }
                else if (newScriptFile != _scriptFile || newInlineScript != _inlineScript)
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

            // Validate that we have either a script file or inline script
            if (string.IsNullOrWhiteSpace(_scriptFile) && string.IsNullOrWhiteSpace(_inlineScript))
            {
                API.Log(_rmHandle, API.LogType.Error, "Neither ScriptFile nor Line parameters are set for NodeJS measure.");
                lock (_stateLock) { _pluginState = PluginState.Uninitialized; }
                return;
            }

            // Log what type of script we're using
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

        // *** NEW METHOD ***
        // This method allows calling a JS function directly from a measure and getting its string return value.
        // Usage in Rainmeter: [&MeasureName:Call("MyFunction(arg1, 'arg2')")]
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
                    // Rainmeter is responsible for freeing this memory.
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
