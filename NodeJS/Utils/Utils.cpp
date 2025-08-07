#include "Utils.h"
#include "../LogsFunctions/Logs.h"
#include <algorithm>

namespace Utils
{
    bool FindNodeExecutable(std::wstring& nodePath)
    {

        std::vector<std::wstring> possiblePaths = {
            L"node",  
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
                WaitForSingleObject(pi.hProcess, 1000); 
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

    std::wstring NormalizePath(const std::wstring& path)
    {
        std::wstring normalizedPath = path;
        std::replace(normalizedPath.begin(), normalizedPath.end(), L'\\', L'/');
        return normalizedPath;
    }
}