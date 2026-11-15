// VoxIME.NativePet —— Cubism 模型加载/渲染（Phase 1 最小版）。
//
// Cubism Framework/Renderer 来自本地 Cubism Native SDK；本文件不复制官方示例源码。
// 当前只加载 model3/moc3/贴图并绘制默认姿势，动作/物理在后续提交加入。

#include "PetModel.h"

#include <windows.h>
#include <d3d11.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <CubismModelSettingJson.hpp>
#include <Math/CubismMatrix44.hpp>
#include <Rendering/D3D11/CubismRenderer_D3D11.hpp>

#include <algorithm>
#include <cstring>
#include <fstream>

using Microsoft::WRL::ComPtr;
using namespace Live2D::Cubism::Framework;

namespace
{
    std::string Error(const std::string& message)
    {
        return message;
    }

    std::wstring Utf8ToWide(const char* text)
    {
        if (!text || !*text)
        {
            return {};
        }

        const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
        if (length <= 0)
        {
            return {};
        }

        std::wstring result(static_cast<size_t>(length - 1), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text, -1, result.data(), length);
        return result;
    }

    bool ReadAllBytes(const std::filesystem::path& path, std::vector<char>& bytes)
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
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 512LL * 1024 * 1024)
        {
            CloseHandle(file);
            return false;
        }
        bytes.resize(static_cast<size_t>(size.QuadPart));
        size_t offset = 0;
        while (offset < bytes.size())
        {
            const DWORD request = static_cast<DWORD>(std::min<size_t>(bytes.size() - offset, 1 << 20));
            DWORD read = 0;
            if (!ReadFile(file, bytes.data() + offset, request, &read, nullptr) || read == 0)
            {
                CloseHandle(file);
                return false;
            }
            offset += read;
        }

        CloseHandle(file);
        return true;
    }

    bool LoadTextureFromFile(
        ID3D11Device* device,
        const std::filesystem::path& path,
        ID3D11ShaderResourceView** view)
    {
        *view = nullptr;

        ComPtr<IWICImagingFactory> factory;
        HRESULT hr = CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&factory));
        if (FAILED(hr))
        {
            return false;
        }

        ComPtr<IWICBitmapDecoder> decoder;
        hr = factory->CreateDecoderFromFilename(
            path.c_str(),
            nullptr,
            GENERIC_READ,
            WICDecodeMetadataCacheOnDemand,
            &decoder);
        if (FAILED(hr))
        {
            return false;
        }

        ComPtr<IWICBitmapFrameDecode> frame;
        hr = decoder->GetFrame(0, &frame);
        if (FAILED(hr))
        {
            return false;
        }

        ComPtr<IWICFormatConverter> converter;
        hr = factory->CreateFormatConverter(&converter);
        if (FAILED(hr))
        {
            return false;
        }

        hr = converter->Initialize(
            frame.Get(),
            GUID_WICPixelFormat32bppBGRA,
            WICBitmapDitherTypeNone,
            nullptr,
            0.0,
            WICBitmapPaletteTypeCustom);
        if (FAILED(hr))
        {
            return false;
        }

        UINT width = 0;
        UINT height = 0;
        hr = converter->GetSize(&width, &height);
        if (FAILED(hr) || width == 0 || height == 0)
        {
            return false;
        }

        const UINT stride = width * 4;
        std::vector<BYTE> pixels(static_cast<size_t>(stride) * height);
        hr = converter->CopyPixels(nullptr, stride, static_cast<UINT>(pixels.size()), pixels.data());
        if (FAILED(hr))
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA initial{};
        initial.pSysMem = pixels.data();
        initial.SysMemPitch = stride;

        ComPtr<ID3D11Texture2D> texture;
        hr = device->CreateTexture2D(&desc, &initial, &texture);
        if (FAILED(hr))
        {
            return false;
        }

        D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc{};
        viewDesc.Format = desc.Format;
        viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        viewDesc.Texture2D.MipLevels = 1;

        return SUCCEEDED(device->CreateShaderResourceView(texture.Get(), &viewDesc, view));
    }
}

namespace VoxIME::NativePet
{
    PetModel::PetModel() = default;

    PetModel::~PetModel()
    {
        ReleaseTextures();
        if (_settings)
        {
            delete _settings;
            _settings = nullptr;
        }
    }

    bool PetModel::Load(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        const std::filesystem::path& modelJsonPath,
        int width,
        int height,
        std::string& error)
    {
        _device = device;
        _context = context;

        std::vector<char> json;
        if (!ReadAllBytes(modelJsonPath, json) || json.empty())
        {
            error = Error("cannot read model3.json");
            return false;
        }
        json.push_back('\0');
        _settings = new CubismModelSettingJson(
            reinterpret_cast<const csmByte*>(json.data()),
            static_cast<csmSizeInt>(json.size() - 1));
        if (!_settings || !_settings->GetModelFileName() || !*_settings->GetModelFileName())
        {
            error = Error("model3.json has no Moc entry");
            return false;
        }

        const std::filesystem::path basePath = modelJsonPath.parent_path();
        const std::filesystem::path mocPath = basePath / Utf8ToWide(_settings->GetModelFileName());
        std::vector<char> moc;
        if (!ReadAllBytes(mocPath, moc) || moc.empty())
        {
            error = Error("cannot read moc3: " + mocPath.string());
            return false;
        }
        LoadModel(reinterpret_cast<const csmByte*>(moc.data()), static_cast<csmSizeInt>(moc.size()));
        if (!GetModel())
        {
            error = Error("CubismUserModel::LoadModel failed");
            return false;
        }

        SetRenderTargetSize(static_cast<csmUint32>(width), static_cast<csmUint32>(height));
        CreateRenderer(static_cast<csmUint32>(width), static_cast<csmUint32>(height));
        if (!GetRenderer<Rendering::CubismRenderer_D3D11>())
        {
            error = Error("CreateRenderer failed");
            return false;
        }

        csmMap<csmString, csmFloat32> layout;
        _settings->GetLayoutMap(layout);
        GetModelMatrix()->SetupFromLayout(layout);
        if (!LoadTextures(device, basePath, error))
        {
            return false;
        }

        _name = modelJsonPath.stem().wstring();
        _loaded = true;
        return true;
    }

    bool PetModel::LoadTextures(
        ID3D11Device* device,
        const std::filesystem::path& basePath,
        std::string& error)
    {
        auto* renderer = GetRenderer<Rendering::CubismRenderer_D3D11>();
        if (!renderer)
        {
            error = Error("no D3D11 renderer");
            return false;
        }

        const HRESULT coInit = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        const bool shouldUninitialize = SUCCEEDED(coInit);

        bool ok = true;
        for (csmInt32 index = 0; index < _settings->GetTextureCount(); ++index)
        {
            const char* fileName = _settings->GetTextureFileName(index);
            if (!fileName || !*fileName)
            {
                _textureViews.emplace_back();
                continue;
            }

            ComPtr<ID3D11ShaderResourceView> view;
            if (!LoadTextureFromFile(device, basePath / Utf8ToWide(fileName), &view))
            {
                error = Error(std::string("cannot load texture: ") + fileName);
                ok = false;
                break;
            }

            renderer->BindTexture(static_cast<csmUint32>(index), view.Get());
            _textureViews.push_back(std::move(view));
        }

        if (shouldUninitialize)
        {
            CoUninitialize();
        }
        return ok;
    }

    void PetModel::ReleaseTextures()
    {
        _textureViews.clear();
    }

    void PetModel::Update(float deltaSeconds)
    {
        if (!_loaded || !GetModel())
        {
            return;
        }

        _timeSeconds += std::max(0.0f, deltaSeconds);
        GetModel()->LoadParameters();
        GetModel()->Update();
        GetModel()->SaveParameters();
    }

    void PetModel::Draw(ID3D11RenderTargetView* renderTarget, int width, int height)
    {
        if (!_loaded || !renderTarget || width <= 0 || height <= 0)
        {
            return;
        }

        auto* renderer = GetRenderer<Rendering::CubismRenderer_D3D11>();
        auto* model = GetModel();
        if (!renderer || !model)
        {
            return;
        }

        renderer->StartFrame(_context);

        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(width);
        viewport.Height = static_cast<float>(height);
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;
        _context->RSSetViewports(1, &viewport);
        _context->OMSetRenderTargets(1, &renderTarget, nullptr);

        CubismMatrix44 projection;
        projection.LoadIdentity();

        if (model->GetCanvasWidth() > 1.0f && width < height)
        {
            GetModelMatrix()->SetWidth(2.0f);
            projection.Scale(1.0f, static_cast<float>(width) / static_cast<float>(height));
        }
        else
        {
            projection.Scale(static_cast<float>(height) / static_cast<float>(width), 1.0f);
        }

        projection.MultiplyByMatrix(GetModelMatrix());
        renderer->SetMvpMatrix(&projection);
        renderer->DrawModel();
        renderer->EndFrame();
    }
}
