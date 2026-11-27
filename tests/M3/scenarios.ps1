# Run from tests/M2/verify.ps1 with its isolated profile, fixture and CLI.
function Wait-M3([scriptblock]$condition, [string]$message) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 80
    } while ($timer.Elapsed.TotalSeconds -lt 20)
    throw $message
}
function Start-M3Call([string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($cli)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $PWD.Path
    $info.RedirectStandardError = $info.RedirectStandardOutput = $true
    foreach ($arg in $arguments) { $info.ArgumentList.Add($arg) }
    return [Diagnostics.Process]::Start($info)
}
function M3Host {
    $p = @(Get-Process -Name DotHerd.Host -ErrorAction SilentlyContinue | Where-Object Path -eq $hostPath)
    Assert ($p.Count -eq 1) 'Expected one host for the isolated profile'
    return $p[0]
}
function Kill-M3Host {
    $p = M3Host
    try { $p.Kill(); Assert ($p.WaitForExit(5000)) 'Host did not terminate' }
    finally { $p.Dispose() }
}
function Wait-M3Disconnect([Diagnostics.Process]$call) {
    Assert ($call.WaitForExit(10000) -and $call.ExitCode -ne 0 -and $call.StandardError.ReadToEnd() -match 'disconnected') 'Interrupted command did not report a disconnect'
}

$app = 'M3.Managed'
$old = '{"schemaVersion":1,"command":"m2-worker","args":["serve"],"readiness":"notify"}'
Pack $app 1.0.0 $a $old
Pack $app 1.1.0 $a ($old.Replace('"serve"','"next"'))
Pack $app 1.2.0 $a ($old.Replace('"serve"','"fail"'))
Pack $app 1.3.0 $a ($old.Replace('"serve"','"paused-ready"'))
Pack $app 1.4.0 $a '{'
Pack M2.Plain 1.1.0 $a
Pack M2.Rid 1.1.0 $a $managed $null @('-p:RuntimeIdentifiers=win-x64')
Pack M2.Stdio 1.1.0 $a $null (Mcp M2.Stdio $standardArgs)

$firstM3 = Install "$app@1.0.0"
$dataM3 = $firstM3.cwd
$baseM3 = Split-Path $firstM3.package.directory
$recordM3 = Join-Path "$profile/apps" ((Split-Path $baseM3 -Leaf) + '.json')
$userData = Join-Path $dataM3 'keep.txt'
[IO.File]::WriteAllText($userData, 'user data')
Call @('update',"$app@1.4.0") 'app.json' | Out-Null
Assert ((State $app).instance -eq $firstM3.instance) 'Invalid candidate interrupted the active instance'

$next = Call @('update',"$app@1.1.0") | ConvertFrom-Json
Assert ($next.state -eq 'ready' -and $next.package.version -eq '1.1.0' -and $next.package.source -eq $a -and
    $next.instance -ne $firstM3.instance -and $next.cwd -eq $dataM3 -and !(Test-Path "$baseM3/1.0.0")) 'Update did not commit the ready candidate'
Assert (!(Get-Process -Id $firstM3.pid -ErrorAction SilentlyContinue)) 'Old version survived the switch'
Call @('update',"$app@1.2.0") 'code 23.*Restored 1.1.0' | Out-Null
$rollbackM3 = State $app
Assert ($rollbackM3.state -eq 'ready' -and $rollbackM3.package.version -eq '1.1.0' -and
    $rollbackM3.instance -ne $next.instance -and !(Test-Path "$baseM3/1.2.0")) 'Candidate failure did not restore the old version'
$rejectM3 = Join-Path $dataM3 'reject-old'
[IO.File]::WriteAllText($rejectM3, '')
Call @('update',"$app@1.2.0") 'code 23.*Rollback to 1.1.0 failed:.*code 24' | Out-Null
Assert ((State $app).updateFailure -match 'code 23.*code 24') 'Status omitted the two failure reasons'
Call @('stop',$app) | Out-Null
[IO.File]::Delete($rejectM3)
$stoppedM3 = Call @('update',"$app@1.0.0") | ConvertFrom-Json
Assert ($stoppedM3.state -eq 'stopped' -and !$stoppedM3.wantRunning -and !$stoppedM3.pid -and
    $stoppedM3.package.version -eq '1.0.0') 'Updating a stopped app started it'
Call @('start',$app) | Out-Null
'PASS M3 candidate validation, running and stopped updates, rollback and double failure'

Call @('start','M2.Stdio') | Out-Null
Call @('stop','M2.Stdio') | Out-Null
$stdioM3 = Call @('update','M2.Stdio','--yes') | ConvertFrom-Json
Assert ($stdioM3.package.version -eq '1.1.0' -and $stdioM3.state -eq 'stopped' -and
    $stdioM3.package.mcp.inputEnvironment.API_KEY -eq 'M2_FIXTURE_VALUE') 'Stopped stdio update lost its intent or input binding'

Call @('host','exit') 'not confirmed' 'n' | ForEach-Object { Assert ($_ -match $app) 'Exit preview did not list the running app' }
Assert ((State $app).state -eq 'ready') 'Declining exit stopped the app'
$loginM3 = Call @('startup','on') | ConvertFrom-Json
$hostM3 = M3Host
try { Call @('host','exit','--yes') | Out-Null; Assert ($hostM3.WaitForExit(20000)) 'Confirmed exit did not end the session' }
finally { $hostM3.Dispose() }

# Recreate the persisted state immediately after stopping the old process.
$registration = Get-Content -Raw -LiteralPath $recordM3 | ConvertFrom-Json -AsHashtable
$registration.discardVersion = '1.3.0'
$candidateM3 = [IO.Directory]::CreateDirectory("$baseM3/1.3.0").FullName
[IO.File]::WriteAllText("$candidateM3/partial", '')
$registration | ConvertTo-Json -Depth 30 -Compress | Set-Content -LiteralPath $recordM3 -Encoding utf8NoBOM
foreach ($entry in Get-ChildItem -LiteralPath "$profile/apps" -Filter '*.json') {
    $oldRecord = Get-Content -Raw -LiteralPath $entry.FullName | ConvertFrom-Json -AsHashtable
    if ($oldRecord.app.name -in 'M2.Plain','M2.Rid') {
        $oldRecord.package.Remove('asProcess') | Out-Null
        $oldRecord | ConvertTo-Json -Depth 30 -Compress | Set-Content -LiteralPath $entry.FullName -Encoding utf8NoBOM
    }
}
$loginLaunchM3 = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $loginM3.command }
Assert ($loginLaunchM3.ReturnValue -eq 0) 'The Run command could not start the host'
Wait-M3 { (State $app).state -eq 'ready' } 'Old version did not recover after the stop-old phase'
Assert ((M3Host).Id -eq $loginLaunchM3.ProcessId -and (State M2.Stdio).state -eq 'stopped') 'Login entry or stopped intent did not survive session exit'
Assert (!(Test-Path -LiteralPath $candidateM3) -and (State $app).package.version -eq '1.0.0') 'Recovery kept an uncommitted version'
$plainM3 = Call @('update','M2.Plain@1.1.0') | ConvertFrom-Json
Assert ($plainM3.package.version -eq '1.1.0' -and $plainM3.args[0] -ceq 'space value' -and
    $plainM3.args[1] -ceq '&literal') 'Legacy plain tool registration lost its argv'
$ridM3 = Call @('update','M2.Rid@1.1.0') | ConvertFrom-Json
Assert ($ridM3.package.version -eq '1.1.0' -and $ridM3.state -eq 'ready' -and $ridM3.executable.EndsWith('.exe')) 'Legacy RID update lost its manifest or native entry'

$oldM3 = State $app
$updateM3 = Start-M3Call @('update',"$app@1.3.0")
try {
    Wait-M3 { Test-Path -LiteralPath "$dataM3/candidate-started" } 'Candidate did not start before interruption'
    Assert (!(Get-Process -Id $oldM3.pid -ErrorAction SilentlyContinue) -and
        (State $app).package.version -eq '1.0.0') 'Candidate superseded the persisted version before readiness'
    Kill-M3Host
    Wait-M3Disconnect $updateM3
    $candidatePid = [int][IO.File]::ReadAllText("$dataM3/candidate-started")
    Wait-M3 { !(Get-Process -Id $candidatePid -ErrorAction SilentlyContinue) } 'Killed host left its candidate alive'
} finally { $updateM3.Dispose() }
Wait-M3 { (State $app).state -eq 'ready' } 'Old version did not restart after a killed update'
Assert ((State $app).package.version -eq '1.0.0' -and !(Test-Path "$baseM3/1.3.0") -and
    (Call @('logs',$app)) -match 'Recovered interrupted update') 'Killed update left an inconsistent registration'

$lockedOld = [IO.File]::Open("$baseM3/1.0.0/in-use.txt", 'CreateNew', 'ReadWrite', 'None')
try {
    Call @('update',"$app@1.1.0") 'Package cleanup pending' | Out-Null
    Assert ((State $app).package.version -eq '1.1.0' -and (State $app).recoveryError) 'Cleanup failure lost the committed candidate or diagnostic'
    Kill-M3Host
} finally { $lockedOld.Dispose() }
Wait-M3 { (State $app).state -eq 'ready' } 'Committed version did not restart after interrupted cleanup'
Assert ((State $app).package.version -eq '1.1.0' -and !(Test-Path "$baseM3/1.0.0")) 'Cleanup recovery removed the committed version'
'PASS M3 stop-old, unready candidate and post-commit recovery, legacy argv, and stopped stdio inputs'

[IO.File]::WriteAllText("$dataM3/hold-shutdown", '')
$uninstallM3 = Start-M3Call @('uninstall',$app)
try {
    Wait-M3 { Test-Path -LiteralPath "$dataM3/shutdown-received" } 'Uninstall did not ask the old version to stop'
    $tombstone = Get-Content -Raw -LiteralPath $recordM3 | ConvertFrom-Json
    Assert ($tombstone.uninstalling -and !$tombstone.wantRunning) 'Uninstall did not cancel restore before requesting shutdown'
    Kill-M3Host
    Wait-M3Disconnect $uninstallM3
} finally {
    $uninstallM3.Dispose()
    [IO.File]::Delete("$dataM3/hold-shutdown")
}
Call @('status',$app) 'Unknown application' | Out-Null
Assert (!(Test-Path $recordM3) -and !(Test-Path $baseM3) -and [IO.File]::ReadAllText($userData) -eq 'user data') 'Interrupted uninstall deleted data or kept the package'

$reinstalledM3 = Install "$app@1.0.0"
$lockedPath = Join-Path $reinstalledM3.package.directory 'in-use.txt'
$locked = [IO.File]::Open($lockedPath, 'CreateNew', 'ReadWrite', 'None')
try {
    Call @('uninstall',$app) 'in-use.txt|denied|拒绝' | Out-Null
    $remainingM3 = State $app
    Assert ($remainingM3.state -eq 'uninstalling' -and !$remainingM3.pid -and $remainingM3.recoveryError) 'Blocked cleanup lost the uninstall record or left the app running'
    Kill-M3Host
} finally { $locked.Dispose() }
Call @('status',$app) 'Unknown application' | Out-Null
Assert (!(Test-Path $reinstalledM3.package.directory) -and (Test-Path $reinstalledM3.cwd)) 'Recovery did not finish the blocked uninstall'

$localExeM3 = (Get-Command dotnet).Source
Call @('add','M3.Local','--cwd',$dataM3,'--',$localExeM3,'--version') | Out-Null
Call @('uninstall','M3.Local') | Out-Null
Assert ((Test-Path $localExeM3) -and [IO.File]::ReadAllText($userData) -eq 'user data') 'Local uninstall touched external files'

$finalM3 = Install "$app@1.0.0"
Call @('startup','on') | Out-Null
$hostM3 = M3Host
try { Call @('disable') | Out-Null; Assert ($hostM3.WaitForExit(20000)) 'Disable did not stop the host' }
finally { $hostM3.Dispose() }
Assert (!(Get-Process -Id $finalM3.pid -ErrorAction SilentlyContinue)) 'Disable left the app running'
Assert ((Call @('disable')) -match 'offline' -and !(Get-Process -Name DotHerd.Host -ErrorAction SilentlyContinue |
    Where-Object Path -eq $hostPath)) 'Offline disable started a host'
Wait-M3 { (State $app).state -eq 'ready' } 'Opening the manager did not restore the expected-running app'
Assert (!(Call @('startup','status') | ConvertFrom-Json).enabled -and (State M2.Stdio).state -eq 'stopped') 'Active use re-enabled login startup or restored a stopped service'
'PASS M3 confirmed exit, interrupted uninstall, user and local file retention, and online/offline disable'
