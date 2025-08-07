#include "Logs.h"
#include <sstream>
#include <regex>

void Logger::Log(LogLevel level, const std::wstring& message)
{
    RmLog(static_cast<int>(level), FormatMessage(message).c_str());
}

void Logger::LogError(const std::wstring& message)
{
    Log(LogLevel::LOG_ERROR_LEVEL, message);
}

void Logger::LogWarning(const std::wstring& message)
{
    Log(LogLevel::LOG_WARNING_LEVEL, message);
}

void Logger::LogNotice(const std::wstring& message)
{
    Log(LogLevel::LOG_NOTICE_LEVEL, message);
}

void Logger::LogDebug(const std::wstring& message)
{
    Log(LogLevel::LOG_DEBUG_LEVEL, message);
}

void Logger::ParseAndLogConsoleOutput(const std::string& output)
{
    if (output.empty())
        return;

    std::istringstream stream(output);
    std::string line;

    while (std::getline(stream, line))
    {

        if (line.empty() || line.find_first_not_of(" \t\r\n") == std::string::npos)
            continue;

        ProcessConsoleLine(line);
    }
}

std::wstring Logger::FormatMessage(const std::wstring& message)
{
    return L"NodeJS: " + message;
}

std::wstring Logger::Utf8ToWideString(const std::string& utf8Str)
{
    if (utf8Str.empty())
        return L"";

    int wchars_num = MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, NULL, 0);
    if (wchars_num <= 0)
        return L"";

    std::wstring wstr(wchars_num - 1, 0);
    MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, &wstr[0], wchars_num);
    return wstr;
}

void Logger::ProcessConsoleLine(const std::string& line)
{

    std::wstring wideLine = Utf8ToWideString(line);

    if (line.find("Error:") != std::string::npos || 
        line.find("error:") != std::string::npos ||
        line.find("ERROR:") != std::string::npos)
    {
        LogError(wideLine);
    }
    else if (line.find("Warning:") != std::string::npos || 
             line.find("warning:") != std::string::npos ||
             line.find("WARNING:") != std::string::npos ||
             line.find("warn:") != std::string::npos)
    {
        LogWarning(wideLine);
    }
    else if (line.find("Debug:") != std::string::npos || 
             line.find("debug:") != std::string::npos ||
             line.find("DEBUG:") != std::string::npos)
    {
        LogDebug(wideLine);
    }
    else
    {

        LogNotice(wideLine);
    }
}