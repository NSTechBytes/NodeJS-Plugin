#include "MeterHelper.h"
#include "../../API/RainmeterAPI.h"
#include "../LogsFunctions/Logs.h"
#include "../CommonOptions/MeterOptions.h"
#include <sstream>
#include <algorithm>

struct Measure
{
    std::wstring scriptPath;
    std::wstring inlineScript;
    std::wstring nodeExecutable;
    std::wstring lastResult;
    bool nodeFound;
    bool initialized;
    bool useInlineScript;
    void* rm;          
    void* skin;        
    PROCESS_INFORMATION processInfo;
    HANDLE hInputWrite;
    HANDLE hOutputRead;
};

namespace MeterHelper 
{
    std::vector<std::wstring> GetSupportedMeterFunctions()
    {
        return {
            L"MeterOption.GetX", L"MeterOption.GetY", L"MeterOption.GetW", L"MeterOption.GetH",
            L"MeterOption.SetX", L"MeterOption.SetY", L"MeterOption.SetW", L"MeterOption.SetH",
            L"MeterOption.Show", L"MeterOption.Hide", L"MeterOption.SetProperty"
        };
    }

    bool IsMeterFunction(const std::wstring& command)
    {
        return command.find(L"MeterOption.") == 0;
    }

    std::vector<std::wstring> ParseFunctionParameters(const std::wstring& params)
    {
        std::vector<std::wstring> paramList;
        std::wstringstream ss(params);
        std::wstring param;

        while (std::getline(ss, param, L','))
        {
            // Remove leading/trailing whitespace and quotes
            param.erase(0, param.find_first_not_of(L" \t'\""));
            param.erase(param.find_last_not_of(L" \t'\"") + 1);
            if (!param.empty())
                paramList.push_back(param);
        }

        return paramList;
    }

    std::wstring ExecuteMeterFunction(Measure* measure, const std::wstring& functionCall)
    {
        if (!measure || !measure->rm || !measure->skin)
        {
            Logger::LogError(L"Meter operations not available: measure, rm or skin not initialized");
            return L"false";
        }

        size_t openParen = functionCall.find(L'(');
        if (openParen == std::wstring::npos)
        {
            Logger::LogError(L"Invalid function call format: " + functionCall);
            return L"false";
        }

        std::wstring funcName = functionCall.substr(0, openParen);
        std::wstring params = functionCall.substr(openParen + 1);

        if (!params.empty() && params.back() == L')')
            params.pop_back();

        std::vector<std::wstring> paramList = ParseFunctionParameters(params);

        try 
        {
            if (funcName == L"MeterOption.GetX")
            {
                if (paramList.size() >= 1)
                {
                    std::wstring defVal = paramList.size() >= 2 ? paramList[1] : L"";
                    return MeterGetX(measure->rm, paramList[0], defVal);
                }
            }
            else if (funcName == L"MeterOption.GetY")
            {
                if (paramList.size() >= 1)
                {
                    std::wstring defVal = paramList.size() >= 2 ? paramList[1] : L"";
                    return MeterGetY(measure->rm, paramList[0], defVal);
                }
            }
            else if (funcName == L"MeterOption.GetW")
            {
                if (paramList.size() >= 1)
                {
                    std::wstring defVal = paramList.size() >= 2 ? paramList[1] : L"";
                    return MeterGetW(measure->rm, paramList[0], defVal);
                }
            }
            else if (funcName == L"MeterOption.GetH")
            {
                if (paramList.size() >= 1)
                {
                    std::wstring defVal = paramList.size() >= 2 ? paramList[1] : L"";
                    return MeterGetH(measure->rm, paramList[0], defVal);
                }
            }
            else if (funcName == L"MeterOption.SetX")
            {
                if (paramList.size() >= 2)
                {
                    bool result = MeterSetX(measure->skin, paramList[0], paramList[1]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.SetY")
            {
                if (paramList.size() >= 2)
                {
                    bool result = MeterSetY(measure->skin, paramList[0], paramList[1]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.SetW")
            {
                if (paramList.size() >= 2)
                {
                    bool result = MeterSetW(measure->skin, paramList[0], paramList[1]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.SetH")
            {
                if (paramList.size() >= 2)
                {
                    bool result = MeterSetH(measure->skin, paramList[0], paramList[1]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.Show")
            {
                if (paramList.size() >= 1)
                {
                    bool result = ShowMeter(measure->skin, paramList[0]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.Hide")
            {
                if (paramList.size() >= 1)
                {
                    bool result = HideMeter(measure->skin, paramList[0]);
                    return result ? L"true" : L"false";
                }
            }
            else if (funcName == L"MeterOption.SetProperty")
            {
                if (paramList.size() >= 3)
                {
                    bool result = SetMeterProperty(measure->skin, paramList[0], paramList[1], paramList[2]);
                    return result ? L"true" : L"false";
                }
            }
        }
        catch (const std::exception& e)
        {
            Logger::LogError(L"Exception in meter function execution: " + std::wstring(e.what(), e.what() + strlen(e.what())));
            return L"false";
        }
        catch (...)
        {
            Logger::LogError(L"Unknown exception in meter function execution");
            return L"false";
        }

        Logger::LogError(L"Unknown meter function or invalid parameters: " + functionCall);
        return L"false";
    }
}