// VoxIME.NativePet —— Cubism shader 磁盘缓存。
//
// 由 ShaderCacheOverride.h 把 CubismShader_D3D11.cpp 中的 D3DCompile 宏替换到
// VoximeCompileShader。缓存内容是设备无关的 DXBC，可在不同 D3D11 设备之间复用。
//
// 查找顺序：
//   1. VOXIME_SHADER_CACHE_DIR（构建时预热/调试）
//   2. exe 目录\ShaderCache（安装包内置，只读）
//   3. %LOCALAPPDATA%\VoxIME\native-shader-cache（运行时写入）
//
// 未命中时调用真正的 D3DCompile，并原子写入本地缓存。

#include <windows.h>
#include <d3dcompiler.h>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

namespace
{
    constexpr uint64_t kFnvOffset = 1469598103934665603ull;
    constexpr uint64_t kFnvPrime = 1099511628211ull;
    constexpr const wchar_t* kCacheVersion = L"voxime-shader-cache-v1";

    void HashBytes(uint64_t& hash, const void* data, size_t size)
    {
        const auto* bytes = static_cast<const unsigned char*>(data);
        for (size_t index = 0; index < size; ++index)
        {
            hash ^= bytes[index];
            hash *= kFnvPrime;
        }
    }

    void HashString(uint64_t& hash, const char* text)
    {
        if (text)
        {
            HashBytes(hash, text, strlen(text));
        }
        const unsigned char separator = 0xFF;
        HashBytes(hash, &separator, 1);
    }

    std::wstring GetEnv(const wchar_t* name)
    {
        DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
        if (length == 0)
        {
            return {};
        }

        std::wstring value(length, L'\0');
        const DWORD written = GetEnvironmentVariableW(name, value.data(), length);
        if (written == 0 || written >= length)
        {
            return {};
        }
        value.resize(written);
        return value;
    }

    std::filesystem::path GetExeDirectory()
    {
        wchar_t path[MAX_PATH]{};
        const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
        if (length == 0 || length >= MAX_PATH)
        {
            return {};
        }
        return std::filesystem::path(path).parent_path();
    }

    std::vector<std::filesystem::path> GetReadDirectories()
    {
        std::vector<std::filesystem::path> directories;

        const std::wstring overrideDir = GetEnv(L"VOXIME_SHADER_CACHE_DIR");
        if (!overrideDir.empty())
        {
            directories.emplace_back(overrideDir);
        }

        const auto exeDirectory = GetExeDirectory();
        if (!exeDirectory.empty())
        {
            directories.push_back(exeDirectory / L"ShaderCache");
        }

        const std::wstring localAppData = GetEnv(L"LOCALAPPDATA");
        if (!localAppData.empty())
        {
            directories.push_back(std::filesystem::path(localAppData) / L"VoxIME" / L"native-shader-cache");
        }

        return directories;
    }

    std::filesystem::path GetWriteDirectory()
    {
        const std::wstring overrideDir = GetEnv(L"VOXIME_SHADER_CACHE_DIR");
        if (!overrideDir.empty())
        {
            return std::filesystem::path(overrideDir);
        }

        const std::wstring localAppData = GetEnv(L"LOCALAPPDATA");
        if (!localAppData.empty())
        {
            return std::filesystem::path(localAppData) / L"VoxIME" / L"native-shader-cache";
        }

        return {};
    }

    std::wstring CacheFileName(
        const void* source,
        size_t sourceSize,
        const char* entrypoint,
        const char* target,
        UINT flags)
    {
        uint64_t hash = kFnvOffset;
        HashBytes(hash, kCacheVersion, wcslen(kCacheVersion) * sizeof(wchar_t));
        HashBytes(hash, source, sourceSize);
        HashString(hash, entrypoint);
        HashString(hash, target);
        HashBytes(hash, &flags, sizeof(flags));

        wchar_t name[32]{};
        swprintf_s(name, L"%016llx.cso", static_cast<unsigned long long>(hash));
        return name;
    }

    bool ReadFileBytes(const std::filesystem::path& path, std::vector<unsigned char>& bytes)
    {
        HANDLE file = CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 16LL * 1024 * 1024)
        {
            CloseHandle(file);
            return false;
        }

        bytes.resize(static_cast<size_t>(size.QuadPart));
        size_t offset = 0;
        while (offset < bytes.size())
        {
            DWORD read = 0;
            if (!ReadFile(
                    file,
                    bytes.data() + offset,
                    static_cast<DWORD>(bytes.size() - offset),
                    &read,
                    nullptr)
                || read == 0)
            {
                CloseHandle(file);
                return false;
            }
            offset += read;
        }

        CloseHandle(file);
        return true;
    }

    bool TryLoadCachedBlob(
        const std::wstring& fileName,
        ID3DBlob** ppCode)
    {
        for (const auto& directory : GetReadDirectories())
        {
            std::error_code error;
            const auto path = directory / fileName;
            if (!std::filesystem::is_regular_file(path, error))
            {
                continue;
            }

            std::vector<unsigned char> bytes;
            if (!ReadFileBytes(path, bytes))
            {
                continue;
            }

            ID3DBlob* blob = nullptr;
            if (SUCCEEDED(D3DCreateBlob(bytes.size(), &blob)) && blob)
            {
                memcpy(blob->GetBufferPointer(), bytes.data(), bytes.size());
                *ppCode = blob;
                return true;
            }
        }

        return false;
    }

    void SaveCachedBlob(
        const std::wstring& fileName,
        ID3DBlob* blob)
    {
        if (!blob || blob->GetBufferSize() == 0)
        {
            return;
        }

        const auto directory = GetWriteDirectory();
        if (directory.empty())
        {
            return;
        }

        std::error_code error;
        std::filesystem::create_directories(directory, error);
        if (error)
        {
            return;
        }

        const auto targetPath = directory / fileName;
        const auto temporaryPath = directory / (fileName + L".tmp");

        HANDLE file = CreateFileW(
            temporaryPath.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        DWORD written = 0;
        const bool ok = WriteFile(
            file,
            blob->GetBufferPointer(),
            static_cast<DWORD>(blob->GetBufferSize()),
            &written,
            nullptr) != FALSE
            && written == blob->GetBufferSize();
        CloseHandle(file);

        if (!ok)
        {
            DeleteFileW(temporaryPath.c_str());
            return;
        }

        MoveFileExW(
            temporaryPath.c_str(),
            targetPath.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    }
}

extern "C" HRESULT WINAPI VoximeCompileShader(
    LPCVOID pSrcData,
    SIZE_T SrcDataSize,
    LPCSTR pFileName,
    const D3D_SHADER_MACRO* pDefines,
    ID3DInclude* pInclude,
    LPCSTR pEntrypoint,
    LPCSTR pTarget,
    UINT Flags1,
    UINT Flags2,
    ID3DBlob** ppCode,
    ID3DBlob** ppErrorMsgs)
{
    if (!ppCode)
    {
        return D3DCompile(
            pSrcData,
            SrcDataSize,
            pFileName,
            pDefines,
            pInclude,
            pEntrypoint,
            pTarget,
            Flags1,
            Flags2,
            ppCode,
            ppErrorMsgs);
    }

    *ppCode = nullptr;
    if (ppErrorMsgs)
    {
        *ppErrorMsgs = nullptr;
    }

    const std::wstring fileName = CacheFileName(
        pSrcData,
        SrcDataSize,
        pEntrypoint,
        pTarget,
        Flags1);

    if (TryLoadCachedBlob(fileName, ppCode))
    {
        return S_OK;
    }

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(
        pSrcData,
        SrcDataSize,
        pFileName,
        pDefines,
        pInclude,
        pEntrypoint,
        pTarget,
        Flags1,
        Flags2,
        &code,
        &errors);

    if (SUCCEEDED(hr) && code)
    {
        SaveCachedBlob(fileName, code);
    }

    *ppCode = code;
    if (ppErrorMsgs)
    {
        *ppErrorMsgs = errors;
    }
    else if (errors)
    {
        errors->Release();
    }

    return hr;
}
