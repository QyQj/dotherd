#requires -Version 7.4
param([Parameter(Mandatory)][string]$Feed)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Feed = (Resolve-Path -LiteralPath $Feed).Path
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$temp = [IO.Directory]::CreateDirectory((Join-Path $repo '../temp')).FullName
$name = 'M5-Tray-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$run = [IO.Directory]::CreateDirectory((Join-Path $temp $name)).FullName
try {
    dotnet tool install DotHerd --version '[0.1.0-rc.1]' --tool-path "$run/t" --source $Feed
    $hostPath = @(Get-ChildItem -LiteralPath "$run/t" -Filter DotHerd.Host.exe -Recurse)
    if ($hostPath.Count -ne 1) { throw 'Expected one installed Windows Host.' }
    dotnet build "$repo/samples/ControlApp" -c Release --artifacts-path "$run/build"
    dotnet build "$PSScriptRoot/Tray" -c Release --artifacts-path "$run/build"
    & "$run/build/bin/Tray/release/Tray.exe" "$run/t/dotherd.exe" $hostPath[0].FullName "$run/h" "$run/build/bin/ControlApp/release/ControlApp.exe"
} finally {
    $resolved = (Resolve-Path -LiteralPath $run).Path
    if ((Split-Path $resolved -Parent) -ne $temp -or (Split-Path $resolved -Leaf) -ne $name) { throw "Unexpected cleanup path: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
