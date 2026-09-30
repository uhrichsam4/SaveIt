# Headless download smoke test: fresh LOCALAPPDATA so the yt-dlp/ffmpeg bootstrap really runs,
# then best + mp3 downloads of a small generic MP4, verified with ffprobe; plus a cancel test.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Work,
    [Parameter(Mandatory)] [string] $LogDir
)
$ErrorActionPreference = 'Stop'
$url = 'https://www.w3schools.com/html/mov_bbb.mp4'

New-Item -ItemType Directory -Force -Path $Work, $LogDir | Out-Null
$env:LOCALAPPDATA = Join-Path $Work 'localappdata'
$env:APPDATA = Join-Path $Work 'appdata'
$env:SAVEIT_NO_PATH_TOOLS = '1'      # ignore any yt-dlp/ffmpeg on the runner's PATH
if (Test-Path $env:LOCALAPPDATA) { Remove-Item -Recurse -Force $env:LOCALAPPDATA }
New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA, $env:APPDATA | Out-Null
$bin = Join-Path $env:LOCALAPPDATA 'SaveIt\bin'

function Invoke-Headless([string] $name, [string[]] $arguments) {
    $log = Join-Path $LogDir "download-$name.log"
    $env:SAVEIT_LOG = $log
    if (Test-Path $log) { Remove-Item $log }
    $argLine = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    Write-Host "> SaveIt.exe $argLine"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -ArgumentList $argLine -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput "$log.stdout.txt" -RedirectStandardError "$log.stderr.txt"
    Write-Host "exit code $($p.ExitCode) after $([int]$sw.Elapsed.TotalSeconds)s"
    if (Test-Path $log) { Get-Content $log | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host "  (no log file)"; Get-Content "$log.stdout.txt" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" } }
    $err = Get-Content "$log.stderr.txt" -Raw -ErrorAction SilentlyContinue
    if ($err) { Write-Host "stderr: $err" }
    return @{ Code = $p.ExitCode; Log = $log }
}

function Get-DonePath($log) {
    $m = Select-String -Path $log -Pattern '^done: (.+)$' | Select-Object -Last 1
    if (-not $m) { throw "no 'done:' line in $log" }
    return $m.Matches[0].Groups[1].Value.Trim()
}

function Probe([string] $path) {
    $ffprobe = Join-Path $bin 'ffprobe.exe'
    $json = & $ffprobe -v error -show_entries 'stream=codec_type,codec_name:format=duration,format_name' -of json -- $path
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed on $path" }
    Write-Host "ffprobe $([IO.Path]::GetFileName($path)): $($json -join '')"
    return ($json -join "`n") | ConvertFrom-Json
}

# 1) best — first run: must bootstrap the tools
$r = Invoke-Headless 'best' @('--saveit-test-download', $url, (Join-Path $Work 'out-best'), 'best')
if ($r.Code -ne 0) { throw "best download failed (exit $($r.Code))" }
if (-not (Select-String -Path $r.Log -Pattern '^setup: ' -Quiet)) { throw 'bootstrap did not run (no setup: lines)' }
foreach ($t in 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe') {
    if (-not (Test-Path (Join-Path $bin $t))) { throw "$t missing from $bin after bootstrap" }
}
Write-Host "bootstrap OK: $((Get-ChildItem $bin | ForEach-Object { "$($_.Name) $([math]::Round($_.Length/1MB,1))MB" }) -join ', ')"
$best = Get-DonePath $r.Log
if (-not (Test-Path -LiteralPath $best)) { throw "best output missing: $best" }
if ([IO.Path]::GetExtension($best) -ne '.mp4') { throw "best output is not .mp4: $best" }
$pb = Probe $best
$v = @($pb.streams | Where-Object codec_type -eq 'video')
if ($v.Count -lt 1) { throw 'best: no video stream' }
if ([double]$pb.format.duration -lt 5) { throw "best: duration too short ($($pb.format.duration))" }

# 2) mp3 — tools already present
$r = Invoke-Headless 'mp3' @('--saveit-test-download', $url, (Join-Path $Work 'out-mp3'), 'mp3')
if ($r.Code -ne 0) { throw "mp3 download failed (exit $($r.Code))" }
if (Select-String -Path $r.Log -Pattern '^setup: \d' -Quiet) { throw 'mp3 run re-downloaded the tools' }
$mp3 = Get-DonePath $r.Log
if ([IO.Path]::GetExtension($mp3) -ne '.mp3') { throw "mp3 output is not .mp3: $mp3" }
$pm = Probe $mp3
$a = @($pm.streams | Where-Object { $_.codec_type -eq 'audio' -and $_.codec_name -eq 'mp3' })
if ($a.Count -lt 1) { throw 'mp3: no mp3 audio stream' }

# 3) cancel — throttled, cancelled at 20%: must exit 1 and leave no partial files behind
$cancelDir = Join-Path $Work 'out-cancel'
$r = Invoke-Headless 'cancel' @('--saveit-test-download', $url, $cancelDir, 'best', '--limit-rate', '60K', '--cancel-at', '20')
if ($r.Code -ne 1) { throw "cancel test: expected exit 1, got $($r.Code)" }
if (-not (Select-String -Path $r.Log -Pattern '^cancelled$' -Quiet)) { throw 'cancel test: no cancelled line' }
$left = @(Get-ChildItem -LiteralPath $cancelDir -File -ErrorAction SilentlyContinue)
if ($left.Count -gt 0) { throw "cancel test left files: $($left.Name -join ', ')" }

Write-Host "SMOKE OK: best=$best mp3=$mp3; cancel cleaned up"
