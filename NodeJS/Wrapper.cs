using System;
using System.IO;
using Rainmeter;

namespace NodeJSPlugin
{
    internal static class Wrapper
    {
        internal static void CreateWrapper(PluginInstanceData instance)
        {
            try
            {
                string scriptContent = WrapperHelper.GetScriptContent(instance);
                if (string.IsNullOrEmpty(scriptContent)) return;

                string wrapperCode = GenerateWrapperCode(scriptContent, instance);
                string tempPath = WrapperHelper.CreateTempWrapperFile(wrapperCode);

                WrapperHelper.CleanupOldWrapper(instance);
                instance.WrapperPath = tempPath;
            }
            catch (Exception ex)
            {
                WrapperHelper.LogError(instance, $"Wrapper creation failed: {WrapperHelper.GetSimpleErrorMessage(ex)}");
            }
        }

        public static string GenerateInlineScriptModule(string inlineScript)
        {
            return $@"
  // Initialize script module only once to preserve state
  if (typeof global.persistentScriptModule === 'undefined') {{
    global.persistentScriptModule = null;
    
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
      
      global.persistentScriptModule = moduleObj.exports;
    }} catch (e) {{
      console.error('Script compilation error:', e.message);
      global.persistentScriptModule = {{}};
    }}
  }}
  
  scriptModule = global.persistentScriptModule;";
        }

        private static string GenerateWrapperCode(string scriptContent, PluginInstanceData instance)
        {
            bool isInlineScript = !string.IsNullOrWhiteSpace(instance.InlineScript);

            string scriptModuleCode = isInlineScript ?
                scriptContent :
                $@"
  // Load script module only once to preserve state
  if (typeof global.persistentScriptModule === 'undefined') {{
    global.persistentScriptModule = null;
    try {{
      global.persistentScriptModule = {scriptContent};
    }} catch (e) {{
      console.error('Script loading error:', e.message);
      global.persistentScriptModule = {{}};
    }}
  }}
  
  let scriptModule = global.persistentScriptModule;";

            return $@"
(function(){{
  // Console overrides for clean logging
  console.log = (...args) => process.stdout.write('@@LOG_NOTICE ' + args.join(' ') + '\n');
  console.warn = (...args) => process.stdout.write('@@LOG_WARNING ' + args.join(' ') + '\n');
  console.debug = (...args) => process.stdout.write('@@LOG_DEBUG ' + args.join(' ') + '\n');
  console.error = (...args) => process.stderr.write('@@LOG_ERROR ' + args.join(' ') + '\n');

{GetRainmeterContextWithTwoWay()}

{scriptModuleCode}

{GetExecutionHandlers()}
}})();
";
        }

        private static string GetRainmeterContextWithTwoWay()
        {
            return @"
  const fs = require('fs');
  
  // Enhanced read function with timeout and retry for async operations
  function readFromHost(timeoutMs = 5000) {
    const BUFSIZE = 4096; 
    let buf = Buffer.alloc(BUFSIZE);
    let bytesRead = 0;
    let attempts = 0;
    const maxAttempts = Math.max(1, Math.floor(timeoutMs / 10));
    
    while (attempts < maxAttempts) {
      try {
        bytesRead = fs.readSync(process.stdin.fd, buf, 0, BUFSIZE, null);
        if (bytesRead > 0) {
          return buf.toString('utf8', 0, bytesRead).trim();
        }
      } catch (e) {
        // If no data is available, wait a bit and retry
        if (e.code === 'EAGAIN' || e.code === 'EWOULDBLOCK') {
          attempts++;
          // Use busy waiting for short intervals to maintain responsiveness
          const start = Date.now();
          while (Date.now() - start < 10) {
            // Busy wait for 10ms
          }
          continue;
        }
        break;
      }
      attempts++;
    }
    return '';
  }

  function rmRequest(command, ...args) {
    try {
      process.stdout.write(command + (args.length ? ' ' + args.join('|') : '') + '\n');
      return readFromHost();
    } catch (e) {
      console.error('RM request failed:', e.message);
      return '';
    }
  }

  // Enhanced RM object with better async support
  global.RM = {
    Execute: (command) => {
      try {
        process.stdout.write('@@RM_EXECUTE ' + command + '\n');
      } catch (e) {
        console.error('RM.Execute failed:', e.message);
      }
    },
    
    GetVariable: (name, defaultValue = '') => {
      try {
        return rmRequest('@@RM_GETVARIABLE', name, defaultValue);
      } catch (e) {
        console.error('RM.GetVariable failed:', e.message);
        return defaultValue;
      }
    },
    
    ReadString: (option, defValue = '') => {
      try {
        return rmRequest('@@RM_READSTRING', option, defValue);
      } catch (e) {
        console.error('RM.ReadString failed:', e.message);
        return defValue;
      }
    },
    
    ReadStringFromSection: (section, option, defValue = '') => {
      try {
        return rmRequest('@@RM_READSTRINGFROMSECTION', section, option, defValue);
      } catch (e) {
        console.error('RM.ReadStringFromSection failed:', e.message);
        return defValue;
      }
    },
    
    ReadDouble: (option, defValue = 0.0) => {
      try {
        const result = rmRequest('@@RM_READDOUBLE', option, defValue);
        const parsed = parseFloat(result);
        return isNaN(parsed) ? defValue : parsed;
      } catch (e) {
        console.error('RM.ReadDouble failed:', e.message);
        return defValue;
      }
    },
    
    ReadDoubleFromSection: (section, option, defValue = 0.0) => {
      try {
        const result = rmRequest('@@RM_READDOUBLEFROMSECTION', section, option, defValue);
        const parsed = parseFloat(result);
        return isNaN(parsed) ? defValue : parsed;
      } catch (e) {
        console.error('RM.ReadDoubleFromSection failed:', e.message);
        return defValue;
      }
    },
    
    ReadInt: (option, defValue = 0) => {
      try {
        const result = rmRequest('@@RM_READINT', option, defValue);
        const parsed = parseInt(result, 10);
        return isNaN(parsed) ? defValue : parsed;
      } catch (e) {
        console.error('RM.ReadInt failed:', e.message);
        return defValue;
      }
    },
    
    ReadIntFromSection: (section, option, defValue = 0) => {
      try {
        const result = rmRequest('@@RM_READINTFROMSECTION', section, option, defValue);
        const parsed = parseInt(result, 10);
        return isNaN(parsed) ? defValue : parsed;
      } catch (e) {
        console.error('RM.ReadIntFromSection failed:', e.message);
        return defValue;
      }
    },
    
    GetMeasureName: () => {
      try {
        return rmRequest('@@RM_GETMEASURENAME');
      } catch (e) {
        console.error('RM.GetMeasureName failed:', e.message);
        return '';
      }
    },
    
    GetSkinName: () => {
      try {
        return rmRequest('@@RM_GETSKINNAME');
      } catch (e) {
        console.error('RM.GetSkinName failed:', e.message);
        return '';
      }
    },
    
    GetSkin: () => {
      try {
        return rmRequest('@@RM_GETSKIN');
      } catch (e) {
        console.error('RM.GetSkin failed:', e.message);
        return '';
      }
    },
    
    GetSkinWindow: () => {
      try {
        return rmRequest('@@RM_GETSKINWINDOW');
      } catch (e) {
        console.error('RM.GetSkinWindow failed:', e.message);
        return '';
      }
    }
  };

  const RM = global.RM;";
        }

        private static string GetExecutionHandlers()
        {
            return @"
  let lastResult = '';
  let isInitialized = false; // Track initialization state

  async function executeFunction(functionName, ...args) {
    try {
      if (!scriptModule || typeof scriptModule[functionName] !== 'function') {
        // If function doesn't exist, return the last known result
        return lastResult;
      }
      const result = await Promise.resolve(scriptModule[functionName](...args));
      const resultStr = result === undefined ? '' : String(result);
      lastResult = resultStr; // Store the result for future reference
      return resultStr;
    } catch (e) {
      console.error(`${functionName} error:`, e.message);
      return lastResult; // Return last known good result on error
    }
  }

  async function runInit() {
    // Prevent double initialization
    if (isInitialized) {
      process.stdout.write('@@INIT_RESULT ' + lastResult + '\n');
      return;
    }
    
    const result = await executeFunction('initialize');
    isInitialized = true;
    process.stdout.write('@@INIT_RESULT ' + result + '\n');
  }

  async function runUpdate() {
    // For update, if no update function exists, return the last result
    // This handles cases where scripts only have specific functions like increment/decrement
    let result;
    if (scriptModule && typeof scriptModule['update'] === 'function') {
      result = await executeFunction('update');
    } else {
      // No update function, return last result or empty string
      result = lastResult || '';
    }
    process.stdout.write('@@UPDATE_RESULT ' + result + '\n');
  }

  async function runCustom(functionCall) {
    try {
      if (!scriptModule) {
        console.error('Script module not loaded');
        process.stdout.write('@@CUSTOM_RESULT ' + lastResult + '\n');
        return;
      }
      
      // Parse function call - handle both 'functionName()' and 'functionName'
      let functionName = functionCall.trim();
      let args = [];
      
      // Simple parsing for function calls
      const parenIndex = functionName.indexOf('(');
      if (parenIndex > -1) {
        const endParenIndex = functionName.lastIndexOf(')');
        if (endParenIndex > parenIndex) {
          const argsStr = functionName.substring(parenIndex + 1, endParenIndex).trim();
          functionName = functionName.substring(0, parenIndex).trim();
          
          // Parse arguments if any (basic parsing for simple cases)
          if (argsStr) {
            try {
              args = argsStr.split(',').map(arg => {
                arg = arg.trim();
                // Try to parse as number
                const num = parseFloat(arg);
                if (!isNaN(num)) return num;
                // Remove quotes if present
                if ((arg.startsWith('""') && arg.endsWith('""')) || 
                    (arg.startsWith(""'"") && arg.endsWith(""'""))) {
                  return arg.slice(1, -1);
                }
                return arg;
              });
            } catch (e) {
              console.error('Error parsing arguments:', e.message);
            }
          }
        }
      }
      
      // Execute the function
      if (typeof scriptModule[functionName] === 'function') {
        const result = await Promise.resolve(scriptModule[functionName](...args));
        const resultStr = result === undefined ? '' : String(result);
        lastResult = resultStr; // Update last result
        process.stdout.write('@@CUSTOM_RESULT ' + resultStr + '\n');
      } else {
        // Try eval as fallback for more complex expressions
        try {
          const result = eval('scriptModule.' + functionCall);
          const resolvedResult = await Promise.resolve(result);
          const resultStr = resolvedResult === undefined ? '' : String(resolvedResult);
          lastResult = resultStr;
          process.stdout.write('@@CUSTOM_RESULT ' + resultStr + '\n');
        } catch (evalError) {
          console.error('Function not found and eval failed:', evalError.message);
          process.stdout.write('@@CUSTOM_RESULT ' + lastResult + '\n');
        }
      }
    } catch (e) {
      console.error('Custom function error:', e.message);
      process.stdout.write('@@CUSTOM_RESULT ' + lastResult + '\n');
    }
  }

  // Main execution logic
  const mode = process.argv[2] || 'update';
  const customCall = process.argv[3] || '';

  if (mode === 'persistent') {
    // Persistent mode - keep process alive and respond to commands via stdin
    // DON'T auto-call runInit() here - let the C# code control initialization
    
    process.stdin.setEncoding('utf8');
    process.stdin.on('readable', async () => {
      let chunk;
      while (null !== (chunk = process.stdin.read())) {
        const lines = chunk.trim().split('\n');
        for (const line of lines) {
          const command = line.trim();
          if (!command) continue;
          
          if (command === 'init') {
            await runInit();
          } else if (command === 'update') {
            await runUpdate();
          } else if (command.startsWith('custom ')) {
            const customCallStr = command.substring(7);
            await runCustom(customCallStr);
          }
        }
      }
    });

    process.stdin.on('end', () => {
      process.exit(0);
    });
    
    // Keep process alive
    process.stdin.resume();
  } else if (mode === 'init') {
    runInit();
  } else if (mode === 'custom' && customCall) {
    runCustom(customCall);
  } else {
    runUpdate();
  }";
        }
    }
}