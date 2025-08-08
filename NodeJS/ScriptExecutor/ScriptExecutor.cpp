#include "ScriptExecutor.h"
#include "../LogsFunctions/Logs.h"
#include "../Utils/Utils.h"
#include <algorithm>
#include <sstream>
#include <iostream>

namespace ScriptExecutor
{
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
            std::string wrapperScript = CreateInlineScriptWrapper(inlineScript, command);

            if (!CreateTempFile(wrapperScript, tempFile))
            {
                CloseHandle(hChildStdoutRd);
                CloseHandle(hChildStdoutWr);
                CloseHandle(hChildStderrRd);
                CloseHandle(hChildStderrWr);
                return L"Failed to create temporary script file";
            }

            cmdLine = L"\"" + nodeExe + L"\" \"" + tempFile + L"\"";
        }
        else
        {
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
            WaitForSingleObject(pi.hProcess, 5000); 

            std::string stdoutOutput = ReadFromPipe(hChildStdoutRd);
            std::string stderrOutput = ReadFromPipe(hChildStderrRd);

            if (!stderrOutput.empty())
            {
                Logger::ParseAndLogConsoleOutput(stderrOutput);
            }

            if (!stdoutOutput.empty())
            {
                result = ProcessStdoutOutput(stdoutOutput);
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

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

    std::string CreateInlineScriptWrapper(const std::wstring& inlineScript, const std::wstring& command)
    {
        std::string scriptUtf8 = Utils::WideStringToUtf8(inlineScript);
        std::string cmdUtf8 = Utils::WideStringToUtf8(command);

        std::string wrapperScript = R"(
// Console output redirection
const originalConsole = {
    log: console.log,
    error: console.error,
    warn: console.warn,
    debug: console.debug,
    info: console.info
};

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

// Meter functions - these will be handled by the C++ plugin with new syntax
global.MeterOption = {
    GetX: function(meterName, defValue = '') {
        throw new Error('MeterOption.GetX should be called via ExecuteBang, not directly in Node.js');
    },
    GetY: function(meterName, defValue = '') {
        throw new Error('MeterOption.GetY should be called via ExecuteBang, not directly in Node.js');
    },
    GetW: function(meterName, defValue = '') {
        throw new Error('MeterOption.GetW should be called via ExecuteBang, not directly in Node.js');
    },
    GetH: function(meterName, defValue = '') {
        throw new Error('MeterOption.GetH should be called via ExecuteBang, not directly in Node.js');
    },
    SetX: function(meterName, value) {
        throw new Error('MeterOption.SetX should be called via ExecuteBang, not directly in Node.js');
    },
    SetY: function(meterName, value) {
        throw new Error('MeterOption.SetY should be called via ExecuteBang, not directly in Node.js');
    },
    SetW: function(meterName, value) {
        throw new Error('MeterOption.SetW should be called via ExecuteBang, not directly in Node.js');
    },
    SetH: function(meterName, value) {
        throw new Error('MeterOption.SetH should be called via ExecuteBang, not directly in Node.js');
    },
    Show: function(meterName) {
        throw new Error('MeterOption.Show should be called via ExecuteBang, not directly in Node.js');
    },
    Hide: function(meterName) {
        throw new Error('MeterOption.Hide should be called via ExecuteBang, not directly in Node.js');
    },
    GetProperty: function(meterName, property, defValue = '') {
        throw new Error('MeterOption.GetProperty should be called via ExecuteBang, not directly in Node.js');
    },
    SetProperty: function(meterName, property, value) {
        throw new Error('MeterOption.SetProperty should be called via ExecuteBang, not directly in Node.js');
    }
};

)";

        wrapperScript += scriptUtf8;
        wrapperScript += "\n\n// Execute the requested function\n";
        wrapperScript += "try {\n";

        if (cmdUtf8 == "initialize")
        {
            wrapperScript += "  if (typeof initialize === 'function') {\n";
            wrapperScript += "    const result = initialize();\n";
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
            // Custom function call
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

    std::wstring CreateFileScriptWrapper(const std::wstring& scriptPath, const std::wstring& command)
    {
        std::wstring normalizedPath = Utils::NormalizePath(scriptPath);

        std::wstringstream jsCode;
        jsCode << L"const path = require('path'); ";
        jsCode << L"const fs = require('fs'); ";

        // Console redirection
        jsCode << L"const originalConsole = { log: console.log, error: console.error, warn: console.warn, debug: console.debug, info: console.info }; ";
        jsCode << L"console.log = (...args) => { process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.error = (...args) => { process.stderr.write('ERROR: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.warn = (...args) => { process.stderr.write('WARNING: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.debug = (...args) => { process.stdout.write('DEBUG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";
        jsCode << L"console.info = (...args) => { process.stdout.write('LOG: ' + args.map(a => String(a)).join(' ') + '\\n'); }; ";

        // Meter function placeholders with new syntax
        jsCode << L"global.MeterOption = { ";
        jsCode << L"GetX: function(meterName, defValue = '') { throw new Error('MeterOption.GetX should be called via ExecuteBang'); }, ";
        jsCode << L"GetY: function(meterName, defValue = '') { throw new Error('MeterOption.GetY should be called via ExecuteBang'); }, ";
        jsCode << L"GetW: function(meterName, defValue = '') { throw new Error('MeterOption.GetW should be called via ExecuteBang'); }, ";
        jsCode << L"GetH: function(meterName, defValue = '') { throw new Error('MeterOption.GetH should be called via ExecuteBang'); }, ";
        jsCode << L"SetX: function(meterName, value) { throw new Error('MeterOption.SetX should be called via ExecuteBang'); }, ";
        jsCode << L"SetY: function(meterName, value) { throw new Error('MeterOption.SetY should be called via ExecuteBang'); }, ";
        jsCode << L"SetW: function(meterName, value) { throw new Error('MeterOption.SetW should be called via ExecuteBang'); }, ";
        jsCode << L"SetH: function(meterName, value) { throw new Error('MeterOption.SetH should be called via ExecuteBang'); }, ";
        jsCode << L"Show: function(meterName) { throw new Error('MeterOption.Show should be called via ExecuteBang'); }, ";
        jsCode << L"Hide: function(meterName) { throw new Error('MeterOption.Hide should be called via ExecuteBang'); }, ";
        jsCode << L"GetProperty: function(meterName, property, defValue = '') { throw new Error('MeterOption.GetProperty should be called via ExecuteBang'); }, ";
        jsCode << L"SetProperty: function(meterName, property, value) { throw new Error('MeterOption.SetProperty should be called via ExecuteBang'); } ";
        jsCode << L"}; ";

        jsCode << L"try { ";
        jsCode << L"const scriptPath = '" << normalizedPath << "'; ";
        jsCode << L"if (!fs.existsSync(scriptPath)) { ";
        jsCode << L"throw new Error('Script file not found: ' + scriptPath); ";
        jsCode << L"} ";
        jsCode << L"const scriptContent = fs.readFileSync(scriptPath, 'utf8'); ";
        jsCode << L"eval(scriptContent); ";

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
            // Custom function call
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
                std::string resultStr = line.substr(7); 
                result = Utils::Utf8ToWideString(resultStr);

                while (!result.empty() && (result.back() == L'\n' || result.back() == L'\r'))
                    result.pop_back();
            }
            else if (!line.empty())
            {
                // Collect log lines
                if (!logLines.empty()) logLines += "\n";
                logLines += line;
            }
        }

        if (!logLines.empty())
        {
            Logger::ParseAndLogConsoleOutput(logLines);
        }

        return result;
    }
}