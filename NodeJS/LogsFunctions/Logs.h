#pragma once

#include <Windows.h>
#include <string>
#include "../../API/RainmeterAPI.h"

enum class LogLevel
{
    LOG_ERROR_LEVEL = LOG_ERROR,
    LOG_WARNING_LEVEL = LOG_WARNING,
    LOG_NOTICE_LEVEL = LOG_NOTICE,
    LOG_DEBUG_LEVEL = LOG_DEBUG
};

class Logger
{
public:

    static void Log(LogLevel level, const std::wstring& message);

    static void LogError(const std::wstring& message);
    static void LogWarning(const std::wstring& message);
    static void LogNotice(const std::wstring& message);
    static void LogDebug(const std::wstring& message);

    static void ParseAndLogConsoleOutput(const std::string& output);

private:

    static std::wstring FormatMessage(const std::wstring& message);

    static std::wstring Utf8ToWideString(const std::string& utf8Str);

    static void ProcessConsoleLine(const std::string& line);
};