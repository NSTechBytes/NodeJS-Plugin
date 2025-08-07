#include "Utils.h"
#include "../Logs/Logs.h"
#include <algorithm>
#include <sstream>
#include <iostream>

namespace Utils
{
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

    std::wstring ExecuteNodeCommand(const std::wstring& nodeExe, 
                                   const std::wstring& scriptPath, 
                                   const std::wstring& inlineScript, 
                                   bool useInline, 
                                   const std::wstring& command)
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
            std::string wrapperScript = CreateInlineScriptWrapper(inlineScript, command);
            
            if (!CreateTempFile(wrapperScript, tempFile))
            {
                CloseHandle(hChildStdoutRd);
                CloseHandle(hChildStdoutWr);
                CloseHandle(hChildStderrRd);
                CloseHandle(hChildStderrWr);
                return L"Failed to create temporary script file";
            }

            // Build command line to execute the temp file
            cmdLine = L"\"" + nodeExe + L"\" \"" + tempFile + L"\"";
        }
        else
        {
            // For file scripts, use enhanced wrapper approach
            std::wstring jsCode = CreateFileScriptWrapper(scriptPath, command);
            cmdLine = L"\"" + nodeExe + L"\" -e \"" + jsCode + L"\"";
        }

        BOOL success = CreateProcess(NULL, const_cast<LPWSTR>(cmdLine.c_str()), NULL, NULL, TRUE,
            CREATE_NO_WINDOW, NULL, NULL, &si, &pi);

        CloseHandle(hChildStdoutWr);
        CloseHandle(hChildStderrWr);

        std::wstring result;
        if (success)
        {
            WaitForSingleObject(pi.hProcess, 5000); // Wait max 5 seconds

            // Read from stdout and stderr
            std::string stdoutOutput = ReadFromPipe(hChildStdoutRd);
            std::string stderrOutput = ReadFromPipe(hChildStderrRd);

            // Process stderr output (errors and warnings) first
            if (!stderrOutput.empty())
            {
                Logger::ParseAndLogConsoleOutput(stderrOutput);
            }

            // Process stdout output and extract result
            if (!stdoutOutput.empty())
            {
                result = ProcessStdoutOutput(stdoutOutput);
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

    bool CreateTempFile(const std::string& content, std::wstring& tempFilePath)
    {
        wchar_t tempPath[MAX_PATH];
        wchar_t tempFileBuffer[MAX_PATH];
        GetTempPath(MAX_PATH, tempPath);
        GetTempFileName(tempPath, L"RMNodeJS", 0, tempFileBuffer);
        tempFilePath = tempFileBuffer;

        HANDLE hFile = CreateFile(tempFilePath.c_str(), GENERIC_WRITE, 0, NULL, 
                                 CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, NULL);
        if (hFile == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        DWORD bytesWritten;
        BOOL writeResult = WriteFile(hFile, content.c_str(), 
                                   static_cast<DWORD>(content.length()), &bytesWritten, NULL);
        CloseHandle(hFile);

        return writeResult != FALSE;
    }

    std::string WideStringToUtf8(const std::wstring& wideStr)
    {
        if (wideStr.empty()) return std::string();

        int utf8Length = WideCharToMultiByte(CP_UTF8, 0, wideStr.c_str(), -1, NULL, 0, NULL, NULL);
        if (utf8Length <= 0) return std::string();

        std::string utf8Str(utf8Length - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, wideStr.c_str(), -1, &utf8Str[0], utf8Length, NULL, NULL);
        return utf8Str;
    }

    std::wstring Utf8ToWideString(const std::string& utf8Str)
    {
        if (utf8Str.empty()) return std::wstring();

        int wchars_num = MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, NULL, 0);
        if (wchars_num <= 0) return std::wstring();

        std::wstring wideStr(wchars_num - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, &wideStr[0], wchars_num);
        return wideStr;
    }

    std::string ReadFromPipe(HANDLE hPipe)
    {
        std::string output;
        DWORD dwRead;
        CHAR chBuf[4096];

        while (true)
        {
            BOOL bSuccess = ReadFile(hPipe, chBuf, sizeof(chBuf), &dwRead, NULL);
            if (!bSuccess || dwRead == 0) break;
            output.append(chBuf, dwRead);
        }

        return output;
    }

    std::wstring NormalizePath(const std::wstring& path)
    {
        std::wstring normalizedPath = path;
        std::replace(normalizedPath.begin(), normalizedPath.end(), L'\\', L'/');
        return normalizedPath;
    }

    // Helper function to create inline script wrapper
    std::string CreateInlineScriptWrapper(const std::wstring& inlineScript, const std::wstring& command)
    {
        std::string scriptUtf8 = WideStringToUtf8(inlineScript);
        std::string cmdUtf8 = WideStringToUtf8(command);

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
        wrapperScript += "\n\n// Execute the requested function\n";
        wrapperScript += "try {\n";
        
        // Handle different command types
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
        
        wrapperScript += "} catch(e) {\n";
        wrapperScript += "  console.error('NodeJS Plugin Error: ' + e.message);\n";
        wrapperScript += "}";

        return wrapperScript;
    }

    // Helper function to create file script wrapper
    std::wstring CreateFileScriptWrapper(const std::wstring& scriptPath, const std::wstring& command)
    {
        std::wstring normalizedPath = NormalizePath(scriptPath);

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

        return jsCode.str();
    }

    // Helper function to process stdout output and extract results
    std::wstring ProcessStdoutOutput(const std::string& stdoutOutput)
    {
        std::wstring result;
        std::istringstream stream(stdoutOutput);
        std::string line;
        std::string logLines;

        while (std::getline(stream, line))
        {
            if (line.find("RESULT:") == 0)
            {
                // Extract result
                std::string resultStr = line.substr(7); // Skip "RESULT:"
                result = Utf8ToWideString(resultStr);

                // Remove trailing newlines
                while (!result.empty() && (result.back() == L'\n' || result.back() == L'\r'))
                    result.pop_back();
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

        return result;
    }
}