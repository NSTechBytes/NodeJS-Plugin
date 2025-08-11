using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rainmeter;

namespace NodeJSPlugin
{
    internal class Common
    {
        public static void LogError(PluginInstanceData instance, string message)
        {
            if (instance.RmHandle != IntPtr.Zero)
                API.Log(instance.RmHandle, API.LogType.Error, message);
        }

        public static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }
}
