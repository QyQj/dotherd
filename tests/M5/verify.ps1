#requires -Version 7.4
param([Parameter(Mandatory)][string]$Cli)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$temp = [IO.Directory]::CreateDirectory((Join-Path $repo '../temp')).FullName
$name = 'M5-' + [guid]::NewGuid().ToString('N').Substring(0,12)
$run = [IO.Directory]::CreateDirectory((Join-Path $temp $name)).FullName
$oldProfile = $env:DOTHERD_HOME
$env:DOTHERD_HOME = "$run/profile"

function Assert($ok, [string]$message) { if (!$ok) { throw $message } }
function State { & $Cli status tray-test | ConvertFrom-Json }
function InvokeInstance($instance) {
    $profile = Get-Content -LiteralPath "$env:DOTHERD_HOME/profile.id" -Raw
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "DotHerd-$($identity.User.Value)-$profile", 'InOut', 'Asynchronous,CurrentUserOnly')
    $identity.Dispose()
    try {
        $pipe.Connect(2000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ command = 'action'; name = 'tray-test'; action = 'open'; instance = $instance } | ConvertTo-Json -Compress))
        $reader = [IO.StreamReader]::new($pipe)
        return $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}

try {
    dotnet build "$repo/samples/ControlApp" -c Release --artifacts-path "$run/build" | Out-Host
    & $Cli add tray-test --readiness notify --action open -- "$run/build/bin/ControlApp/release/ControlApp.exe" | Out-Null
    $first = & $Cli start tray-test | ConvertFrom-Json
    $visible = & $Cli tray tray-test on | ConvertFrom-Json
    $hidden = & $Cli tray tray-test off | ConvertFrom-Json
    Assert (!$first.separateTray -and $visible.separateTray -and !$hidden.separateTray) 'Tray preference did not apply.'
    $limit = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $state = State
        if ($state.tooltip -eq 'Waiting for an action') { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $limit)
    $again = & $Cli tray tray-test on | ConvertFrom-Json
    Assert ($again.tooltip -eq 'Waiting for an action' -and $again.actions.open) 'Tray updates were lost while hidden.'
    & $Cli host exit --yes | Out-Null
    $restored = State
    Assert ($restored.separateTray) 'Host restart lost the user override.'
    $next = & $Cli start tray-test | ConvertFrom-Json
    Assert ($next.instance -ne $first.instance) 'Test did not replace the original instance.'
    $stale = InvokeInstance $first.instance
    Assert (!$stale.ok -and $stale.output -match 'instance changed') 'The host accepted an action from an old instance.'
    Assert ((InvokeInstance $next.instance).ok) 'The host rejected an action from the current instance.'
    & $Cli stop tray-test | Out-Null
    $stopped = State
    Assert (!$stopped.actions.open -and $stopped.separateTray) 'Stopped app did not disable the action while preserving visibility.'
    $default = & $Cli tray tray-test default | ConvertFrom-Json
    Assert (!$default.separateTray) 'Restoring the manifest default did not remove the override.'
    'PASS persisted/default tray preference, retained tray.update, current-instance and offline actions'
}
finally {
    try { & $Cli disable | Out-Null }
    finally {
        $env:DOTHERD_HOME = $oldProfile
        $resolved = (Resolve-Path -LiteralPath $run).Path
        if ((Split-Path $resolved -Parent) -ne $temp -or (Split-Path $resolved -Leaf) -ne $name) { throw "Unexpected cleanup path: $resolved" }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
