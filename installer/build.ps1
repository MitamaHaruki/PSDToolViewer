<#
.SYNOPSIS
  PSDTool Viewer の配布物（MSI インストーラーとポータブル版 ZIP）を作る。

.EXAMPLE
  ./installer/build.ps1                 # Directory.Build.props のバージョンで x64・arm64 を作る
  ./installer/build.ps1 -Version 1.0.1  # バージョンを指定する
#>
param(
    [string]$Version,
    [string[]]$Architectures = @('x64', 'arm64'),
    [string]$OutputDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
    }
    $out = New-Item -ItemType Directory -Force -Path (Join-Path $root $OutputDir)
    Write-Host "PSDTool Viewer $Version を作成します（$($Architectures -join ', ')）"

    # エクスプローラー用サムネイル拡張（C++）のビルドに使う MSBuild（Visual Studio の C++ ツールが必要）
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Visual Studio の C++ ビルドツール（MSBuild）が見つかりません。' }

    foreach ($arch in $Architectures) {
        $publish = Join-Path $root "artifacts\publish\$arch"
        $installerOut = Join-Path $root "artifacts\installer\$arch"
        $thumbnail = Join-Path $root "artifacts\thumbnail\$arch"

        $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
        & $msbuild thumbnail/PSDToolViewer.Thumbnail.vcxproj -nologo -v:minimal -p:Configuration=Release -p:Platform=$platform `
            "-p:OutDir=$thumbnail\" "-p:IntDir=$(Join-Path $root "artifacts\thumbnail\obj\$arch")\"
        if ($LASTEXITCODE -ne 0) { throw "サムネイル拡張のビルドに失敗しました（$arch）" }

        # 単一の exe（.NET のインストール不要）
        dotnet publish src/PSDToolViewer/PSDToolViewer.csproj -c Release -r "win-$arch" --self-contained true `
            -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publish
        if ($LASTEXITCODE -ne 0) { throw "発行に失敗しました（$arch）" }

        dotnet build installer/PSDToolViewer.Installer.wixproj -c Release `
            -p:InstallerPlatform=$arch -p:ProductVersion=$Version -p:PublishDir="$publish\" -p:ThumbnailDir="$thumbnail\" -o $installerOut
        if ($LASTEXITCODE -ne 0) { throw "インストーラーの作成に失敗しました（$arch）" }

        $name = "PSDToolViewer-$Version-win-$arch"
        Copy-Item (Join-Path $installerOut "$name.msi") (Join-Path $out "$name.msi") -Force
        Compress-Archive -Path (Join-Path $publish 'PSDToolViewer.exe') -DestinationPath (Join-Path $out "$name-portable.zip") -CompressionLevel Optimal -Force
        Write-Host "  $name.msi / $name-portable.zip"
    }
}
finally {
    Pop-Location
}
