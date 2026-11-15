#pragma once
inline LRESULT CALLBACK ExactWindowProc(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_PAINT) {
        PAINTSTRUCT paint{}; HDC dc = BeginPaint(hwnd, &paint);
        RECT client{}; GetClientRect(hwnd, &client);
        HBRUSH brush = CreateSolidBrush(RGB(18, 52, 86)); FillRect(dc, &client, brush); DeleteObject(brush);
        EndPaint(hwnd, &paint); return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wp, lp);
}
inline int RunExactWindowHost() {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WNDCLASSW cls{}; cls.lpfnWndProc = ExactWindowProc; cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = L"VNotchExactWindowFixture"; RegisterClassW(&cls);
    const std::wstring title = L"VNotch exact GPU test " + std::to_wstring(GetCurrentProcessId());
    HWND hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOACTIVATE, cls.lpszClassName, title.c_str(), WS_POPUP,
        100, 100, 256, 128, nullptr, nullptr, cls.hInstance, nullptr);
    if (!hwnd) return 2;
    ShowWindow(hwnd, SW_SHOWNOACTIVATE); UpdateWindow(hwnd);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
inline void VerifyExactWindows(GpuGlass& gpu, HWND selfWindow) {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    wchar_t executable[32768]{}; GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    std::wstring command = L"\"" + std::wstring(executable) + L"\" --exact-window-host";
    STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION child{};
    Require(CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child), "Cannot create controlled WGC source");
    struct Process {
        PROCESS_INFORMATION child;
        ~Process() { if (WaitForSingleObject(child.hProcess, 0) != WAIT_OBJECT_0) TerminateProcess(child.hProcess, 0); CloseHandle(child.hThread); CloseHandle(child.hProcess); }
    } process{child};
    const std::wstring title = L"VNotch exact GPU test " + std::to_wstring(child.dwProcessId);
    HWND source = nullptr;
    ULONGLONG deadline = GetTickCount64() + 5000;
    while (!source && GetTickCount64() < deadline) { source = FindWindowW(L"VNotchExactWindowFixture", title.c_str()); Sleep(10); }
    Require(source != nullptr, "Controlled WGC window did not appear");
    // The probe's own window covers the sample. Exact capture must omit it.
    SetWindowPos(selfWindow, HWND_TOPMOST, 132, 132, 64, 32, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    UpdateWindow(selfWindow); DwmFlush();
    Frame frame{}; frame.x = 140; frame.y = 140; frame.width = 32; frame.height = 16;
    HRESULT hr = E_PENDING;
    deadline = GetTickCount64() + 10000;
    while (GetTickCount64() < deadline) {
        hr = gpu.RenderWindows(frame);
        if (hr == S_OK) break;
        Require(hr == E_PENDING || hr == S_FALSE, "Exact capture failed"); Sleep(10);
    }
    Require(hr == S_OK, "Exact WGC composition never became ready");
    UINT output = gpu.nextOutput;
    Check(gpu.WaitReady(1000));
    ComPtr<IDirect3DSurface9> ready;
    deadline = GetTickCount64() + 5000;
    while ((hr = gpu.TakeReady(&ready)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
    Require(hr == S_OK, "Exact window frame did not finish");
    D3D11_TEXTURE2D_DESC desc{}; gpu.output[output].texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.MiscFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> readback; Check(gpu.device->CreateTexture2D(&desc, nullptr, &readback));
    gpu.context->CopyResource(readback.Get(), gpu.output[output].texture.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{}; Check(gpu.context->Map(readback.Get(), 0, D3D11_MAP_READ, 0, &mapped));
    bool same = true;
    for (int y = 0; y < frame.height; ++y) for (int x = 0; x < frame.width; ++x) {
        const auto pixel = reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(mapped.pData) + y * mapped.RowPitch)[x];
        for (int shift = 0; shift < 24; shift += 8)
            same &= abs(static_cast<int>((pixel >> shift) & 255) - static_cast<int>((0xff123456u >> shift) & 255)) <= 1;
    }
    gpu.context->Unmap(readback.Get(), 0);
    Require(same, "Exact WGC composition sampled the notch or wrong source coordinates");
    SetWindowLongPtrW(source, GWL_EXSTYLE, GetWindowLongPtrW(source, GWL_EXSTYLE) | WS_EX_LAYERED);
    SetLayeredWindowAttributes(source, 0, 128, LWA_ALPHA);
    Require(gpu.RenderWindows(frame) == E_PENDING, "Translucent source did not select exact CPU fallback");
    deadline = GetTickCount64() + 2200;
    while (GetTickCount64() < deadline) { gpu.RenderWindows(frame); Sleep(20); }
    SetWindowLongPtrW(source, GWL_EXSTYLE, GetWindowLongPtrW(source, GWL_EXSTYLE) & ~WS_EX_LAYERED);
    InvalidateRect(source, nullptr, FALSE);
    deadline = GetTickCount64() + 5000;
    while ((hr = gpu.RenderWindows(frame)) != S_OK && GetTickCount64() < deadline) {
        Require(hr == E_PENDING || hr == S_FALSE, "Exact capture failed after idle eviction"); Sleep(10);
    }
    Require(hr == S_OK, "Exact capture did not recover its shared device after idle eviction");
    Check(gpu.WaitReady(1000));
    ready.Reset();
    Require(gpu.TakeReady(&ready) == S_OK, "Recovered exact frame did not finish");
    PostMessageW(source, WM_CLOSE, 0, 0);
    ShowWindow(selfWindow, SW_HIDE);
    gpu.ResetCapture();
    printf("PASS: live exact WGC composition excludes this process, falls back for translucent layers and recovers after idle eviction.\n");
}
