#include <Windows.h>
#include <string>
#include <sstream>
#include <iostream>
#include <memory>
#include <vector>
#include <algorithm>
#include <cwctype>
#include "../API/RainmeterAPI.h"
#include "LogsFunctions/Logs.h"
#include "Utils/Utils.h"
#include "ScriptExecutor/ScriptExecutor.h"
#include "ParserFunctionality/ParserFunctionality.h"
#include "CommonOptions/MeterOptions.h"
#include "CommonOptions/MeterHelper.h"

struct Measure
{
    std::wstring scriptPath;
    std::wstring inlineScript;
    std::wstring nodeExecutable;
    std::wstring lastResult;
    bool nodeFound;
    bool initialized;
    bool useInlineScript;
    void* rm;          
    void* skin;        
    PROCESS_INFORMATION processInfo;
    HANDLE hInputWrite;
    HANDLE hOutputRead;

    Measure() : nodeFound(false), initialized(false), useInlineScript(false), rm(nullptr), skin(nullptr)
    {
        ZeroMemory(&processInfo, sizeof(PROCESS_INFORMATION));
        hInputWrite = NULL;
        hOutputRead = NULL;
    }
};

PLUGIN_EXPORT void Initialize(void** data, void* rm)
{
    Measure* measure = new Measure;
    *data = measure;

    measure->rm = rm;
    measure->skin = RmGetSkin(rm);

    if (!Utils::FindNodeExecutable(measure->nodeExecutable))
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

    measure->rm = rm;
    measure->skin = RmGetSkin(rm);

    if (!measure->nodeFound)
    {
        return;
    }

    LPCWSTR firstLine = RmReadString(rm, L"Line", L"", FALSE);
    if (wcslen(firstLine) > 0)
    {
        measure->useInlineScript = true;
        measure->inlineScript = firstLine;

        for (int i = 2; i <= 100; i++) 
        {
            wchar_t lineName[20];
            swprintf_s(lineName, 20, L"Line%d", i);
            LPCWSTR lineContent = RmReadString(rm, lineName, L"", FALSE);
            if (wcslen(lineContent) == 0)
                break; 

            measure->inlineScript += L"\n";
            measure->inlineScript += lineContent;
        }

        Logger::LogNotice(L"Using inline script with meter options support");
    }
    else
    {
        measure->useInlineScript = false;
        LPCWSTR scriptFile = RmReadPath(rm, L"ScriptFile", L"");
        if (wcslen(scriptFile) == 0)
        {
            Logger::LogError(L"Either ScriptFile parameter or Line parameters are required");
            return;
        }

        measure->scriptPath = scriptFile;

        DWORD fileAttr = GetFileAttributes(measure->scriptPath.c_str());
        if (fileAttr == INVALID_FILE_ATTRIBUTES)
        {
            Logger::LogError(L"Script file not found: " + measure->scriptPath);
            return;
        }

        Logger::LogNotice(L"Using script file with meter options support: " + measure->scriptPath);
    }

    std::wstring result = ScriptExecutor::ExecuteNodeCommand(
        measure->nodeExecutable, 
        measure->scriptPath, 
        measure->inlineScript, 
        measure->useInlineScript, 
        L"initialize"
    );

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

    std::wstring result = ScriptExecutor::ExecuteNodeCommand(
        measure->nodeExecutable, 
        measure->scriptPath, 
        measure->inlineScript, 
        measure->useInlineScript, 
        L"update"
    );

    if (!result.empty())
    {
        measure->lastResult = result;

        try
        {
            return std::stod(result);
        }
        catch (const std::exception&)
        {
            Logger::LogDebug(L"Could not convert result to double: " + result);
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

    if (!measure->lastResult.empty())
    {
        return measure->lastResult.c_str();
    }

    std::wstring result = ScriptExecutor::ExecuteNodeCommand(
        measure->nodeExecutable, 
        measure->scriptPath, 
        measure->inlineScript, 
        measure->useInlineScript, 
        L"getString"
    );

    measure->lastResult = result;
    return measure->lastResult.c_str();
}

/**
 * @brief Executes a JavaScript expression passed via a section variable.
 * * This function is exposed to Rainmeter as a section variable function. It allows
 * executing arbitrary JavaScript code.
 * * @param data Pointer to the measure's data structure.
 * @param argc The number of arguments.
 * @param argv An array of wide string arguments. argv[0] contains the JS code.
 * @return LPCWSTR A pointer to a wide string containing the result of the execution.
 * The pointer is to a static string, which remains valid after the function returns.
 */
PLUGIN_EXPORT LPCWSTR Execute(void* data, const int argc, const WCHAR* argv[])
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure || !measure->nodeFound || !measure->initialized)
    {
        return L"";
    }

    if (argc < 1 || !argv || !argv[0])
    {
        return L"";
    }

    std::wstring command = argv[0];
    if (command.empty())
    {
        Logger::LogWarning(L"Empty command provided to Execute");
        return L"";
    }

    // The result must be stored in a static variable so the pointer remains valid.
    static std::wstring persistentResult;

    // Handle direct calls to MeterOption functions
    if (MeterHelper::IsMeterFunction(command))
    {
        persistentResult = MeterHelper::ExecuteMeterFunction(measure, command);
        Logger::LogDebug(L"Execute function '" + command + L"' returned: " + persistentResult);
        return persistentResult.c_str();
    }

    // Execute other JavaScript expressions via Node.js
    Logger::LogDebug(L"Executing expression: " + command);

    std::wstring result = ScriptExecutor::ExecuteNodeCommand(
        measure->nodeExecutable,
        measure->scriptPath,
        measure->inlineScript,
        measure->useInlineScript,
        command // Pass the raw JavaScript expression
    );

    persistentResult = result;
    if (!persistentResult.empty())
    {
        Logger::LogDebug(L"Execute expression '" + command + L"' returned: " + persistentResult);
    }
    
    return persistentResult.c_str();
}


PLUGIN_EXPORT void ExecuteBang(void* data, LPCWSTR args)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure->nodeFound || !measure->initialized)
    {
        Logger::LogWarning(L"Cannot execute bang: Node.js not found or plugin not initialized");
        return;
    }

    std::wstring command = args ? args : L"";
    if (command.empty())
    {
        Logger::LogWarning(L"Empty command provided to ExecuteBang");
        return;
    }

    if (MeterHelper::IsMeterFunction(command))
    {
        // Execute meter function directly
        std::wstring result = MeterHelper::ExecuteMeterFunction(measure, command);
        measure->lastResult = result;
        Logger::LogDebug(L"Meter function '" + command + L"' returned: " + result);
        return;
    }

    std::wstring functionCall = ParserFunctionality::ParseAndBuildFunctionCall(command);

    if (functionCall.empty())
    {
        Logger::LogError(L"Failed to parse function call: " + command);
        return;
    }

    Logger::LogDebug(L"Executing function call: " + functionCall);

    std::wstring result = ScriptExecutor::ExecuteNodeCommand(
        measure->nodeExecutable, 
        measure->scriptPath,
        measure->inlineScript, 
        measure->useInlineScript, 
        functionCall
    );

    if (!result.empty())
    {
        measure->lastResult = result;
        Logger::LogDebug(L"Bang '" + command + L"' returned: " + result);
    }
}

PLUGIN_EXPORT void Finalize(void* data)
{
    Measure* measure = static_cast<Measure*>(data);

    if (!measure)
    {
        return;
    }

    if (measure->nodeFound && measure->initialized)
    {
        try 
        {
            ScriptExecutor::ExecuteNodeCommand(
                measure->nodeExecutable, 
                measure->scriptPath, 
                measure->inlineScript, 
                measure->useInlineScript, 
                L"finalize"
            );
        }
        catch (...)
        {
            Logger::LogWarning(L"Exception during finalization, continuing cleanup");
        }
    }

    if (measure->hInputWrite)
    {
        CloseHandle(measure->hInputWrite);
    }
    if (measure->hOutputRead)
    {
        CloseHandle(measure->hOutputRead);
    }
    if (measure->processInfo.hProcess)
    {
        CloseHandle(measure->processInfo.hProcess);
    }
    if (measure->processInfo.hThread)
    {
        CloseHandle(measure->processInfo.hThread);
    }

    delete measure;
}
