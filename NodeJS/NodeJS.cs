using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Rainmeter;

namespace NodeJSPlugin
{
    // Instance data class to hold per-measure data
    internal class PluginInstanceData
    {
        public string ScriptFile { get; set; } = "";
        public string InlineScript { get; set; } = "";
        public string WrapperPath { get; set; } = "";
        public double LastValue { get; set; } = 0.0;
        public string LastStringValue { get; set; } = "";
        public bool HasStringValue { get; set; } = false;
        public readonly object ValueLock = new object();
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public Plugin.PluginState State { get; set; } = Plugin.PluginState.Uninitialized;
        public readonly object StateLock = new object();
        public bool ForceReload { get; set; } = false;
        public IntPtr RmHandle { get; set; } = IntPtr.Zero;

        // Instance-specific process management
        public Process PersistentProcess { get; set; } = null;
        public readonly object PersistentProcessLock = new object();
        public bool PersistentProcessInitialized { get; set; } = false;
    }

    public static class Plugin
    {
        internal enum PluginState { Uninitialized, Initializing, Initialized }

        // Dictionary to store instance data per measure
        private static readonly Dictionary<IntPtr, PluginInstanceData> _instances = new Dictionary<IntPtr, PluginInstanceData>();
        private static readonly object _instancesLock = new object();

        // Helper method to get or create instance data
        private static PluginInstanceData GetInstanceData(IntPtr data)
        {
            lock (_instancesLock)
            {
                if (!_instances.TryGetValue(data, out PluginInstanceData instance))
                {
                    instance = new PluginInstanceData();
                    _instances[data] = instance;
                }
                return instance;
            }
        }

        // Helper method to remove instance data
        private static void RemoveInstanceData(IntPtr data)
        {
            lock (_instancesLock)
            {
                _instances.Remove(data);
            }
        }

        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            var instance = GetInstanceData(data);
            instance.RmHandle = rm;

            lock (instance.StateLock)
            {
                instance.CancellationTokenSource?.Cancel();
                instance.CancellationTokenSource?.Dispose();
                instance.CancellationTokenSource = new CancellationTokenSource();

                lock (instance.ValueLock)
                {
                    instance.LastValue = 0.0;
                    instance.LastStringValue = "";
                    instance.HasStringValue = false;
                }
            }
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            CleanupAndReset(data);
            RemoveInstanceData(data);
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            var instance = GetInstanceData(data);
            instance.RmHandle = rm;
            var api = new API(rm);
            string newScriptFile = api.ReadPath("ScriptFile", "").Trim();
            string newInlineScript = BuildInlineScript(api);

            bool configurationChanged = newScriptFile != instance.ScriptFile || newInlineScript != instance.InlineScript;
            bool needsNewWrapper = false;

            lock (instance.StateLock)
            {
                if (instance.State == PluginState.Initialized && !configurationChanged && !instance.ForceReload)
                {
                    return;
                }

                instance.ForceReload = false;

                if (configurationChanged || instance.ForceReload)
                {
                    if (instance.State == PluginState.Initialized || instance.State == PluginState.Initializing)
                    {
                        CleanupResources(instance);
                    }

                    instance.State = PluginState.Initializing;
                    instance.ScriptFile = newScriptFile;
                    instance.InlineScript = newInlineScript;

                    instance.CancellationTokenSource?.Cancel();
                    instance.CancellationTokenSource?.Dispose();
                    instance.CancellationTokenSource = new CancellationTokenSource();
                }

                needsNewWrapper = string.IsNullOrEmpty(instance.WrapperPath) || !File.Exists(instance.WrapperPath) || configurationChanged;
            }

            if (string.IsNullOrWhiteSpace(instance.ScriptFile) && string.IsNullOrWhiteSpace(instance.InlineScript))
            {
                LogError(instance, "Neither ScriptFile nor Line parameters are configured.");
                lock (instance.StateLock) { instance.State = PluginState.Uninitialized; }
                return;
            }

            try
            {
                if (needsNewWrapper)
                {
                    Wrapper.CreateWrapper(instance);
                }

                var (initialValue, initialStringValue) = NodeProcess.RunNodeSynchronous(instance, "init");

                lock (instance.StateLock)
                {
                    lock (instance.ValueLock)
                    {
                        if (initialValue.HasValue)
                            instance.LastValue = initialValue.Value;

                        instance.LastStringValue = initialStringValue ?? "";
                        instance.HasStringValue = !string.IsNullOrEmpty(instance.LastStringValue);
                    }

                    instance.State = PluginState.Initialized;
                }
            }
            catch (Exception ex)
            {
                LogError(instance, $"Script initialization failed: {GetSimpleErrorMessage(ex)}");
                lock (instance.StateLock) { instance.State = PluginState.Uninitialized; }
            }
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            var instance = GetInstanceData(data);

            if (instance.State == PluginState.Initialized &&
                (!string.IsNullOrWhiteSpace(instance.ScriptFile) || !string.IsNullOrWhiteSpace(instance.InlineScript)))
            {
                try
                {
                    var (updateValue, updateString) = NodeProcess.RunNodeSynchronous(instance, "update");

                    lock (instance.ValueLock)
                    {
                        if (updateValue.HasValue)
                            instance.LastValue = updateValue.Value;

                        if (!string.IsNullOrEmpty(updateString))
                        {
                            instance.LastStringValue = updateString;
                            instance.HasStringValue = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError(instance, $"Update execution failed: {GetSimpleErrorMessage(ex)}");
                }
            }

            lock (instance.ValueLock)
            {
                return instance.LastValue;
            }
        }

        [DllExport]
        public static void ExecuteBang(IntPtr data, [MarshalAs(UnmanagedType.LPWStr)] string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return;

            var instance = GetInstanceData(data);

            if (instance.State != PluginState.Initialized)
            {
                LogError(instance, "Cannot execute function: Plugin not initialized.");
                return;
            }

            try
            {
                string functionCall = args.Trim();

                if (!functionCall.Contains("("))
                {
                    functionCall += "()";
                }

                var (resultValue, resultString) = NodeProcess.RunNodeSynchronous(instance, "custom", functionCall);

                lock (instance.ValueLock)
                {
                    if (resultValue.HasValue)
                        instance.LastValue = resultValue.Value;

                    instance.LastStringValue = resultString ?? "";
                    instance.HasStringValue = !string.IsNullOrEmpty(instance.LastStringValue);
                }
            }
            catch (Exception ex)
            {
                LogError(instance, $"ExecuteBang failed: {GetSimpleErrorMessage(ex)}");
            }
        }

        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            var instance = GetInstanceData(data);
            lock (instance.ValueLock)
            {
                if (instance.HasStringValue && !string.IsNullOrEmpty(instance.LastStringValue))
                {
                    return Marshal.StringToHGlobalUni(instance.LastStringValue);
                }
            }
            return IntPtr.Zero;
        }

        [DllExport]
        public static IntPtr Call(IntPtr data, int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] argv)
        {
            if (argc == 0 || string.IsNullOrWhiteSpace(argv[0]))
                return IntPtr.Zero;

            var instance = GetInstanceData(data);

            if (instance.State != PluginState.Initialized)
            {
                LogError(instance, "Cannot execute call: Plugin not initialized.");
                return IntPtr.Zero;
            }

            try
            {
                string functionCall = argv[0].Trim();

                if (!functionCall.Contains("("))
                {
                    functionCall += "()";
                }

                var (_, resultString) = NodeProcess.RunNodeSynchronous(instance, "custom", functionCall);

                lock (instance.ValueLock)
                {
                    if (!string.IsNullOrEmpty(resultString))
                    {
                        instance.LastStringValue = resultString;
                        instance.HasStringValue = true;

                        if (double.TryParse(resultString, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double v))
                        {
                            instance.LastValue = v;
                        }
                    }
                }

                return resultString != null ? Marshal.StringToHGlobalUni(resultString) : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LogError(instance, $"Call '{argv[0]}' failed: {GetSimpleErrorMessage(ex)}");
                return IntPtr.Zero;
            }
        }

        private static void CleanupAndReset(IntPtr data)
        {
            var instance = GetInstanceData(data);
            lock (instance.StateLock)
            {
                instance.State = PluginState.Uninitialized;
                instance.ForceReload = true;

                try
                {
                    instance.CancellationTokenSource?.Cancel();
                    Thread.Sleep(100);
                }
                catch { }

                CleanupResources(instance);
                ResetState(instance);
            }
        }

        private static void CleanupResources(PluginInstanceData instance)
        {
            NodeProcess.CleanupAsyncOperations(instance);

            try
            {
                instance.CancellationTokenSource?.Cancel();
                instance.CancellationTokenSource?.Dispose();
                instance.CancellationTokenSource = null;
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(instance.WrapperPath) && File.Exists(instance.WrapperPath))
                {
                    File.Delete(instance.WrapperPath);
                    instance.WrapperPath = "";
                }
            }
            catch { }
        }

        private static void ResetState(PluginInstanceData instance)
        {
            instance.ScriptFile = "";
            instance.InlineScript = "";
            instance.WrapperPath = "";
            instance.State = PluginState.Uninitialized;

            lock (instance.ValueLock)
            {
                instance.LastValue = 0.0;
                instance.LastStringValue = "";
                instance.HasStringValue = false;
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

        private static void LogError(PluginInstanceData instance, string message)
        {
            if (instance.RmHandle != IntPtr.Zero)
                API.Log(instance.RmHandle, API.LogType.Error, message);
        }

        private static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }
}