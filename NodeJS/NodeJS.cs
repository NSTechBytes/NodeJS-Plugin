using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Rainmeter;

namespace NodeJSPlugin
{
    public static class Plugin
    {
        private static string _scriptFile = "";
        private static string _wrapperPath = "";
        private static IntPtr _rmHandle = IntPtr.Zero;
        private static double _lastValue = 0.0;
        private static readonly object _valueLock = new object();

        // Create or recreate the temporary wrapper JS which overrides console and calls your script.
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

  try {{
    const script = require('{escapedScriptPath}');
    async function runInit() {{
      try {{
        if (typeof script.initialize === 'function') {{
          const res = await script.initialize();
          process.stdout.write('@@INIT_RESULT ' + (res === undefined ? '' : String(res)) + '\n');
        }} else {{
          process.stdout.write('@@INIT_RESULT \n');
        }}
      }} catch (e) {{
        process.stderr.write('@@LOG_ERROR ' + (e && e.stack ? e.stack : e) + '\n');
      }}
    }}

    async function runUpdate() {{
      try {{
        if (typeof script.update === 'function') {{
          const res = await script.update();
          process.stdout.write('@@UPDATE_RESULT ' + (res === undefined ? '' : String(res)) + '\n');
        }} else {{
          process.stdout.write('@@UPDATE_RESULT \n');
        }}
      }} catch (e) {{
        process.stderr.write('@@LOG_ERROR ' + (e && e.stack ? e.stack : e) + '\n');
      }}
    }}

    const mode = process.argv[2] || 'update';
    if (mode === 'init') runInit(); else runUpdate();
  }} catch (e) {{
    process.stderr.write('@@LOG_ERROR ' + (e && e.stack ? e.stack : e) + '\n');
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

        // Run the wrapper with the given mode ("init" or "update") asynchronously
        private static void RunNodeAsync(string mode)
        {
            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_wrapperPath) || !File.Exists(_wrapperPath))
                    {
                        if (_rmHandle != IntPtr.Zero)
                            API.Log(_rmHandle, API.LogType.Error, "Wrapper JS not found.");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{_wrapperPath}\" {mode}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_scriptFile)) ?? Environment.CurrentDirectory
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        if (_rmHandle != IntPtr.Zero)
                            API.Log(_rmHandle, API.LogType.Error, "Failed to start Node.js process.");
                        return;
                    }

                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        try
                        {
                            string line = e.Data.Trim();
                            if (line.StartsWith("@@LOG_NOTICE "))
                            {
                                API.Log(_rmHandle, API.LogType.Notice, line.Substring(13));
                            }
                            else if (line.StartsWith("@@LOG_WARNING "))
                            {
                                API.Log(_rmHandle, API.LogType.Warning, line.Substring(14));
                            }
                            else if (line.StartsWith("@@LOG_DEBUG "))
                            {
                                API.Log(_rmHandle, API.LogType.Debug, line.Substring(12));
                            }
                            else if (line.StartsWith("@@LOG_ERROR "))
                            {
                                API.Log(_rmHandle, API.LogType.Error, line.Substring(12));
                            }
                            else if (line.StartsWith("@@INIT_RESULT "))
                            {
                                string payload = line.Substring(14).Trim();
                                if (!string.IsNullOrEmpty(payload))
                                {
                                    string cleaned = payload.Trim();
                                    if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                                    {
                                        lock (_valueLock) { _lastValue = v; }
                                    }
                                    else
                                    {
                                        API.Log(_rmHandle, API.LogType.Notice, "initialize() result (non-numeric): " + payload);
                                    }
                                }
                            }
                            else if (line.StartsWith("@@UPDATE_RESULT "))
                            {
                                string payload = line.Substring(16).Trim();
                                if (!string.IsNullOrEmpty(payload))
                                {
                                    string cleaned = payload.Trim();
                                    if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                                    {
                                        lock (_valueLock) { _lastValue = v; }
                                    }
                                    else
                                    {
                                        API.Log(_rmHandle, API.LogType.Notice, "update() result (non-numeric): " + payload);
                                    }
                                }
                            }
                            else
                            {
                                API.Log(_rmHandle, API.LogType.Notice, line);
                            }
                        }
                        catch (Exception ex)
                        {
                            API.Log(_rmHandle, API.LogType.Error, "Output handler exception: " + ex.Message);
                        }
                    };

                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        string line = e.Data.Trim();
                        if (line.StartsWith("@@LOG_ERROR "))
                            API.Log(_rmHandle, API.LogType.Error, line.Substring(12));
                        else
                            API.Log(_rmHandle, API.LogType.Error, line);
                    };

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    proc.WaitForExit();
                }
                catch (Exception ex)
                {
                    if (_rmHandle != IntPtr.Zero)
                        API.Log(_rmHandle, API.LogType.Error, "RunNodeAsync exception: " + ex.Message);
                }
            });
        }

        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            _rmHandle = rm;
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            try
            {
                if (!string.IsNullOrEmpty(_wrapperPath) && File.Exists(_wrapperPath))
                {
                    File.Delete(_wrapperPath);
                    _wrapperPath = "";
                }
            }
            catch { }
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            _rmHandle = rm;
            var api = new API(rm);
            _scriptFile = api.ReadPath("ScriptFile", "").Trim();

            if (string.IsNullOrWhiteSpace(_scriptFile))
            {
                API.Log(_rmHandle, API.LogType.Error, "ScriptFile not set for NodeJS measure.");
                return;
            }

            CreateWrapper();
            RunNodeAsync("init");
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            if (!string.IsNullOrWhiteSpace(_scriptFile))
            {
                RunNodeAsync("update");
            }

            lock (_valueLock)
            {
                return _lastValue;
            }
        }
    }
}
