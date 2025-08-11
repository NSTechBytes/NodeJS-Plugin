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

        internal static void CleanupAsyncOperations(PluginInstanceData instance)
        {
            lock (_asyncLock)
            {
                try
                {
                    _asyncCancellationTokenSource?.Cancel();

                    if (_currentAsyncProcess != null && !_currentAsyncProcess.HasExited)
                    {
                        try
                        {
                            _currentAsyncProcess.Kill();
                            _currentAsyncProcess.WaitForExit(1000);
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

            CleanupPersistentProcess(instance);
        }

        private static void CleanupPersistentProcess(PluginInstanceData instance)
        {
            lock (instance.PersistentProcessLock)
            {
                try
                {
                    if (instance.PersistentProcess != null && !instance.PersistentProcess.HasExited)
                    {
                        try
                        {
                            instance.PersistentProcess.Kill();
                            instance.PersistentProcess.WaitForExit(1000);
                        }
                        catch { }
                    }

                    instance.PersistentProcess?.Dispose();
                    instance.PersistentProcess = null;
                    instance.PersistentProcessInitialized = false;
                }
                catch { }
            }
        }

        private static Process GetOrCreatePersistentProcess(PluginInstanceData instance)
        {
            lock (instance.PersistentProcessLock)
            {
                if (instance.PersistentProcess == null || instance.PersistentProcess.HasExited || !instance.PersistentProcessInitialized)
                {
                    CleanupPersistentProcess(instance);

                    if (!NodeProcessHelper.ValidateWrapper(instance)) return null;

                    var psi = CreateProcessStartInfo(instance, "persistent", "");
                    instance.PersistentProcess = Process.Start(psi);

                    if (instance.PersistentProcess != null)
                    {
                        SetupPersistentProcessHandlers(instance.PersistentProcess, instance);
                        instance.PersistentProcess.BeginOutputReadLine();
                        instance.PersistentProcess.BeginErrorReadLine();
                        instance.PersistentProcessInitialized = true;
                    }
                }

                return instance.PersistentProcess;
            }
        }

        private static void SetupPersistentProcessHandlers(Process proc, PluginInstanceData instance)
        {
            var api = new API(instance.RmHandle);

            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                try
                {
                    ProcessPersistentOutput(e.Data.Trim(), instance, api, proc);
                }
                catch (Exception ex)
                {
                    API.Log(instance.RmHandle, API.LogType.Error, $"Persistent output handler error: {Common.GetSimpleErrorMessage(ex)}");
                }
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    string error = e.Data.Trim();
                    API.Log(instance.RmHandle, API.LogType.Error,
                        error.StartsWith("@@LOG_ERROR ") ? error.Substring(12) : error);
                }
            };
        }

        private static void ProcessPersistentOutput(string line, PluginInstanceData instance, API api, Process proc)
        {
            if (NodeProcessHelper.ProcessLogMessage(line, instance)) return;

            if (line.StartsWith("@@RM_EXECUTE "))
            {
                api.Execute(line.Substring(13));
                return;
            }

            if (RainmeterCommands.ProcessRainmeterCommand(line, api, proc,instance)) return;

            if (line.StartsWith("@@UPDATE_RESULT ") ||
                line.StartsWith("@@INIT_RESULT ") ||
                line.StartsWith("@@CUSTOM_RESULT "))
            {
                UpdatePersistentResult(line, instance);
                return;
            }

            if (!string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("@@") &&
                line.Trim() != "")
            {
                API.Log(instance.RmHandle, API.LogType.Notice, line);
            }
        }

        private static void UpdatePersistentResult(string line, PluginInstanceData instance)
        {
            string payload = "";

            if (line.StartsWith("@@UPDATE_RESULT "))
                payload = line.Substring(16);
            else if (line.StartsWith("@@INIT_RESULT "))
                payload = line.Substring(14);
            else if (line.StartsWith("@@CUSTOM_RESULT "))
                payload = line.Substring(16);

            lock (instance.ValueLock)
            {
                instance.LastStringValue = payload;
                instance.HasStringValue = !string.IsNullOrEmpty(payload);

                if (!string.IsNullOrEmpty(payload) &&
                    double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    instance.LastValue = v;
                }
            }
        }

        internal static (double?, string) RunNodeSynchronous(PluginInstanceData instance, string mode = "init", string customCall = "")
        {
            return RunWithPersistentProcess(instance, mode, customCall);
        }

        private static (double?, string) RunWithPersistentProcess(PluginInstanceData instance, string mode, string customCall)
        {
            var persistentProc = GetOrCreatePersistentProcess(instance);
            if (persistentProc == null)
            {
                Common.LogError(instance, "Failed to create persistent Node.js process.");
                return (null, "");
            }

            try
            {
                string command = mode;
                if (!string.IsNullOrEmpty(customCall))
                    command += $" {customCall}";

                lock (instance.ValueLock)
                {
                    instance.LastStringValue = "";
                    instance.HasStringValue = false;
                }

                NodeProcessHelper.SendToNode(persistentProc, command);

                int timeout = 0;
                const int maxTimeout = 50;

                while (timeout < maxTimeout)
                {
                    Thread.Sleep(10);
                    timeout++;

                    lock (instance.ValueLock)
                    {
                        if (instance.HasStringValue)
                        {
                            double? numResult = null;
                            if (!string.IsNullOrEmpty(instance.LastStringValue) &&
                                double.TryParse(instance.LastStringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                            {
                                numResult = v;
                            }

                            return (numResult, instance.LastStringValue);
                        }
                    }
                }

                lock (instance.ValueLock)
                {
                    double? numResult = null;
                    if (!string.IsNullOrEmpty(instance.LastStringValue) &&
                        double.TryParse(instance.LastStringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                    {
                        numResult = v;
                    }

                    return (numResult, instance.LastStringValue);
                }
            }
            catch (Exception ex)
            {
                Common.LogError(instance, $"Persistent process execution failed: {Common.GetSimpleErrorMessage(ex)}");
                CleanupPersistentProcess(instance);
                return (null, "");
            }
        }

        internal static void RunNodeAsync(PluginInstanceData instance)
        {
            var persistentProc = GetOrCreatePersistentProcess(instance);
            if (persistentProc != null && !persistentProc.HasExited)
            {
                try
                {
                    NodeProcessHelper.SendToNode(persistentProc, "update");
                    return;
                }
                catch
                {
                    // Fall back to original async method
                }
            }

            RunNodeAsyncOriginal(instance);
        }

        private static void RunNodeAsyncOriginal(PluginInstanceData instance)
        {
            lock (_asyncLock)
            {
                if (_asyncRunning)
                {
                    return;
                }
                _asyncRunning = true;
            }

            string currentWrapperPath = instance.WrapperPath;
            string currentScriptFile = instance.ScriptFile;
            IntPtr currentRmHandle = instance.RmHandle;

            _asyncCancellationTokenSource?.Cancel();
            _asyncCancellationTokenSource?.Dispose();
            _asyncCancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _asyncCancellationTokenSource.Token;

            Task.Run(async () =>
            {
                try
                {
                    if (token.IsCancellationRequested || !NodeProcessHelper.ValidateWrapper(instance))
                        return;

                    string tempExecutionPath = NodeProcessHelper.CreateTempWrapper(currentWrapperPath);
                    if (string.IsNullOrEmpty(tempExecutionPath))
                        return;

                    token.ThrowIfCancellationRequested();
                    await ExecuteAsyncNodeProcessAsync(tempExecutionPath, currentScriptFile, currentRmHandle, instance, token);

                    NodeProcessHelper.CleanupTempFile(tempExecutionPath);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        API.Log(currentRmHandle, API.LogType.Error, $"Async execution failed: {Common.GetSimpleErrorMessage(ex)}");
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

        private static ProcessStartInfo CreateProcessStartInfo(PluginInstanceData instance, string mode, string customCall)
        {
            string arguments = $"\"{instance.WrapperPath}\" {mode}";
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
                WorkingDirectory = NodeProcessHelper.GetWorkingDirectory(instance)
            };

            return psi;
        }

        private static async Task ExecuteAsyncNodeProcessAsync(string tempPath, string scriptFile, IntPtr rmHandle, PluginInstanceData instance, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{tempPath}\" update",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
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

                lock (_asyncLock)
                {
                    _currentAsyncProcess = proc;
                }

                SetupAsyncEventHandlersWithTwoWay(proc, rmHandle, instance);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (!proc.HasExited && !token.IsCancellationRequested)
                {
                    await Task.Delay(50, token);
                }

                if (!proc.HasExited && !token.IsCancellationRequested)
                {
                    try
                    {
                        proc.Kill();
                        await Task.Delay(100, CancellationToken.None);
                    }
                    catch { }
                }

                if (proc.ExitCode != 0 && !token.IsCancellationRequested)
                    API.Log(rmHandle, API.LogType.Error, $"Async Node.js process exited with code {proc.ExitCode}.");
            }
            catch (OperationCanceledException)
            {
                if (proc != null && !proc.HasExited)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(1000);
                    }
                    catch { }
                }
                throw;
            }
            catch (Exception ex)
            {
                API.Log(rmHandle, API.LogType.Error, $"Async process execution failed: {Common.GetSimpleErrorMessage(ex)}");
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

        private static void SetupAsyncEventHandlersWithTwoWay(Process proc, IntPtr rmHandle, PluginInstanceData instance)
        {
            var api = new API(rmHandle);

            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                try
                {
                    ProcessAsyncOutputWithTwoWay(e.Data.Trim(), rmHandle, api, proc, instance);
                }
                catch (Exception ex)
                {
                    API.Log(rmHandle, API.LogType.Error, $"Output handler error: {Common.GetSimpleErrorMessage(ex)}");
                }
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    string error = e.Data.Trim();
                    API.Log(rmHandle, API.LogType.Error,
                        error.StartsWith("@@LOG_ERROR ") ? error.Substring(12) : error);
                }
            };
        }

        private static void ProcessAsyncOutputWithTwoWay(string line, IntPtr rmHandle, API api, Process proc, PluginInstanceData instance)
        {
            if (NodeProcessHelper.ProcessLogMessage(line, instance)) return;

            if (line.StartsWith("@@RM_EXECUTE "))
            {
                api.Execute(line.Substring(13));
                return;
            }

            if (RainmeterCommands.ProcessRainmeterCommand(line, api, proc,instance)) return;

            if (line.StartsWith("@@UPDATE_RESULT "))
            {
                UpdateAsyncResult(line.Substring(16), instance);
                return;
            }

            if (!string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("@@") &&
                line.Trim() != "")
            {
                API.Log(rmHandle, API.LogType.Notice, line);
            }
        }

        private static void UpdateAsyncResult(string payload, PluginInstanceData instance)
        {
            lock (instance.ValueLock)
            {
                instance.LastStringValue = payload;
                instance.HasStringValue = !string.IsNullOrEmpty(payload);

                if (!string.IsNullOrEmpty(payload) &&
                    double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    instance.LastValue = v;
                }
            }
        }
    }
}