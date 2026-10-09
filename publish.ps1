<#
  打包 MaoX Launcher（需要 .NET 10 SDK）

    .\publish.ps1            生成 dist\MaoX Launcher.exe（Windows x64，单个 exe，无需安装 .NET）
    .\publish.ps1 -Mac       额外生成 dist\MaoX-Launcher-macOS-arm64.tar.gz 和 -x64.tar.gz
    .\publish.ps1 -MacOnly   只生成 macOS 版

  exe 放在哪个文件夹，游戏、配置和工具就保存在哪个文件夹；
  macOS 版的数据保存在 ~/Library/Application Support/MaoX Launcher。
  在 Mac 上也可以直接运行 ./publish.sh，会顺带做 ad-hoc 签名。
#>
param([switch]$Mac, [switch]$MacOnly)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\MaoX\MaoX.csproj"
$work = Join-Path $root "build\publish"
$dist = Join-Path $root "dist"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw "找不到 .NET SDK，请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download" }
}
$version = (Select-Xml -Path (Join-Path $root "src\Directory.Build.props") -XPath "//Version").Node.InnerText

function Publish([string]$rid, [string[]]$extra) {
    $out = Join-Path $work $rid
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "> 发布 $rid" -ForegroundColor Cyan
    & $dotnet publish $project -nologo -c Release -r $rid --self-contained true `
        -p:DebugType=none -p:DebugSymbols=false -o $out @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "发布 $rid 失败" }
    return $out
}

New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $MacOnly) {
    $out = Publish "win-x64" @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                               "-p:EnableCompressionInSingleFile=true")
    $exe = Join-Path $dist "MaoX Launcher.exe"
    Copy-Item (Join-Path $out "MaoXLauncher.exe") $exe -Force
    Write-Host ("完成：{0}（{1:F1} MB）" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
}

if ($Mac -or $MacOnly) {
    $packer = Join-Path $root "packaging\macos\PackMac.cs"
    foreach ($rid in "osx-arm64", "osx-x64") {
        $out = Publish $rid @()
        $archive = Join-Path $dist ("MaoX-Launcher-macOS-{0}.tar.gz" -f $rid.Substring(4))
        & $dotnet run $packer -- $out $version $archive
        if ($LASTEXITCODE -ne 0) { throw "打包 $rid 失败" }
    }
}
