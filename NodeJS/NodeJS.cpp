/*
  NodeJS Plugin for Rainmeter
  Allows execution of Node.js scripts from Rainmeter measures

  This program is free software; you can redistribute it and/or
  modify it under the terms of the GNU General Public License
  as published by the Free Software Foundation; either version 2
  of the License, or (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU General Public License for more details.
*/

#include <Windows.h>
#include <string>
#include <sstream>
#include <iostream>
#include <memory>
#include <vector>
#include <algorithm>
#include <cwctype>
#include "../API/RainmeterAPI.h"
#include "Logs/Logs.h"

struct Measure
{
    std::wstring scriptPath;
    std::wstring inlineScript;
    std::wstring nodeExecutable;
    std::wstring lastResult;
    bool nodeFound;
    bool initialized;
    bool useInlineScript;
    PROCESS_INFORMATION processInfo;
    HANDLE hInputWrite;
    HANDLE hOutputRead;

    Measure() : nodeFound(false), initialized(false), useInlineScript(false)
    {
        ZeroMemory(&processInfo, sizeof(PROCESS_INFORMATION));
        hInputWrite = NULL;
        hOutputRead = NULL;
    }
};

// Forward declarations for helper functions
std::wstring ParseAndBuildFunctionCall(const std::wstring& input);
std::vector<std::wstring> ParseParameters(const std::wstring& paramString);
bool IsNumeric(const std::wstring& str);
std::wstring EscapeString(const std::wstring& str);

// Helper function to check if Node.js is available
bool FindNodeExecutable(std::wstring& nodePath)
{
    // Try common Node.js installation paths
    std::vector<std::wstring> possiblePaths = {
        L"node",  // If in PATH
        L"C:\\Program Files\\nodejs\\node.exe",
        L"C:\\Program Files (x86)\\nodejs\\node.exe"
    };

    for (const std::wstring& path : possiblePaths)
    {
        STARTUPINFO si;
        PROCESS_INFORMATION pi;
        ZeroMemory(&si, sizeof(si));
        si.cb = sizeof(si);
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = SW_HIDE;
        ZeroMemory(&pi, sizeof(pi));

        std::wstring cmdLine = path + L" --version";

        if (CreateProcess(NULL, const_cast<LPWSTR>(cmdLine.c_str()), NULL, NULL, FALSE,
            CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
        {
            WaitForSingleObject(pi.hProcess, 1000); // Wait max 1 second
            DWORD exitCode;
            if (GetExitCodeProcess(pi.hProcess, &exitCode) && exitCode == 0)
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                nodePath = path;
                return true;
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
    }
    return false;
}

// Execute Node.js command with enhanced console output handling
std::wstring ExecuteNodeCommand(const std::wstring& nodeExe, const std::wstring& scriptPath, const std::wstring& inlineScript, bool useInline, const std::wstring& command)
{
    SECURITY_ATTRIBUTES saAttr;
    saAttr.nLength = sizeof(SECURITY_ATTRIBUTES);
    saAttr.bInheritHandle = TRUE;
    saAttr.lpSecurityDescriptor = NULL;

    // Create pipes for stdout and stderr separately
    HANDLE hChildStdoutRd, hChildStdoutWr;
    HANDLE hChildStderrRd, hChildStderrWr;

    if (!CreatePipe(&hChildStdoutRd, &hChildStdoutWr, &saAttr, 0))
        return L"";
    if (!CreatePipe(&hChildStderrRd, &hChildStderrWr, &saAttr, 0))
    {
        CloseHandle(hChildStdoutRd);
        CloseHandle(hChildStdoutWr);
        return L"";
    }

    if (!SetHandleInformation(hChildStdoutRd, HANDLE_FLAG_INHERIT, 0) ||
        !SetHandleInformation(hChildStderrRd, HANDLE_FLAG_INHERIT, 0))
    {
        CloseHandle(hChildStdoutRd);
        CloseHandle(hChildStdoutWr);
        CloseHandle(hChildStderrRd);
        CloseHandle(hChildStderrWr);
        return L"";
    }

    STARTUPINFO si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.hStdError = hChildStderrWr;
    si.hStdOutput = hChildStdoutWr;
    si.dwFlags |= STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    std::wstring cmdLine;
    std::wstring tempFile;

    if (useInline)
    {
        // For inline scripts, create a temporary file with enhanced console handling
        wchar_t tempPath[MAX_PATH];
        wchar_t tempFileBuffer[MAX_PATH];
        GetTempPath(MAX_PATH, tempPath);
        GetTempFileName(tempPath, L"RMNodeJS", 0, tempFileBuffer);
        tempFile = tempFileBuffer;

        // Write the inline script to temp file with enhanced console wrapper
        HANDLE hFile = CreateFile(tempFile.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, NULL);
        if (hFile != INVALID_HANDLE_VALUE)
        {
            // Convert inline script to UTF-8
            std::string scriptUtf8;
            int utf8Length = WideCharToMultiByte(CP_UTF8, 0, inlineScript.c_str(), -1, NULL, 0, NULL, NULL);
            if (utf8Length > 0)
            {
                scriptUtf8.resize(utf8Length - 1);
                WideCharToMultiByte(CP_UTF8, 0, inlineScript.c_str(), -1, &scriptUtf8[0], utf8Length, NULL, NULL);
            }

            // Create wrapper that captures console methods and marks output appropriately
            std::string wrapperScript = R"(
// Store original console methods
const originalConsole = {
    log: console.log,
    error: console.error,
    warn: console.warn,
    debug: console.debug,
    info: console.info
};

// Override console methods to add prefixes for log level detection
console.log = (...args) => {
    process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\n');
};

console.error = (...args) => {
    process.stderr.write('ERROR: ' + args.map(a => String(a)).join(' ') + '\n');
};

console.warn = (...args) => {
    process.stderr.write('WARNING: ' + args.map(a => String(a)).join(' ') + '\n');
};

console.debug = (...args) => {
    process.stdout.write('DEBUG: ' + args.map(a => String(a)).join(' ') + '\n');
};

console.info = (...args) => {
    process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\n');
};

// User script content
)";

            wrapperScript += scriptUtf8;

            // Add function execution
            wrapperScript += "\n\n// Execute the requested function\n";
            wrapperScript += "try {\n";

            // Convert command to UTF8
            int cmdUtf8Length = WideCharToMultiByte(CP_UTF8, 0, command.c_str(), -1, NULL, 0, NULL, NULL);
            if (cmdUtf8Length > 0)
            {
                std::string cmdUtf8(cmdUtf8Length - 1, '\0');
                WideCharToMultiByte(CP_UTF8, 0, command.c_str(), -1, &cmdUtf8[0], cmdUtf8Length, NULL, NULL);

                // Handle special lifecycle functions
                if (cmdUtf8 == "initialize")
                {
                    wrapperScript += "  if (typeof initialize === 'function') {\n";
                    wrapperScript += "    const result = initialize();\n";
                    wrapperScript += "    if (result !== undefined && result !== null) {\n";
                    wrapperScript += "      process.stdout.write('RESULT:' + String(result) + '\\n');\n";
                    wrapperScript += "    }\n";
                    wrapperScript += "  }\n";
                }
                else if (cmdUtf8 == "finalize")
                {
                    wrapperScript += "  if (typeof finalize === 'function') {\n";
                    wrapperScript += "    const result = finalize();\n";
                    wrapperScript += "    if (result !== undefined && result !== null) {\n";
                    wrapperScript += "      process.stdout.write('RESULT:' + String(result) + '\\n');\n";
                    wrapperScript += "    }\n";
                    wrapperScript += "  }\n";
                }
                else if (cmdUtf8 == "update")
                {
                    wrapperScript += "  if (typeof update === 'function') {\n";
                    wrapperScript += "    const result = update();\n";
                    wrapperScript += "    if (result !== undefined && result !== null) {\n";
                    wrapperScript += "      process.stdout.write('RESULT:' + String(result) + '\\n');\n";
                    wrapperScript += "    }\n";
                    wrapperScript += "  }\n";
                }
                else if (cmdUtf8 == "getString")
                {
                    wrapperScript += "  if (typeof getString === 'function') {\n";
                    wrapperScript += "    const result = getString();\n";
                    wrapperScript += "    if (result !== undefined && result !== null) {\n";
                    wrapperScript += "      process.stdout.write('RESULT:' + String(result) + '\\n');\n";
                    wrapperScript += "    }\n";
                    wrapperScript += "  }\n";
                }
                else
                {
                    // For any other command, evaluate it directly as JavaScript code
                    wrapperScript += "  const result = eval('";
                    wrapperScript += cmdUtf8;
                    wrapperScript += "');\n";
                    wrapperScript += "  if (result !== undefined && result !== null) {\n";
                    wrapperScript += "    process.stdout.write('RESULT:' + String(result) + '\\n');\n";
                    wrapperScript += "  }\n";
                }
            }

            wrapperScript += "} catch(e) {\n";
            wrapperScript += "  console.error('NodeJS Plugin Error: ' + e.message);\n";
            wrapperScript += "}";

            DWORD bytesWritten;
            WriteFile(hFile, wrapperScript.c_str(), static_cast<DWORD>(wrapperScript.length()), &bytesWritten, NULL);
            CloseHandle(hFile);

            // Build command line to execute the temp file
            cmdLine = L"\"" + std::wstring(nodeExe) + L"\" \"" + tempFile + L"\"";
        }
        else
        {
            return L"Failed to create temporary script file";
        }
    }
    else
    {
        // For file scripts, use similar enhanced wrapper approach
        std::wstring normalizedPath = scriptPath;
        std::replace(normalizedPath.begin(), normalizedPath.end(), L'\\', L'/');

        std::wstringstream jsCode;
        jsCode << L"const path = require('path'); ";
        jsCode << L"const fs = require('fs'); ";

        // Add console override wrapper
        jsCode << L"const originalConsole = { log: console.log, error: console.error, warn: console.warn, debug: console.debug, info: console.info }; ";
        jsCode << L"console.log = (...args) => { process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.error = (...args) => { process.stderr.write('ERROR: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.warn = (...args) => { process.stderr.write('WARNING: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.debug = (...args) => { process.stdout.write('DEBUG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.info = (...args) => { process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";

        jsCode << L"try { ";
        jsCode << L"const scriptPath = '" << normalizedPath << "'; ";
        jsCode << L"if (!fs.existsSync(scriptPath)) { ";
        jsCode << L"throw new Error('Script file not found: ' + scriptPath); ";
        jsCode << L"} ";
        jsCode << L"const scriptContent = fs.readFileSync(scriptPath, 'utf8'); ";
        jsCode << L"eval(scriptContent); ";

        // Handle different command types
        if (command == L"initialize")
        {
            jsCode << L"if (typeof initialize === 'function') { ";
            jsCode << L"const result = initialize(); ";
            jsCode << L"if (result !== undefined && result !== null) { ";
            jsCode << L"process.stdout.write('RESULT:' + String(result) + '\\n'); ";
            jsCode << L"} ";
            jsCode << L"} ";
        }
        else if (command == L"finalize")
        {
            jsCode << L"if (typeof finalize === 'function') { ";
            jsCode << L"const result = finalize(); ";
            jsCode << L"if (result !== undefined && result !== null) { ";
            jsCode << L"process.stdout.write('RESULT:' + String(result) + '\\n'); ";
            jsCode << L"} ";
            jsCode << L"} ";
        }
        else if (command == L"update")
        {
            jsCode << L"if (typeof update === 'function') { ";
            jsCode << L"const result = update(); ";
            jsCode << L"if (result !== undefined && result !== null) { ";
            jsCode << L"process.stdout.write('RESULT:' + String(result) + '\\n'); ";
            jsCode << L"} ";
            jsCode << L"} ";
        }
        else if (command == L"getString")
        {
            jsCode << L"if (typeof getString === 'function') { ";
            jsCode << L"const result = getString(); ";
            jsCode << L"if (result !== undefined && result !== null) { ";
            jsCode << L"process.stdout.write('RESULT:' + String(result) + '\\n'); ";
            jsCode << L"} ";
            jsCode << L"} ";
        }
        else
        {
            // For any other command, evaluate it directly
            jsCode << L"const result = eval('" << command << L"'); ";
            jsCode << L"if (result !== undefined && result !== null) { ";
            jsCode << L"process.stdout.write('RESULT:' + String(result) + '\\n'); ";
            jsCode << L"} ";
        }

        jsCode << L"} catch(e) { ";
        jsCode << L"console.error('NodeJS Plugin Error: ' + e.message); ";
        jsCode << L"}";

        cmdLine = L"\"" + nodeExe + L"\" -e \"" + jsCode.str() + L"\"";
    }

    BOOL success = CreateProcess(NULL, const_cast<LPWSTR>(cmdLine.c_str()), NULL, NULL, TRUE,
        CREATE_NO_WINDOW, NULL, NULL, &si, &pi);

    CloseHandle(hChildStdoutWr);
    CloseHandle(hChildStderrWr);

    std::wstring result;
    if (success)
    {
        WaitForSingleObject(pi.hProcess, 5000); // Wait max 5 seconds

        DWORD dwRead;
        CHAR chBuf[4096];
        std::string stdoutOutput, stderrOutput;

        // Read from stdout
        while (true)
        {
            BOOL bSuccess = ReadFile(hChildStdoutRd, chBuf, sizeof(chBuf), &dwRead, NULL);
            if (!bSuccess || dwRead == 0) break;
            stdoutOutput.append(chBuf, dwRead);
        }

        // Read from stderr  
        while (true)
        {
            BOOL bSuccess = ReadFile(hChildStderrRd, chBuf, sizeof(chBuf), &dwRead, NULL);
            if (!bSuccess || dwRead == 0) break;
            stderrOutput.append(chBuf, dwRead);
        }

        // Process stderr output (errors and warnings) first
        if (!stderrOutput.empty())
        {
            Logger::ParseAndLogConsoleOutput(stderrOutput);
        }

        // Process stdout output and extract result
        if (!stdoutOutput.empty())
        {
            std::istringstream stream(stdoutOutput);
            std::string line;
            std::string logLines;

            while (std::getline(stream, line))
            {
                if (line.find("RESULT:") == 0)
                {
                    // Extract result
                    std::string resultStr = line.substr(7); // Skip "RESULT:"

                    // Convert to wide string
                    int wchars_num = MultiByteToWideChar(CP_UTF8, 0, resultStr.c_str(), -1, NULL, 0);
                    if (wchars_num > 0)
                    {
                        wchar_t* wstr = new wchar_t[wchars_num];
                        MultiByteToWideChar(CP_UTF8, 0, resultStr.c_str(), -1, wstr, wchars_num);
                        result = wstr;
                        delete[] wstr;

                        // Remove trailing newlines
                        while (!result.empty() && (result.back() == L'\n' || result.back() == L'\r'))
                            result.pop_back();
                    }
                }
                else if (!line.empty())
                {
                    // Accumulate log lines
                    if (!logLines.empty()) logLines += "\n";
                    logLines += line;
                }
            }

            // Process accumulated log lines
            if (!logLines.empty())
            {
                Logger::ParseAndLogConsoleOutput(logLines);
            }
        }

        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);

        // Clean up temporary file if we created one
        if (useInline && !tempFile.empty())
        {
            DeleteFile(tempFile.c_str());
        }
    }

    CloseHandle(hChildStdoutRd);
    CloseHandle(hChildStderrRd);
    return result;
}

// Helper function to parse and build proper function calls
std::wstring ParseAndBuildFunctionCall(const std::wstring& input)
{
    // Remove leading/trailing whitespace
    std::wstring trimmed = input;
    size_t start = trimmed.find_first_not_of(L" \t\r\n");
    if (start == std::wstring::npos) return L"";

    size_t end = trimmed.find_last_not_of(L" \t\r\n");
    trimmed = trimmed.substr(start, end - start + 1);

    // If the input already looks like a proper function call (contains parentheses), use it as-is
    if (trimmed.find(L'(') != std::wstring::npos && trimmed.find(L')') != std::wstring::npos)
    {
        return trimmed;
    }

    // Parse function name and parameters from formats like:
    // "FunctionName"
    // "FunctionName param1"
    // "FunctionName param1 param2"
    // "FunctionName(param1, param2)" - already handled above

    size_t spacePos = trimmed.find(L' ');
    std::wstring functionName;
    std::wstring parameters;

    if (spacePos == std::wstring::npos)
    {
        // No parameters, just function name
        functionName = trimmed;
        return functionName + L"()";
    }
    else
    {
        // Extract function name and parameters
        functionName = trimmed.substr(0, spacePos);
        parameters = trimmed.substr(spacePos + 1);

        // Parse parameters - split by spaces but respect quoted strings
        std::vector<std::wstring> params = ParseParameters(parameters);

        // Build function call
        std::wstring functionCall = functionName + L"(";
        for (size_t i = 0; i < params.size(); ++i)
        {
            if (i > 0) functionCall += L", ";

            // Check if parameter looks like a number
            if (IsNumeric(params[i]))
            {
                functionCall += params[i];
            }
            else if (params[i] == L"true" || params[i] == L"false")
            {
                // Boolean values
                functionCall += params[i];
            }
            else if (params[i] == L"null" || params[i] == L"undefined")
            {
                // Null/undefined values
                functionCall += params[i];
            }
            else
            {
                // String parameter - add quotes if not already quoted
                if (params[i].front() != L'"' && params[i].front() != L'\'')
                {
                    functionCall += L"\"" + EscapeString(params[i]) + L"\"";
                }
                else
                {
                    functionCall += params[i];
                }
            }
        }
        functionCall += L")";

        return functionCall;
    }
}

// Helper function to parse parameters, respecting quoted strings
std::vector<std::wstring> ParseParameters(const std::wstring& paramString)
{
    std::vector<std::wstring> params;
    std::wstring current;
    bool inQuotes = false;
    wchar_t quoteChar = L'\0';

    for (size_t i = 0; i < paramString.length(); ++i)
    {
        wchar_t c = paramString[i];

        if (!inQuotes && (c == L'"' || c == L'\''))
        {
            // Starting a quoted string
            inQuotes = true;
            quoteChar = c;
            current += c;
        }
        else if (inQuotes && c == quoteChar)
        {
            // Ending a quoted string (check for escape)
            if (i > 0 && paramString[i - 1] == L'\\')
            {
                current += c; // Escaped quote
            }
            else
            {
                inQuotes = false;
                current += c;
            }
        }
        else if (!inQuotes && (c == L' ' || c == L'\t'))
        {
            // Parameter separator
            if (!current.empty())
            {
                params.push_back(current);
                current.clear();
            }
        }
        else
        {
            current += c;
        }
    }

    // Add the last parameter
    if (!current.empty())
    {
        params.push_back(current);
    }

    return params;
}

// Helper function to check if a string represents a number
bool IsNumeric(const std::wstring& str)
{
    if (str.empty()) return false;

    size_t start = 0;
    if (str[0] == L'-' || str[0] == L'+') start = 1;
    if (start >= str.length()) return false;

    bool hasDecimal = false;
    for (size_t i = start; i < str.length(); ++i)
    {
        if (str[i] == L'.')
        {
            if (hasDecimal) return false; // Multiple decimals
            hasDecimal = true;
        }
        else if (!std::iswdigit(str[i]))
        {
            return false;
        }
    }

    return true;
}

// Helper function to escape strings for JavaScript
std::wstring EscapeString(const std::wstring& str)
{
    std::wstring escaped;
    for (wchar_t c : str)
    {
        switch (c)
        {
        case L'"':  escaped += L"\\\""; break;
        case L'\\': escaped += L"\\\\"; break;
        case L'\n': escaped += L"\\n"; break;
        case L'\r': escaped += L"\\r"; break;
        case L'\t': escaped += L"\\t"; break;
        default:    escaped += c; break;
        }
    }
    return escaped;
}

PLUGIN_EXPORT void Initialize(void** data, void* rm)
{
    Measure* measure = new Measure;
    *data = measure;

    // Check if Node.js is available
    if (!FindNodeExecutable(measure->nodeExecutable))
    {
        measure->nodeFound = false;
        Logger::LogError(L"Node.js not found in system PATH or common installation directories");
        return;
    }

    measure->nodeFound = true;
    Logger::LogNotice(L"Found Node.js at " + measure->nodeExecutable);
}

PLUGIN_EXPORT void Reload(void* data, void* rm, double* maxValue)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure->nodeFound)
    {
        return;
    }

    // Check if using inline script (Line, Line2, etc.) or script file
    LPCWSTR firstLine = RmReadString(rm, L"Line", L"", FALSE);
    if (wcslen(firstLine) > 0)
    {
        // Using inline script
        measure->useInlineScript = true;
        measure->inlineScript = firstLine;

        // Collect all Line parameters (Line2, Line3, etc.)
        for (int i = 2; i <= 100; i++) // Support up to Line100
        {
            wchar_t lineName[20];
            swprintf_s(lineName, 20, L"Line%d", i);
            LPCWSTR lineContent = RmReadString(rm, lineName, L"", FALSE);
            if (wcslen(lineContent) == 0)
                break; // Stop when we find an empty line

            measure->inlineScript += L"\n";
            measure->inlineScript += lineContent;
        }

        Logger::LogNotice(L"Using inline script");
    }
    else
    {
        // Using script file
        measure->useInlineScript = false;
        LPCWSTR scriptFile = RmReadPath(rm, L"ScriptFile", L"");
        if (wcslen(scriptFile) == 0)
        {
            Logger::LogError(L"Either ScriptFile parameter or Line parameters are required");
            return;
        }

        measure->scriptPath = scriptFile;

        // Check if script file exists
        DWORD fileAttr = GetFileAttributes(measure->scriptPath.c_str());
        if (fileAttr == INVALID_FILE_ATTRIBUTES)
        {
            Logger::LogError(L"Script file not found: " + measure->scriptPath);
            return;
        }

        Logger::LogNotice(L"Using script file: " + measure->scriptPath);
    }

    // Try to call initialize function, but don't fail if it doesn't exist
    std::wstring result = ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, measure->inlineScript, measure->useInlineScript, L"initialize");

    // Only log the result if it's not empty (console logs are handled separately now)
    if (!result.empty())
    {
        Logger::LogDebug(L"Initialize returned: " + result);
    }

    measure->initialized = true;
}

PLUGIN_EXPORT double Update(void* data)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure->nodeFound || !measure->initialized)
    {
        return 0.0;
    }

    // Try to call update function, but don't fail if it doesn't exist
    std::wstring result = ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, measure->inlineScript, measure->useInlineScript, L"update");

    if (!result.empty())
    {
        measure->lastResult = result;

        // Try to convert result to double
        try
        {
            return std::stod(result);
        }
        catch (...)
        {
            // If conversion fails, return 0 and store string result for GetString()
            return 0.0;
        }
    }

    return 0.0;
}

PLUGIN_EXPORT LPCWSTR GetString(void* data)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure->nodeFound || !measure->initialized)
    {
        return measure->nodeFound ? L"Script not initialized" : L"Node.js not found";
    }

    // If we have a cached result from Update(), return it
    if (!measure->lastResult.empty())
    {
        return measure->lastResult.c_str();
    }

    // Otherwise try to call a getString function in the script
    std::wstring result = ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, measure->inlineScript, measure->useInlineScript, L"getString");
    measure->lastResult = result;
    return measure->lastResult.c_str();
}

// Enhanced ExecuteBang function with parameter parsing
PLUGIN_EXPORT void ExecuteBang(void* data, LPCWSTR args)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure->nodeFound || !measure->initialized)
    {
        Logger::LogWarning(L"Cannot execute bang: Node.js not found or plugin not initialized");
        return;
    }

    // Convert args to string
    std::wstring command = args ? args : L"";
    if (command.empty())
    {
        Logger::LogWarning(L"Empty command provided to ExecuteBang");
        return;
    }

    // Parse the command to extract function name and parameters
    std::wstring functionCall = ParseAndBuildFunctionCall(command);

    if (functionCall.empty())
    {
        Logger::LogError(L"Failed to parse function call: " + command);
        return;
    }

    Logger::LogDebug(L"Executing function call: " + functionCall);

    // Execute the parsed function call
    std::wstring result = ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath,
        measure->inlineScript, measure->useInlineScript, functionCall);

    // Store result for potential retrieval
    if (!result.empty())
    {
        measure->lastResult = result;
        Logger::LogDebug(L"Bang '" + command + L"' returned: " + result);
    }
}

PLUGIN_EXPORT void Finalize(void* data)
{
    Measure* measure = static_cast<Measure*>(data);

    if (measure->nodeFound && measure->initialized)
    {
        // Try to call finalize function, but don't fail if it doesn't exist
        ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, measure->inlineScript, measure->useInlineScript, L"finalize");
    }

    delete measure;
}