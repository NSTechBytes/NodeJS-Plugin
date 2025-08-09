using System;
using System.IO;
using Rainmeter;

namespace NodeJSPlugin
{
    internal static class Wrapper
    {
        internal static void CreateWrapper()
        {
            try
            {
                string scriptContent = GetScriptContent();
                if (string.IsNullOrEmpty(scriptContent)) return;

                string wrapperCode = GenerateWrapperCode(scriptContent);
                string tempPath = CreateTempWrapperFile(wrapperCode);
                
                CleanupOldWrapper();
                Plugin._wrapperPath = tempPath;
            }
            catch (Exception ex)
            {
                LogError($"Wrapper creation failed: {GetSimpleErrorMessage(ex)}");
            }
        }

        private static string GetScriptContent()
        {
            if (!string.IsNullOrWhiteSpace(Plugin._inlineScript))
            {
                return GenerateInlineScriptModule(Plugin._inlineScript);
            }
            
            if (!string.IsNullOrWhiteSpace(Plugin._scriptFile))
            {
                string scriptFull = Path.GetFullPath(Plugin._scriptFile);
                string escapedScriptPath = scriptFull.Replace("\\", "\\\\").Replace("'", "\\'");
                return $"require('{escapedScriptPath}')";
            }
            
            return null;
        }

        private static string GenerateInlineScriptModule(string inlineScript)
        {
            return $@"
  let scriptModule = null;
  try {{
    // Create a proper module context with require support
    const Module = require('module');
    const path = require('path');
    
    // Set up module context
    const moduleObj = new Module();
    moduleObj.filename = __filename;
    moduleObj.paths = Module._nodeModulePaths(process.cwd());
    
    // Create script function with proper context
    const scriptFunction = new Function(
      'module', 'exports', 'require', '__filename', '__dirname', 'RM', 'global', 'process', 'console',
      `
      {inlineScript}
      
      if (typeof initialize !== 'undefined') module.exports.initialize = initialize;
      if (typeof update !== 'undefined') module.exports.update = update;
      
      for (let key in this) {{
        if (typeof this[key] === 'function' && key !== 'initialize' && key !== 'update') {{
          module.exports[key] = this[key];
        }}
      }}
      `
    );
    
    // Execute with full Node.js context
    scriptFunction.call(
      {{}}, 
      moduleObj, 
      moduleObj.exports, 
      moduleObj.require.bind(moduleObj),
      moduleObj.filename,
      path.dirname(moduleObj.filename),
      RM,
      global,
      process,
      console
    );
    
    scriptModule = moduleObj.exports;
  }} catch (e) {{
    console.error('Script compilation error:', e.message);
    scriptModule = {{}};
  }}";
        }

        private static string GenerateWrapperCode(string scriptContent)
        {
            bool isInlineScript = !string.IsNullOrWhiteSpace(Plugin._inlineScript);
            
            string scriptModuleCode = isInlineScript ? 
                scriptContent : 
                $@"
  let scriptModule = null;
  try {{
    scriptModule = {scriptContent};
  }} catch (e) {{
    console.error('Script loading error:', e.message);
    scriptModule = {{}};
  }}";

            return $@"
(function(){{
  // Console overrides for clean logging
  console.log = (...args) => process.stdout.write('@@LOG_NOTICE ' + args.join(' ') + '\n');
  console.warn = (...args) => process.stdout.write('@@LOG_WARNING ' + args.join(' ') + '\n');
  console.debug = (...args) => process.stdout.write('@@LOG_DEBUG ' + args.join(' ') + '\n');
  console.error = (...args) => process.stderr.write('@@LOG_ERROR ' + args.join(' ') + '\n');

{GetRainmeterContext()}

{scriptModuleCode}

{GetExecutionHandlers()}
}})();
";
        }

        private static string GetRainmeterContext()
        {
            return @"
  const fs = require('fs');
  
  function readFromHost() {
    const BUFSIZE = 4096; 
    let buf = Buffer.alloc(BUFSIZE);
    let bytesRead = 0;
    try {
      bytesRead = fs.readSync(process.stdin.fd, buf, 0, BUFSIZE, null);
    } catch (e) {
      return '';
    }
    return bytesRead === 0 ? '' : buf.toString('utf8', 0, bytesRead).trim();
  }

  function rmRequest(command, ...args) {
    process.stdout.write(command + (args.length ? ' ' + args.join('|') : '') + '\n');
    return readFromHost();
  }

  global.RM = {
    Execute: (command) => process.stdout.write('@@RM_EXECUTE ' + command + '\n'),
    GetVariable: (name, defaultValue = '') => rmRequest('@@RM_GETVARIABLE', name, defaultValue),
    ReadString: (option, defValue = '') => rmRequest('@@RM_READSTRING', option, defValue),
    ReadStringFromSection: (section, option, defValue = '') => rmRequest('@@RM_READSTRINGFROMSECTION', section, option, defValue),
    ReadDouble: (option, defValue = 0.0) => parseFloat(rmRequest('@@RM_READDOUBLE', option, defValue)),
    ReadDoubleFromSection: (section, option, defValue = 0.0) => parseFloat(rmRequest('@@RM_READDOUBLEFROMSECTION', section, option, defValue)),
    ReadInt: (option, defValue = 0) => parseInt(rmRequest('@@RM_READINT', option, defValue), 10),
    ReadIntFromSection: (section, option, defValue = 0) => parseInt(rmRequest('@@RM_READINTFROMSECTION', section, option, defValue), 10),
    GetMeasureName: () => rmRequest('@@RM_GETMEASURENAME'),
    GetSkinName: () => rmRequest('@@RM_GETSKINNAME'),
    GetSkin: () => rmRequest('@@RM_GETSKIN'),
    GetSkinWindow: () => rmRequest('@@RM_GETSKINWINDOW')
  };

  const RM = global.RM;";
        }

        private static string GetExecutionHandlers()
        {
            return @"
  async function executeFunction(functionName, ...args) {
    try {
      if (!scriptModule || typeof scriptModule[functionName] !== 'function') {
        return '';
      }
      const result = await Promise.resolve(scriptModule[functionName](...args));
      return result === undefined ? '' : String(result);
    } catch (e) {
      console.error(`${functionName} error:`, e.message);
      return '';
    }
  }

  async function runInit() {
    const result = await executeFunction('initialize');
    process.stdout.write('@@INIT_RESULT ' + result + '\n');
  }

  async function runUpdate() {
    const result = await executeFunction('update');
    process.stdout.write('@@UPDATE_RESULT ' + result + '\n');
  }

  async function runCustom(functionCall) {
    try {
      if (!scriptModule) {
        console.error('Script module not loaded');
        return;
      }
      
      const result = eval('scriptModule.' + functionCall);
      const resolvedResult = await Promise.resolve(result);
      process.stdout.write('@@CUSTOM_RESULT ' + (resolvedResult === undefined ? '' : String(resolvedResult)) + '\n');
    } catch (e) {
      console.error('Custom function error:', e.message);
      process.stdout.write('@@CUSTOM_RESULT \n');
    }
  }

  // Main execution logic
  const mode = process.argv[2] || 'update';
  const customCall = process.argv[3] || '';

  if (mode === 'init') {
    runInit();
  } else if (mode === 'custom' && customCall) {
    runCustom(customCall);
  } else {
    runUpdate();
  }";
        }

        private static string CreateTempWrapperFile(string wrapperCode)
        {
            string tempDir = Path.GetTempPath();
            string fileName = "RainNodeWrapper_" + Guid.NewGuid().ToString("N") + ".js";
            string filePath = Path.Combine(tempDir, fileName);
            
            File.WriteAllText(filePath, wrapperCode);
            return filePath;
        }

        private static void CleanupOldWrapper()
        {
            try
            {
                if (!string.IsNullOrEmpty(Plugin._wrapperPath) && File.Exists(Plugin._wrapperPath))
                    File.Delete(Plugin._wrapperPath);
            }
            catch { }
        }

        private static void LogError(string message)
        {
            if (Plugin._rmHandle != IntPtr.Zero)
                API.Log(Plugin._rmHandle, API.LogType.Error, message);
        }

        private static string GetSimpleErrorMessage(Exception ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }
}