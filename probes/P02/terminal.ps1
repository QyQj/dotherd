#requires -Version 7.4
param([string] $Run, [string] $Id)
$ErrorActionPreference = 'Stop'
$pipe = "DotHerd.P02.$Id"
$ready = [Threading.Semaphore]::new(0, 2, "$pipe.ready")
$gate = [Threading.EventWaitHandle]::new($false, 'ManualReset', "$pipe.go")
$clients = @()
try {
    $start = [Diagnostics.ProcessStartInfo]::new("$Run/tools/dotherd-p02.exe")
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in 'start', $Id, '--sync-cold') { $start.ArgumentList.Add($arg) }
    foreach ($i in 1,2) {
        $client = [Diagnostics.Process]::Start($start)
        $clients += [pscustomobject]@{
            Process = $client
            Output = $client.StandardOutput.ReadToEndAsync()
            Error = $client.StandardError.ReadToEndAsync()
        }
    }
    if (!$ready.WaitOne(15000) -or !$ready.WaitOne(15000)) { throw 'Both CLI processes must observe a cold start.' }
    [void]$gate.Set()
    for ($i = 0; $i -lt 2; $i++) {
        $client = $clients[$i]
        if (!$client.Process.WaitForExit(15000)) { throw "CLI $($i + 1) timed out." }
        if ($client.Process.ExitCode -ne 0) { throw $client.Error.GetAwaiter().GetResult() }
        Set-Content -LiteralPath "$Run/cli$($i + 1).json" -Value $client.Output.GetAwaiter().GetResult()
    }
    Set-Content -LiteralPath "$Run/terminal-result" -Value 'Both CLI exit codes: 0'
} catch {
    Set-Content -LiteralPath "$Run/terminal-error" -Value $_
    throw
} finally {
    [void]$gate.Set()
    foreach ($client in $clients) {
        if (!$client.Process.HasExited) { $client.Process.Kill() }
        $client.Process.Dispose()
    }
    $gate.Dispose()
    $ready.Dispose()
}
