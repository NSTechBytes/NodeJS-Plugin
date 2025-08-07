#include "ParserFunctionality.h"
#include <algorithm>
#include <cwctype>

namespace ParserFunctionality
{
    std::wstring ParseAndBuildFunctionCall(const std::wstring& input)
    {

        std::wstring trimmed = input;
        size_t start = trimmed.find_first_not_of(L" \t\r\n");
        if (start == std::wstring::npos) return L"";

        size_t end = trimmed.find_last_not_of(L" \t\r\n");
        trimmed = trimmed.substr(start, end - start + 1);

        if (trimmed.find(L'(') != std::wstring::npos && trimmed.find(L')') != std::wstring::npos)
        {
            return trimmed;
        }

        size_t spacePos = trimmed.find(L' ');
        std::wstring functionName;
        std::wstring parameters;

        if (spacePos == std::wstring::npos)
        {

            functionName = trimmed;
            return functionName + L"()";
        }
        else
        {

            functionName = trimmed.substr(0, spacePos);
            parameters = trimmed.substr(spacePos + 1);

            std::vector<std::wstring> params = ParseParameters(parameters);

            std::wstring functionCall = functionName + L"(";
            for (size_t i = 0; i < params.size(); ++i)
            {
                if (i > 0) functionCall += L", ";

                if (IsNumeric(params[i]))
                {
                    functionCall += params[i];
                }
                else if (params[i] == L"true" || params[i] == L"false")
                {

                    functionCall += params[i];
                }
                else if (params[i] == L"null" || params[i] == L"undefined")
                {

                    functionCall += params[i];
                }
                else
                {

                    if (params[i].front() != L'"' && params[i].front() != L'\'')
                    {
                        functionCall += L"\"" + EscapeString(params[i]) + L"\"";
                    }
                    else
                    {
                        functionCall += params[i];
                    }
                }
            }
            functionCall += L")";

            return functionCall;
        }
    }

    std::vector<std::wstring> ParseParameters(const std::wstring& paramString)
    {
        std::vector<std::wstring> params;
        std::wstring current;
        bool inQuotes = false;
        wchar_t quoteChar = L'\0';

        for (size_t i = 0; i < paramString.length(); ++i)
        {
            wchar_t c = paramString[i];

            if (!inQuotes && (c == L'"' || c == L'\''))
            {

                inQuotes = true;
                quoteChar = c;
                current += c;
            }
            else if (inQuotes && c == quoteChar)
            {

                if (i > 0 && paramString[i - 1] == L'\\')
                {
                    current += c; 
                }
                else
                {
                    inQuotes = false;
                    current += c;
                }
            }
            else if (!inQuotes && (c == L' ' || c == L'\t'))
            {

                if (!current.empty())
                {
                    params.push_back(current);
                    current.clear();
                }
            }
            else
            {
                current += c;
            }
        }

        if (!current.empty())
        {
            params.push_back(current);
        }

        return params;
    }

    bool IsNumeric(const std::wstring& str)
    {
        if (str.empty()) return false;

        size_t start = 0;
        if (str[0] == L'-' || str[0] == L'+') start = 1;
        if (start >= str.length()) return false;

        bool hasDecimal = false;
        for (size_t i = start; i < str.length(); ++i)
        {
            if (str[i] == L'.')
            {
                if (hasDecimal) return false; 
                hasDecimal = true;
            }
            else if (!std::iswdigit(str[i]))
            {
                return false;
            }
        }

        return true;
    }

    std::wstring EscapeString(const std::wstring& str)
    {
        std::wstring escaped;
        for (wchar_t c : str)
        {
            switch (c)
            {
            case L'"':  escaped += L"\\\""; break;
            case L'\\': escaped += L"\\\\"; break;
            case L'\n': escaped += L"\\n"; break;
            case L'\r': escaped += L"\\r"; break;
            case L'\t': escaped += L"\\t"; break;
            default:    escaped += c; break;
            }
        }
        return escaped;
    }
}