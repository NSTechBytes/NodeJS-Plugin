#pragma once

#include <Windows.h>
#include <string>
#include <vector>

namespace Utils
{

    bool FindNodeExecutable(std::wstring& nodePath);

    std::string WideStringToUtf8(const std::wstring& wideStr);

    std::wstring Utf8ToWideString(const std::string& utf8Str);

    std::wstring NormalizePath(const std::wstring& path);
}