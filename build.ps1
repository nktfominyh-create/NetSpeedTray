$ErrorActionPreference = "Stop"

$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"

if (-not (Test-Path $csc)) {
    $fw = "C:\Windows\Microsoft.NET\Framework\v4.0.30319"
    $csc = Join-Path $fw "csc.exe"
}

$refs = @(
    "/reference:$fw\System.dll",
    "/reference:$fw\System.Drawing.dll",
    "/reference:$fw\System.Windows.Forms.dll"
)

& $csc /nologo /target:winexe /optimize+ /win32manifest:app.manifest /out:NetSpeedTray.exe @refs Program.cs

if ($LASTEXITCODE -eq 0) {
    Write-Host "OK: NetSpeedTray.exe"
} else {
    Write-Host "Build failed (exit code $LASTEXITCODE)"
    exit $LASTEXITCODE
}
