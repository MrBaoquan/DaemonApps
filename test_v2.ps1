$exePath = "C:\Users\Administrator\source\repos\DaemonApps\DaemonKit\bin\x64\Debug\net8.0-windows\win-x64\DaemonKit.exe"
$logsDir = "C:\Users\Administrator\source\repos\DaemonApps\DaemonKit\bin\x64\Debug\net8.0-windows\win-x64\Logs"

# Clean old dated logs
Get-ChildItem "$logsDir\DaemonKit-*.log" -ErrorAction SilentlyContinue | Remove-Item -Force

$results = @()
for ($i = 1; $i -le 10; $i++) {
    Write-Host "--- Round $i/10 ---"
    
    # Kill any existing
    $wmi = Get-WmiObject Win32_Process -Filter "Name='DaemonKit.exe'" -ErrorAction SilentlyContinue
    if ($wmi) {
        $wmi | ForEach-Object { $_.Terminate() | Out-Null }
        Write-Host "  Killed old process, waiting 8s for port release..."
        Start-Sleep -Seconds 8
    }

    # Start
    Start-Process -FilePath $exePath -WindowStyle Normal
    Write-Host "  Started, waiting 35s..."
    Start-Sleep -Seconds 35

    # Check
    $p = Get-Process -Name "DaemonKit" -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host "  PASS: running (PID=$($p.Id))"
        $results += "PASS"
    }
    else {
        Write-Host "  FAIL: not running"
        $results += "FAIL"
    }
}

# Final kill
$wmi = Get-WmiObject Win32_Process -Filter "Name='DaemonKit.exe'" -ErrorAction SilentlyContinue
if ($wmi) { $wmi | ForEach-Object { $_.Terminate() | Out-Null } }

# Check logs for watchdog warnings
Write-Host ""
Write-Host "=== Log Analysis ==="
$logFiles = Get-ChildItem "$logsDir\DaemonKit-*.log" -ErrorAction SilentlyContinue | Sort-Object Name
$hangCount = 0
foreach ($lf in $logFiles) {
    $content = Get-Content $lf.FullName -Raw -ErrorAction SilentlyContinue
    if ($content -match "\u770B\u95E8\u72D7.*\u65E0\u54CD\u5E94") {
        $hangCount++
        Write-Host "  HANG in: $($lf.Name)"
    }
}
Write-Host "  Total logs with watchdog hangs: $hangCount / $($logFiles.Count)"

Write-Host ""
Write-Host "=== Results ==="
for ($j = 0; $j -lt $results.Count; $j++) {
    Write-Host "  Round $($j+1): $($results[$j])"
}
$passCount = ($results | Where-Object { $_ -eq "PASS" }).Count
Write-Host ""
Write-Host "TOTAL: $passCount/10 PASS"
