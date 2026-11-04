#include "vnotch_native.h"
#include <oledb.h>
#include <msdasc.h>
#include <wrl/client.h>

#pragma comment(lib, "oledb.lib")
#pragma comment(lib, "uuid.lib")
#pragma comment(lib, "oleaut32.lib")

using Microsoft::WRL::ComPtr;

namespace {
struct SearchResult { BSTR name, path; int folder; };
struct Column { BSTR value; DBSTATUS status; };
struct Apartment {
    HRESULT status = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~Apartment() { if (SUCCEEDED(status)) CoUninitialize(); }
};
}

// Successful rows transfer ownership of both BSTRs, including on a later error.
VN_EXPORT HRESULT vn_windows_search_execute(const wchar_t* sql, HANDLE cancel,
    SearchResult* output, int capacity, int* count) noexcept
{
    if (!sql || !cancel || !output || capacity <= 0 || !count) return E_INVALIDARG;
    *count = 0;
    Apartment apartment;
    if (FAILED(apartment.status) && apartment.status != RPC_E_CHANGED_MODE) return apartment.status;
    ComPtr<IDataInitialize> initializer;
    HRESULT hr = CoCreateInstance(CLSID_MSDAINITIALIZE, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&initializer));
    if (FAILED(hr)) return hr;
    ComPtr<IDBInitialize> source;
    hr = initializer->GetDataSource(nullptr, CLSCTX_INPROC_SERVER,
        L"Provider=Search.CollatorDSO;Extended Properties='Application=Windows'", IID_IDBInitialize, reinterpret_cast<IUnknown**>(source.GetAddressOf()));
    if (FAILED(hr) || FAILED(hr = source->Initialize())) return hr;
    ComPtr<IDBCreateSession> sessions;
    ComPtr<IDBCreateCommand> commands;
    ComPtr<ICommandText> command;
    if (FAILED(hr = source.As(&sessions)) ||
        FAILED(hr = sessions->CreateSession(nullptr, IID_IDBCreateCommand, reinterpret_cast<IUnknown**>(commands.GetAddressOf()))) ||
        FAILED(hr = commands->CreateCommand(nullptr, IID_ICommandText, reinterpret_cast<IUnknown**>(command.GetAddressOf()))) ||
        FAILED(hr = command->SetCommandText(DBGUID_DEFAULT, sql))) return hr;

    ComPtr<ICommandProperties> properties;
    if (SUCCEEDED(command.As(&properties))) {
        DBPROP timeout{};
        timeout.dwPropertyID = DBPROP_COMMANDTIMEOUT;
        timeout.dwOptions = DBPROPOPTIONS_OPTIONAL;
        timeout.vValue.vt = VT_I4;
        timeout.vValue.lVal = 1;
        DBPROPSET set{ &timeout, 1, DBPROPSET_ROWSET };
        properties->SetProperties(1, &set);
    }
    if (WaitForSingleObject(cancel, 0) == WAIT_OBJECT_0) return E_ABORT;
    ComPtr<IRowset> rows;
    if (FAILED(hr = command->Execute(nullptr, IID_IRowset, nullptr, nullptr,
        reinterpret_cast<IUnknown**>(rows.GetAddressOf())))) return hr;
    ComPtr<IAccessor> accessor;
    if (FAILED(hr = rows.As(&accessor))) return hr;
    DBBINDING bindings[3]{};
    for (size_t i = 0; i < 3; ++i) {
        auto& b = bindings[i];
        b.iOrdinal = i + 1;
        b.obValue = i * sizeof(Column) + offsetof(Column, value);
        b.obStatus = i * sizeof(Column) + offsetof(Column, status);
        b.dwPart = DBPART_VALUE | DBPART_STATUS;
        b.wType = DBTYPE_BSTR;
        b.dwMemOwner = DBMEMOWNER_CLIENTOWNED;
    }
    HACCESSOR handle{};
    if (FAILED(hr = accessor->CreateAccessor(DBACCESSOR_ROWDATA, 3, bindings,
        3 * sizeof(Column), &handle, nullptr))) return hr;
    while (*count < capacity) {
        if (WaitForSingleObject(cancel, 0) == WAIT_OBJECT_0) { hr = E_ABORT; break; }
        HROW row{};
        HROW* buffer = &row;
        DBCOUNTITEM fetched{};
        hr = rows->GetNextRows(DB_NULL_HCHAPTER, 0, 1, &fetched, &buffer);
        if (FAILED(hr) || !fetched) break;
        Column cols[3]{};
        hr = rows->GetData(row, handle, cols);
        rows->ReleaseRows(1, &row, nullptr, nullptr, nullptr);
        if (SUCCEEDED(hr) && cols[0].status == DBSTATUS_S_OK && cols[1].status == DBSTATUS_S_OK && cols[0].value && cols[1].value) {
            output[(*count)++] = { cols[0].value, cols[1].value,
                cols[2].status == DBSTATUS_S_OK && cols[2].value && !_wcsicmp(cols[2].value, L"Directory") };
            cols[0].value = cols[1].value = nullptr;
        }
        for (auto& col : cols) SysFreeString(col.value);
        if (FAILED(hr)) break;
    }
    accessor->ReleaseAccessor(handle, nullptr);
    return hr;
}
