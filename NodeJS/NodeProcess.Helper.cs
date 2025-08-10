using System;
using System.Diagnostics;
using System.IO;
using Rainmeter;

namespace NodeJSPlugin
{
    internal class NodeProcessHelper
    {
        public static void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        public static void SendToNode(Process proc, string message)
        {
            try
            {
                proc.StandardInput.WriteLine(message);
                proc.StandardInput.Flush();
            }
            catch { }
        }

        public static void LogError(PluginInstanceData instance, string message)
        {
            API.Log(instance.RmHandle, API.LogType.Error, message);
        }

        public static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }

        public static string CreateTempWrapper(string currentWrapperPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(currentWrapperPath) || !File.Exists(currentWrapperPath))
                    return null;

                string tempPath = Path.GetTempFileName();
                File.Copy(currentWrapperPath, tempPath, true);
                return tempPath;
            }
            catch
            {
                return null;
            }
        }

        public static string GetWorkingDirectory(PluginInstanceData instance)
        {
            if (!string.IsNullOrEmpty(instance.ScriptFile))
            {
                return Path.GetDirectoryName(Path.GetFullPath(instance.ScriptFile)) ?? Environment.CurrentDirectory;
            }
            return Environment.CurrentDirectory;
        }

        public static bool ProcessLogMessage(string trimmed, PluginInstanceData instance)
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
                    API.Log(instance.RmHandle, logType, trimmed.Substring(offset));
                    return true;
                }
            }
            return false;
        }

        public static bool ValidateWrapper(PluginInstanceData instance)
        {
            if (string.IsNullOrWhiteSpace(instance.WrapperPath) || !File.Exists(instance.WrapperPath))
            {
                LogError(instance, "Wrapper file does not exist.");
                return false;
            }
            return true;
        }
    }
}