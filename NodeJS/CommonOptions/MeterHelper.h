#pragma once

#include <Windows.h>
#include <string>
#include <vector>

struct Measure;

namespace MeterHelper 
{
    std::wstring ExecuteMeterFunction(Measure* measure, const std::wstring& functionCall);
    std::vector<std::wstring> ParseFunctionParameters(const std::wstring& params);
    bool IsMeterFunction(const std::wstring& command);
    std::vector<std::wstring> GetSupportedMeterFunctions();
}