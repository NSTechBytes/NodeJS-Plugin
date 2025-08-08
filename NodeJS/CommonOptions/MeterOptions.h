#ifndef METEROPTIONS_H
#define METEROPTIONS_H

#include <Windows.h>
#include <string>

// Remove C linkage since we're using C++ std::wstring
std::wstring MeterGetX(void* rm, const std::wstring& meterName, const std::wstring& defValue = L"");
std::wstring MeterGetY(void* rm, const std::wstring& meterName, const std::wstring& defValue = L"");
std::wstring MeterGetW(void* rm, const std::wstring& meterName, const std::wstring& defValue = L"");
std::wstring MeterGetH(void* rm, const std::wstring& meterName, const std::wstring& defValue = L"");

bool MeterSetX(void* skin, const std::wstring& meterName, const std::wstring& value);
bool MeterSetY(void* skin, const std::wstring& meterName, const std::wstring& value);
bool MeterSetW(void* skin, const std::wstring& meterName, const std::wstring& value);
bool MeterSetH(void* skin, const std::wstring& meterName, const std::wstring& value);

bool ShowMeter(void* skin, const std::wstring& meterName);
bool HideMeter(void* skin, const std::wstring& meterName);

//std::wstring GetMeterProperty(void* rm, const std::wstring& meterName, const std::wstring& property, const std::wstring& defValue = L"");
bool SetMeterProperty(void* skin, const std::wstring& meterName, const std::wstring& property, const std::wstring& value);

#endif