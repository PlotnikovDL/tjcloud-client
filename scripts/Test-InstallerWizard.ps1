[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$ExpectedDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Only the temporary GitHub-hosted Windows runner may exercise the installer.'
}
$evidence = New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP 'wizard-evidence')
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WizardControl {
    public IntPtr Handle;
    public string Text, Class;
    public uint Pid;
    public bool Enabled;
    delegate bool Visit(IntPtr window, IntPtr arg);
    [DllImport("user32")] static extern bool EnumWindows(Visit callback, IntPtr arg);
    [DllImport("user32")] static extern bool EnumChildWindows(IntPtr parent, Visit callback, IntPtr arg);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, UIntPtr size, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32")] static extern bool PostMessage(IntPtr window, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    public struct Rect { public int Left, Top, Right, Bottom; }
    public void Click() { if (!Enabled || !PostMessage(Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("Button unavailable: " + Text); }
    public static List<WizardControl> Visible(IntPtr parent, uint process) {
        var controls = new List<WizardControl>();
        Visit visit = (window, _) => {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (!IsWindowVisible(window) || (process != 0 && pid != process)) return true;
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            var text = new StringBuilder(4096);
            if (parent == IntPtr.Zero) GetWindowText(window, text, text.Capacity);
            else {
                UIntPtr result;
                SendMessageTimeout(window, 0x000D, (UIntPtr)(uint)text.Capacity, text, 2, 250, out result);
            }
            controls.Add(new WizardControl { Handle=window, Pid=pid, Class=name.ToString(),
                Text=text.ToString().Replace("&",""), Enabled=IsWindowEnabled(window) });
            return true;
        };
        if (parent == IntPtr.Zero) EnumWindows(visit, IntPtr.Zero);
        else EnumChildWindows(parent, visit, IntPtr.Zero);
        return controls;
    }
}
'@
function Wait-For([scriptblock]$Read, [string]$Description, [int]$Seconds = 20) {
    $limit = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $item = & $Read
        if ($item) { return $item }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $limit)
    throw "Timed out waiting for $Description"
}
function Find-Control($Window, [string]$Pattern, [switch]$Clickable, [int]$Seconds = 20) {
    Wait-For {
        [WizardControl]::Visible($Window.Handle,$Window.Pid) | Where-Object {
            $_.Text -match $Pattern -and (!$Clickable -or ($_.Enabled -and $_.Class -match 'Button|Link'))
        } | Select-Object -First 1
    } $Pattern $Seconds
}
function Save-Page($Window, [string]$Name) {
    $rows = [WizardControl]::Visible($Window.Handle,$Window.Pid) | Select-Object Class,Text,Enabled
    $rows | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence.FullName "$Name.json") -Encoding utf8
    $rect = New-Object WizardControl+Rect
    if (![WizardControl]::GetWindowRect($Window.Handle,[ref]$rect)) { throw 'Window rectangle unavailable.' }
    $image = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    try {
        $dc = $graphics.GetHdc()
        try { if (![WizardControl]::PrintWindow($Window.Handle,$dc,2)) { throw 'Wizard screenshot failed.' } }
        finally { $graphics.ReleaseHdc($dc) }
        $image.Save((Join-Path $evidence.FullName "$Name.png"),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Find-Dialog($Wizard, [string]$ChildPattern) {
    Wait-For {
        foreach ($window in [WizardControl]::Visible([IntPtr]::Zero,$Wizard.Pid)) {
            if ($window.Handle -ne $Wizard.Handle -and
                ([WizardControl]::Visible($window.Handle,$Wizard.Pid) | Where-Object Text -match $ChildPattern)) { return $window }
        }
    } $ChildPattern
}
$setup = Start-Process -FilePath $SetupPath -ArgumentList '/SP-','/NORESTART','/NOICONS' -WindowStyle Normal -PassThru
try {
    $wizard = Wait-For {
        [WizardControl]::Visible([IntPtr]::Zero,0) | Where-Object {
            $_.Class -eq 'TWizardForm' -and $_.Text -like 'Установка - TJ Cloud Client*'
        } | Select-Object -First 1
    } 'Russian setup window' 60
    $ancestor = $wizard.Pid
    while ($ancestor -ne $setup.Id) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ancestor"
        if (!$process -or !$process.ParentProcessId) { throw 'The wizard is not owned by this test.' }
        $ancestor = $process.ParentProcessId
    }
    [void](Find-Control $wizard 'Лицензия MIT')
    [void](Find-Control $wizard 'не принимаю условия')
    Save-Page $wizard '01-license'
    (Find-Control $wizard '^Я принимаю условия' -Clickable).Click()
    (Find-Control $wizard 'Далее' -Clickable).Click()
    [void](Find-Control $wizard ([regex]::Escape($ExpectedDirectory)))
    [void](Find-Control $wizard 'Выбор папки установки')
    Save-Page $wizard '02-directory'
    (Find-Control $wizard 'Обзор' -Clickable).Click()
    $browse = Find-Dialog $wizard 'Выберите папку'
    Save-Page $browse '03-browse'
    (Find-Control $browse '^Отмена$' -Clickable).Click()
    (Find-Control $wizard 'Далее' -Clickable).Click()
    [void](Find-Control $wizard 'готово к установке')
    [void](Find-Control $wizard 'Назад' -Clickable)
    Save-Page $wizard '04-ready'
    (Find-Control $wizard '^Отмена$' -Clickable).Click()
    $exitDialog = Find-Dialog $wizard 'продолжить установку'
    [void](Find-Control $exitDialog 'Да, выйти' -Clickable)
    Save-Page $exitDialog '05-exit'
    (Find-Control $exitDialog 'Нет, продолжить установку' -Clickable).Click()
    (Find-Control $wizard '^Установить$' -Clickable).Click()
    [void](Find-Control $wizard 'Завершение мастера установки' -Seconds 100)
    Save-Page $wizard '06-finished'
    (Find-Control $wizard '^Завершить$' -Clickable).Click()
    if (!$setup.WaitForExit(30000) -or $setup.ExitCode -ne 0) { throw 'Interactive installer did not complete.' }
    Write-Host 'PASS Russian license, destination, browse, ready, cancellation and finish screens.'
} catch {
    if (Get-Variable wizard -ValueOnly -ErrorAction SilentlyContinue) { Save-Page $wizard 'failed-page' }
    throw
} finally { $setup.Dispose() }
