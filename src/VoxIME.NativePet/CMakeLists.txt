cmake_minimum_required(VERSION 3.16)

# VoxIME.NativePet —— 原生桌宠进程骨架。
#
# 当前阶段只验证三件基础设施：
#   1. Win32 透明/置顶窗口；
#   2. D3D11 交换链 + DirectComposition 逐像素透明合成；
#   3. 命名管道控制通道与父进程看门狗。
#
# Cubism 模型/动作/渲染会在后续阶段接入（官方 LAppModel/CubismDirectXRenderer）。
# 该阶段不依赖 Cubism SDK，保持可独立构建。

project(VoxIME.NativePet LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT MSVC)
  message(FATAL_ERROR "VoxIME.NativePet currently targets Windows/MSVC only.")
endif()

add_executable(VoxIME.NativePet WIN32
  src/main.cpp
)

target_compile_definitions(VoxIME.NativePet PRIVATE
  UNICODE
  _UNICODE
  WIN32_LEAN_AND_MEAN
  NOMINMAX
)

target_link_libraries(VoxIME.NativePet PRIVATE
  d3d11
  dxgi
  dcomp
  dwmapi
  user32
  gdi32
)

target_compile_options(VoxIME.NativePet PRIVATE /W4 /permissive- /utf-8)

set_target_properties(VoxIME.NativePet PROPERTIES
  OUTPUT_NAME "VoxIME.NativePet"
  VS_DEBUGGER_WORKING_DIRECTORY "$<TARGET_FILE_DIR:VoxIME.NativePet>"
)
