# Kiriha の署名付き Velopack パッケージを作成し、Cloudflare R2 へ配信する。
[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string[]]$Runtimes = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Bucket = 'kiriha-updates'
$BaseUrl = 'https://kiriha.nephilim.jp'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"
$WranglerVersion = '4.110.0'
$VpkVersion = '1.2.0'
$RuntimeMatrix = @{
    'win-x64'   = @{ PlatformTarget = 'x64';   Channel = 'win' }
    'win-arm64' = @{ PlatformTarget = 'ARM64'; Channel = 'win-arm64' }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
$WorkDir = Join-Path $RepoRoot 'local-release'
$ArtifactsDir = Join-Path $WorkDir 'artifacts'

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    & $Block
    if ($LASTEXITCODE -ne 0) {
        throw "$Description が失敗しました (exit $LASTEXITCODE)"
    }
}

function Remove-WorkDirectory {
    if (-not (Test-Path $WorkDir)) { return }
    $resolved = (Resolve-Path $WorkDir).Path
    if (-not $resolved.StartsWith($RepoRoot + [IO.Path]::DirectorySeparatorChar)) {
        throw "作業ディレクトリがリポジトリ外です: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Write-Host '== プリフライト ==' -ForegroundColor Cyan
if (-not ${env:ProgramFiles(x86)}) {
    ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)'
}
# 一部の非対話実行環境ではOS環境変数が欠落し、Native AOTがWindows間の
# publishまでCross-OSと誤判定するため、Windows標準値を補完する。
if (-not $env:OS) {
    $env:OS = 'Windows_NT'
}
$vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vsInstallerDir*") {
    $env:PATH = "$env:PATH;$vsInstallerDir"
}
$env:DOTNET_ROLL_FORWARD = 'Major'

$versionNode = ([xml](Get-Content 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'Directory.Build.props からバージョンを取得できませんでした' }
Write-Host "バージョン: $version"

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -like "CN=$CertSubjectName*" -and $_.NotAfter -gt (Get-Date) }
if (-not $cert) {
    throw '署名証明書が見つかりません。SimplySign Desktop へログインしてください。'
}

$vpkInstalled = dotnet tool list --global |
    Where-Object { $_ -match '^vpk\s+' } |
    ForEach-Object { ($_ -split '\s+')[1] } |
    Where-Object { $_ -eq $VpkVersion }
if (-not $vpkInstalled) {
    dotnet tool uninstall --global vpk 2>$null | Out-Null
    Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
}
Write-Host "vpk: $VpkVersion"

Remove-WorkDirectory
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

foreach ($runtime in $Runtimes) {
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未対応のRuntimeIdentifierです: $runtime" }
    $publishDir = Join-Path $WorkDir "publish-$runtime"

    Write-Host "== publish: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet publish ($runtime)" {
        dotnet publish src/Kiriha/Kiriha.csproj -c Release -r $runtime `
            -p:PlatformTarget=$($config.PlatformTarget) -o $publishDir
    }
    if (-not (Test-Path (Join-Path $publishDir 'Kiriha.exe'))) {
        throw "Kiriha.exe がpublish出力にありません: $runtime"
    }

    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "vpk pack ($runtime)" {
        vpk pack `
            --packId Kiriha `
            --packVersion $version `
            --packTitle 'Kiriha' `
            --packAuthors '1llum1n4t1s' `
            --mainExe Kiriha.exe `
            --icon (Join-Path 'src' 'Kiriha' 'icon' 'app.ico') `
            --packDir $publishDir `
            --outputDir $ArtifactsDir `
            --channel $config.Channel `
            --shortcuts 'StartMenu,Desktop' `
            --signParams $SignParams
    }
}

Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($exe in Get-ChildItem $ArtifactsDir -Filter '*.exe') {
    $signature = Get-AuthenticodeSignature $exe.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証に失敗しました: $($exe.Name) ($($signature.Status))"
    }
    Write-Host "  ✅ $($exe.Name)"
}

if ($SkipUpload) {
    Write-Host "✅ 署名済み成果物: $ArtifactsDir" -ForegroundColor Green
    return
}

Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
$files = Get-ChildItem $ArtifactsDir -File
$orderedFiles = @($files | Where-Object { $_.Name -notlike 'releases.*.json' }) +
    @($files | Where-Object { $_.Name -like 'releases.*.json' })
foreach ($file in $orderedFiles) {
    Write-Host "  ↑ $($file.Name)"
    Invoke-Native "R2 put ($($file.Name))" {
        npx --yes "wrangler@$WranglerVersion" r2 object put "$Bucket/$($file.Name)" `
            --file $file.FullName --remote
    }
}

Write-Host '== 配信確認 ==' -ForegroundColor Cyan
$verifyDir = Join-Path $WorkDir 'remote-verification'
New-Item -ItemType Directory -Path $verifyDir -Force | Out-Null
foreach ($runtime in $Runtimes) {
    $channel = $RuntimeMatrix[$runtime].Channel
    $manifestName = "releases.$channel.json"
    $localManifest = Join-Path $ArtifactsDir $manifestName
    if (-not (Test-Path $localManifest)) { throw "$manifestName が生成されませんでした" }
    $remoteManifest = Join-Path $verifyDir $manifestName
    $response = Invoke-WebRequest -Uri "$BaseUrl/$manifestName`?verify=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())" `
        -Headers @{ 'Cache-Control' = 'no-cache' } -OutFile $remoteManifest -PassThru -TimeoutSec 30
    if ($response.StatusCode -ne 200) { throw "releases.$channel.json の配信確認に失敗しました" }
    $localManifestHash = (Get-FileHash $localManifest -Algorithm SHA256).Hash
    $remoteManifestHash = (Get-FileHash $remoteManifest -Algorithm SHA256).Hash
    if ($localManifestHash -ne $remoteManifestHash) { throw "$manifestName の配信内容がローカル成果物と一致しません" }

    $manifest = Get-Content $remoteManifest -Raw | ConvertFrom-Json
    foreach ($asset in $manifest.Assets) {
        if ($asset.Version -ne $version) { throw "$manifestName のバージョンが不正です: $($asset.Version)" }
        $remoteAsset = Join-Path $verifyDir $asset.FileName
        Invoke-WebRequest -Uri "$BaseUrl/$($asset.FileName)" -OutFile $remoteAsset -TimeoutSec 180
        if ((Get-Item $remoteAsset).Length -ne [long]$asset.Size) { throw "$($asset.FileName) のサイズが一致しません" }
        if ((Get-FileHash $remoteAsset -Algorithm SHA256).Hash -ne $asset.SHA256) { throw "$($asset.FileName) のSHA256が一致しません" }
    }
    Write-Host "  ✅ ${manifestName}: version/hash/size"
}

foreach ($setup in Get-ChildItem $ArtifactsDir -Filter '*-Setup.exe') {
    $remoteSetup = Join-Path $verifyDir $setup.Name
    Invoke-WebRequest -Uri "$BaseUrl/$($setup.Name)" -OutFile $remoteSetup -TimeoutSec 180
    if ((Get-FileHash $setup.FullName -Algorithm SHA256).Hash -ne (Get-FileHash $remoteSetup -Algorithm SHA256).Hash) {
        throw "$($setup.Name) の配信内容がローカル成果物と一致しません"
    }
    $remoteSignature = Get-AuthenticodeSignature $remoteSetup
    if ($remoteSignature.Status -ne 'Valid' -or $remoteSignature.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "$($setup.Name) の配信後署名検証に失敗しました"
    }
    Write-Host "  ✅ $($setup.Name): SHA256/署名"
}

Write-Host "🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
