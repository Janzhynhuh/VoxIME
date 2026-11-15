// VoxIME.NativePet —— 原生桌宠进程骨架。
//
// 许可说明：本文件是 VoxIME 自己的代码；它当前不包含 Live2D Cubism SDK 代码。
// Cubism Native SDK 由开发者从 Live2D 官方获取，放在 third_party/cubism-native/，
// 不随本仓库提交。后续接入官方 LAppModel/CubismDirectXRenderer 时，相关文件的
// 版权头与 LICENSE/NOTICE 必须一并保留。
//
// 当前实现验证：
//   - Win32 无边框、置顶、不抢焦点窗口；
//   - D3D11 flip swapchain + DirectComposition 逐像素透明合成；
//   - 命名管道控制通道；
//   - 父进程退出/崩溃时自动退出。
//
// 注意：这还不是最终桌宠；模型渲染在后续提交接入。

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <shellapi.h>
#include <wrl/client.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr wchar_t kWindowClass[] = L"VoxIME.NativePet.Window";
    constexpr wchar_t kWindowTitle[] = L"VoxIME Native Pet";
    constexpr UINT kMsgCommand = WM_APP + 1;

    struct Options
    {
        std::wstring PipeName;
        DWORD ParentPid = 0;
        int Width = 482;
        int Height = 600;
        int Monitor = 1; // 1-based；1=主屏，2=第二块屏
        bool DebugColor = false;
    };

    std::atomic<bool> g_clickThrough{true};
    std::atomic<bool> g_quit{false};
    Options g_options;
    HWND g_hwnd = nullptr;

    void Log(const wchar_t* message)
    {
        OutputDebugStringW(L"[VoxIME.NativePet] ");
        OutputDebugStringW(message);
        OutputDebugStringW(L"\n");
    }

    void LogHr(const wchar_t* what, HRESULT hr)
    {
        wchar_t buffer[256];
        swprintf_s(buffer, L"%s failed: 0x%08X", what, static_cast<unsigned>(hr));
        Log(buffer);
    }

    BOOL CALLBACK CollectMonitorProc(HMONITOR monitor, HDC, LPRECT, LPARAM data)
    {
        auto* workAreas = reinterpret_cast<std::vector<RECT>*>(data);
        MONITORINFO info{};
        info.cbSize = sizeof(info);
        if (GetMonitorInfoW(monitor, &info))
        {
            workAreas->push_back(info.rcWork);
        }
        return TRUE;
    }

    bool GetMonitorWorkArea(int oneBasedIndex, RECT& workArea)
    {
        std::vector<RECT> workAreas;
        EnumDisplayMonitors(nullptr, nullptr, CollectMonitorProc, reinterpret_cast<LPARAM>(&workAreas));

        if (oneBasedIndex < 1 || static_cast<size_t>(oneBasedIndex) > workAreas.size())
        {
            return false;
        }

        workArea = workAreas[static_cast<size_t>(oneBasedIndex - 1)];
        return true;
    }

    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int length = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (length <= 0)
        {
            return {};
        }

        std::string result(static_cast<size_t>(length - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), length, nullptr, nullptr);
        return result;
    }

    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int length = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
        if (length <= 0)
        {
            return {};
        }

        std::wstring result(static_cast<size_t>(length - 1), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, result.data(), length);
        return result;
    }

    std::wstring TrimAsciiWhitespace(const std::wstring& value)
    {
        size_t begin = 0;
        while (begin < value.size() && (value[begin] == L' ' || value[begin] == L'\t' || value[begin] == L'\r'))
        {
            ++begin;
        }

        size_t end = value.size();
        while (end > begin && (value[end - 1] == L' ' || value[end - 1] == L'\t' || value[end - 1] == L'\r'))
        {
            --end;
        }

        return value.substr(begin, end - begin);
    }

    bool SendPipeJson(HANDLE pipe, const std::string& json)
    {
        // 帧格式：uint32 little-endian 长度 + UTF-8 JSON 字节。
        const uint32_t length = static_cast<uint32_t>(json.size());
        DWORD written = 0;
        if (!WriteFile(pipe, &length, sizeof(length), &written, nullptr) || written != sizeof(length))
        {
            return false;
        }

        if (!json.empty()
            && (!WriteFile(pipe, json.data(), static_cast<DWORD>(json.size()), &written, nullptr)
                || written != json.size()))
        {
            return false;
        }

        return true;
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD size)
    {
        auto* cursor = static_cast<unsigned char*>(buffer);
        DWORD remaining = size;
        while (remaining > 0)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, cursor, remaining, &read, nullptr) || read == 0)
            {
                return false;
            }

            cursor += read;
            remaining -= read;
        }

        return true;
    }

    bool ReadPipeJson(HANDLE pipe, std::string& json)
    {
        uint32_t length = 0;
        if (!ReadExact(pipe, &length, sizeof(length)) || length == 0 || length > 64 * 1024)
        {
            return false;
        }

        json.resize(length);
        return ReadExact(pipe, json.data(), length);
    }

    void PipeServerThread(std::wstring pipeName)
    {
        while (!g_quit.load())
        {
            HANDLE pipe = CreateNamedPipeW(
                pipeName.c_str(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                4096,
                4096,
                0,
                nullptr);

            if (pipe == INVALID_HANDLE_VALUE)
            {
                Log(L"CreateNamedPipeW failed");
                return;
            }

            const BOOL connected = ConnectNamedPipe(pipe, nullptr) != FALSE
                || GetLastError() == ERROR_PIPE_CONNECTED;

            if (!connected)
            {
                CloseHandle(pipe);
                continue;
            }

            SendPipeJson(pipe, R"({"type":"ready","width":482,"height":600})");

            while (!g_quit.load())
            {
                std::string line;
                if (!ReadPipeJson(pipe, line))
                {
                    break;
                }

                const std::wstring command = TrimAsciiWhitespace(Utf8ToWide(line));
                if (command.empty())
                {
                    continue;
                }

                if (command == L"shutdown")
                {
                    PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
                }
                else if (command == L"show")
                {
                    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
                }
                else if (command == L"hide")
                {
                    ShowWindow(g_hwnd, SW_HIDE);
                }
                else if (command == L"ping")
                {
                    SendPipeJson(pipe, R"({"type":"pong"})");
                }
                else if (command.find(L"click-through ") == 0)
                {
                    g_clickThrough.store(command.find(L"true") != std::wstring::npos);
                }
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    }

    DWORD ParentWatchdogThread(DWORD parentPid)
    {
        if (parentPid == 0)
        {
            return 0;
        }

        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
        if (!parent)
        {
            Log(L"OpenProcess(parent) failed; exiting");
            PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
            return 0;
        }

        WaitForSingleObject(parent, INFINITE);
        CloseHandle(parent);
        if (!g_quit.load())
        {
            Log(L"parent exited; closing pet");
            PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
        }
        return 0;
    }

    struct Graphics
    {
        ComPtr<ID3D11Device> Device;
        ComPtr<ID3D11DeviceContext> Context;
        ComPtr<IDXGISwapChain1> SwapChain;
        ComPtr<ID3D11RenderTargetView> RenderTarget;
        ComPtr<IDCompositionDevice> Composition;
        ComPtr<IDCompositionTarget> CompositionTarget;
        ComPtr<IDCompositionVisual> Visual;
        int Width = 0;
        int Height = 0;

        bool Initialize(HWND hwnd, int width, int height)
        {
            Width = width;
            Height = height;

            UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#ifdef _DEBUG
            flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

            D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
            D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_11_0;
            HRESULT hr = D3D11CreateDevice(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                flags,
                levels,
                ARRAYSIZE(levels),
                D3D11_SDK_VERSION,
                &Device,
                &got,
                &Context);
            if (FAILED(hr))
            {
                LogHr(L"D3D11CreateDevice", hr);
                return false;
            }

            ComPtr<IDXGIDevice> dxgiDevice;
            hr = Device.As(&dxgiDevice);
            if (FAILED(hr))
            {
                LogHr(L"IDXGIDevice", hr);
                return false;
            }

            ComPtr<IDXGIAdapter> adapter;
            hr = dxgiDevice->GetAdapter(&adapter);
            if (FAILED(hr))
            {
                LogHr(L"GetAdapter", hr);
                return false;
            }

            ComPtr<IDXGIFactory2> factory;
            hr = adapter->GetParent(IID_PPV_ARGS(&factory));
            if (FAILED(hr))
            {
                LogHr(L"IDXGIFactory2", hr);
                return false;
            }

            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = static_cast<UINT>(width);
            desc.Height = static_cast<UINT>(height);
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.Stereo = FALSE;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.Scaling = DXGI_SCALING_STRETCH;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
            desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;

            hr = factory->CreateSwapChainForComposition(Device.Get(), &desc, nullptr, &SwapChain);
            if (FAILED(hr))
            {
                LogHr(L"CreateSwapChainForComposition", hr);
                return false;
            }

            ComPtr<ID3D11Texture2D> backBuffer;
            hr = SwapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
            if (FAILED(hr))
            {
                LogHr(L"SwapChain GetBuffer", hr);
                return false;
            }

            hr = Device->CreateRenderTargetView(backBuffer.Get(), nullptr, &RenderTarget);
            if (FAILED(hr))
            {
                LogHr(L"CreateRenderTargetView", hr);
                return false;
            }

            hr = DCompositionCreateDevice(dxgiDevice.Get(), IID_PPV_ARGS(&Composition));
            if (FAILED(hr))
            {
                LogHr(L"DCompositionCreateDevice", hr);
                return false;
            }

            hr = Composition->CreateTargetForHwnd(hwnd, TRUE, &CompositionTarget);
            if (FAILED(hr))
            {
                LogHr(L"CreateTargetForHwnd", hr);
                return false;
            }

            hr = Composition->CreateVisual(&Visual);
            if (FAILED(hr))
            {
                LogHr(L"CreateVisual", hr);
                return false;
            }

            hr = Visual->SetContent(SwapChain.Get());
            if (FAILED(hr))
            {
                LogHr(L"Visual SetContent", hr);
                return false;
            }

            hr = CompositionTarget->SetRoot(Visual.Get());
            if (FAILED(hr))
            {
                LogHr(L"SetRoot", hr);
                return false;
            }

            hr = Composition->Commit();
            if (FAILED(hr))
            {
                LogHr(L"Composition Commit", hr);
                return false;
            }

            return true;
        }

        void Render(bool debugColor)
        {
            if (!Context || !RenderTarget || !SwapChain)
            {
                return;
            }

            float clear[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
            if (debugColor)
            {
                clear[0] = 0.10f;
                clear[1] = 0.35f;
                clear[2] = 0.75f;
                clear[3] = 0.35f;
            }
            Context->ClearRenderTargetView(RenderTarget.Get(), clear);
            SwapChain->Present(1, 0);
        }
    };

    LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
        case WM_NCHITTEST:
            if (g_clickThrough.load())
            {
                return HTTRANSPARENT;
            }
            return HTCLIENT;

        case WM_ERASEBKGND:
            return 1;

        case WM_CLOSE:
            g_quit.store(true);
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            g_hwnd = nullptr;
            PostQuitMessage(0);
            return 0;

        case WM_COMMAND:
            return 0;

        default:
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }
}

int wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    // 桌宠窗口按物理像素创建，避免 125%/150% DPI 下 DComp 缓冲被二次缩放。
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    {
        SetProcessDPIAware();
    }

    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv)
    {
        return 1;
    }

    for (int index = 1; index < argc; ++index)
    {
        const std::wstring arg = argv[index];
        if (arg == L"--pipe" && index + 1 < argc)
        {
            g_options.PipeName = argv[++index];
        }
        else if (arg == L"--parent-pid" && index + 1 < argc)
        {
            g_options.ParentPid = static_cast<DWORD>(_wtoi(argv[++index]));
        }
        else if (arg == L"--width" && index + 1 < argc)
        {
            g_options.Width = _wtoi(argv[++index]);
        }
        else if (arg == L"--height" && index + 1 < argc)
        {
            g_options.Height = _wtoi(argv[++index]);
        }
        else if (arg == L"--monitor" && index + 1 < argc)
        {
            g_options.Monitor = _wtoi(argv[++index]);
        }
        else if (arg == L"--debug-color")
        {
            g_options.DebugColor = true;
        }
    }
    LocalFree(argv);

    const HINSTANCE module = instance;
    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.style = CS_HREDRAW | CS_VREDRAW;
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = module;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.lpszClassName = kWindowClass;
    if (!RegisterClassExW(&windowClass))
    {
        Log(L"RegisterClassExW failed");
        return 2;
    }

    const int width = g_options.Width > 0 ? g_options.Width : 482;
    const int height = g_options.Height > 0 ? g_options.Height : 600;

    RECT workArea{};
    if (!GetMonitorWorkArea(g_options.Monitor, workArea))
    {
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &workArea, 0);
    }
    const int left = workArea.left + ((workArea.right - workArea.left) - width) / 2;
    const int top = workArea.top + ((workArea.bottom - workArea.top) - height) / 2;

    const DWORD exStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
    HWND hwnd = CreateWindowExW(
        exStyle,
        kWindowClass,
        kWindowTitle,
        WS_POPUP,
        left,
        top,
        width,
        height,
        nullptr,
        nullptr,
        module,
        nullptr);
    if (!hwnd)
    {
        Log(L"CreateWindowExW failed");
        return 3;
    }
    g_hwnd = hwnd;

    Graphics graphics;
    if (!graphics.Initialize(hwnd, width, height))
    {
        DestroyWindow(hwnd);
        return 4;
    }

    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd);

    if (!g_options.PipeName.empty())
    {
        std::thread(PipeServerThread, g_options.PipeName).detach();
    }
    if (g_options.ParentPid != 0)
    {
        std::thread(ParentWatchdogThread, g_options.ParentPid).detach();
    }

    const auto frameInterval = std::chrono::milliseconds(16);
    auto nextFrame = std::chrono::steady_clock::now();

    MSG message{};
    while (!g_quit.load())
    {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                g_quit.store(true);
                break;
            }

            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        if (g_quit.load())
        {
            break;
        }

        graphics.Render(g_options.DebugColor);

        nextFrame += frameInterval;
        const auto now = std::chrono::steady_clock::now();
        if (nextFrame > now)
        {
            std::this_thread::sleep_until(nextFrame);
        }
        else
        {
            nextFrame = now;
        }
    }

    if (g_hwnd)
    {
        DestroyWindow(g_hwnd);
        g_hwnd = nullptr;
    }

    Log(L"exited");
    return 0;
}
