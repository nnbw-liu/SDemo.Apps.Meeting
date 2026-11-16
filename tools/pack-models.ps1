# 把模型权重打进安装包目录（权重本身不进 git）。
#
# 用法：
#   pwsh ./pack-models.ps1                                   # 默认档 → ./artifacts/models
#   pwsh ./pack-models.ps1 -SourceDir D:\models              # 指定模型来源
#   pwsh ./pack-models.ps1 -IncludeOptional                  # 连 compact/upgrade 一起打
#   pwsh ./pack-models.ps1 -ManifestOnly                     # 只写清单，不复制
#
# 产物：<OutDir>\<模型文件> + <OutDir>\models.json
# models.json 与运行期的 manifest 同格式，所以安装包解到数据目录后直接就是"已就绪"。

param(
    [string]$SourceDir = $env:SDEMO_MEETING_MODEL_DIR,
    [string]$OutDir = (Join-Path (Get-Location) 'artifacts/models'),
    [switch]$IncludeOptional,
    [switch]$ManifestOnly
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $env:APPDATA 'SDemo/Apps/sdemo.meeting/models'
}

# 与 Contracts/ModelPacks.cs 保持一致：id / 文件名 / 期望字节 / sha256 / 档位
$packs = @(
    @{
        Id = 'whisper-turbo-q8'
        File = 'ggml-large-v3-turbo-q8_0.bin'
        Bytes = 874188075
        Sha256 = '317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1'
        Tier = 'Default'
        Languages = @('zh', 'en', 'ja', 'yue', 'auto')
    },
    @{
        Id = 'whisper-small-q5'
        File = 'ggml-small-q5_1.bin'
        Bytes = 0
        Sha256 = $null
        Tier = 'Compact'
        Languages = @('zh', 'en', 'ja', 'auto')
    }
)

Write-Host "模型来源：$SourceDir"
Write-Host "输出目录：$OutDir"
Write-Host ''

if (-not (Test-Path $SourceDir)) {
    throw "模型来源目录不存在：$SourceDir"
}

if (-not $ManifestOnly) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$manifest = [ordered]@{ version = 2; packs = [ordered]@{} }
$totalBytes = 0
$problems = @()

foreach ($pack in $packs) {
    if ($pack.Tier -ne 'Default' -and -not $IncludeOptional) {
        Write-Host ("  {0,-22} 跳过（非默认档，加 -IncludeOptional）" -f $pack.Id)
        continue
    }

    $src = Join-Path $SourceDir $pack.File
    if (-not (Test-Path $src)) {
        Write-Warning ("  {0,-22} 缺失：{1}" -f $pack.Id, $src)
        $problems += $pack.Id
        continue
    }

    $item = Get-Item $src
    if ($pack.Bytes -gt 0 -and $item.Length -ne $pack.Bytes) {
        throw ("{0} 大小不符：{1} 字节，期望 {2}" -f $pack.Id, $item.Length, $pack.Bytes)
    }

    $sha = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($pack.Sha256 -and $sha -ne $pack.Sha256) {
        throw ("{0} sha256 不符：{1}，期望 {2}" -f $pack.Id, $sha, $pack.Sha256)
    }

    if (-not $ManifestOnly) {
        Copy-Item $src (Join-Path $OutDir $pack.File) -Force
    }

    $totalBytes += $item.Length
    $manifest.packs[$pack.Id] = [ordered]@{
        PackId = $pack.Id
        FileName = $pack.File
        DisplayName = $pack.Id
        Bytes = $item.Length
        Sha256 = $sha
        Source = 'bundled'
        ImportedAt = (Get-Date).ToUniversalTime().ToString('o')
        Tier = $pack.Tier
        Languages = $pack.Languages
    }

    Write-Host ("  {0,-22} {1,8:N1} MB  sha256 {2}…  OK" -f $pack.Id, ($item.Length / 1MB), $sha.Substring(0, 12))
}

if (-not $ManifestOnly) {
    $manifestPath = Join-Path $OutDir 'models.json'
    ($manifest | ConvertTo-Json -Depth 6) | Set-Content $manifestPath -Encoding utf8
    Write-Host ''
    Write-Host ("已写入清单：$manifestPath")
}

Write-Host ("合计 {0:N2} GB" -f ($totalBytes / 1GB))

if ($problems.Count -gt 0) {
    Write-Warning ("缺少模型：{0}（安装包里不会带，用户需自行导入）" -f ($problems -join ', '))
    exit 2
}
