param(
  [string]$Text = "",
  [int]$DelaySec = 3,
  [switch]$NoEnter,
  [switch]$PasteMode
)

Add-Type -AssemblyName System.Windows.Forms

function Send-ScannerCode {
  param([string]$Code)

  if ([string]::IsNullOrWhiteSpace($Code)) {
    return
  }

  Write-Host ""
  Write-Host "Через $DelaySec сек переключись в окно браузера с TSD..." -ForegroundColor Yellow

  for ($i = $DelaySec; $i -gt 0; $i--) {
    Write-Host "$i..."
    Start-Sleep -Seconds 1
  }

  Write-Host "SEND: $Code" -ForegroundColor Cyan

  if ($PasteMode) {
    Set-Clipboard -Value $Code
    [System.Windows.Forms.SendKeys]::SendWait("^v")
  } else {
    [System.Windows.Forms.SendKeys]::SendWait($Code)
  }

  if (-not $NoEnter) {
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
  }

  Write-Host "OK" -ForegroundColor Green
}

if (-not [string]::IsNullOrWhiteSpace($Text)) {
  Send-ScannerCode -Code $Text
  exit 0
}

Write-Host "FlowStock TSD scanner simulator" -ForegroundColor Green
Write-Host "1) Открой TSD-страницу в браузере"
Write-Host "2) Введи HU-код здесь"
Write-Host "3) Во время отсчета переключись в браузер"
Write-Host "4) Симулятор отправит код + Enter"
Write-Host ""
Write-Host "Enter без кода = выход"
Write-Host ""

while ($true) {
  $code = Read-Host "HU / barcode"
  if ([string]::IsNullOrWhiteSpace($code)) {
    break
  }

  Send-ScannerCode -Code $code
}
