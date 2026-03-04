$exePath = "C:\Users\Administrator\source\repos\DaemonApps\DaemonKit\bin\x64\Debug\net8.0-windows\win-x64\DaemonKit.exe"
$logsDir = "C:\Users\Administrator\source\repos\DaemonApps\DaemonKit\bin\x64\Debug\net8.0-windows\win-x64\Logs"
$totalRounds = 10
$startupWait = 25
$observeWait = 15
$results = @()

Write-Host "=== DaemonKit Kill/Restart Test ($totalRounds rounds) ==="
Write-Host "EXE: $exePath"
Write-Host ""

# Clean old logs
Get-ChildItem "$logsDir\DaemonKit-*.log" -ErrorAction SilentlyContinue | Remove-Item -Force

for ($i = 1; $i -le $totalRounds; $i++) {
    Write-Host "--- Round $i/$totalRounds ---"
    
    # Kill existing
    $existing = Get-Process -Name "DaemonKit" -ErrorAction SilentlyContinue
    if ($existing) {
        $existing | ForEach-Object {
            $wmiProc = Get-WmiObject Win32_Process -Filter "ProcessId=$($_.Id)"
            if ($wmiProc) { [void]$wmiProc.Terminate() }
        }
        Start-Sleep -Seconds 3
    }

    # Record log files before start
    $logsBefore = @(Get-ChildItem "$logsDir\DaemonKit-*.log" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)

    # Start DaemonKit
    Start-Process -FilePath $exePath -WindowStyle Normal
    Write-Host "  Started, waiting ${startupWait}s..."
    Start-Sleep -Seconds $startupWait

    # Check process alive
    $proc = Get-Process -Name "DaemonKit" -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Host "  FAIL: Process not running after ${startupWait}s"
        $results += "Round $i : FAIL (not started)"
        continue
    }

    # Observe
    Write-Host "  Process alive (PID=$($proc.Id)), observing ${observeWait}s..."
    Start-Sleep -Seconds $observeWait

    $proc2 = Get-Process -Name "DaemonKit" -ErrorAction SilentlyContinue
    if ($proc2) {
        Write-Host "  PASS: Still running"
        $results += "Round $i : PASS"
    } else {
        Write-Host "  FAIL: Process died during observe"
        $results += "Round $i : FAIL (died)"
    }

    # Find new log for this round
    $logsAfter = @(Get-ChildItem "$logsDir\DaemonKit-*.log" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    $newLogs = $logsAfter | Where-Object { $_ -notin $logsBefore }
    if ($newLogs) {
        foreach ($log in $newLogs) {
            $content = Get-Content "$logsDir\$log" -Raw -ErrorAction SilentlyContinue
            if ($content -match "\[看门狗\] UI线程无响应") {
                $watchdogLines = ($content -split "`n") | Where-Object { $_ -match "看门狗.*无响应" }
                Write-Host "  WARNING: Watchdog hang detected in $log ($($watchdogLines.Count) lines)"
            }
        }
    }
}

# Kill final
Get-Process -Name "DaemonKit" -ErrorAction SilentlyContinue | ForEach-Object {
    $wmiProc = Get-WmiObject Win32_Process -Filter "ProcessId=$($_.Id)"
    if ($wmiProc) { [void]$wmiProc.Terminate() }
}

Write-Host ""
Write-Host "=== Results ==="
$results | ForEach-Object { Write-Host $_ }
$passCount = ($results | Where-Object { $_ -match "PASS" }).Count
Write-Host ""
Write-Host "Total: $passCount/$totalRounds PASS"