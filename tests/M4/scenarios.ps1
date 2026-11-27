# Run from tests/M2/verify.ps1 with its isolated profile and package feed.
Call @('host','exit','--yes') | Out-Null
function M4Manifest($id, $label) {
    Mcp $id (',"packageArguments":[{"type":"positional","value":"mcp"},{"type":"positional","value":"' + $label + '"}]')
}
Pack M4.Alpha 1.0.0 $a $null (M4Manifest M4.Alpha first)
Pack M4.Alpha 1.1.0 $a $null (M4Manifest M4.Alpha next)
Pack M4.Alpha 1.2.0 $a $null (M4Manifest M4.Alpha invalid)
Pack M4.Legacy 1.0.0 $a $null (M4Manifest M4.Legacy legacy)
Pack M4-Alpha 1.0.0 $a $null (M4Manifest M4-Alpha conflict)
Pack M4.Incompatible 1.0.0 $a '{"schemaVersion":1,"command":"m2-worker","kind":"mcp","args":["serve"],"readiness":"notify","mcp":{"transport":"stdio","args":["bridge","invalid"]}}'
Install 'M4.Alpha@1.0.0' @('--yes') | Out-Null
Install 'M4.Legacy@1.0.0' @('--yes') | Out-Null
Call @('install','M4-Alpha','--source',$a,'--yes') 'conflicts with M4.Alpha' | Out-Null
$incompatibleM4 = Install 'M4.Incompatible'
Wait-M3 { (Call @('mcp','list','M4.Incompatible') | ConvertFrom-Json).error -match 'Invalid MCP tool name' } 'Incompatible MCP did not report its protocol error'
Assert ((State 'M4.Incompatible').pid -eq $incompatibleM4.pid) 'MCP failure restarted a healthy resident process'
dotnet build "$PSScriptRoot/GatewayChecks.csproj" -c Release --artifacts-path "$run/gateway-check" | Out-Host
& "$run/gateway-check/bin/GatewayChecks/release/GatewayChecks.exe" $cli
