#pragma once

#include <Windows.h>
#include <string>

namespace ScriptExecutor
{

    std::wstring ExecuteNodeCommand(const std::wstring& nodeExe, 
                                   const std::wstring& scriptPath, 
                                   const std::wstring& inlineScript, 
                                   bool useInline, 
                                   const std::wstring& command);

    std::string CreateInlineScriptWrapper(const std::wstring& inlineScript, const std::wstring& command);

    std::wstring CreateFileScriptWrapper(const std::wstring& scriptPath, const std::wstring& command);

    std::wstring ProcessStdoutOutput(const std::string& stdoutOutput);

    bool CreateTempFile(const std::string& content, std::wstring& tempFilePath);

    std::string ReadFromPipe(HANDLE hPipe);
}