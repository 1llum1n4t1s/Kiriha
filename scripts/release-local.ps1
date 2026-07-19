# release-local.ps1 — ローカル署名付き Velopack リリース
#
# SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが必要で
# GitHub Actions からは署名できないため、リリースは本スクリプトでローカル実行する。
#
# 前提:
#   - SimplySign Desktop が接続済み (証明書が CurrentUser\My に見えていること)
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - C:\Users\IMT\dev\Secret\secrets.json に cloudflare.api_token があること
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)
#   pwsh scripts/release-local.ps1 -Runtimes win-x64   # 対象 RID を絞る (テスト用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string[]]$Runtimes = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Bucket = 'kiriha-updates'
$BaseUrl = 'https://kiriha.nephilim.jp'
$ZoneName = 'nephilim.jp'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'C:\Users\IMT\dev\Secret\secrets.json'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"
$WranglerVersion = '4.110.0'
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
Write-Host "署名証明書: $($cert.Subject) (期限 $($cert.NotAfter.ToString('yyyy-MM-dd')))"

# Velopack (vpk) は常に最新安定版を使う (ゆろ君ルール): NuGet から実行時に最新を解決して pin する
$VpkVersion = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/vpk/index.json' -TimeoutSec 30).versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
if (-not $VpkVersion) { throw 'vpk の最新安定版バージョンの取得に失敗しました (NuGet API)' }
Write-Host "vpk 最新安定版: $VpkVersion"

$vpkInstalled = (dotnet tool list --global | Select-String -SimpleMatch 'vpk') -match [regex]::Escape($VpkVersion)
if (-not $vpkInstalled) {
    dotnet tool uninstall --global vpk 2>$null | Out-Null
    Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
}
Write-Host "vpk: $VpkVersion"

# Cloudflare トークン (アップロード時のみ必要)
# zone 解決もここで行う: トークンに zone:read / cache purge 権限が無い場合に
# R2 アップロード後の途中失敗 (新ファイルだけ R2 に乗ってパージ・クリーンアップが
# 走らない半端なリリース) を避け、何もアップロードしていない時点で fail fast する
if (-not $SkipUpload) {
    $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
    if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
    $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId

    $cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
    $zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$ZoneName" -Headers $cfHeaders -TimeoutSec 30
    if (-not $zoneResp.success -or @($zoneResp.result).Count -eq 0) { throw "Cloudflare zone '$ZoneName' の取得に失敗しました (トークンの zone:read 権限を確認してください)" }
    $zoneId = $zoneResp.result[0].id
    Write-Host "Cloudflare zone: $ZoneName ($zoneId)"
}

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

    # README.txt 生成 (Markdown 記法を簡易除去してプレーンテキスト化)
    $content = Get-Content 'README.md' -Raw -Encoding utf8
    $content = $content -replace '!\[.*?\]\(.*?\)\r?\n?', ''
    $content = $content -replace '<img[^>]*/?>\r?\n?', ''
    $content = $content -replace '\[([^\]]+)\]\(([^\)]+)\)', '$1 ($2)'
    $content = $content -replace '(?m)^#{1,6}\s+', ''
    $content = $content -replace '\*\*([^*]+)\*\*', '$1'
    $content = $content -replace '`([^`]+)`', '$1'
    $content = $content -replace '(?m)^\| .+\|$', ''
    $content = $content -replace '(?m)^\|[-: ]+\|$', ''
    $content = $content -replace '(?m)^>\s*', ''
    $content = $content -replace '\r?\n{3,}', "`n`n"
    [System.IO.File]::WriteAllText((Join-Path $publishDir 'README.txt'), $content.Trim(), [System.Text.Encoding]::UTF8)

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
            --shortcuts 'StartMenuRoot,Desktop' `
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
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
$files = Get-ChildItem $ArtifactsDir -File
$orderedFiles = @($files | Where-Object { $_.Name -notlike 'releases.*.json' }) +
    @($files | Where-Object { $_.Name -like 'releases.*.json' })
$uploaded = 0
foreach ($file in $orderedFiles) {
    Write-Host "  ↑ $($file.Name)"
    Invoke-Native "R2 put ($($file.Name))" {
        pnpm dlx "wrangler@$WranglerVersion" r2 object put "$Bucket/$($file.Name)" `
            --file $file.FullName --remote
    }
    $uploaded++
}
Write-Host "✅ R2 アップロード完了: $uploaded ファイル"

# ---- Cloudflare エッジキャッシュのパージ ----
# 固定名ファイル (Setup.exe / Portable.zip / RELEASES* / releases.*.json / assets.*.json) は
# 毎リリースで中身が変わるのに URL が不変。CDN エッジが旧版を Cache-Control の max-age 分保持するため、
# パージしないと新規ダウンロード・自動更新が旧バージョンを掴む。バージョン付き nupkg は URL が一意
# (旧キャッシュなし) のためパージ不要。
# R2 アップロードは既に成功済みのため、パージ失敗はリリースを止めず warning-and-continue にする
# (CDN は max-age 経過で自然に新版へ追従する)。
Write-Host '== Cloudflare キャッシュパージ ==' -ForegroundColor Cyan
$purgeUrls = @($orderedFiles | Where-Object { $_.Name -notlike '*.nupkg' } | ForEach-Object { "$BaseUrl/$($_.Name)" })
if ($purgeUrls.Count -gt 0) {
    try {
        # purge_cache は 1 リクエストあたり最大 30 URL までのため分割送信する
        for ($i = 0; $i -lt $purgeUrls.Count; $i += 30) {
            $batch = $purgeUrls[$i..[Math]::Min($i + 29, $purgeUrls.Count - 1)]
            $purgeBody = ConvertTo-Json -InputObject @{ files = $batch } -Compress
            $purgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
                -Headers $cfHeaders -ContentType 'application/json' -Body $purgeBody -TimeoutSec 30
            if (-not $purgeResp.success) { throw "Cloudflare キャッシュパージに失敗しました: $($purgeResp.errors | ConvertTo-Json -Compress)" }
        }
        Write-Host "  ✅ パージ: $($purgeUrls.Count) URL"
        $purgeUrls | ForEach-Object { Write-Host "     $_" }
    } catch {
        Write-Warning "  Cloudflare キャッシュパージに失敗しました（アップロード済みリリースには影響なし、max-age 経過で自然反映されます）— $($_.Exception.Message)"
    }
} else {
    Write-Host '  パージ対象なし'
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

# ---- 旧バージョン nupkg のクリーンアップ (Aggressive 戦略) ----
# ローカル artifacts の manifest (= 今アップロードしたものと同一) から keep set を作り、
# R2 上の「.nupkg かつ manifest 外」だけを削除する。固定ファイル名 (Setup.exe /
# Portable.zip / RELEASES* / releases.*.json / assets.*.json) は対象外なので安全。
Write-Host '== 旧 nupkg クリーンアップ ==' -ForegroundColor Cyan
$keep = @{}
$manifests = Get-ChildItem $ArtifactsDir -Filter 'releases.*.json'
if (-not $manifests) { throw 'artifacts に releases.*.json が見つかりません' }
foreach ($m in $manifests) {
    foreach ($asset in (Get-Content $m.FullName -Raw | ConvertFrom-Json).Assets) {
        if ($asset.FileName) { $keep[$asset.FileName] = $true }
    }
}
Write-Host "  保持対象 nupkg: $($keep.Count) 件"

$api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket"
$headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

$allKeys = [System.Collections.Generic.List[string]]::new()
$cursor = ''
while ($true) {
    $uri = "$api/objects?per_page=1000" + $(if ($cursor) { "&cursor=$cursor" })
    $resp = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
    foreach ($obj in $resp.result) { $allKeys.Add($obj.key) }
    # 全件 1 ページに収まると result_info が省略される (StrictMode 下では直接参照が throw)
    $info = $resp.PSObject.Properties['result_info']
    if (-not $info -or -not $info.Value) { break }
    $truncated = $info.Value.PSObject.Properties['is_truncated']
    if (-not $truncated -or -not $truncated.Value) { break }
    $cursorProp = $info.Value.PSObject.Properties['cursor']
    $cursor = if ($cursorProp) { $cursorProp.Value } else { '' }
    if (-not $cursor) { break }
}

$toDelete = $allKeys | Where-Object { $_ -like '*.nupkg' -and -not $keep.ContainsKey($_) }
if (-not $toDelete) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    foreach ($key in $toDelete) {
        $encoded = [uri]::EscapeDataString($key)
        try {
            Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
            Write-Host "  🗑️  $key"
            $deleted++
        } catch {
            Write-Warning "  削除失敗: $key — $($_.Exception.Message)"
            $failed++
        }
    }
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗"
    # 全件失敗は token 権限等の異常なので fail (一部失敗は次回リリースで再試行される)
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
