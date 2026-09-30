# Launches each forced island state and screenshots the top-center 900x320 of the primary screen.
# Also drives a real hover (SetCursorPos) to check hover-expand / leave-collapse.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $LogDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace Win -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
'@
[Win.U]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir, $LogDir | Out-Null

$work = Join-Path $env:RUNNER_TEMP 'saveit-shots'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:LOCALAPPDATA = Join-Path $work 'localappdata'
$env:APPDATA = Join-Path $work 'appdata'
New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA, $env:APPDATA | Out-Null

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "primary screen: $bounds"

# A light, wallpaper-like backdrop so the black island and its edges are visible.
$backdropScript = Join-Path $work 'backdrop.ps1'
@'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'
$f.StartPosition = 'Manual'
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
# 2px short of the monitor so SaveIt doesn't treat it as a fullscreen app.
$f.Bounds = New-Object System.Drawing.Rectangle $b.X, $b.Y, $b.Width, ($b.Height - 2)
$f.BackColor = [System.Drawing.Color]::FromArgb(203, 210, 222)
$f.ShowInTaskbar = $false
[System.Windows.Forms.Application]::Run($f)
'@ | Set-Content -Path $backdropScript -Encoding UTF8
$backdrop = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $backdropScript -PassThru
Start-Sleep -Seconds 3

$w = 900; $h = 320
$x = $bounds.X + [int](($bounds.Width - $w) / 2); $y = $bounds.Y
$op = [System.Drawing.CopyPixelOperation]([int][System.Drawing.CopyPixelOperation]::SourceCopy -bor [int][System.Drawing.CopyPixelOperation]::CaptureBlt)

function Shot([string] $name) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h), $op)
    $file = Join-Path $OutDir "$name.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $file"
}

$failures = @()
[Win.U]::SetCursorPos($bounds.X + 40, $bounds.Y + $bounds.Height - 40) | Out-Null   # park the cursor away from the island

$states = 'collapsed', 'expanded', 'downloading', 'downloading-expanded', 'setup', 'done', 'done-expanded', 'error'
foreach ($s in $states) {
    $p = Start-Process -FilePath $Exe -ArgumentList "--saveit-state=$s" -PassThru
    Start-Sleep -Milliseconds 2500
    if ($p.HasExited) { $failures += "$s exited early (code $($p.ExitCode))" }
    Shot $s
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

# Real hover: no forced state. Cursor to the top-center pixel row -> expands; away -> collapses.
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 2
Shot 'hover-0-idle'
[Win.U]::SetCursorPos($bounds.X + [int]($bounds.Width / 2), $bounds.Y) | Out-Null
Start-Sleep -Milliseconds 1200
Shot 'hover-1-expanded'
[Win.U]::SetCursorPos($bounds.X + [int]($bounds.Width / 2), $bounds.Y + 600) | Out-Null
Start-Sleep -Milliseconds 1500
Shot 'hover-2-left'
if ($p.HasExited) { $failures += "hover run exited early (code $($p.ExitCode))" }
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

Stop-Process -Id $backdrop.Id -Force -ErrorAction SilentlyContinue
$appLog = Join-Path $env:LOCALAPPDATA 'SaveIt\saveit.log'
if (Test-Path $appLog) {
    Copy-Item $appLog (Join-Path $LogDir 'app-saveit.log')
    Write-Host "app log:"; Get-Content $appLog | ForEach-Object { Write-Host "  $_" }
    if (Select-String -Path $appLog -Pattern 'unhandled|fatal' -Quiet) { $failures += 'unhandled exception logged (see app-saveit.log)' }
}
if ($failures.Count) { throw ("screenshot run problems:`n" + ($failures -join "`n")) }
