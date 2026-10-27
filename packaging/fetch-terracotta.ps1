# 下载未经修改的陶瓦官方安装包，供 publish 嵌入启动器。不提交到 git。
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$Dest
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root "src\MaoX.Core\Multiplayer.cs"
$text = Get-Content -LiteralPath $src -Raw
if ($text -notmatch 'public const string Version = "([^"]+)"') {
    throw "无法从 Multiplayer.cs 读取陶瓦版本"
}
$version = $Matches[1]
$map = @{
    "win-x64"     = "windows-x86_64"
    "win-arm64"   = "windows-arm64"
    "osx-arm64"   = "macos-arm64"
    "osx-x64"     = "macos-x86_64"
    "linux-x64"   = "linux-x86_64"
    "linux-arm64" = "linux-arm64"
}
$classifier = $map[$Rid]
if (-not $classifier) { throw "未知 RID：$Rid" }
if ($text -notmatch ('\["' + [regex]::Escape($classifier) + '"\] = new\(\s*"([0-9a-f]{128})"')) {
    throw "无法从 Multiplayer.cs 读取 $classifier 的 SHA-512"
}
$hash = $Matches[1]
$name = "terracotta-$version-$classifier-pkg.tar.gz"
$github = "https://github.com/burningtnt/Terracotta/releases/download/v$version/$name"
$mirrors = @(
    "https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v$version/$name",
    "https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v$version/$name",
    "https://ghfast.top/$github",
    "https://gh-proxy.com/$github"
)
# GitHub Actions 在国外，官方地址更快；本机打包则先走国内镜像。
$urls = if ($env:CI -eq "true") { @($github) + $mirrors } else { $mirrors + @($github) }

if (Test-Path -LiteralPath $Dest) {
    $actual = (Get-FileHash -LiteralPath $Dest -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actual -eq $hash) { exit 0 }
    Remove-Item -LiteralPath $Dest -Force
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Dest) | Out-Null
$tmp = "$Dest.part"
$curl = Get-Command curl.exe -ErrorAction SilentlyContinue
foreach ($url in $urls) {
    Write-Host "> 下载陶瓦 $url" -ForegroundColor Cyan
    try {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
        if ($curl) {
            & curl.exe -fL --retry 2 --connect-timeout 20 --max-time 360 -o $tmp $url
            if ($LASTEXITCODE -ne 0) { throw "curl 退出码 $LASTEXITCODE" }
        }
        else {
            Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -TimeoutSec 360
        }
        if (-not (Test-Path -LiteralPath $tmp) -or ((Get-Item -LiteralPath $tmp).Length -lt 1MB)) {
            throw "文件太小"
        }
        $actual = (Get-FileHash -LiteralPath $tmp -Algorithm SHA512).Hash.ToLowerInvariant()
        if ($actual -ne $hash) { throw "SHA-512 不符" }
        Move-Item -LiteralPath $tmp -Destination $Dest -Force
        exit 0
    }
    catch {
        Write-Host ("  失败：{0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    }
}
throw "无法下载陶瓦联机 $name"
