cmake_minimum_required(VERSION 3.16)

# VoxIME.NativePet —— 原生桌宠进程。
#
# 默认构建窗口/D3D/IPC 骨架，不依赖 Cubism SDK。
# 设置 CUBISM_SDK_DIR（或 VOXIME_CUBISM_SDK 环境变量）后，会编译 PetModel.cpp 并
# 链接本地 Cubism Native SDK；SDK 不进入仓库。
#
# 当前 Cubism 部分只加载 model3/moc3/贴图并绘制默认姿势；动作/表情/物理在后续提交加入。

project(VoxIME.NativePet LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT MSVC)
  message(FATAL_ERROR "VoxIME.NativePet currently targets Windows/MSVC only.")
endif()

set(CUBISM_SDK_DIR "" CACHE PATH "Live2D Cubism SDK for Native 根目录（包含 Core/Framework）")
if(NOT CUBISM_SDK_DIR AND DEFINED ENV{VOXIME_CUBISM_SDK})
  set(CUBISM_SDK_DIR "$ENV{VOXIME_CUBISM_SDK}")
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

target_include_directories(VoxIME.NativePet PRIVATE src)

target_link_libraries(VoxIME.NativePet PRIVATE
  d3d11
  dxgi
  dcomp
  dwmapi
  user32
  gdi32
  ole32
)

target_compile_options(VoxIME.NativePet PRIVATE /W4 /permissive- /utf-8)

set_target_properties(VoxIME.NativePet PROPERTIES
  OUTPUT_NAME "VoxIME.NativePet"
  VS_DEBUGGER_WORKING_DIRECTORY "$<TARGET_FILE_DIR:VoxIME.NativePet>"
)

if(CUBISM_SDK_DIR AND EXISTS "${CUBISM_SDK_DIR}/Core" AND EXISTS "${CUBISM_SDK_DIR}/Framework")
  message(STATUS "NativePet: 启用 Cubism SDK：${CUBISM_SDK_DIR}")

  set(CUBISM_CORE_INCLUDE "${CUBISM_SDK_DIR}/Core/include")
  set(CUBISM_CORE_LIB "${CUBISM_SDK_DIR}/Core/dll/windows/x86_64/Live2DCubismCore.lib")
  set(CUBISM_CORE_DLL "${CUBISM_SDK_DIR}/Core/dll/windows/x86_64/Live2DCubismCore.dll")

  add_library(Live2DCubismCore STATIC IMPORTED)
  set_target_properties(Live2DCubismCore PROPERTIES
    IMPORTED_LOCATION_RELEASE "${CUBISM_CORE_LIB}"
    IMPORTED_LOCATION_DEBUG "${CUBISM_CORE_LIB}"
    INTERFACE_INCLUDE_DIRECTORIES "${CUBISM_CORE_INCLUDE}"
  )

  set(FRAMEWORK_SOURCE D3D11)
  add_subdirectory("${CUBISM_SDK_DIR}/Framework" "${CMAKE_BINARY_DIR}/CubismFramework")
  target_link_libraries(Framework PRIVATE Live2DCubismCore d3d11 d3dcompiler)

  target_sources(VoxIME.NativePet PRIVATE src/PetModel.cpp src/ShaderCacheOverride.cpp)
  target_compile_definitions(VoxIME.NativePet PRIVATE VOXIME_NATIVE_PET_CUBISM=1)
  target_compile_options(Framework PRIVATE "/FI${CMAKE_CURRENT_SOURCE_DIR}/src/ShaderCacheOverride.h")
  target_include_directories(VoxIME.NativePet PRIVATE
    "${CUBISM_SDK_DIR}/Framework/src"
    "${CUBISM_CORE_INCLUDE}"
  )
  target_link_libraries(VoxIME.NativePet PRIVATE
    Framework
    Live2DCubismCore
    d3dcompiler
    windowscodecs
  )

  add_custom_command(TARGET VoxIME.NativePet POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CUBISM_CORE_DLL}" "$<TARGET_FILE_DIR:VoxIME.NativePet>"
    COMMAND ${CMAKE_COMMAND} -E copy_directory
      "${CUBISM_SDK_DIR}/Framework/src/Rendering/D3D11/Shaders"
      "$<TARGET_FILE_DIR:VoxIME.NativePet>/FrameworkShaders"
  )
else()
  message(STATUS "NativePet: 未配置 CUBISM_SDK_DIR，只构建窗口/D3D/IPC 骨架")
endif()
