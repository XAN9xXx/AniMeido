<#
.SYNOPSIS
    AniMeido 插件签名工具 - 生成密钥对 / 签署插件清单
.DESCRIPTION
    用法:
      # 首次使用：生成密钥对
      .\sign-plugin.ps1 -GenerateKeyPair

      # 签署插件
      .\sign-plugin.ps1 -PluginDir "Plugins\AnimeAssist.Plugin.Base" -PrivateKeyBase64 "<私钥>"

      # 签署插件（从文件读取私钥）
      .\sign-plugin.ps1 -PluginDir "Plugins\AnimeAssist.Plugin.Base" -PrivateKeyFile "key.pem"
#>

param(
    [switch]$GenerateKeyPair,
    [string]$PluginDir,
    [string]$PrivateKeyBase64,
    [string]$PrivateKeyFile
)

function Generate-KeyPair {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $priv = [Convert]::ToBase64String($rsa.ExportRSAPrivateKey())
    $pub = [Convert]::ToBase64String($rsa.ExportRSAPublicKey())

    $output = @"
==================================================
AniMeido 插件签名密钥对
==================================================

公钥（粘贴到 PluginSignatureVerifier.cs 的 EmbeddedPublicKey）：
$pub

私钥（安全保管，不要提交到 Git）：
$priv

==================================================
"@
    Write-Host $output

    # 同时写入文件
    $pub | Out-File -Encoding utf8NoBOM "plugin_public_key.txt"
    Write-Host "公钥已写入 plugin_public_key.txt"
    Write-Host "警告：私钥仅显示在控制台，请立即安全保存！"
}

function Sign-Plugin {
    if (-not $PluginDir) {
        Write-Error "请指定 -PluginDir"
        return
    }

    $manifestPath = Join-Path $PluginDir "plugin.json"
    $dllName = Split-Path $PluginDir -Leaf
    $dllPath = Join-Path $PluginDir "$dllName.dll"

    if (-not (Test-Path $dllPath)) {
        Write-Error "未找到插件 DLL: $dllPath"
        return
    }

    # 读取或创建 manifest
    if (Test-Path $manifestPath) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        Write-Host "已加载现有清单: $manifestPath"
    }
    else {
        Write-Host "创建新清单: $manifestPath"
        $manifest = [PSCustomObject]@{
            pluginId      = "unknown"
            displayName   = "未知插件"
            version       = "0.0.0"
            minAppVersion = "0.0.0"
            entryAssembly = "$dllName.dll"
            hash          = ""
            hashAlgorithm = "SHA256"
            signature     = ""
        }
    }

    # 计算 DLL 哈希
    $hash = Get-FileHash $dllPath -Algorithm SHA256
    $manifest.hash = $hash.Hash.ToLower()
    Write-Host "DLL 哈希: $($manifest.hash)"

    # 获取私钥
    $privKey = $PrivateKeyBase64
    if ($PrivateKeyFile -and (Test-Path $PrivateKeyFile)) {
        $privKey = Get-Content $PrivateKeyFile -Raw
        $privKey = $privKey.Trim()
    }

    if (-not $privKey) {
        Write-Error "请提供私钥 (-PrivateKeyBase64 或 -PrivateKeyFile)"
        return
    }

    # 规范化 payload（不含 signature）
    $payload = [PSCustomObject]@{
        displayName   = $manifest.displayName
        entryAssembly = $manifest.entryAssembly
        hash          = $manifest.hash
        hashAlgorithm = $manifest.hashAlgorithm
        minAppVersion = $manifest.minAppVersion
        pluginId      = $manifest.pluginId
        version       = $manifest.version
    }
    $payloadJson = $payload | ConvertTo-Json -Compress

    # RSA 签名
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.ImportRSAPrivateKey([Convert]::FromBase64String($privKey), $null)
    $payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
    $sig = $rsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $manifest.signature = [Convert]::ToBase64String($sig)

    # 写入 manifest
    $manifestJson = $manifest | ConvertTo-Json
    $manifestJson | Out-File -Encoding utf8NoBOM $manifestPath
    Write-Host "清单已签署并写入: $manifestPath"
}

if ($GenerateKeyPair) {
    Generate-KeyPair
}
elseif ($PluginDir) {
    Sign-Plugin
}
else {
    Write-Host "用法:"
    Write-Host "  .\sign-plugin.ps1 -GenerateKeyPair"
    Write-Host "  .\sign-plugin.ps1 -PluginDir <路径> -PrivateKeyBase64 <私钥>"
    Write-Host "  .\sign-plugin.ps1 -PluginDir <路径> -PrivateKeyFile <密钥文件>"
}
