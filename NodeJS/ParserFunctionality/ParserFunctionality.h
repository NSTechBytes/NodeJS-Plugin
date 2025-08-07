#pragma once

#include <Windows.h>
#include <string>
#include <vector>

namespace ParserFunctionality
{
    /**
     * Parses input string and builds a proper JavaScript function call
     * Handles formats like:
     * - "FunctionName" -> "FunctionName()"
     * - "FunctionName param1 param2" -> "FunctionName(param1, param2)"
     * - "FunctionName(param1, param2)" -> returns as-is
     * 
     * @param input The input string to parse
     * @return Properly formatted JavaScript function call
     */
    std::wstring ParseAndBuildFunctionCall(const std::wstring& input);

    /**
     * Parses parameter string into individual parameters
     * Respects quoted strings and handles escape sequences
     * 
     * @param paramString String containing space-separated parameters
     * @return Vector of individual parameter strings
     */
    std::vector<std::wstring> ParseParameters(const std::wstring& paramString);

    /**
     * Checks if a string represents a numeric value
     * Supports integers and floating-point numbers with optional sign
     * 
     * @param str String to check
     * @return true if string represents a valid number
     */
    bool IsNumeric(const std::wstring& str);

    /**
     * Escapes special characters in a string for JavaScript compatibility
     * Handles quotes, backslashes, and control characters
     * 
     * @param str String to escape
     * @return Escaped string safe for JavaScript
     */
    std::wstring EscapeString(const std::wstring& str);
}