$ErrorActionPreference = 'Stop'
$repoResolved = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NovaShortPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
}
'@
$shortBuffer = [System.Text.StringBuilder]::new(1024)
$shortLength = [NovaShortPath]::GetShortPathName($repoResolved, $shortBuffer, [uint32]$shortBuffer.Capacity)
$repo = if ($shortLength -gt 0 -and $shortLength -lt $shortBuffer.Capacity) { $shortBuffer.ToString() } else { $repoResolved }
$dependencyRoot = Join-Path $env:TEMP 'nova-webrtc-vcpkg'
$vendor = Join-Path $dependencyRoot 'tools\vcpkg'
$vcpkgBuildtrees = Join-Path $dependencyRoot 'buildtrees'
$vcpkgInstalled = Join-Path $dependencyRoot 'installed'
$vcpkgDownloads = Join-Path $dependencyRoot 'downloads'
$cachedVcpkgTool = Join-Path $repoResolved 'build\tools\vcpkg\vcpkg.exe'
if ($dependencyRoot.Contains(' ') -or $vendor.Contains(' ') -or $vcpkgDownloads.Contains(' ')) { throw "Paths without spaces are required for libnice's autotools build: $dependencyRoot" }
$vcpkgCommit = 'aa2d37682e3318d93aef87efa7b0e88e81cd3d59'
$vsDevCmd = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $vsDevCmd)) { throw "Visual Studio Developer Environment not found: $vsDevCmd" }
if (-not (Test-Path -LiteralPath (Join-Path $vendor '.git'))) {
    New-Item -ItemType Directory -Force -Path (Split-Path $vendor) | Out-Null
    git clone https://github.com/microsoft/vcpkg.git $vendor
}
git -C $vendor fetch --depth 1 origin $vcpkgCommit
git -C $vendor checkout --detach $vcpkgCommit
$toolVersion = ''
if (Test-Path -LiteralPath $cachedVcpkgTool) {
    Copy-Item -LiteralPath $cachedVcpkgTool -Destination (Join-Path $vendor 'vcpkg.exe') -Force
    $toolVersion = (& (Join-Path $vendor 'vcpkg.exe') version --disable-metrics | Out-String).Trim()
}
if ($toolVersion -match '2025-12-16-44bb3ce006467fc13ba37ca099f64077b8bbf84d') {
    Set-Content -LiteralPath (Join-Path $vendor 'vcpkg.disable-metrics') -Value ''
} else {
    & (Join-Path $vendor 'bootstrap-vcpkg.bat') -disableMetrics
    if ($LASTEXITCODE -ne 0) { throw 'vcpkg bootstrap failed' }
}
$env:VCPKG_ROOT = $vendor
$env:VCPKG_DOWNLOADS = $vcpkgDownloads
New-Item -ItemType Directory -Force -Path $vcpkgDownloads | Out-Null
$buildDir = Join-Path $repo 'build\native-x64'
if (Test-Path -LiteralPath $buildDir) { Remove-Item -LiteralPath $buildDir -Recurse -Force }
$triplet = 'x64-windows-static'
& (Join-Path $vendor 'vcpkg.exe') install --vcpkg-root=$vendor --triplet $triplet --x-manifest-root $repo "--x-buildtrees-root=$vcpkgBuildtrees" "--x-install-root=$vcpkgInstalled"
if ($LASTEXITCODE -ne 0) { throw 'vcpkg manifest install failed' }
$toolchain = Join-Path $vendor 'scripts\buildsystems\vcpkg.cmake'
$cmake = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$configure = "call `"$vsDevCmd`" -arch=x64 -host_arch=x64 && `"$cmake`" -S `"$repo`" -B `"$buildDir`" -G `"Visual Studio 18 2026`" -A x64 -DCMAKE_TOOLCHAIN_FILE=`"$toolchain`" -DVCPKG_TARGET_TRIPLET=$triplet -DVCPKG_INSTALLED_DIR=`"$vcpkgInstalled`" -DVCPKG_MANIFEST_MODE=ON && `"$cmake`" --build `"$buildDir`" --config Release --target nova_webrtc"
& cmd.exe /d /s /c $configure
if ($LASTEXITCODE -ne 0) { throw 'CMake configure/build failed' }
