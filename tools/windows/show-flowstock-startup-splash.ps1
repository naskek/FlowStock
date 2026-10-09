[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StatusPath,

    [ValidateRange(1, 600)]
    [int] $TimeoutSeconds = 120,

    [switch] $Headless
)

$ErrorActionPreference = 'Stop'

function Read-StartupStatus {
    if (-not (Test-Path -LiteralPath $StatusPath -PathType Leaf)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-TrackedProcessExited {
    $pidPath = "$StatusPath.pid"
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
        return $false
    }

    $trackedProcessId = 0
    try {
        $pidText = Get-Content -LiteralPath $pidPath -Raw
    } catch {
        return $false
    }

    if (-not [int]::TryParse($pidText.Trim(), [ref]$trackedProcessId) -or $trackedProcessId -le 0) {
        return $false
    }

    try {
        Get-Process -Id $trackedProcessId -ErrorAction Stop | Out-Null
        return $false
    } catch {
        return $true
    }
}

function Get-Outcome($Status, [Diagnostics.Stopwatch] $Stopwatch) {
    if ($null -ne $Status) {
        if ($Status.state -eq 'ready') {
            return 'ready'
        }

        if ($Status.state -eq 'error') {
            return 'error'
        }

        if ($Status.state -eq 'starting' -and (Test-TrackedProcessExited)) {
            return 'process-exited'
        }
    }

    if ($Stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        return 'timeout'
    }

    return $null
}

function Get-FailureText($Status, [string] $Outcome) {
    if ($Outcome -eq 'timeout') {
        return "FlowStock не сообщил о готовности за $TimeoutSeconds сек."
    }

    if ($Outcome -eq 'process-exited') {
        return 'Процесс FlowStock завершился до готовности интерфейса.'
    }

    if ($null -ne $Status -and -not [string]::IsNullOrWhiteSpace([string]$Status.message)) {
        return [string]$Status.message
    }

    return 'Не удалось завершить запуск FlowStock.'
}

$watch = [Diagnostics.Stopwatch]::StartNew()

if ($Headless) {
    while ($true) {
        $status = Read-StartupStatus
        $outcome = Get-Outcome $status $watch
        if ($outcome -eq 'ready') {
            exit 0
        }

        if ($outcome -in @('error', 'process-exited')) {
            [Console]::Error.WriteLine("FLOWSTOCK_STARTUP_ERROR: " + (Get-FailureText $status $outcome))
            exit 2
        }

        if ($outcome -eq 'timeout') {
            [Console]::Error.WriteLine("FLOWSTOCK_STARTUP_TIMEOUT: " + (Get-FailureText $status $outcome))
            exit 3
        }

        Start-Sleep -Milliseconds 100
    }
}

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$window = New-Object Windows.Window
$window.Title = 'FlowStock'
$window.Width = 440
$window.Height = 220
$window.MinWidth = 440
$window.MinHeight = 220
$window.ResizeMode = [Windows.ResizeMode]::NoResize
$window.WindowStartupLocation = [Windows.WindowStartupLocation]::CenterScreen
$window.ShowInTaskbar = $true

$root = New-Object Windows.Controls.Grid
$root.Margin = New-Object Windows.Thickness -ArgumentList 28

0..4 | ForEach-Object {
    $row = New-Object Windows.Controls.RowDefinition
    $row.Height = [Windows.GridLength]::Auto
    $root.RowDefinitions.Add($row)
}

$title = New-Object Windows.Controls.TextBlock
$title.Text = 'FlowStock'
$title.FontSize = 28
$title.FontWeight = [Windows.FontWeights]::SemiBold
[Windows.Controls.Grid]::SetRow($title, 0)
$root.Children.Add($title) | Out-Null

$statusText = New-Object Windows.Controls.TextBlock
$statusText.Text = 'Запуск FlowStock…'
$statusText.FontSize = 15
$statusText.Margin = New-Object Windows.Thickness -ArgumentList 0, 12, 0, 12
[Windows.Controls.Grid]::SetRow($statusText, 1)
$root.Children.Add($statusText) | Out-Null

$progress = New-Object Windows.Controls.ProgressBar
$progress.IsIndeterminate = $true
$progress.Height = 5
$progress.Margin = New-Object Windows.Thickness -ArgumentList 0, 0, 0, 12
[Windows.Controls.Grid]::SetRow($progress, 2)
$root.Children.Add($progress) | Out-Null

$details = New-Object Windows.Controls.TextBlock
$details.TextWrapping = [Windows.TextWrapping]::Wrap
$details.Visibility = [Windows.Visibility]::Collapsed
$details.MaxHeight = 55
[Windows.Controls.Grid]::SetRow($details, 3)
$root.Children.Add($details) | Out-Null

$close = New-Object Windows.Controls.Button
$close.Content = 'Закрыть'
$close.Width = 100
$close.Height = 30
$close.HorizontalAlignment = [Windows.HorizontalAlignment]::Right
$close.Margin = New-Object Windows.Thickness -ArgumentList 0, 12, 0, 0
$close.Visibility = [Windows.Visibility]::Collapsed
$close.Add_Click({ $window.Close() })
[Windows.Controls.Grid]::SetRow($close, 4)
$root.Children.Add($close) | Out-Null

$window.Content = $root

function Show-Failure([string] $Message, [string] $LogPath) {
    $statusText.Text = 'Запуск не завершён'
    $progress.IsIndeterminate = $false
    $progress.Visibility = [Windows.Visibility]::Collapsed
    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        $details.Text = $Message
    } else {
        $details.Text = $Message + [Environment]::NewLine + 'Диагностика: ' + $LogPath
    }
    $details.Visibility = [Windows.Visibility]::Visible
    $close.Visibility = [Windows.Visibility]::Visible
}

$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(150)
$timer.Add_Tick({
    $state = Read-StartupStatus
    $outcome = Get-Outcome $state $watch

    if ($null -ne $state -and
        $state.state -eq 'starting' -and
        -not [string]::IsNullOrWhiteSpace([string]$state.message)) {
        $statusText.Text = [string]$state.message
    }

    if ($outcome -eq 'ready') {
        $timer.Stop()
        $window.Close()
        return
    }

    if ($outcome -in @('error', 'process-exited', 'timeout')) {
        $timer.Stop()
        $logPath = if ($null -ne $state) { [string]$state.logPath } else { '' }
        Show-Failure (Get-FailureText $state $outcome) $logPath
    }
})

$window.Add_Closed({ $timer.Stop() })
$timer.Start()
[void]$window.ShowDialog()
