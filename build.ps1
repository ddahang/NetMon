# build.ps1 - 编译 NetMon.exe（含内嵌图标），零依赖（使用系统自带 csc.exe）
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }

# 1) 图标：优先使用 favicon.ico，否则生成系统默认 app.ico
$ico = Join-Path $base 'favicon.ico'
if (-not (Test-Path $ico)) {
    $ico = Join-Path $base 'app.ico'
    if (-not (Test-Path $ico)) {
        Add-Type -AssemblyName System.Drawing
        $fs = [System.IO.File]::Create($ico)
        [System.Drawing.SystemIcons]::Application.Save($fs)
        $fs.Close()
        Write-Host "[OK] app.ico 生成（未找到 favicon.ico，使用默认图标）"
    }
} else {
    Write-Host "[OK] 使用 favicon.ico"
}

# 2) 源码统一补 UTF-8 BOM，防止中文乱码
$enc = New-Object System.Text.UTF8Encoding $true
Get-ChildItem "$base\*.cs" | ForEach-Object {
    if ($_.Name -eq 'genico.cs') { return }
    $c = Get-Content $_.FullName -Raw -Encoding UTF8
    [System.IO.File]::WriteAllText($_.FullName, $c, $enc)
}

# 3) 编译（winexe 无控制台窗口 + 内嵌图标）
& $csc /nologo /target:winexe /platform:anycpu /win32icon:"$ico" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    /out:"$base\NetMon.exe" "$base\NetMon.cs" 2>&1
if ($LASTEXITCODE -eq 0) { Write-Host "[OK] NetMon.exe 编译成功" -ForegroundColor Green }
else { Write-Host "[ERR] 编译失败，错误码 $LASTEXITCODE" -ForegroundColor Red; exit 1 }
