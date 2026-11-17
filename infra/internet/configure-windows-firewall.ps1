#requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$rules = @(
    @{ Name = "NOVA Internet - Rendezvous TCP 443"; Protocol = "TCP"; ProtocolNumber = 6; LocalPort = "443" },
    @{ Name = "NOVA Internet - TURN UDP 3478"; Protocol = "UDP"; ProtocolNumber = 17; LocalPort = "3478" },
    @{ Name = "NOVA Internet - TURN TCP 3478"; Protocol = "TCP"; ProtocolNumber = 6; LocalPort = "3478" },
    @{ Name = "NOVA Internet - TURN TLS TCP 5349"; Protocol = "TCP"; ProtocolNumber = 6; LocalPort = "5349" },
    @{ Name = "NOVA Internet - TURN relay UDP"; Protocol = "UDP"; ProtocolNumber = 17; LocalPort = "49160-49259" }
)

foreach ($spec in $rules) {
    $existing = @(Get-NetFirewallRule -DisplayName $spec.Name -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 1) {
        throw "Multiple firewall rules named '$($spec.Name)' exist. Inspect them manually; no rules were changed."
    }

    if ($existing.Count -eq 1) {
        $rule = $existing[0]
        $portFilter = $rule | Get-NetFirewallPortFilter
        if ($rule.Direction -ne "Inbound" -or $rule.Action -ne "Allow" -or
            [int]$portFilter.Protocol -ne $spec.ProtocolNumber -or
            [string]$portFilter.LocalPort -ne $spec.LocalPort) {
            throw "Firewall rule '$($spec.Name)' exists with different settings. Inspect it manually; it was not overwritten."
        }
        if ($rule.Enabled -ne "True") {
            Set-NetFirewallRule -DisplayName $spec.Name -Enabled True -Profile Domain,Private,Public
        }
        Write-Output "Verified existing rule: $($spec.Name)"
        continue
    }

    New-NetFirewallRule -DisplayName $spec.Name -Direction Inbound -Action Allow `
        -Protocol $spec.Protocol -LocalPort $spec.LocalPort -Profile Domain,Private,Public | Out-Null
    Write-Output "Created rule: $($spec.Name)"
}

Write-Output "Firewall rules are host-local only. Router port forwarding and DDNS are separate manual settings."
