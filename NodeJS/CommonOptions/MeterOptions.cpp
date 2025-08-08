#include "MeterOptions.h"
#include "../../API/RainmeterAPI.h"
#include "../LogsFunctions/Logs.h"
#include "../Utils/Utils.h"

std::wstring MeterGetX(void* rm, const std::wstring& meterName, const std::wstring& defValue)
{
    if (meterName.empty())
    {
        return defValue;
    }

    std::wstring varStr = L"[" + meterName + L":X]";
    LPCWSTR replaced = RmReplaceVariables(rm, varStr.c_str());

    if (!replaced || wcslen(replaced) == 0 || wcscmp(replaced, varStr.c_str()) == 0)
    {
        return defValue;
    }

    return std::wstring(replaced);
}

std::wstring MeterGetY(void* rm, const std::wstring& meterName, const std::wstring& defValue)
{
    if (meterName.empty())
    {
        return defValue;
    }

    std::wstring varStr = L"[" + meterName + L":Y]";
    LPCWSTR replaced = RmReplaceVariables(rm, varStr.c_str());

    if (!replaced || wcslen(replaced) == 0 || wcscmp(replaced, varStr.c_str()) == 0)
    {
        return defValue;
    }

    return std::wstring(replaced);
}

std::wstring MeterGetW(void* rm, const std::wstring& meterName, const std::wstring& defValue)
{
    if (meterName.empty())
    {
        return defValue;
    }

    std::wstring varStr = L"[" + meterName + L":W]";
    LPCWSTR replaced = RmReplaceVariables(rm, varStr.c_str());

    if (!replaced || wcslen(replaced) == 0 || wcscmp(replaced, varStr.c_str()) == 0)
    {
        return defValue;
    }

    return std::wstring(replaced);
}

std::wstring MeterGetH(void* rm, const std::wstring& meterName, const std::wstring& defValue)
{
    if (meterName.empty())
    {
        return defValue;
    }

    std::wstring varStr = L"[" + meterName + L":H]";
    LPCWSTR replaced = RmReplaceVariables(rm, varStr.c_str());

    if (!replaced || wcslen(replaced) == 0 || wcscmp(replaced, varStr.c_str()) == 0)
    {
        return defValue;
    }

    return std::wstring(replaced);
}

bool MeterSetX(void* skin, const std::wstring& meterName, const std::wstring& value)
{
    if (meterName.empty() || value.empty())
    {
        Logger::LogWarning(L"MeterSetX: Invalid meter name or value");
        return false;
    }

    std::wstring command = L"[!SetOption " + meterName + L" X " + value +
                          L"][!UpdateMeter " + meterName + L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"MeterSetX: Set " + meterName + L" X to " + value);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"MeterSetX: Failed to execute command for " + meterName);
        return false;
    }
}

bool MeterSetY(void* skin, const std::wstring& meterName, const std::wstring& value)
{
    if (meterName.empty() || value.empty())
    {
        Logger::LogWarning(L"MeterSetY: Invalid meter name or value");
        return false;
    }

    std::wstring command = L"[!SetOption " + meterName + L" Y " + value +
                          L"][!UpdateMeter " + meterName + L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"MeterSetY: Set " + meterName + L" Y to " + value);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"MeterSetY: Failed to execute command for " + meterName);
        return false;
    }
}

bool MeterSetW(void* skin, const std::wstring& meterName, const std::wstring& value)
{
    if (meterName.empty() || value.empty())
    {
        Logger::LogWarning(L"MeterSetW: Invalid meter name or value");
        return false;
    }

    std::wstring command = L"[!SetOption " + meterName + L" W " + value +
                          L"][!UpdateMeter " + meterName + L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"MeterSetW: Set " + meterName + L" W to " + value);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"MeterSetW: Failed to execute command for " + meterName);
        return false;
    }
}

bool MeterSetH(void* skin, const std::wstring& meterName, const std::wstring& value)
{
    if (meterName.empty() || value.empty())
    {
        Logger::LogWarning(L"MeterSetH: Invalid meter name or value");
        return false;
    }

    std::wstring command = L"[!SetOption " + meterName + L" H " + value +
                          L"][!UpdateMeter " + meterName + L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"MeterSetH: Set " + meterName + L" H to " + value);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"MeterSetH: Failed to execute command for " + meterName);
        return false;
    }
}

bool ShowMeter(void* skin, const std::wstring& meterName)
{
    if (meterName.empty())
    {
        Logger::LogWarning(L"ShowMeter: Invalid meter name");
        return false;
    }

    std::wstring command = L"[!ShowMeter " + meterName +
                          L"][!UpdateMeter " + meterName +
                          L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"ShowMeter: Showed " + meterName);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"ShowMeter: Failed to execute command for " + meterName);
        return false;
    }
}

bool HideMeter(void* skin, const std::wstring& meterName)
{
    if (meterName.empty())
    {
        Logger::LogWarning(L"HideMeter: Invalid meter name");
        return false;
    }

    std::wstring command = L"[!HideMeter " + meterName +
                          L"][!UpdateMeter " + meterName +
                          L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"HideMeter: Hid " + meterName);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"HideMeter: Failed to execute command for " + meterName);
        return false;
    }
}

/*std::wstring GetMeterProperty(void* rm, const std::wstring& meterName, const std::wstring& property, const std::wstring& defValue)
{
    if (meterName.empty() || property.empty())
    {
        return defValue;
    }

    std::wstring varStr = L"[" + meterName + L":" + property + L"]";
    LPCWSTR replaced = RmReplaceVariables(rm, varStr.c_str());

    if (!replaced || wcslen(replaced) == 0 || wcscmp(replaced, varStr.c_str()) == 0)
    {
        return defValue;
    }

    return std::wstring(replaced);
}*/

bool SetMeterProperty(void* skin, const std::wstring& meterName, const std::wstring& property, const std::wstring& value)
{
    if (meterName.empty() || property.empty() || value.empty())
    {
        Logger::LogWarning(L"SetMeterProperty: Invalid parameters - meter: " + meterName + L", property: " + property + L", value: " + value);
        return false;
    }

    std::wstring command = L"[!SetOption " + meterName + L" " + property + L" " + value +
                          L"][!UpdateMeter " + meterName + L"][!Redraw]";

    try
    {
        RmExecute(skin, command.c_str());
        Logger::LogDebug(L"SetMeterProperty: Set " + meterName + L" " + property + L" to " + value);
        return true;
    }
    catch (...)
    {
        Logger::LogError(L"SetMeterProperty: Failed to execute command for " + meterName + L"." + property);
        return false;
    }
}