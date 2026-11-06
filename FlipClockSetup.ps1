# FlipClockSetup.ps1
# FlipClock 屏保的安装 / 卸载逻辑，由「安装屏保.bat」「卸载屏保.bat」调用。
# 所有系统改动都集中在这一段脚本里，方便逐行核对。
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Elevated,
    [string]$TargetSid
)

$ErrorActionPreference = 'Continue'
$ScriptDir = $PSScriptRoot
$ScrName   = 'FlipClock.scr'
$ResName   = 'FlipClock.ClockHtml'
$SysScr    = Join-Path $env:WINDIR ('System32\' + $ScrName)

function Step($msg) { Write-Host "[*] $msg" -ForegroundColor Cyan }
function Pause-Window {
    Write-Host ''
    Write-Host '按任意键关闭本窗口...' -ForegroundColor DarkGray
    try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch {}
}
function Die($msg) {
    Write-Host "[!] $msg" -ForegroundColor Red
    Pause-Window
    exit 1
}

# ---------- 提权：复制文件到 System32 需要管理员；注册表仍写登录用户的 SID ----------
$curSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if (-not $TargetSid) { $TargetSid = $curSid }
$RegPath = "HKU\$TargetSid\Control Panel\Desktop"
$FeaPath = "HKU\$TargetSid\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                 ('"' + $PSCommandPath + '"'), '-Elevated', '-TargetSid', $TargetSid)
    if ($Install)   { $argList += '-Install' }
    if ($Uninstall) { $argList += '-Uninstall' }
    # 自解压 EXE 是 32 位，从它启动 powershell.exe 会被 WOW64 重定向到 SysWOW64，
    # 之后写 System32 实际落到 SysWOW64，屏保下拉列表里就选不到。
    # Sysnative 只对 32 位进程可见，指向真正的 64 位 powershell.exe。
    $psExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $psNative = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path -LiteralPath $psNative) { $psExe = $psNative }
    try {
        Start-Process -FilePath $psExe -Verb RunAs -ArgumentList $argList -Wait
    } catch {
        Write-Host '[!] 已取消管理员授权，没有改动任何文件与设置。' -ForegroundColor Yellow
        Pause-Window
        exit 1
    }
    exit 0
}

# ---------- 卸载 ----------
if ($Uninstall) {
    Step '卸载 FlipClock 屏保'
    & reg.exe add $RegPath /v ScreenSaveActive /t REG_SZ /d 0 /f 2>$null | Out-Null
    & reg.exe delete $RegPath /v 'SCRNSAVE.EXE' /f 2>$null | Out-Null
    & reg.exe delete $FeaPath /v $ScrName /f 2>$null | Out-Null
    if (Test-Path -LiteralPath $SysScr) {
        try { Remove-Item -LiteralPath $SysScr -Force } catch { Write-Host '[!] 无法删除 System32 中的 .scr' -ForegroundColor Yellow }
    }
    $tmpHtml = Join-Path $env:TEMP 'FlipClock.html'
    if (Test-Path -LiteralPath $tmpHtml) { Remove-Item -LiteralPath $tmpHtml -Force -ErrorAction SilentlyContinue }
    $build = Join-Path $env:WINDIR 'Temp\FlipClockBuild'
    if (Test-Path -LiteralPath $build) {
        Remove-Item -LiteralPath (Join-Path $build 'clock.html'), (Join-Path $build 'shell.cs'), (Join-Path $build 'clock.scr') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $build -Force -ErrorAction SilentlyContinue
    }
    Write-Host '[OK] 已卸载，屏幕保护程序恢复为「无」。' -ForegroundColor Green
    Pause-Window
    exit 0
}

# ---------- 安装 ----------
Step '查找同目录下的 FlipClock.html 与壳子源码'
$html = $null
foreach ($n in @('clock.html', 'FlipClock.html', '翻页时钟.html')) {
    $p = Join-Path $ScriptDir $n
    if (Test-Path -LiteralPath $p) { $html = Get-Item -LiteralPath $p; break }
}
if (-not $html) {
    $html = Get-ChildItem -LiteralPath $ScriptDir -Filter '*.html' -File -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $html) { Die '同目录没找到 FlipClock.html（翻页时钟网页）。请把安装脚本和网页放在同一个文件夹。' }
$cs = $null
foreach ($n in @('shell.cs', 'FlipClockShell.cs')) {
    $p = Join-Path $ScriptDir $n
    if (Test-Path -LiteralPath $p) { $cs = $p; break }
}
if (-not $cs) { Die '同目录没找到 FlipClockShell.cs（WinForms 壳子源码）。' }

Step '定位 Windows 自带的 .NET Framework 编译器'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { Die '未找到 csc.exe，需要系统启用 .NET Framework 4.x。' }

# 在无空格目录里用纯相对文件名编译，避开中文路径、空格与引号转义问题
$build = Join-Path $env:WINDIR 'Temp\FlipClockBuild'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$htmlTmp = Join-Path $build 'clock.html'
$csTmp   = Join-Path $build 'shell.cs'
$scrTmp  = Join-Path $build 'clock.scr'
Copy-Item -LiteralPath $html.FullName -Destination $htmlTmp -Force
Copy-Item -LiteralPath $cs -Destination $csTmp -Force

Step '编译 .scr（网页作为资源嵌进去，成品不依赖外部文件）'
$resArg = '/resource:clock.html,' + $ResName
Push-Location $build
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:clock.scr `
       /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
       $resArg shell.cs
$rc = $LASTEXITCODE
Pop-Location
if ($rc -ne 0 -or -not (Test-Path -LiteralPath $scrTmp)) {
    Die '编译失败。请把上面 csc.exe 的报错发给助手；此时没有改动任何系统设置。'
}

Step '复制到系统屏保目录（下拉列表即可看到）'
try {
    Copy-Item -LiteralPath $scrTmp -Destination $SysScr -Force
} catch {
    Die '复制失败：请右键「安装屏保.bat」选「以管理员身份运行」。'
}

Step '写入当前用户的屏保设置'
$regErrors = 0
function Set-RegValue($path, $name, $type, $data) {
    & reg.exe add $path /v $name /t $type /d $data /f 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[!] 注册表项写入失败：$name" -ForegroundColor Yellow
        $script:regErrors++
    }
}
Set-RegValue $RegPath 'SCRNSAVE.EXE'        'REG_SZ'    $SysScr
Set-RegValue $RegPath 'ScreenSaveActive'    'REG_SZ'    '1'
Set-RegValue $RegPath 'ScreenSaverIsSecure' 'REG_SZ'    '0'
Set-RegValue $RegPath 'ScreenSaveTimeOut'   'REG_SZ'    '600'
# 让内嵌 WebBrowser 使用 IE11 文档模式（仅作用于本进程名，用户级）
Set-RegValue $FeaPath $ScrName              'REG_DWORD' '11001'

# 二次确认文件确实在真正的 System32 里，且每一项设置都写进去了
if (-not (Test-Path -LiteralPath $SysScr)) { Die '未能在 System32 中找到 FlipClock.scr，安装未完成。' }
if ($regErrors -gt 0) { Die "有 $regErrors 项设置未写入，请以管理员身份重新运行。" }

Remove-Item -LiteralPath $htmlTmp, $csTmp, $scrTmp -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $build -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '[OK] 安装完成：FlipClock 已设为当前屏保，空闲 10 分钟启动。' -ForegroundColor Green
Write-Host '     打开「屏幕保护程序设置」可预览、改等待时间；点「设置」直接改时钟选项，改动自动保存。'
Write-Host '     设置保存在 %LOCALAPPDATA%\FlipClock\config.json，屏保与设置窗口共用。'
Write-Host '     卸载：双击「卸载屏保.exe」或「卸载屏保.bat」。'
Pause-Window
exit 0
