$ErrorActionPreference = 'Stop'

$version = '8.1.2'
$build = "ffmpeg-$version-essentials_build"
$sha256 = 'db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec'
$directory = Join-Path $env:RUNNER_TEMP ("ffmpeg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$archive = Join-Path $directory "$build.zip"

Invoke-WebRequest -Uri "https://github.com/GyanD/codexffmpeg/releases/download/$version/$build.zip" `
    -OutFile $archive -MaximumRetryCount 3 -TimeoutSec 180
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
    throw 'FFmpeg archive SHA-256 mismatch.'
}

Expand-Archive -LiteralPath $archive -DestinationPath $directory
$ffmpeg = Join-Path $directory "$build/bin/ffmpeg.exe"
& $ffmpeg -version
if ($LASTEXITCODE -ne 0) { throw "FFmpeg could not start ($LASTEXITCODE)." }
Split-Path -Parent $ffmpeg | Add-Content -LiteralPath $env:GITHUB_PATH -Encoding utf8
