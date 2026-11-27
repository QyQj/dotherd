#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$packageId = 'DotHerd.P01.Probe'
$version = '1.0.0'
$project = Join-Path $PSScriptRoot 'tool/Probe.csproj'
$tempRoot = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot '../../../temp')).FullName
$run = (New-Item -ItemType Directory -Path (Join-Path $tempRoot ("dotherd-P01-" + [guid]::NewGuid()))).FullName

function Read-Entry($zip, $path) {
    $entry = $zip.GetEntry($path)
    if (!$entry) { throw "Missing package entry: $path" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Read-ProbePackage($feed, $id, $type, $toolsDir) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$id.$version.nupkg"))
    try {
        $metadata = ([xml](Read-Entry $zip "$id.nuspec")).package.metadata
        if ($metadata.id -cne $id -or $metadata.version -ne $version -or
            (@($metadata.packageTypes.packageType.name) -join ',') -cne $type) {
            throw "Unexpected package identity or type in $id.$version.nupkg"
        }
        $manifest = $null
        if ($type -eq 'DotnetTool') {
            $manifest = Read-Entry $zip '.dotherd/app.json' | ConvertFrom-Json
            if ($manifest.schemaVersion -ne 1) { throw "Unexpected schemaVersion in $id" }
        } elseif ($zip.GetEntry('.dotherd/app.json')) {
            throw "The RID implementation unexpectedly contains the top-level manifest"
        }
        $settings = ([xml](Read-Entry $zip "$toolsDir/DotnetToolSettings.xml")).DotNetCliTool
        $command = $settings.Commands.Command
        if ($command.HasAttribute('EntryPoint') -and !$zip.GetEntry("$toolsDir/$($command.EntryPoint)")) {
            throw "Missing SDK entry point in $id"
        }
        [pscustomobject]@{ Manifest = $manifest; Settings = $settings; Command = $command }
    } finally {
        $zip.Dispose()
    }
}

$previousPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $run 'packages'
    dotnet --version
    foreach ($source in 'A', 'B', 'RID') {
        $feed = (New-Item -ItemType Directory -Path (Join-Path $run "feed-$source")).FullName
        $ridOptions = @(if ($source -eq 'RID') { '-p:RuntimeIdentifiers=win-x64' })
        dotnet pack $project -c Release --artifacts-path "$run/build-$source" -o $feed --source $feed `
            "-p:Description=source-$source" -p:EnableRuntimePackDownload=false @ridOptions
    }

    foreach ($source in 'A', 'B', 'RID') {
        $feed = Join-Path $run "feed-$source"
        $toolsDir = if ($source -eq 'RID') { 'tools/any/any' } else { 'tools/net10.0/any' }
        $top = Read-ProbePackage $feed $packageId DotnetTool $toolsDir
        $manifest = $top.Manifest
        if ($manifest.command -cne 'dotherd-p01' -or $manifest.command -cne $top.Command.Name) {
            throw "Manifest and SDK command disagree in $feed"
        }

        $implementation = $top
        $installIds = @($packageId)
        if ($source -eq 'RID') {
            $rid = $top.Settings.RuntimeIdentifierPackages.RuntimeIdentifierPackage
            if ($rid.RuntimeIdentifier -cne 'win-x64' -or $rid.Id -cne "$packageId.win-x64") {
                throw "Unexpected RID mapping in $packageId"
            }
            $implementation = Read-ProbePackage $feed $rid.Id DotnetToolRidPackage 'tools/net10.0/win-x64'
            $installIds += $rid.Id
        }
        if ($implementation.Command.Name -cne $manifest.command -or !$implementation.Command.GetAttribute('EntryPoint')) {
            throw "Missing implementation for $($manifest.command) in $feed"
        }

        foreach ($id in $installIds) {
            $install = Join-Path $run "install-$source/$id"
            dotnet tool install $id --version "[$version]" --source $feed --tool-path $install
            $launcher = Get-Command (Join-Path $install $manifest.command) -CommandType Application
            $arguments = $manifest.args
            $output = @(& $launcher.Path @arguments)
            if ($output.Count -ne 1 -or $output[0] -cne "source-$source") { throw "Selected $feed but received '$output'" }
            Write-Host "$source | $id | $($launcher.Name) | $($implementation.Command.EntryPoint) | $output | exit $LASTEXITCODE"
        }
    }
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    $resolvedRun = (Resolve-Path -LiteralPath $run).Path
    if ((Split-Path $resolvedRun -Parent) -ne $tempRoot) { throw "Unexpected cleanup path: $resolvedRun" }
    Remove-Item -LiteralPath $resolvedRun -Recurse -Force
}
