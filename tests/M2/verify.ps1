#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $repo '../temp')).FullName
$name = 'M2-' + [guid]::NewGuid().ToString('N').Substring(0,12)
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot $name)).FullName
$manager, $a, $b, $caller = 'manager','A','B','caller' | ForEach-Object { [IO.Directory]::CreateDirectory("$run/$_").FullName }
$version = '0.1.0-preview.2'
$fixture = "$PSScriptRoot/Fixture"
$profile = "$run/h"
$cli = "$run/u/.dotnet/tools/dotherd.exe"
$saved = @{}
foreach ($key in 'DOTHERD_HOME','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_NOLOGO','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','M2_FIXTURE_VALUE') {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
$original = $PWD
function Assert($ok, $message) { if (!$ok) { throw $message } }
function Pack($id, $v, $dest, $app = $null, $mcp = $null, [string[]]$extra = @()) {
    $opts = @("-p:PackageId=$id","-p:Version=$v","-p:Description=origin-$(Split-Path $dest -Leaf)",'-p:EnableRuntimePackDownload=false')
    if ($null -ne $app) {
        $app | Set-Content -LiteralPath "$run/app.json" -Encoding utf8NoBOM
        $opts += "-p:AppFile=$run/app.json"
    }
    if ($null -ne $mcp) {
        $mcp | Set-Content -LiteralPath "$run/server.json" -Encoding utf8NoBOM
        $opts += "-p:McpFile=$run/server.json"
    }
    dotnet pack $fixture -c Release --artifacts-path "$run/f-$id-$v-$(Split-Path $dest -Leaf)" --source $dest -o $dest @opts @extra | Out-Host
}
function Call([string[]]$arguments, [string]$failure, [string]$stdin = '', [string]$program = $cli) {
    $p = [Diagnostics.ProcessStartInfo]::new($program)
    $p.WorkingDirectory = $PWD.Path
    $p.UseShellExecute = $false
    $p.RedirectStandardInput = $p.RedirectStandardOutput = $p.RedirectStandardError = $true
    $p.StandardOutputEncoding = $p.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($arg in $arguments) { $p.ArgumentList.Add($arg) }
    $proc = [Diagnostics.Process]::Start($p)
    try {
        $out = $proc.StandardOutput.ReadToEndAsync()
        $err = $proc.StandardError.ReadToEndAsync()
        if ($stdin) { $proc.StandardInput.WriteLine($stdin) }
        $proc.StandardInput.Close()
        if (!$proc.WaitForExit(60000)) { throw "CLI timed out: $arguments" }
        $stdout = $out.Result.Trim()
        $stderr = $err.Result.Trim()
        if ($failure) {
            Assert ($proc.ExitCode -ne 0 -and $stderr -match $failure) "Expected '$failure' for $arguments, got $($proc.ExitCode) $stderr $stdout"
            return $stderr
        }
        Assert ($proc.ExitCode -eq 0) "$arguments failed: $stderr $stdout"
        return $stdout
    } finally { $proc.Dispose() }
}
function State($id) { Call @('status',$id) | ConvertFrom-Json }
function Install($id, [string[]]$options = @()) { Call (@('install',$id,'--source',$a) + $options) | ConvertFrom-Json }
function Mcp($id, $body) { '{"packages":[{"registryType":"nuget","identifier":"' + $id + '","transport":{"type":"stdio"}' + $body + '}]}' }

try {
    # Build dependencies from the repository configuration; install every tool only from the test feeds.
    dotnet pack "$repo/src/DotHerd" -c Release --artifacts-path "$run/build" -p:EnableRuntimePackDownload=false -o $manager | Out-Host
    $env:DOTHERD_HOME = $profile
    $env:DOTNET_CLI_HOME = "$run/u"
    $env:NUGET_PACKAGES = "$run/n"
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:M2_FIXTURE_VALUE = 'do-not-print-this-fixture-value'
    $managed = '{"schemaVersion":1,"command":"m2-worker","args":["serve","two words"],"readiness":"notify"}'
    Pack M2.Managed 1.0.0 $a $managed
    Pack M2.Managed 1.1.0 $a $managed
    Pack M2.Managed 1.1.0 $b $managed
    Pack M2.Managed 2.0.0-beta.1 $b $managed
    Pack M2.Rid 1.0.0 $a $managed $null @('-p:RuntimeIdentifiers=win-x64')
    Pack M2.Plain 1.0.0 $a
    Pack M2.Library 1.0.0 $a $null $null @('-p:PackAsTool=false')
    Pack M2.Broken 1.0.0 $a '{'
    Pack M2.Schema 1.0.0 $a ($managed.Replace('"schemaVersion":1','"schemaVersion":2'))
    Pack M2.Command 1.0.0 $a ($managed.Replace('m2-worker','foreign'))
    Pack M2.Icon 1.0.0 $a '{"schemaVersion":1,"command":"m2-worker","tray":{"icon":"../escape.png"}}'
    Pack M2.Fails 1.0.0 $a ($managed.Replace('"serve","two words"','"fail"'))
    $standardArgs = ',"packageArguments":[{"type":"positional","value":"mcp"}],"environmentVariables":[{"name":"API_KEY","isRequired":true,"isSecret":true}]'
    Pack M2.Stdio 1.0.0 $a $null (Mcp M2.Stdio $standardArgs)
    $http = '{"packages":[{"registryType":"nuget","identifier":"M2.Http","transport":{"type":"streamable-http","url":"http://127.0.0.1:{port}/mcp","headers":[{"name":"Authorization","isRequired":true,"isSecret":true}]},"packageArguments":[{"type":"positional","valueHint":"port","default":"8765"}]}]}'
    Pack M2.Http 1.0.0 $a $null $http
    Pack M2.Remote 1.0.0 $a $null '{"remotes":[{"type":"streamable-http","url":"https://example.org/mcp"}]}'
    $dual = '{"schemaVersion":1,"command":"m2-worker","readiness":"notify","kind":"mcp","mcp":{"transport":"stdio","args":["bridge"]}}'
    $bridge = ',"packageArguments":[{"type":"positional","value":"bridge"}]'
    Pack M2.Dual 1.0.0 $a $dual (Mcp M2.Dual $bridge)
    Pack M2.Conflict 1.0.0 $a $dual (Mcp M2.Conflict ($bridge.Replace('bridge','different')))
    Pack DotHerd 0.0.1 $a $null $null @('-p:ToolCommandName=dotherd')
    [IO.File]::WriteAllText("$caller/NuGet.Config", "<configuration><packageSources><clear/><add key='A' value='$a'/><add key='B' value='$b'/></packageSources></configuration>")
    Set-Location -LiteralPath $caller
    $dnx = @("DotHerd@$version",'--source',$manager,'--yes','--')
    & dnx @dnx --help | Out-Null
    Assert ((& dnx @dnx --version) -eq $version -and !(Test-Path $profile) -and !(Test-Path $cli)) 'dnx help/version initialized the manager'

    $first = (& dnx @dnx install M2.Managed --source B) | ConvertFrom-Json
    Assert ((Test-Path $cli) -and $first.package.version -eq '1.1.0' -and $first.package.source -eq $b -and $first.state -eq 'ready') 'dnx handoff or stable selection'
    Assert ((Call @('logs','M2.Managed')) -match 'origin-B') 'wrong source binary'
    $login = Call @('startup','status') | ConvertFrom-Json
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $actual = Get-ItemPropertyValue -LiteralPath $runKey -Name $login.valueName
    Assert ($actual -eq $login.command -and $actual -match '^"([^"]+)" --home "([^"]+)"$') 'invalid actual Run command'
    $hostPath = $Matches[1]
    Assert ((Test-Path $hostPath) -and $hostPath.StartsWith([IO.Path]::GetFullPath("$run/u/.dotnet/tools/.store/"),[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFullPath($Matches[2]) -eq [IO.Path]::GetFullPath($profile)) 'Run points outside the persistent global tool'
    Assert ((Call @('install','M2.Managed') | ConvertFrom-Json).instance -eq $first.instance) 'reinstall restarted a running app'
    Call @('install','M2.Managed@1.0.0') 'update' | Out-Null
    Call @('stop','M2.Managed') | Out-Null
    Assert ((Call @('install','M2.Managed@1.1.0') | ConvertFrom-Json).state -eq 'stopped') 'reinstall started a stopped app'
    Call @('install','M2.Managed@1.0.0') 'update' | Out-Null
    Assert ((State M2.Managed).state -eq 'stopped') 'version mismatch restarted app'
    Call @('host','exit') | Out-Null
    $started = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $actual }
    Assert ($started.ReturnValue -eq 0) 'Run command failed to launch'
    Assert ((State M2.Managed).state -eq 'stopped' -and (Get-Process -Id $started.ProcessId).Path -eq $hostPath) 'direct login launch or stopped intent'
    Call @('startup','off') | Out-Null
    $again = (& dnx @dnx install M2.Managed) | ConvertFrom-Json
    Assert ($again.state -eq 'stopped' -and !(Call @('startup','status') | ConvertFrom-Json).enabled) 'dnx/global reuse re-enabled startup'
    'PASS clean dnx bootstrap, persistent Run target, compatible global reuse, exact source/version, stopped idempotency'

    $plain = Call @('install','M2.Plain','--source',$a)
    Assert ($plain -match 'm2-worker' -and $plain -match '--as-process') 'plain tool did not preview commands'
    Call @('status','M2.Plain') 'Unknown application' | Out-Null
    $resident = Install M2.Plain @('--as-process','--command','m2-worker','--','space value','&literal')
    Assert ($resident.args[1] -ceq '&literal' -and $resident.state -eq 'running') 'plain tool arguments'
    $rid = Install M2.Rid
    Assert ($rid.state -eq 'ready' -and $rid.executable.EndsWith('.exe') -and (Call @('logs','M2.Rid')) -match 'origin-A') 'RID concrete entry'
    foreach ($bad in @{
        'M2.Rid.win-x64'='top-level';'M2.Library'='no independently installable';'M2.Broken'='app.json';'M2.Schema'='schemaVersion';
        'M2.Command'='command';'M2.Icon'='tray.icon';'M2.Conflict'='mcp.args';'M2.Remote'='remote endpoints'
    }.GetEnumerator()) {
        Call @('install',$bad.Key,'--source',$a) $bad.Value | Out-Null
        Call @('status',$bad.Key) 'Unknown application' | Out-Null
    }
    Call @('install','M2.Fails','--source',$a) 'code 23' | Out-Null
    Assert ((State M2.Fails).package.version -eq '1.0.0' -and (Call @('logs','M2.Fails')) -match 'code 23') 'lost failed installation'
    Call @('stop','M2.Fails') | Out-Null
    'PASS ordinary tool opt-in, native RID entry, library and malformed manifests rejected, first launch failure retained'

    Call @('install','M2.Stdio','--source',$a,'--yes') 'required input API_KEY' | Out-Null
    $confirm = @('install','M2.Stdio','--source',$a,'--input-env','API_KEY=M2_FIXTURE_VALUE')
    $declined = Call $confirm 'not confirmed' 'n'
    Assert ($declined -match 'stdio' -and $declined -match 'mcp' -and $declined -match 'API_KEY' -and $declined -notmatch $env:M2_FIXTURE_VALUE) 'incorrect MCP preview'
    Call @('status','M2.Stdio') 'Unknown application' | Out-Null
    $service = Call $confirm $null 'y' | ConvertFrom-Json
    Assert ($service.state -eq 'registered' -and !$service.pid -and $service.package.mcp.args[0] -eq 'mcp') 'stdio registration launched a process'
    $serviceFile = Get-ChildItem "$profile/apps/*.json" | Where-Object { (Get-Content -Raw $_ | ConvertFrom-Json).app.name -eq 'M2.Stdio' }
    Assert ((Get-Content -Raw $serviceFile) -notmatch $env:M2_FIXTURE_VALUE) 'secret persisted'
    Call @('stop','M2.Stdio') | Out-Null
    Assert ((Install M2.Stdio).state -eq 'stopped' -and !(State M2.Stdio).wantRunning) 'reinstall enabled a stopped stdio service'
    $both = Install M2.Dual
    Assert ($both.state -eq 'ready' -and !$both.package.mcp.onConnection -and $both.package.mcp.args[0] -eq 'bridge') 'resident MCP and bridge mixed'
    Call @('install','M2.Http','--source',$a,'--yes') 'required input Authorization' | Out-Null
    $web = Install M2.Http @('--yes','--input-env','Authorization=M2_FIXTURE_VALUE')
    Assert ($web.state -eq 'running' -and $web.package.mcp.url -eq 'http://127.0.0.1:8765/mcp' -and
        $web.package.mcp.headers.Authorization -eq '{Authorization}') 'local HTTP inputs or URL'
    'PASS required input, MCP preview/confirmation, idle stdio, loopback HTTP and dual-manifest resident MCP'

    Call @('host','exit') | Out-Null
    $env:DOTHERD_HOME = "$run/g"
    $env:DOTNET_CLI_HOME = "$run/guser"
    dotnet tool install -g DotHerd --version "[$version]" --source $manager | Out-Host
    $globalCli = "$run/guser/.dotnet/tools/dotherd.exe"
    $init = Call @() $null '' $globalCli | ConvertFrom-Json
    Assert ($init.enabled) 'empty global command did not initialize'
    try {
        Call @('install','M2.Managed') 'multiple sources.*--source' '' $globalCli | Out-Null
        $selected = Call @('install','M2.Managed@1.1.0','--source','A') $null '' $globalCli | ConvertFrom-Json
        Assert ($selected.package.source -eq $a -and (Call @('logs','M2.Managed') $null '' $globalCli) -match 'origin-A') 'feed B leaked into A'
    } finally {
        Call @('startup','off') $null '' $globalCli | Out-Null
        Call @('host','exit') $null '' $globalCli | Out-Null
    }
    $env:DOTNET_CLI_HOME = "$run/legacy"
    $env:DOTHERD_HOME = "$run/legacy-profile"
    dotnet tool install -g DotHerd --version '[0.0.1]' --source $a | Out-Host
    $errorText = Call (@('dnx') + $dnx + @('install','M2.Plain','--source',$a)) 'global DotHerd 0.0.1.*dotnet tool update' '' (Get-Command dotnet).Source
    Assert (!(Test-Path $env:DOTHERD_HOME) -and $errorText.Contains($version)) 'incompatible dnx initialized host or omitted versions'
    'PASS clean global install, ambiguous feeds, same-version source isolation and incompatible global diagnostic'
} finally {
    Set-Location $original
    try {
        foreach ($test in @(@('h','u'), @('g','guser'))) {
            $env:DOTHERD_HOME = "$run/$($test[0])"
            $env:DOTNET_CLI_HOME = "$run/$($test[1])"
            $idFile = "$env:DOTHERD_HOME/profile.id"
            if (!(Test-Path -LiteralPath $idFile)) { continue }
            $ownKey = 'DotHerd-' + [guid]::ParseExact([IO.File]::ReadAllText($idFile),'N').ToString('N')
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            if ($key) { try { $key.DeleteValue($ownKey,$false) } finally { $key.Dispose() } }
            $entry = "$env:DOTNET_CLI_HOME/.dotnet/tools/dotherd.exe"
            if (Test-Path $entry) { Call @('host','exit') $null '' $entry | Out-Null }
        }
    } finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        $full = (Resolve-Path -LiteralPath $run).Path
        if ((Split-Path $full -Parent) -ne $tempRoot -or (Split-Path $full -Leaf) -ne $name) { throw "Unexpected cleanup path: $full" }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
