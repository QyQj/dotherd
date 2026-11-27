#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$id = [guid]::NewGuid().ToString('N')
$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot '../../../temp')).FullName
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot "P02 $id")).FullName
$install = Join-Path $run 'tools'
$cli = Join-Path $install 'dotherd-p02.exe'
$previousPackages = $env:NUGET_PACKAGES
$terminal = $null

function Get-ProbeProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'P02Host.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$install\", [StringComparison]::OrdinalIgnoreCase) -and
        $_.CommandLine -match "\s(host|app|worker)\s+$id$"
    }
}

try {
    $feed = (New-Item -ItemType Directory -Path "$run/feed").FullName
    $env:NUGET_PACKAGES = Join-Path $run 'packages'
    dotnet --version
    dotnet pack "$PSScriptRoot/cli/Cli.csproj" -c Release --artifacts-path "$run/build" --source $feed -o $feed
    dotnet tool install DotHerd.P02 --version '[1.0.0]' --source $feed --tool-path $install
    $entry = @(Get-ChildItem -LiteralPath "$install/.store" -File -Recurse -Filter 'P02Host.exe')
    if ($entry.Count -ne 1) { throw "Expected one installed desktop host, found $($entry.Count)." }
    $hostPath = $entry[0].FullName

    $start = [Diagnostics.ProcessStartInfo]::new("$env:SystemRoot/System32/conhost.exe")
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($arg in '--headless', (Get-Command pwsh -CommandType Application | Select-Object -First 1).Path,
        '-NoProfile', '-File', "$PSScriptRoot/terminal.ps1", $run, $id) { $start.ArgumentList.Add($arg) }
    $terminal = [Diagnostics.Process]::Start($start)
    if (!$terminal.WaitForExit(45000)) { throw 'Test console did not close.' }
    if (Test-Path -LiteralPath "$run/terminal-error") { throw (Get-Content -Raw "$run/terminal-error") }
    if ($terminal.ExitCode -ne 0 -or !(Test-Path -LiteralPath "$run/terminal-result")) { throw 'Test console failed.' }

    $first, $second = foreach ($i in 1,2) { Get-Content -Raw "$run/cli$i.json" | ConvertFrom-Json }
    if ($first.cliPid -eq $second.cliPid -or !$first.cliConsole -or !$second.cliConsole -or
        $first.hostPid -ne $second.hostPid -or $first.hostConsole -or $second.hostConsole) {
        throw "Expected two console clients and one detached host. First: $first. Second: $second."
    }
    "CLI $($first.cliPid) -> host $($first.hostPid), exit 0"
    "CLI $($second.cliPid) -> host $($second.hostPid), exit 0"
    $state = & $cli status $id | ConvertFrom-Json
    $wait = [Diagnostics.Stopwatch]::StartNew()
    do {
        $rows = @(Get-ProbeProcesses)
        $hosts = @($rows | Where-Object CommandLine -Match "\shost\s+$id$")
        $apps = @($rows | Where-Object { $_.ParentProcessId -eq $state.hostPid -and $_.ProcessId -eq $state.appPid })
        $workers = @($rows | Where-Object { $_.ParentProcessId -eq $state.appPid -and $_.CommandLine -match "\sworker\s+$id$" })
        if ($hosts.Count -eq 1 -and $apps.Count -eq 1 -and $workers.Count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ($wait.Elapsed.TotalSeconds -lt 5)
    if ($state.hostPid -ne $first.hostPid -or $state.hostPath -ne $hostPath -or $hosts.Count -ne 1 -or
        $hosts[0].ProcessId -ne $state.hostPid) { throw 'Expected the same single host after console exit.' }
    "Console $($terminal.Id) exited; host $($state.hostPid) answered status; live hosts = $($hosts.Count)"

    if ($apps.Count -ne 1 -or $workers.Count -ne 1) { throw 'Expected a live host -> app -> worker tree.' }
    $tree = @($state.hostPid, $state.appPid, $workers[0].ProcessId | ForEach-Object { Get-Process -Id $_ })
    try {
        foreach ($node in $tree) { [void]$node.Handle }
        $tree[0].Kill()
        foreach ($node in $tree) {
            if (!$node.WaitForExit(5000)) { throw "Process $($node.Id) survived host termination." }
        }
        "Killed only host $($tree[0].Id); app $($tree[1].Id) and worker $($tree[2].Id) both exited"
    } finally {
        foreach ($node in $tree) { $node.Dispose() }
    }

    # Exercise the quoted Run command line without changing the user's login settings.
    $loginCommand = '"' + $hostPath + '" host ' + $id
    if ($loginCommand.Length -gt 260) { throw "Run command exceeds the 260-character Windows limit." }
    $launch = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $loginCommand }
    if ($launch.ReturnValue -ne 0) { throw "Login command creation failed: $($launch.ReturnValue)." }
    $login = & $cli status $id | ConvertFrom-Json
    $loginProcess = Get-Process -Id $login.hostPid
    if ($login.hostPid -ne $launch.ProcessId -or $login.hostConsole -or $login.hostPath -ne $hostPath -or
        $loginProcess.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) {
        throw 'The direct login command did not start the installed host without a console.'
    }
    "Direct login command -> host $($login.hostPid), console = $($login.hostConsole), length = $($loginCommand.Length)"
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    if ($terminal) {
        if (!$terminal.HasExited) { $terminal.Kill($true); $terminal.WaitForExit() }
        $terminal.Dispose()
    }
    foreach ($row in Get-ProbeProcesses) {
        $process = Get-Process -Id $row.ProcessId -ErrorAction Ignore
        if ($process) {
            try {
                if (!$process.HasExited -and $process.Path -eq $row.ExecutablePath) {
                    $process.Kill()
                    if (!$process.WaitForExit(5000)) { throw "Cleanup timed out for $($process.Id)." }
                }
            } finally { $process.Dispose() }
        }
    }
    $resolvedRun = (Resolve-Path -LiteralPath $run).Path
    if ((Split-Path $resolvedRun -Parent) -ne $tempRoot -or (Split-Path $resolvedRun -Leaf) -ne "P02 $id") {
        throw "Unexpected cleanup path: $resolvedRun"
    }
    Remove-Item -LiteralPath $resolvedRun -Recurse -Force
}
