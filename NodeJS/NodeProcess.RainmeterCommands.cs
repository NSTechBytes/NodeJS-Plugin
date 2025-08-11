using System;
using System.Diagnostics;
using System.Globalization;
using Rainmeter;

namespace NodeJSPlugin
{
    internal class RainmeterCommands
    {
        public static bool ProcessRainmeterCommand(string trimmed, API api, Process proc, PluginInstanceData instance)
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
                    NodeProcessHelper.SendToNode(proc, result);
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READSTRINGFROMSECTION "))
                {
                    string[] parts = trimmed.Substring(27).Split(new[] { '|' }, 3);
                    if (parts.Length >= 3)
                    {
                        string result = api.ReadStringFromSection(parts[0], parts[1], parts[2]);
                        NodeProcessHelper.SendToNode(proc, result);
                    }
                    else
                    {
                        NodeProcessHelper.SendToNode(proc, "");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READSTRING "))
                {
                    string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    string defaultValue = parts.Length > 1 ? parts[1] : "";

                    string result = api.ReadString(option, defaultValue);
                    NodeProcessHelper.SendToNode(proc, result);
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
                        NodeProcessHelper.SendToNode(proc, result.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        NodeProcessHelper.SendToNode(proc, "0");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READDOUBLE "))
                {
                    string[] parts = trimmed.Substring(16).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    double defaultValue = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double defVal) ? defVal : 0.0;

                    double result = api.ReadDouble(option, defaultValue);
                    NodeProcessHelper.SendToNode(proc, result.ToString(CultureInfo.InvariantCulture));
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
                        NodeProcessHelper.SendToNode(proc, result.ToString());
                    }
                    else
                    {
                        NodeProcessHelper.SendToNode(proc, "0");
                    }
                    return true;
                }

                if (trimmed.StartsWith("@@RM_READINT "))
                {
                    string[] parts = trimmed.Substring(13).Split(new[] { '|' }, 2);
                    string option = parts.Length > 0 ? parts[0] : "";
                    int defaultValue = parts.Length > 1 && int.TryParse(parts[1], out int defVal) ? defVal : 0;

                    int result = api.ReadInt(option, defaultValue);
                    NodeProcessHelper.SendToNode(proc, result.ToString());
                    return true;
                }

                // Simple commands without parameters
                if (trimmed.StartsWith("@@RM_GETMEASURENAME"))
                {
                    NodeProcessHelper.SendToNode(proc, api.GetMeasureName());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKINNAME"))
                {
                    NodeProcessHelper.SendToNode(proc, api.GetSkinName());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKIN"))
                {
                    NodeProcessHelper.SendToNode(proc, api.GetSkin().ToString());
                    return true;
                }

                if (trimmed.StartsWith("@@RM_GETSKINWINDOW"))
                {
                    NodeProcessHelper.SendToNode(proc, api.GetSkinWindow().ToString());
                    return true;
                }
            }
            catch (Exception ex)
            {
                Common.LogError(instance,$"RM command failed: {Common.GetSimpleErrorMessage(ex)}");
                NodeProcessHelper.SendToNode(proc, "");
                return true;
            }

            return false;
        }
    }
}
