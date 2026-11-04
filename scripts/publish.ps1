<#
.SYNOPSIS
    发布 WordBase。默认同时产出「自包含便携版」和「依赖运行时的小体积版」。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -PortableOnly
#>
param(
    [switch]$PortableOnly,
    [switch]$FrameworkOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WordBase.App\WordBase.App.csproj'
$dist = Join-Path $root 'dist'

Push-Location $root
try {
    if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
    $portableDir = Join-Path $dist 'WordBase-portable'
    $frameworkDir = Join-Path $dist 'WordBase-framework'

    if ($FrameworkOnly) { $portableDir = $null }
    if ($PortableOnly) { $frameworkDir = $null }
    foreach ($dir in @($portableDir, $frameworkDir)) {
        if ($dir -and (Test-Path $dir)) { Remove-Item $dir -Recurse -Force }
    }

    if (-not $FrameworkOnly) {
        Write-Host '==> 自包含便携版（目标机器零依赖，约 200-260MB）' -ForegroundColor Cyan
        dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
            -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None `
            -p:PublishReadyToRun=true -p:DebugType=None -o $portableDir
        if ($LASTEXITCODE -ne 0) { throw '自包含发布失败' }
    }

    if (-not $PortableOnly) {
        Write-Host '==> 依赖运行时版（约 40MB，需目标机器已装 .NET 9 桌面运行时 + Windows App Runtime）' -ForegroundColor Cyan
        dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained false `
            -p:WindowsAppSDKSelfContained=false -p:WindowsPackageType=None `
            -p:DebugType=None -o $frameworkDir
        if ($LASTEXITCODE -ne 0) { throw '依赖运行时发布失败' }
    }

    foreach ($folder in Get-ChildItem $dist -Directory) {
        $size = (Get-ChildItem $folder.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
        Write-Host ("{0}: {1:N1} MB" -f $folder.Name, $size)
    }

    Write-Host "完成，产物在 $dist" -ForegroundColor Green
}
finally {
    Pop-Location
}
