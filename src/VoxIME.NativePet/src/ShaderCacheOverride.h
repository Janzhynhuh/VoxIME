// VoxIME.NativePet —— 强制包含到 Cubism Framework 的 shader 编译钩子。
//
// 通过把 D3DCompile 宏替换为 VoximeCompileShader，实现在不改动 SDK 源码的前提下
// 增加磁盘缓存。缓存内容是设备无关的 DXBC，可随安装包分发。
#pragma once

#include <windows.h>
#include <d3dcompiler.h>

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
    ID3DBlob** ppErrorMsgs);

#define D3DCompile VoximeCompileShader
