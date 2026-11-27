#requires -Version 7.4
param([switch] $SlowWorker)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot '../../../temp')).FullName
$runName = 'P03-' + [guid]::NewGuid().ToString('N')
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot $runName)).FullName
$core = $null
try {
    $project = (New-Item -ItemType Directory -Path "$run/project").FullName
    git -C $project init --quiet
    Set-Content -LiteralPath "$project/sample.py" -Value 'value = 42'
    git -C $project add -- sample.py
    $worker = (Get-Command serena -CommandType Application | Select-Object -First 1).Source
    & $worker --version
    dotnet --version
    dotnet build "$PSScriptRoot/Probe.csproj" -c Release --artifacts-path "$run/build"
    dotnet tool install SerenaPool --version '[0.3.0]' --source https://api.nuget.org/v3/index.json --tool-path "$run/tools"
    $deps = Get-ChildItem -LiteralPath "$run/tools/.store" -Recurse -Filter 'SerenaPool.deps.json'
    $libs = (Get-Content -Raw -LiteralPath $deps.FullName | ConvertFrom-Json).libraries.PSObject.Properties.Name
    if ($libs -notcontains 'ModelContextProtocol.Core/2.4.0') { throw 'Unexpected Pool MCP SDK version.' }
    $probe = (Resolve-Path "$run/build/bin/Probe/release/Probe.exe").Path
    $pool = (Resolve-Path "$run/tools/serena-pool.exe").Path
    $trace = "$run/worker-wire.txt"
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start()
    try { $port = $socket.LocalEndpoint.Port } finally { $socket.Stop() }
    $url = "http://127.0.0.1:$port"
    $start = [Diagnostics.ProcessStartInfo]::new($pool)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $run
    foreach ($arg in '--core','--urls',$url) { $start.ArgumentList.Add($arg) }
    $start.Environment['Pool__WebDashboard'] = 'false'
    $start.Environment['Pool__Executable'] = $probe
    $start.Environment['DOTHERD_P03_WORKER'] = $worker
    $start.Environment['DOTHERD_P03_TRACE'] = $trace
    $start.Environment['DOTHERD_P03_SLOW'] = [int]$SlowWorker.IsPresent
    $core = [Diagnostics.Process]::Start($start)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $ready = [IO.Pipes.NamedPipeClientStream]::new('.', "serena-pool-core-$sid-$port", 'InOut')
    try { $ready.Connect(10000) } finally { $ready.Dispose() }
    if ($core.HasExited) { throw 'The isolated core exited during startup.' }

    $seen = 0
    foreach ($revision in '2026-07-28','2025-11-25') {
        try { & $probe --queue $revision $project $pool mcp --urls $url }
        catch {
            if (Test-Path -LiteralPath $trace) { Get-Content -LiteralPath $trace | Select-Object -Unique }
            throw
        }
        $records = @(Get-Content -LiteralPath $trace)
        $new = @($records | Select-Object -Skip $seen)
        if ($new -notcontains 'tools/call 2026-07-28') { throw "No modern worker call for frontend $revision." }
        $seen = $records.Count
        "worker wire for $revision = " + (($new | Select-Object -Unique) -join ', ')
    }
    if ($records -notcontains 'server/discover ok' -or $records -notcontains 'server/discover 2026-07-28') {
        throw 'Missing successful modern worker discovery.'
    }
} finally {
    if ($core) {
        if (!$core.HasExited) { $core.Kill(); $core.WaitForExit() }
        $core.Dispose()
    }
    $resolved = (Resolve-Path -LiteralPath $run).Path
    if ((Split-Path $resolved -Parent) -ne $tempRoot -or (Split-Path $resolved -Leaf) -ne $runName) {
        throw "Unexpected cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
