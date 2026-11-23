#requires -Version 7.0

$ErrorActionPreference = 'Stop'

function Publish-ReleaseArchives {
    param([string]$Stage, [string]$OutputDirectory, [ref]$PreserveStage)

    $lockPath = Join-Path $OutputDirectory '.release-publish.lock'
    $publishLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $archives = foreach ($name in @('TURZX-Studio-win-x64.zip', 'TURZX-WinUsbDisplay-SDK.zip')) {
            $target = Join-Path $OutputDirectory $name
            $backup = Join-Path $Stage "$name.previous"
            $hadPrevious = [IO.File]::Exists($target)
            if ($hadPrevious) {
                [IO.File]::Copy($target, $backup)
            }
            @{
                Source = Join-Path $Stage $name
                Target = $target
                Backup = $backup
                HadPrevious = $hadPrevious
            }
        }

        $published = 0
        try {
            foreach ($archive in $archives) {
                [IO.File]::Move($archive.Source, $archive.Target, $true)
                $published++
            }
        }
        catch {
            $publishFailure = $_.Exception
            try {
                for ($index = $published - 1; $index -ge 0; $index--) {
                    $archive = $archives[$index]
                    if ($archive.HadPrevious) {
                        [IO.File]::Move($archive.Backup, $archive.Target, $true)
                    }
                    else {
                        [IO.File]::Delete($archive.Target)
                    }
                }
            }
            catch {
                $PreserveStage.Value = $true
                throw [AggregateException]::new("Release publication and rollback failed. Backups remain in $Stage.",
                    [Exception[]]@($publishFailure, $_.Exception))
            }
            throw
        }
    }
    finally {
        $publishLock.Dispose()
    }
}

$stage = $null
$preserveStage = $false
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $output = Join-Path (Get-Location).Path 'artifacts'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $outputDirectory = Get-Item -LiteralPath $output
    if ($outputDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "The artifacts directory must be a local directory: $output"
    }
    # 每次构建使用独立的暂存目录，避免将上一轮的文件打入发布包。
    $stageName = [guid]::NewGuid().ToString('N')
    $stage = (New-Item -ItemType Directory -Path (Join-Path $output $stageName)).FullName
    $studio = Join-Path $stage 'Studio'
    $sdk = Join-Path $stage 'SDK'

    # 发布程序集省略调试符号和指向本机构建目录的 PDB 路径。
    $releaseProperties = @('-p:DebugType=None', '-p:DebugSymbols=false')
    dotnet publish src/TURZX.WinUsbDisplay.Studio -c Release -r win-x64 --self-contained true -o $studio --artifacts-path "$stage/studio-build" @releaseProperties
    if ($LASTEXITCODE -ne 0) { throw "Studio publish failed ($LASTEXITCODE)." }

    dotnet build src/TURZX.WinUsbDisplay.Media -c Release --artifacts-path "$stage/sdk-build" @releaseProperties
    if ($LASTEXITCODE -ne 0) { throw "SDK build failed ($LASTEXITCODE)." }

    New-Item -ItemType Directory -Path "$sdk/lib", "$sdk/docs", "$sdk/examples/SdkDiscover" | Out-Null
    Copy-Item "$stage/sdk-build/bin/TURZX.WinUsbDisplay/release/TURZX.WinUsbDisplay.dll",
        "$stage/sdk-build/bin/TURZX.WinUsbDisplay/release/TURZX.WinUsbDisplay.xml",
        "$stage/sdk-build/bin/TURZX.WinUsbDisplay.Media/release/TURZX.WinUsbDisplay.Media.dll",
        "$stage/sdk-build/bin/TURZX.WinUsbDisplay.Media/release/TURZX.WinUsbDisplay.Media.xml" -Destination "$sdk/lib"
    Copy-Item LICENSE, NOTICE.md, NOTICE.zh-CN.md, SECURITY.md, SECURITY.zh-CN.md -Destination $sdk
    Copy-Item README.sdk-package.md -Destination "$sdk/README.md"
    Copy-Item README.sdk-package.zh-CN.md -Destination "$sdk/README.zh-CN.md"
    Copy-Item docs/developer-guide*.md, docs/win207-protocol*.md -Destination "$sdk/docs"
    Copy-Item examples/SdkDiscover/Program.cs, examples/SdkDiscover/SdkDiscover.csproj -Destination "$sdk/examples/SdkDiscover"

    Copy-Item LICENSE, NOTICE.md, NOTICE.zh-CN.md, SECURITY.md, SECURITY.zh-CN.md -Destination $studio
    Copy-Item README.studio-package.md -Destination "$studio/README.md"
    Copy-Item README.studio-package.zh-CN.md -Destination "$studio/README.zh-CN.md"
    Copy-Item docs/scenes.md, docs/scenes.en.md, docs/compatibility.md, docs/compatibility.en.md,
        docs/first-display.md, docs/first-display.en.md -Destination "$studio/docs"

    # 从暂存目录构建示例，验证打包后的 SDK 文件足以编译。
    dotnet build "$sdk/examples/SdkDiscover/SdkDiscover.csproj" -c Release --artifacts-path "$stage/example-build"
    if ($LASTEXITCODE -ne 0) { throw "The packaged SDK example failed to build ($LASTEXITCODE)." }

    # 两个归档都成功生成后再替换发布目录中的成品。
    $studioZip = Join-Path $stage 'TURZX-Studio-win-x64.zip'
    $sdkZip = Join-Path $stage 'TURZX-WinUsbDisplay-SDK.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($studio, $studioZip)
    [IO.Compression.ZipFile]::CreateFromDirectory($sdk, $sdkZip)
    Publish-ReleaseArchives -Stage $stage -OutputDirectory $output -PreserveStage ([ref]$preserveStage)
}
finally {
    try {
        if (!$preserveStage -and $null -ne $stage -and (Test-Path -LiteralPath $stage)) {
            $resolvedOutput = (Resolve-Path -LiteralPath $output).ProviderPath
            $resolvedStage = (Resolve-Path -LiteralPath $stage).ProviderPath
            if ($resolvedStage -ne (Join-Path $resolvedOutput $stageName) -or $resolvedOutput -ne $output -or
                ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unexpected release staging directory: $resolvedStage"
            }
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
    catch {
        Write-Warning "Could not clean the release staging directory. $_"
    }
    finally {
        Pop-Location
    }
}
