cmake_minimum_required(VERSION 3.25)
project(NovaWebRtcTransport LANGUAGES C CXX)

# Manifest mode provides the pinned OpenSSL build used by libdatachannel.
if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  message(FATAL_ERROR "Configure with scripts/build-native.ps1, which bootstraps pinned vcpkg and passes the manifest toolchain.")
endif()

# x64-windows-static packages are built with the static MSVC runtime. Apply the
# same runtime before FetchContent creates libdatachannel's targets; otherwise a
# /MD nova_webrtc.dll is linked with /MT GLib/libnice archives (LNK4098).
if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING
      "MSVC runtime used by nova_webrtc and static vcpkg dependencies" FORCE)
endif()

# GLib and libnice ship pkg-config metadata, but no CMake package exports. The
# FindLibNice module bundled with libdatachannel resolves only nice, gobject and
# glib and, in a multi-config generator, caches the Debug library paths for all
# configurations. Model the installed pkg-config dependency graph explicitly
# with imported targets so CMake selects debug/lib for Debug and lib for Release.
if(NOT DEFINED VCPKG_INSTALLED_DIR OR NOT DEFINED VCPKG_TARGET_TRIPLET)
  message(FATAL_ERROR "The vcpkg installed directory and target triplet are required for libnice.")
endif()

set(NOVA_VCPKG_PREFIX "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}")

function(nova_import_vcpkg_static target library)
  add_library(${target} STATIC IMPORTED GLOBAL)
  set_target_properties(${target} PROPERTIES
    IMPORTED_CONFIGURATIONS "DEBUG;RELEASE"
    IMPORTED_LOCATION_DEBUG "${NOVA_VCPKG_PREFIX}/debug/lib/${library}.lib"
    IMPORTED_LOCATION_RELEASE "${NOVA_VCPKG_PREFIX}/lib/${library}.lib"
    MAP_IMPORTED_CONFIG_RELWITHDEBINFO RELEASE
    MAP_IMPORTED_CONFIG_MINSIZEREL RELEASE
    IMPORTED_LINK_INTERFACE_LANGUAGES "C")
endfunction()

find_package(OpenSSL REQUIRED)
find_package(ZLIB REQUIRED)
set(PCRE2_USE_STATIC_LIBS ON)
find_package(PCRE2 CONFIG REQUIRED)
find_package(unofficial-libffi CONFIG REQUIRED)

nova_import_vcpkg_static(NovaVcpkg::intl intl)
nova_import_vcpkg_static(NovaVcpkg::iconv iconv)
nova_import_vcpkg_static(NovaVcpkg::glib glib-2.0)
nova_import_vcpkg_static(NovaVcpkg::gobject gobject-2.0)
nova_import_vcpkg_static(NovaVcpkg::gmodule gmodule-2.0)
nova_import_vcpkg_static(NovaVcpkg::gthread gthread-2.0)
nova_import_vcpkg_static(NovaVcpkg::gio gio-2.0)
nova_import_vcpkg_static(LibNice::LibNice nice)

set_target_properties(NovaVcpkg::glib PROPERTIES
  INTERFACE_INCLUDE_DIRECTORIES
    "${NOVA_VCPKG_PREFIX}/include/glib-2.0;${NOVA_VCPKG_PREFIX}/lib/glib-2.0/include"
  INTERFACE_LINK_LIBRARIES
    "PCRE2::8BIT;NovaVcpkg::intl;NovaVcpkg::iconv;ws2_32;winmm")
set_target_properties(NovaVcpkg::gobject PROPERTIES
  INTERFACE_LINK_LIBRARIES
    "NovaVcpkg::glib;unofficial::libffi::libffi;NovaVcpkg::intl")
set_target_properties(NovaVcpkg::gmodule PROPERTIES
  INTERFACE_LINK_LIBRARIES "NovaVcpkg::glib;NovaVcpkg::intl")
set_target_properties(NovaVcpkg::gthread PROPERTIES
  INTERFACE_LINK_LIBRARIES "NovaVcpkg::glib;NovaVcpkg::intl")
set_target_properties(NovaVcpkg::gio PROPERTIES
  INTERFACE_LINK_LIBRARIES
    "NovaVcpkg::glib;NovaVcpkg::gobject;NovaVcpkg::gmodule;ZLIB::ZLIB;NovaVcpkg::intl;shlwapi;dnsapi;iphlpapi;ws2_32")
set_target_properties(LibNice::LibNice PROPERTIES
  INTERFACE_COMPILE_DEFINITIONS "_REENTRANT"
  INTERFACE_INCLUDE_DIRECTORIES
    "${NOVA_VCPKG_PREFIX}/include;${NOVA_VCPKG_PREFIX}/include/glib-2.0;${NOVA_VCPKG_PREFIX}/lib/glib-2.0/include"
  INTERFACE_LINK_LIBRARIES
    "NovaVcpkg::gio;NovaVcpkg::gthread;OpenSSL::SSL;iphlpapi;ws2_32")

# libdatachannel's FindLibNice short-circuits when this target exists. Mark the
# package found as well, because its module does not set LibNice_FOUND in that
# short-circuit path.
set(LibNice_FOUND TRUE)

include(FetchContent)
set(FETCHCONTENT_FULLY_DISCONNECTED OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(NO_MEDIA ON CACHE BOOL "" FORCE)
set(NO_WEBSOCKET ON CACHE BOOL "" FORCE)
set(USE_GNUTLS OFF CACHE BOOL "" FORCE)
set(USE_NICE ON CACHE BOOL "" FORCE)
set(USE_MBEDTLS OFF CACHE BOOL "" FORCE)
set(USE_SYSTEM_SRTP OFF CACHE BOOL "" FORCE)
set(OPENSSL_USE_STATIC_LIBS TRUE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
FetchContent_Declare(libdatachannel
  GIT_REPOSITORY https://github.com/paullouisageneau/libdatachannel.git
  GIT_TAG v0.24.5
  GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(libdatachannel)

add_library(nova_webrtc SHARED native/src/nova_webrtc.cpp)
target_compile_features(nova_webrtc PRIVATE cxx_std_20)
target_compile_definitions(nova_webrtc PRIVATE NOVA_WEBRTC_EXPORTS RTC_STATIC)
target_include_directories(nova_webrtc PRIVATE native/include)
target_link_libraries(nova_webrtc PRIVATE LibDataChannel::LibDataChannelStatic)
set_target_properties(nova_webrtc PROPERTIES OUTPUT_NAME "nova_webrtc")
