using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rainmeter;

namespace NodeJSPlugin
{
    internal static class NodeProcess
    {
        private static volatile bool _asyncRunning = false;
        private static readonly object _asyncLock = new object();
        private static CancellationTokenSource _asyncCancellationTokenSource;
        private static Process _currentAsyncProcess = null;

        private static readonly ProcessStartInfo DefaultProcessStartInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        internal static void CleanupAsyncOperations()
        {
            lock (_asyncLock)
            {
                try
                {
                    _asyncCancellationTokenSource?.Cancel();

                    // Kill any running async process
                    if (_currentAsyncProcess != null && !_currentAsyncProcess.HasExited)
                    {
                        try
                        {
                            _currentAsyncProcess.Kill();
                            _currentAsyncProcess.WaitForExit(1000); // Wait up to 1 second
                        }
                        catch { }
                        finally
                        {
                            _currentAsyncProcess?.Dispose();
                            _currentAsyncProcess = null;
                        }
                    }

                    _asyncCancellationTokenSource?.Dispose();
                    _asyncCancellationTokenSource = null;
                    _asyncRunning = false;
                }
                catch { }
            }
        }

        internal static (double?, string) RunNodeSynchronous(string mode = "init", string customCall = "")
        {
            if (!ValidateWrapper()) return (null, "");

            try
            {
                var psi = CreateProcessStartInfo(mode, customCall);
                using var proc = Process.Start(psi);

                if (proc == null)
                {
                    LogError($"Failed to start Node.js process for {mode}.");
                    return (null, "");
                }

                var errors = new StringBuilder();
                proc.ErrorDataReceived += (sender, args) => {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        errors.AppendLine(args.Data);
                };
                proc.BeginErrorReadLine();

                double? numResult = null;
                string strResult = "";
                string resultPrefix = GetResultPrefix(mode);
                var api = new API(Plugin._rmHandle);

                ProcessNodeOutput(proc, api, resultPrefix, ref numResult, ref strResult);
                proc.WaitForExit();

                HandleErrors(errors.ToString());

                if (proc.ExitCode != 0)
                    LogError($"Node.js '{mode}' process exited with code {proc.ExitCode}.");

                return (numResult, strResult);
            }
            catch (Exception ex)
            {
                LogError($"Node execution failed: {GetSimpleErrorMessage(ex)}");
                return (null, "");
            }
        }

        internal static void RunNodeAsync()
        {
            // Prevent multiple async executions from running simultaneously
            lock (_asyncLock)
            {
                if (_asyncRunning)
                {
                    return; // Skip this update if async is already running
                }
                _asyncRunning = true;
            }

            string currentWrapperPath = Plugin._wrapperPath;
            string currentScriptFile = Plugin._scriptFile;
            IntPtr currentRmHandle = Plugin._rmHandle;

            // Cancel any previous async operation
            _asyncCancellationTokenSource?.Cancel();
            _asyncCancellationTokenSource?.Dispose();
            _asyncCancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _asyncCancellationTokenSource.Token;

            Task.Run(async () =>
            {
                try
                {
                    if (token.IsCancellationRequested || !ValidateWrapper())
                        return;

                    string tempExecutionPath = CreateTempWrapper(currentWrapperPath);
                    if (string.IsNullOrEmpty(tempExecutionPath))
                        return;

                    token.ThrowIfCancellationRequested();
                    await ExecuteAsyncNodeProcessAsync(tempExecutionPath, currentScriptFile, currentRmHandle, token);

                    // Clean up temp file
                    CleanupTempFile(tempExecutionPath);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        API.Log(currentRmHandle, API.LogType.Error, $"Async execution failed: {GetSimpleErrorMessage(ex)}");
                }
                finally
                {
                    lock (_asyncLock)
                    {
                        _asyncRunning = false;
                        if (_currentAsyncProcess != null)
                        {
                            try
                            {
                                _currentAsyncProcess.Dispose();
                            }
                            catch { }
                            finally
                            {
                                _currentAsyncProcess = null;
                            }
                        }
                    }
                }
            }, token);
        }

        private static bool ValidateWrapper()
        {
            if (string.IsNullOrWhiteSpace(Plugin._wrapperPath) || !File.Exists(Plugin._wrapperPath))
            {
                LogError("Wrapper file does not exist.");
                return false;
            }
            return true;
        }

        private static ProcessStartInfo CreateProcessStartInfo(string mode, string customCall)
        {
            string arguments = $"\"{Plugin._wrapperPath}\" {mode}";
            if (!string.IsNullOrEmpty(customCall))
                arguments += $" \"{customCall}\"";

            var psi = new ProcessStartInfo(DefaultProcessStartInfo.FileName, arguments)
            {
                RedirectStandardOutput = DefaultProcessStartInfo.RedirectStandardOutput,
                RedirectStandardError = DefaultProcessStartInfo.RedirectStandardError,
                RedirectStandardInput = DefaultProcessStartInfo.RedirectStandardInput,
                UseShellExecute = DefaultProcessStartInfo.UseShellExecute,
                CreateNoWindow = DefaultProcessStartInfo.CreateNoWindow,
                StandardOutputEncoding = DefaultProcessStartInfo.StandardOutputEncoding,
                StandardErrorEncoding = DefaultProcessStartInfo.StandardErrorEncoding,
                WorkingDirectory = GetWorkingDirectory()
            };

            return psi;
        }

        private static string GetWorkingDirectory()
        {
            if (!string.IsNullOrEmpty(Plugin._scriptFile))
            {
                return Path.GetDirectoryName(Path.GetFullPath(Plugin._scriptFile)) ?? Environment.CurrentDirectory;
            }
            return Environment.CurrentDirectory;
        }

        private static string GetResultPrefix(string mode)
        {
            return mode switch
            {
                "custom" => "@@CUSTOM_RESULT ",
                "init" => "@@INIT_RESULT ",
                _ => "@@UPDATE_RESULT "
            };
        }

        private static void ProcessNodeOutput(Process proc, API api, string resultPrefix, ref double? numResult, ref string strResult)
        {
            string line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                string trimmed = line.Trim();

                if (ProcessLogMessage(trimmed)) continue;
                if (ProcessRainmeterCommand(trimmed, api, proc)) continue;
                if (ProcessResult(trimmed, resultPrefix, ref numResult, ref strResult)) continue;

                // Only log non-empty lines that aren't internal commands
                if (!string.IsNullOrWhiteSpace(trimmed) &&
                    !trimmed.StartsWith("@@") &&
                    trimmed != "")
                {
                    API.Log(Plugin._rmHandle, API.LogType.Notice, line);
                }
            }
        }

        private static bool ProcessLogMessage(string trimmed)
        {
            var logMappings = new[]
            {
                ("@@LOG_NOTICE ", API.LogType.Notice, 13),
                ("@@LOG_WARNING ", API.LogType.Warning, 14),
                ("@@LOG_DEBUG ", API.LogType.Debug, 12),
                ("@@LOG_ERROR ", API.LogType.Error, 12)
            };

            foreach (var (prefix, logType, offset) in logMappings)
            {
                if (trimmed.StartsWith(prefix))
                {
                    API.Log(Plugin._rmHandle, logType, trimmed.Substring(offset));
                    return true;
                }
            }
            return false;
        }

        private static bool ProcessRainmeterCommand(string trimmed, API api, Process proc)
        {
            if (trimmed.StartsWith("@@RM_EXECUTE "))
            {
                api.Execute(trimmed.Substring(13));
                return true;
            }

            try
            {
                // Handle each command individually with proper parameter parsing
                if (trimmed.StartsWith("@@RM_GETVARIABLE "))
                {
                    string[] parts = trimmed.Substring(17).Split(new[] { '|' }, 2);
                    string varName = parts.Length > 0 ? parts[0] : "";
                    string defaultValue = parts.Length > 1 ? parts[1] : "";

                    string varValue = api.ReplaceVariables($"#{varName}#");
                    string result = varValue == $"#{varName}#" ? defaultValue : varValue;
                    SendToNode(proc, result);
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READSTRINGFROMSECTION "))
                {
                    string[] parts = trimmed.Substring(27).Split(new[] { '|' }, 3);
                    if (parts.Length >= 3)
                    {
                        string result = api.ReadStringFromSection(parts[0], parts[1], parts[2]);
                        SendToNode(proc, result);
                    }
                    else
                    {
                        SendToNode(proc, "");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READSTRING "))
                {
                    string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    string defaultValue = parts.Length > 1 ? parts[1] : "";

                    string result = api.ReadString(option, defaultValue);
                    SendToNode(proc, result);
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READDOUBLEFROMSECTION "))
                {
                    string[] parts = trimmed.Substring(27).Split(new[] { '|' }, 3);
                    if (parts.Length >= 3)
                    {
                        string section = parts[0];
                        string option = parts[1];
                        double defaultValue = double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double defVal) ? defVal : 0.0;

                        double result = api.ReadDoubleFromSection(section, option, defaultValue);
                        SendToNode(proc, result.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        SendToNode(proc, "0");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READDOUBLE "))
                {
                    string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    double defaultValue = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double defVal) ? defVal : 0.0;

                    double result = api.ReadDouble(option, defaultValue);
                    SendToNode(proc, result.ToString(CultureInfo.InvariantCulture));
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READINTFROMSECTION "))
                {
                    string[] parts = trimmed.Substring(24).Split(new[] { '|' }, 3);
                    if (parts.Length >= 3)
                    {
                        string section = parts[0];
                        string option = parts[1];
                        int defaultValue = int.TryParse(parts[2], out int defVal) ? defVal : 0;

                        int result = api.ReadIntFromSection(section, option, defaultValue);
                        SendToNode(proc, result.ToString());
                    }
                    else
                    {
                        SendToNode(proc, "0");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READINT "))
                {
                    string[] parts = trimmed.Substring(13).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    int defaultValue = parts.Length > 1 && int.TryParse(parts[1], out int defVal) ? defVal : 0;

                    int result = api.ReadInt(option, defaultValue);
                    SendToNode(proc, result.ToString());
                    return true;
                }

                // Simple commands without parameters
                if (trimmed.StartsWith("@@RM_GETMEASURENAME"))
                {
                    SendToNode(proc, api.GetMeasureName());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKINNAME"))
                {
                    SendToNode(proc, api.GetSkinName());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKIN"))
                {
                    SendToNode(proc, api.GetSkin().ToString());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKINWINDOW"))
                {
                    SendToNode(proc, api.GetSkinWindow().ToString());
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError($"RM command failed: {GetSimpleErrorMessage(ex)}");
                SendToNode(proc, "");
                return true;
            }

            return false;
        }

        private static bool ProcessResult(string trimmed, string resultPrefix, ref double? numResult, ref string strResult)
        {
            if (trimmed.StartsWith(resultPrefix))
            {
                string payload = trimmed.Substring(resultPrefix.Length);
                strResult = payload;

                if (!string.IsNullOrEmpty(payload) &&
                    double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    numResult = v;
                }
                return true;
            }
            return false;
        }

        private static string CreateTempWrapper(string currentWrapperPath)
        {
            try
            {
                lock (Plugin._stateLock)
                {
                    if (string.IsNullOrWhiteSpace(currentWrapperPath) || !File.Exists(currentWrapperPath))
                        return null;

                    string tempPath = Path.GetTempFileName();
                    File.Copy(currentWrapperPath, tempPath, true);
                    return tempPath;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task ExecuteAsyncNodeProcessAsync(string tempPath, string scriptFile, IntPtr rmHandle, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{tempPath}\" update",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = !string.IsNullOrEmpty(scriptFile) ?
                    (Path.GetDirectoryName(Path.GetFullPath(scriptFile)) ?? Environment.CurrentDirectory) :
                    Environment.CurrentDirectory
            };

            Process proc = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null)
                {
                    API.Log(rmHandle, API.LogType.Error, "Failed to start async Node.js process.");
                    return;
                }

                // Store reference for cleanup
                lock (_asyncLock)
                {
                    _currentAsyncProcess = proc;
                }

                SetupAsyncEventHandlers(proc, rmHandle);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Wait for process to complete with cancellation support
                while (!proc.HasExited && !token.IsCancellationRequested)
                {
                    await Task.Delay(50, token);
                }

                if (!proc.HasExited && !token.IsCancellationRequested)
                {
                    try
                    {
                        proc.Kill();
                        await Task.Delay(100, CancellationToken.None); // Brief wait for cleanup
                    }
                    catch { }
                }

                if (proc.ExitCode != 0 && !token.IsCancellationRequested)
                    API.Log(rmHandle, API.LogType.Error, $"Async Node.js process exited with code {proc.ExitCode}.");
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation
                if (proc != null && !proc.HasExited)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(1000);
                    }
                    catch { }
                }
                throw; // Re-throw to be handled by caller
            }
            catch (Exception ex)
            {
                API.Log(rmHandle, API.LogType.Error, $"Async process execution failed: {GetSimpleErrorMessage(ex)}");
            }
            finally
            {
                try
                {
                    proc?.Dispose();
                }
                catch { }

                lock (_asyncLock)
                {
                    if (_currentAsyncProcess == proc)
                        _currentAsyncProcess = null;
                }
            }
        }

        private static void SetupAsyncEventHandlers(Process proc, IntPtr rmHandle)
        {
            proc.OutputDataReceived += (s, e) => {
                if (string.IsNullOrEmpty(e.Data)) return;

                try
                {
                    ProcessAsyncOutput(e.Data.Trim(), rmHandle);
                }
                catch (Exception ex)
                {
                    API.Log(rmHandle, API.LogType.Error, $"Output handler error: {GetSimpleErrorMessage(ex)}");
                }
            };

            proc.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    string error = e.Data.Trim();
                    API.Log(rmHandle, API.LogType.Error,
                        error.StartsWith("@@LOG_ERROR ") ? error.Substring(12) : error);
                }
            };
        }

        private static void ProcessAsyncOutput(string line, IntPtr rmHandle)
        {
            if (ProcessLogMessage(line)) return;

            if (line.StartsWith("@@RM_EXECUTE "))
            {
                new API(rmHandle).Execute(line.Substring(13));
                return;
            }

            if (line.StartsWith("@@RM_") && !line.StartsWith("@@RM_EXECUTE "))
            {
                API.Log(rmHandle, API.LogType.Warning,
                    "RM API calls are not supported in async updates. Use bangs or initialize for this functionality.");
                return;
            }

            if (line.StartsWith("@@UPDATE_RESULT "))
            {
                UpdateAsyncResult(line.Substring(16));
                return;
            }

            // Only log non-empty lines that aren't internal commands
            if (!string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("@@") &&
                line.Trim() != "")
            {
                API.Log(rmHandle, API.LogType.Notice, line);
            }
        }

        private static void UpdateAsyncResult(string payload)
        {
            lock (Plugin._valueLock)
            {
                Plugin._lastStringValue = payload;
                Plugin._hasStringValue = !string.IsNullOrEmpty(payload);

                if (!string.IsNullOrEmpty(payload) &&
                    double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    Plugin._lastValue = v;
                }
            }
        }

        private static void HandleErrors(string allErrors)
        {
            if (string.IsNullOrWhiteSpace(allErrors)) return;

            foreach (string errorLine in allErrors.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string cleanError = errorLine.StartsWith("@@LOG_ERROR ") ? errorLine.Substring(12) : errorLine;
                API.Log(Plugin._rmHandle, API.LogType.Error, cleanError);
            }
        }

        private static void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private static void SendToNode(Process proc, string message)
        {
            try
            {
                proc.StandardInput.WriteLine(message);
                proc.StandardInput.Flush();
            }
            catch { }
        }

        private static void LogError(string message)
        {
            API.Log(Plugin._rmHandle, API.LogType.Error, message);
        }

        private static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }
}