#Requires -RunAsAdministrator
param([string]$BridgeExecutable = (Join-Path $PSScriptRoot 'Bridge.App.exe'))
$ErrorActionPreference = 'Stop'
$resolvedBridge = (Resolve-Path -LiteralPath $BridgeExecutable).Path
if ([IO.Path]::GetFileName($resolvedBridge) -ne 'Bridge.App.exe') { throw 'Select the published Bridge.App.exe.' }
# Windows may have created a block rule when its first-launch prompt was dismissed.
# Keep that block on public networks; the explicit rules below allow only local private-network traffic.
Get-NetFirewallApplicationFilter -Program $resolvedBridge | Get-NetFirewallRule |
    Where-Object { $_.Action -eq 'Block' -and $_.Direction -eq 'Inbound' -and ($_.Name -like 'TCP Query User*' -or $_.Name -like 'UDP Query User*') } |
    Set-NetFirewallRule -Profile Public
foreach ($spec in @(@{Name='PhantomDustBridge-HTTPS';Protocol='TCP';Port=17431},@{Name='PhantomDustBridge-Discovery';Protocol='UDP';Port=5353})) {
    $existingRule = Get-NetFirewallRule -Name $spec.Name -ErrorAction SilentlyContinue
    if ($existingRule) {
        $existingRule | Set-NetFirewallRule -Program $resolvedBridge -Enabled True -Action Allow -Direction Inbound -Protocol $spec.Protocol -LocalPort $spec.Port -Profile Private -RemoteAddress LocalSubnet
    } else {
        New-NetFirewallRule -Name $spec.Name -DisplayName $spec.Name -Direction Inbound -Program $resolvedBridge -Action Allow -Protocol $spec.Protocol -LocalPort $spec.Port -Profile Private -RemoteAddress LocalSubnet | Out-Null
    }
}
Write-Host 'Bridge allowed on Private networks for the local subnet. Restart Bridge in normal mode.'
