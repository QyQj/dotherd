#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $repo '../temp')).FullName
$name = 'DotHerd M1 ' + [guid]::NewGuid().ToString('N')
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot $name)).FullName
$previousPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $run 'packages'
    $feed = (New-Item -ItemType Directory -Path "$run/feed").FullName
    dotnet pack "$repo/src/DotHerd/DotHerd.csproj" -c Release --disable-build-servers --artifacts-path "$run/build" -o $feed -p:EnableRuntimePackDownload=false
    dotnet tool install DotHerd --version '[0.1.0-rc.1]' --tool-path "$run/tools" --source $feed
    dotnet build "$repo/samples/ControlApp/ControlApp.csproj" -c Release --artifacts-path "$run/build" --source $feed
    dotnet build "$PSScriptRoot/M1.csproj" -c Release --artifacts-path "$run/build" --source $feed
    & "$run/build/bin/M1/release/M1.exe" "$run/tools/dotherd.exe" "$run/build/bin/ControlApp/release/ControlApp.exe" $run
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    $resolved = (Resolve-Path -LiteralPath $run).Path
    if ((Split-Path $resolved -Parent) -ne $tempRoot -or (Split-Path $resolved -Leaf) -ne $name) { throw "Unexpected cleanup path: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
