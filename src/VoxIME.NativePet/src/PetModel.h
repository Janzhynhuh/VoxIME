// VoxIME.NativePet —— Cubism 模型加载/渲染（Phase 1 最小版）。
//
// 使用 Live2D Cubism Native Framework 的公开 API；Cubism SDK 从
// VOXIME_CUBISM_SDK 指向的本地目录构建，不进入本仓库。
//
// 当前只加载 model3/moc3/贴图并绘制默认姿势；动作/表情/物理/多 MotionManager
// 在后续提交加入。

#pragma once

#include <d3d11.h>
#include <filesystem>
#include <string>
#include <vector>
#include <wrl/client.h>

#include <CubismFramework.hpp>
#include <CubismModelSettingJson.hpp>
#include <Model/CubismUserModel.hpp>

namespace VoxIME::NativePet
{
    class PetModel final : public Csm::CubismUserModel
    {
    public:
        PetModel();
        ~PetModel() override;

        PetModel(const PetModel&) = delete;
        PetModel& operator=(const PetModel&) = delete;

        bool Load(
            ID3D11Device* device,
            ID3D11DeviceContext* context,
            const std::filesystem::path& modelJsonPath,
            int width,
            int height,
            std::string& error);

        void Update(float deltaSeconds);
        void Draw(ID3D11RenderTargetView* renderTarget, int width, int height);

        bool IsLoaded() const { return _loaded; }
        const std::wstring& Name() const { return _name; }

    private:
        bool LoadTextures(ID3D11Device* device, const std::filesystem::path& basePath, std::string& error);
        void ReleaseTextures();

        ID3D11Device* _device = nullptr;
        ID3D11DeviceContext* _context = nullptr;
        bool _loaded = false;
        float _timeSeconds = 0.0f;
        std::wstring _name;
        Csm::CubismModelSettingJson* _settings = nullptr;
        std::vector<Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>> _textureViews;
    };
}
