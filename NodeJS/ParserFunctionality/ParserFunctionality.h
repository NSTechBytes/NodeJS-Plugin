#pragma once

#include <Windows.h>
#include <string>
#include <vector>

namespace ParserFunctionality
{
    std::wstring ParseAndBuildFunctionCall(const std::wstring& input);

    std::vector<std::wstring> ParseParameters(const std::wstring& paramString);

    bool IsNumeric(const std::wstring& str);

    std::wstring EscapeString(const std::wstring& str);
}