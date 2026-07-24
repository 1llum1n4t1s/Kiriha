# Kiriha ライセンスキーの手動発行（再発行・特別発行用）
#
# 使い方:
#   pwsh scripts/sign-license.ps1 -Email user@example.com -PurchaseId pi_xxxx
#
# 通常の購入では Worker（/license/issue）が自動発行するため、このスクリプトは
# キー紛失時の再発行や手動発行にだけ使う。秘密鍵は dev\Secret\kiriha-license から読む。
param(
    [Parameter(Mandatory = $true)][string]$Email,
    [Parameter(Mandatory = $true)][string]$PurchaseId
)

$ErrorActionPreference = 'Stop'

$keyFile = 'C:\Users\IMT\dev\Secret\kiriha-license\signing-key.json'
$signing = Get-Content $keyFile -Raw | ConvertFrom-Json

$payload = [ordered]@{
    e = $Email.Trim().ToLowerInvariant()
    p = $PurchaseId.Trim()
    d = (Get-Date).ToUniversalTime().ToString('o')
}
$payloadJson = ($payload | ConvertTo-Json -Compress)
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payloadJson)

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportPkcs8PrivateKey([Convert]::FromBase64String($signing.privateKeyPkcs8), [ref]$null)
$signature = $ecdsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

function ConvertTo-Base64Url([byte[]]$bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# base64url 自体が '-' を含むため、payload と署名の区切りは '.'（JWT 風）
$key = "KIRIHA-$(ConvertTo-Base64Url $payloadBytes).$(ConvertTo-Base64Url $signature)"
Write-Host "ライセンスキー:"
Write-Host $key
