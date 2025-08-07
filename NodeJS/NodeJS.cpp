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
#include "Utils/Utils.h"
#include "ParserFunctionality/ParserFunctionality.h"

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

PLUGIN_EXPORT void Initialize(void** data, void* rm)
{
    Measure* measure = new Measure;
    *data = measure;

    // Check if Node.js is available
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
    std::wstring result = Utils::ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, 
                                                   measure->inlineScript, measure->useInlineScript, L"initialize");

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
    std::wstring result = Utils::ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, 
                                                   measure->inlineScript, measure->useInlineScript, L"update");

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
    std::wstring result = Utils::ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, 
                                                   measure->inlineScript, measure->useInlineScript, L"getString");
    measure->lastResult = result;
    return measure->lastResult.c_str();
}

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
    std::wstring functionCall = ParserFunctionality::ParseAndBuildFunctionCall(command);

    if (functionCall.empty())
    {
        Logger::LogError(L"Failed to parse function call: " + command);
        return;
    }

    Logger::LogDebug(L"Executing function call: " + functionCall);

    // Execute the parsed function call
    std::wstring result = Utils::ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath,
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
        Utils::ExecuteNodeCommand(measure->nodeExecutable, measure->scriptPath, 
                                measure->inlineScript, measure->useInlineScript, L"finalize");
    }

    delete measure;
}