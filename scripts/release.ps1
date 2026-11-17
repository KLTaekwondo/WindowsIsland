<#
.SYNOPSIS
    在本地打一个 Release 并上传构建产物（不依赖 GitHub Actions）。

.DESCRIPTION
    需要一个 GitHub Personal Access Token（勾选 repo / contents:write 权限），
    通过环境变量传入，避免把 token 写进脚本或命令行历史：

        $env:GITHUB_TOKEN = "ghp_xxx"

    然后：

        .\scripts\release.ps1 -Tag v1.0.0 -Notes "首个版本"

.PARAMETER Tag
    版本号，例如 v1.0.0。

.PARAMETER Repo
    仓库，owner/name 形式。

.PARAMETER Notes
    Release 说明文本。

.PARAMETER Files
    要上传的文件，默认带上安装包与免安装单文件（存在才传）。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Repo = 'KLTaekwondo/WindowsIsland',

    [string]$Notes = '',

    [string[]]$Files = @(
        'dist\WindowsIslandSetup.exe',
        'dist\single\WindowsIsland.exe'
    )
)

$ErrorActionPreference = 'Stop'

$token = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    throw '请先设置环境变量 GITHUB_TOKEN（GitHub Personal Access Token，需 contents:write 权限）'
}

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}

Write-Host "创建 Release $Tag ($Repo) ..."

$body = @{
    tag_name   = $Tag
    name       = $Tag
    body       = $Notes
    draft      = $false
    prerelease = $false
} | ConvertTo-Json

$release = Invoke-RestMethod `
    -Method Post `
    -Uri "https://api.github.com/repos/$Repo/releases" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $body

# upload_url 形如 https://uploads.github.com/.../assets{?name,label}
$uploadBase = ($release.upload_url -replace '\{.*$', '')

foreach ($file in $Files) {
    if (-not (Test-Path $file)) {
        Write-Host "  跳过（不存在）: $file"
        continue
    }

    $name = Split-Path $file -Leaf
    $size = [math]::Round((Get-Item $file).Length / 1MB, 1)

    Write-Host "  上传 $name ($size MB) ..."

    $uri = "$uploadBase?name=$([uri]::EscapeDataString($name))"

    Invoke-RestMethod `
        -Method Post `
        -Uri $uri `
        -Headers $headers `
        -ContentType 'application/octet-stream' `
        -InFile (Resolve-Path $file) | Out-Null
}

Write-Host ""
Write-Host "完成: $($release.html_url)"
