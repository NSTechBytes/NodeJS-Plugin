#pragma once

#include <Windows.h>
#include <string>
#include <vector>

namespace Utils
{
    /**
     * Searches for Node.js executable in common installation paths
     * Tries PATH first, then standard installation directories
     * 
     * @param nodePath Reference to string that will receive the found Node.js path
     * @return true if Node.js executable was found, false otherwise
     */
    bool FindNodeExecutable(std::wstring& nodePath);

    /**
     * Executes a Node.js command with enhanced console output handling
     * Supports both inline scripts and script files
     * Captures stdout, stderr, and return values separately
     * 
     * @param nodeExe Path to Node.js executable
     * @param scriptPath Path to script file (if using file-based script)
     * @param inlineScript Inline script content (if using inline script)
     * @param useInline true to use inline script, false to use script file
     * @param command JavaScript command/function to execute
     * @return String result from the executed command
     */
    std::wstring ExecuteNodeCommand(const std::wstring& nodeExe, 
                                   const std::wstring& scriptPath, 
                                   const std::wstring& inlineScript, 
                                   bool useInline, 
                                   const std::wstring& command);

    /**
     * Creates a temporary file with the given content
     * Used for inline script execution
     * 
     * @param content Content to write to temporary file
     * @param tempFilePath Reference to string that will receive the temp file path
     * @return true if temporary file was created successfully
     */
    bool CreateTempFile(const std::string& content, std::wstring& tempFilePath);

    /**
     * Converts wide string to UTF-8 encoded string
     * 
     * @param wideStr Wide string to convert
     * @return UTF-8 encoded string
     */
    std::string WideStringToUtf8(const std::wstring& wideStr);

    /**
     * Converts UTF-8 encoded string to wide string
     * 
     * @param utf8Str UTF-8 string to convert
     * @return Wide string
     */
    std::wstring Utf8ToWideString(const std::string& utf8Str);

    /**
     * Reads output from a pipe handle
     * Used for capturing stdout/stderr from child processes
     * 
     * @param hPipe Handle to the pipe to read from
     * @return String containing the read data
     */
    std::string ReadFromPipe(HANDLE hPipe);

    /**
     * Normalizes file path by replacing backslashes with forward slashes
     * Makes paths JavaScript-friendly
     * 
     * @param path Path to normalize
     * @return Normalized path string
     */
    std::wstring NormalizePath(const std::wstring& path);

    /**
     * Creates JavaScript wrapper code for inline scripts
     * Adds console overrides and command execution logic
     * 
     * @param inlineScript The inline script content
     * @param command The command to execute
     * @return Complete JavaScript wrapper code
     */
    std::string CreateInlineScriptWrapper(const std::wstring& inlineScript, const std::wstring& command);

    /**
     * Creates JavaScript wrapper code for file-based scripts
     * Adds console overrides and command execution logic
     * 
     * @param scriptPath Path to the script file
     * @param command The command to execute
     * @return Complete JavaScript wrapper code
     */
    std::wstring CreateFileScriptWrapper(const std::wstring& scriptPath, const std::wstring& command);

    /**
     * Processes stdout output to extract results and log messages
     * Separates RESULT: prefixed lines from console output
     * 
     * @param stdoutOutput Raw stdout output string
     * @return Extracted result string
     */
    std::wstring ProcessStdoutOutput(const std::string& stdoutOutput);
}