using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rainmeter;

namespace NodeJSPlugin
{
    internal class WrapperHelper
    {
        public static string CreateTempWrapperFile(string wrapperCode)
        {
            string tempDir = Path.GetTempPath();
            string fileName = "RainNodeWrapper_" + Guid.NewGuid().ToString("N") + ".js";
            string filePath = Path.Combine(tempDir, fileName);

            File.WriteAllText(filePath, wrapperCode);
            return filePath;
        }

        public static void CleanupOldWrapper(PluginInstanceData instance)
        {
            try
            {
                if (!string.IsNullOrEmpty(instance.WrapperPath) && File.Exists(instance.WrapperPath))
                    File.Delete(instance.WrapperPath);
            }
            catch { }
        }

        public static void LogError(PluginInstanceData instance, string message)
        {
            if (instance.RmHandle != IntPtr.Zero)
                API.Log(instance.RmHandle, API.LogType.Error, message);
        }

        public static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }

        public static string GetScriptContent(PluginInstanceData instance)
        {
            if (!string.IsNullOrWhiteSpace(instance.InlineScript))
            {
                return Wrapper.GenerateInlineScriptModule(instance.InlineScript);
            }

            if (!string.IsNullOrWhiteSpace(instance.ScriptFile))
            {
                string scriptFull = Path.GetFullPath(instance.ScriptFile);
                string escapedScriptPath = scriptFull.Replace("\\", "\\\\").Replace("'", "\\'");
                return $"require('{escapedScriptPath}')";
            }

            return null;
        }
    }
}