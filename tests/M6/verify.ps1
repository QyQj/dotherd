#requires -Version 7.4
param([Parameter(Mandatory)][string] $Source)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$feed = (Resolve-Path -LiteralPath $Source).Path
$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot '../../../temp')).FullName
$name = 'M6-' + [guid]::NewGuid().ToString('N')
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot $name)).FullName
$cli = "$run/cli/dotherd.exe"
$saved = @{}
try {
    $worker = (Get-Command serena -CommandType Application | Select-Object -First 1).Source
    & $worker --version
    dotnet --version
    dotnet build "$PSScriptRoot/SerenaChecks.csproj" -c Release --artifacts-path "$run/build"
    $checks = "$run/build/bin/SerenaChecks/release/SerenaChecks.exe"

    git init --quiet "$run/worktree-a"
    Set-Content -LiteralPath "$run/worktree-a/sample.py" -Value 'value = 42'
    git -C "$run/worktree-a" add -- sample.py
    git -C "$run/worktree-a" -c user.name='DotHerd M6' -c user.email=dotherd@example.invalid `
        -c commit.gpgsign=false commit --quiet -m 'Create M6 worktree fixture'
    git -C "$run/worktree-a" worktree add --quiet --detach "$run/worktree-b" HEAD
    Set-Content -LiteralPath "$run/worktree-b/sample.py" -Value 'value = 84'

    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start()
    try { $url = "http://127.0.0.1:$($socket.LocalEndpoint.Port)" } finally { $socket.Stop() }
    $trace = "$run/worker-wire.txt"
    $settings = @{
        DOTHERD_HOME = "$run/h"
        DOTNET_CLI_HOME = "$run/u"
        NUGET_PACKAGES = "$run/n"
        DOTNET_NOLOGO = '1'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        LOCALAPPDATA = "$run/local"
        SERENA_HOME = "$run/serena"
        SERENA_POOL_LANGUAGE = 'en'
        urls = $url
        Pool__Executable = $checks
        Pool__IdleTimeout = '00:00:03'
        Pool__WebDashboard = 'true'
        Pool__WebDashboardOpenOnLaunch = 'false'
        DOTHERD_P03_WORKER = $worker
        DOTHERD_P03_TRACE = $trace
        DOTHERD_P03_SLOW = '1'
    }
    foreach ($key in $settings.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    dotnet tool install DotHerd --version '[0.1.0-rc.1]' --source $feed --tool-path "$run/cli"
    & $cli install SerenaPool --source $feed | Out-Null
    & $cli startup off | Out-Null
    & $checks $cli "$run/worktree-a" "$run/worktree-b" $url
    $records = @(Get-Content -LiteralPath $trace)
    if (@($records -ceq 'server/discover ok').Count -lt 2 -or
        $records -cnotcontains 'tools/call 2026-07-28' -or ($records -cmatch '^initialize(\s|$)')) {
        throw 'Worker trace did not show successful discovery and calls without a legacy handshake.'
    }
    'PASS worker wire: server/discover 2026-07-28 and tools/call, zero legacy initialize requests'
} catch {
    if ($saved.ContainsKey('DOTHERD_HOME') -and (Test-Path -LiteralPath $cli)) {
        $PSNativeCommandUseErrorActionPreference = $false
        & $cli logs SerenaPool
        & $cli mcp list SerenaPool
        $PSNativeCommandUseErrorActionPreference = $true
    }
    if (Test-Path -LiteralPath "$run/worker-wire.txt") { Get-Content -LiteralPath "$run/worker-wire.txt" | Select-Object -Unique }
    throw
} finally {
    try {
        if ($saved.ContainsKey('DOTHERD_HOME') -and (Test-Path -LiteralPath $cli)) { & $cli disable | Out-Null }
    } finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        $full = (Resolve-Path -LiteralPath $run).Path
        if ((Split-Path $full -Parent) -ne $tempRoot -or (Split-Path $full -Leaf) -ne $name) { throw "Unexpected cleanup path: $full" }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
