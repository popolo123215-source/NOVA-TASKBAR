[CmdletBinding()]
param(
    [ValidateSet("start", "stop", "status", "logs")]
    [string]$Action = "status",
    [switch]$Follow
)

$ErrorActionPreference = "Stop"
$infraPath = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $infraPath "..\..")).Path
$envPath = Join-Path $infraPath ".env"
$runtimePath = Join-Path $infraPath "runtime"
$statePath = Join-Path $runtimePath "server-processes.json"
$logsPath = Join-Path $infraPath "logs"
$publishPath = Join-Path $repoRoot "build\NOVA-RendezvousServer"

function Read-DotEnv {
    param([string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*(#|$)') { continue }
        $separator = $line.IndexOf("=")
        if ($separator -lt 1) { continue }
        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }
    return $values
}

function Set-DotEnvValue {
    param(
        [string]$Path,
        [string]$Name,
        [string]$Value
    )

    $updated = New-Object 'System.Collections.Generic.List[string]'
    $found = $false
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match ('^\s*' + [regex]::Escape($Name) + '\s*=')) {
            if (-not $found) {
                $updated.Add("$Name=$Value")
                $found = $true
            }
        } else {
            $updated.Add($line)
        }
    }
    if (-not $found) { $updated.Add("$Name=$Value") }

    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList 0
    [System.IO.File]::WriteAllLines($Path, $updated.ToArray(), $utf8NoBom)
}

function Test-PublicIPv4 {
    param([string]$Address)

    $bytes = [System.Net.IPAddress]::Parse($Address).GetAddressBytes()
    if ($bytes.Length -ne 4) { return $false }
    if ($bytes[0] -eq 0 -or $bytes[0] -eq 10 -or $bytes[0] -eq 127 -or $bytes[0] -ge 224) { return $false }
    if ($bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127) { return $false }
    if ($bytes[0] -eq 169 -and $bytes[1] -eq 254) { return $false }
    if ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) { return $false }
    if ($bytes[0] -eq 192 -and $bytes[1] -eq 168) { return $false }
    if ($bytes[0] -eq 192 -and $bytes[1] -eq 0 -and $bytes[2] -eq 2) { return $false }
    if ($bytes[0] -eq 198 -and ($bytes[1] -eq 18 -or $bytes[1] -eq 19)) { return $false }
    if ($bytes[0] -eq 198 -and $bytes[1] -eq 51 -and $bytes[2] -eq 100) { return $false }
    if ($bytes[0] -eq 203 -and $bytes[1] -eq 0 -and $bytes[2] -eq 113) { return $false }
    if ($bytes[0] -eq 192 -and $bytes[1] -eq 88 -and $bytes[2] -eq 99) { return $false }
    return $true
}

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
    return (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json)
}

function Write-State {
    param([object]$State)

    New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList 0
    $json = ConvertTo-Json -InputObject $State -Depth 6
    [System.IO.File]::WriteAllText($statePath, $json, $utf8NoBom)
}

function Get-ManagedProcess {
    param([object]$Entry)

    if ($null -eq $Entry -or $null -eq $Entry.Pid) { return $null }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($Entry.Pid)" -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    if (-not [string]::Equals($process.ExecutablePath, $Entry.ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    if ($Entry.CommandLineMarker -and
        ($null -eq $process.CommandLine -or $process.CommandLine.IndexOf($Entry.CommandLineMarker, [System.StringComparison]::OrdinalIgnoreCase) -lt 0)) { return $null }
    return $process
}

function Stop-ManagedProcess {
    param([object]$Entry)

    if ($null -eq $Entry) { return $true }
    $process = Get-ManagedProcess -Entry $Entry
    if ($null -eq $process) {
        $pidProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($Entry.Pid)" -ErrorAction SilentlyContinue
        if ($null -eq $pidProcess) { return $true }
        Write-Warning "PID $($Entry.Pid) no longer matches the recorded server executable; it was not stopped."
        return $false
    }

    Stop-Process -Id ([int]$Entry.Pid)
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Start-Sleep -Milliseconds 300
        if ($null -eq (Get-CimInstance Win32_Process -Filter "ProcessId = $($Entry.Pid)" -ErrorAction SilentlyContinue)) {
            return $true
        }
    }
    Write-Warning "Server process $($Entry.Pid) did not exit after Stop-Process."
    return $false
}

function Assert-PortsFree {
    $tcpPorts = @(443, 3478, 5349)
    $tcpListeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $tcpPorts -contains $_.LocalPort })
    $udpListeners = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -eq 3478 -or ($_.LocalPort -ge 49160 -and $_.LocalPort -le 49259) })
    if ($tcpListeners.Count -gt 0 -or $udpListeners.Count -gt 0) {
        $details = @()
        $details += $tcpListeners | ForEach-Object { "TCP/$($_.LocalPort) PID $($_.OwningProcess)" }
        $details += $udpListeners | ForEach-Object { "UDP/$($_.LocalPort) PID $($_.OwningProcess)" }
        throw "Required server ports are already in use: $($details -join ', '). No listeners were changed."
    }
}

function Test-TurnPorts {
    param([int]$ProcessId)

    $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $ProcessId -and ($_.LocalPort -eq 3478 -or $_.LocalPort -eq 5349) })
    $udp = @(Get-NetUDPEndpoint -LocalPort 3478 -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $ProcessId })
    return (($tcp.LocalPort -contains 3478) -and ($tcp.LocalPort -contains 5349) -and $udp.Count -gt 0)
}

function Wait-ForTurn {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds = 15
    )

    for ($attempt = 0; $attempt -lt ($TimeoutSeconds * 2); $attempt++) {
        if ($null -eq (Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue)) {
            throw "coturn exited before binding UDP/TCP 3478 and TCP/TLS 5349. Review its log files."
        }
        if (Test-TurnPorts -ProcessId $ProcessId) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "coturn did not bind UDP/TCP 3478 and TCP/TLS 5349 within $TimeoutSeconds seconds."
}

function Test-TlsListener {
    param(
        [string]$HostName,
        [int]$Port
    )

    $client = New-Object System.Net.Sockets.TcpClient
    $stream = $null
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = [System.Net.Security.SslStream]::new($client.GetStream(), $false)
        $stream.AuthenticateAsClient($HostName)
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

function Test-RendezvousEndpoint {
    param([string]$HostName)

    $resolve = "{0}:443:127.0.0.1" -f $HostName
    $httpCode = & curl.exe --silent --show-error --noproxy "*" --output NUL --write-out "%{http_code}" `
        --resolve $resolve "https://$HostName/v1/connect" 2>$null
    if ($LASTEXITCODE -ne 0 -or $httpCode -notmatch '^[1-5]\d\d$') {
        throw "Local Rendezvous TLS/HTTPS check failed for /v1/connect."
    }
    return $httpCode
}

function Invoke-Start {
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        throw "Missing $envPath. Copy .env.example to .env and enter real hostname, secrets, coturn path, and certificate password."
    }

    $existingState = Read-State
    if ($null -ne $existingState) {
        $existingRendezvous = Get-ManagedProcess -Entry $existingState.Rendezvous
        $existingTurn = Get-ManagedProcess -Entry $existingState.Turn
        if ($null -ne $existingRendezvous -or $null -ne $existingTurn) {
            throw "A managed server process is already running. Use -Action status or stop before starting again."
        }
    }

    $config = Read-DotEnv -Path $envPath
    $publicHost = $config["NOVA_PUBLIC_HOST"]
    if ([string]::IsNullOrWhiteSpace($publicHost) -or $publicHost -match '[\s/:\\]') {
        throw "Set NOVA_PUBLIC_HOST to the real DNS/DDNS hostname in infra/internet/.env."
    }
    $secret = $config["NOVA_TURN_SHARED_SECRET"]
    if ([string]::IsNullOrWhiteSpace($secret) -or $secret.Length -lt 32) {
        throw "NOVA_TURN_SHARED_SECRET must be a unique secret of at least 32 characters."
    }
    if ([string]::IsNullOrWhiteSpace($config["NOVA_TLS_PFX_PASSWORD"])) {
        throw "Set NOVA_TLS_PFX_PASSWORD in infra/internet/.env."
    }

    $turnServerPath = $config["NOVA_COTURN_EXECUTABLE"]
    if ([string]::IsNullOrWhiteSpace($turnServerPath)) {
        throw "Set NOVA_COTURN_EXECUTABLE to a Windows-native coturn turnserver.exe built with TLS support."
    }
    if (-not [System.IO.Path]::IsPathRooted($turnServerPath)) {
        $turnServerPath = Join-Path $repoRoot $turnServerPath
    }
    if ([System.IO.Path]::GetExtension($turnServerPath) -ne ".exe" -or -not (Test-Path -LiteralPath $turnServerPath -PathType Leaf)) {
        throw "NOVA_COTURN_EXECUTABLE must point to an existing Windows turnserver.exe."
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw ".NET 10 SDK and ASP.NET Core 10 runtime are required to run Rendezvous natively." }
    $sdks = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
        throw "The .NET 10 SDK is required to publish the Rendezvous service."
    }
    $runtimes = & dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match 'Microsoft\.AspNetCore\.App 10\.')) {
        throw "The Microsoft.AspNetCore.App 10 runtime is required for the native Rendezvous service."
    }

    $pfxPath = Join-Path $infraPath "certs\rendezvous.pfx"
    $chainPath = Join-Path $infraPath "certs\fullchain.pem"
    $keyPath = Join-Path $infraPath "certs\privkey.pem"
    foreach ($certificatePath in @($pfxPath, $chainPath, $keyPath)) {
        if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
            throw "Required TLS certificate file is missing: $certificatePath"
        }
    }

    $aRecords = @(Resolve-DnsName -Name $publicHost -Type A -DnsOnly -ErrorAction Stop |
        Where-Object { $_.Type -eq "A" } |
        Select-Object -ExpandProperty IPAddress -Unique)
    if ($aRecords.Count -ne 1 -or -not (Test-PublicIPv4 -Address $aRecords[0])) {
        throw "NOVA_PUBLIC_HOST must resolve to exactly one real public IPv4 A record. Update DDNS first; CGNAT/private and sample addresses are not usable."
    }

    $defaultRoute = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -AddressFamily IPv4 -ErrorAction Stop |
        Sort-Object RouteMetric, InterfaceMetric |
        Select-Object -First 1
    if (-not $defaultRoute) { throw "Could not determine the Windows host's default IPv4 route." }
    $localAddress = Get-NetIPAddress -InterfaceIndex $defaultRoute.InterfaceIndex -AddressFamily IPv4 -Type Unicast -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike "169.254.*" } |
        Select-Object -First 1 -ExpandProperty IPAddress
    if ([string]::IsNullOrWhiteSpace($localAddress)) {
        throw "Could not determine the LAN IPv4 address for coturn's external-ip mapping."
    }

    Assert-PortsFree
    New-Item -ItemType Directory -Force -Path $runtimePath, $logsPath | Out-Null
    Set-DotEnvValue -Path $envPath -Name "NOVA_TURN_EXTERNAL_IP" -Value $aRecords[0]
    Set-DotEnvValue -Path $envPath -Name "NOVA_TURN_INTERNAL_IP" -Value $localAddress

    Push-Location $repoRoot
    try {
        & dotnet publish src\Nova.RendezvousService\Nova.RendezvousService.csproj `
            --configuration Release --output $publishPath
        if ($LASTEXITCODE -ne 0) { throw "Failed to publish the Rendezvous service." }
    } finally {
        Pop-Location
    }

    $turnConfigPath = Join-Path $infraPath "turnserver.conf"
    $turnConfig = @(
        "listening-port=3478",
        "tls-listening-port=5349",
        "min-port=49160",
        "max-port=49259",
        "fingerprint",
        "use-auth-secret",
        "static-auth-secret=$secret",
        "realm=$publicHost",
        "external-ip=$($aRecords[0])/$localAddress",
        "cert=..\certs\fullchain.pem",
        "pkey=..\certs\privkey.pem",
        "no-multicast-peers",
        "log-file=stdout"
    )
    [System.IO.File]::WriteAllLines($turnConfigPath, $turnConfig, [System.Text.Encoding]::ASCII)

    $runId = Get-Date -Format "yyyyMMdd-HHmmssfff"
    $turnLog = Join-Path $logsPath "coturn-$runId.log"
    $turnError = Join-Path $logsPath "coturn-$runId.err.log"
    $rendezvousLog = Join-Path $logsPath "rendezvous-$runId.log"
    $rendezvousError = Join-Path $logsPath "rendezvous-$runId.err.log"
    $state = [pscustomobject]@{
        PublicHost = $publicHost
        TurnConfigPath = $turnConfigPath
        Turn = $null
        Rendezvous = $null
    }

    try {
        $turnArguments = '-c ..\turnserver.conf'
        $turnProcess = Start-Process -FilePath $turnServerPath -ArgumentList $turnArguments `
            -WorkingDirectory $logsPath -WindowStyle Hidden `
            -RedirectStandardOutput $turnLog -RedirectStandardError $turnError -PassThru
        $state.Turn = [pscustomobject]@{
            Pid = $turnProcess.Id
            ExecutablePath = $turnServerPath
            CommandLineMarker = "turnserver.conf"
            Stdout = $turnLog
            Stderr = $turnError
        }
        Write-State -State $state
        Wait-ForTurn -ProcessId $turnProcess.Id
        Test-TlsListener -HostName $publicHost -Port 5349

        $rendezvousDll = Join-Path $publishPath "Nova.RendezvousService.dll"
        $environment = @{
            NOVA_HTTPS_PORT = "443"
            NOVA_STATE_PATH = (Join-Path $infraPath "state\rendezvous")
            NOVA_TLS_PFX_PATH = $pfxPath
            NOVA_TLS_PFX_PASSWORD = $config["NOVA_TLS_PFX_PASSWORD"]
            NOVA_PUBLIC_HOST = $publicHost
            NOVA_STUN_URIS = "stun:$publicHost`:3478"
            NOVA_TURN_URIS = "turn:${publicHost}:3478?transport=udp;turn:${publicHost}:3478?transport=tcp;turns:${publicHost}:5349?transport=tcp"
            NOVA_TURN_SHARED_SECRET = $secret
            NOVA_TURN_CREDENTIAL_TTL_SECONDS = "600"
            NOVA_MAX_ONLINE_DEVICES = $(if ($config["NOVA_MAX_ONLINE_DEVICES"]) { $config["NOVA_MAX_ONLINE_DEVICES"] } else { "5000" })
            NOVA_CONNECTS_PER_IP_PER_MINUTE = $(if ($config["NOVA_CONNECTS_PER_IP_PER_MINUTE"]) { $config["NOVA_CONNECTS_PER_IP_PER_MINUTE"] } else { "30" })
            NOVA_MAX_RELAY_FILE_BYTES = $(if ($config["NOVA_MAX_RELAY_FILE_BYTES"]) { $config["NOVA_MAX_RELAY_FILE_BYTES"] } else { "107374182400" })
        }
        $oldEnvironment = @{}
        foreach ($name in $environment.Keys) {
            $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
            [Environment]::SetEnvironmentVariable($name, [string]$environment[$name], "Process")
        }
        try {
            $rendezvousArguments = '"{0}"' -f $rendezvousDll
            $rendezvousProcess = Start-Process -FilePath $dotnet.Source -ArgumentList $rendezvousArguments `
                -WorkingDirectory $publishPath -WindowStyle Hidden `
                -RedirectStandardOutput $rendezvousLog -RedirectStandardError $rendezvousError -PassThru
        } finally {
            foreach ($name in $oldEnvironment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], "Process")
            }
        }
        $state.Rendezvous = [pscustomobject]@{
            Pid = $rendezvousProcess.Id
            ExecutablePath = $dotnet.Source
            CommandLineMarker = "Nova.RendezvousService.dll"
            Stdout = $rendezvousLog
            Stderr = $rendezvousError
        }
        Write-State -State $state

        $rendezvousReady = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if ($null -eq (Get-CimInstance Win32_Process -Filter "ProcessId = $($rendezvousProcess.Id)" -ErrorAction SilentlyContinue)) {
                throw "Rendezvous exited before listening on TCP 443. Review its log files."
            }
            $listener = @(Get-NetTCPConnection -State Listen -LocalPort 443 -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -eq $rendezvousProcess.Id })
            if ($listener.Count -gt 0) {
                $httpCode = Test-RendezvousEndpoint -HostName $publicHost
                $rendezvousReady = $true
                break
            }
            Start-Sleep -Seconds 1
        }
        if (-not $rendezvousReady) { throw "Rendezvous did not become ready on TCP 443 within 30 seconds." }

        Write-Output "Rendezvous: RUNNING (PID $($rendezvousProcess.Id), TCP 443, local HTTPS response $httpCode)"
        Write-Output "coturn: RUNNING (PID $($turnProcess.Id), UDP/TCP 3478 and TLS/TCP 5349 verified)"
        Write-Output "Logs: $logsPath"
        Write-Output "The checks are local only; router forwarding and Internet reachability are not verified."
    } catch {
        $turnStopped = Stop-ManagedProcess -Entry $state.Turn
        $rendezvousStopped = Stop-ManagedProcess -Entry $state.Rendezvous
        if ($turnStopped -and $rendezvousStopped) {
            if (Test-Path -LiteralPath $turnConfigPath) { Remove-Item -LiteralPath $turnConfigPath }
            $state.StoppedAt = [DateTime]::UtcNow.ToString("o")
            Write-State -State $state
        }
        throw
    }
}

if ($Action -eq "start") {
    if ($Follow) { throw "-Follow is only valid with -Action logs." }
    Invoke-Start
    return
}

if ($Action -eq "status") {
    if ($Follow) { throw "-Follow is only valid with -Action logs." }
    $state = Read-State
    if ($null -eq $state) {
        Write-Output "Rendezvous: STOPPED (not started by this manager)"
        Write-Output "coturn: STOPPED (not started by this manager)"
        return
    }

    foreach ($service in @(@{ Name = "Rendezvous"; Entry = $state.Rendezvous }, @{ Name = "coturn"; Entry = $state.Turn })) {
        $process = Get-ManagedProcess -Entry $service.Entry
        if ($null -eq $process) {
            Write-Output "$($service.Name): STOPPED"
        } else {
            Write-Output "$($service.Name): RUNNING (PID $($service.Entry.Pid))"
        }
    }
    $turnProcess = Get-ManagedProcess -Entry $state.Turn
    if ($null -ne $turnProcess) {
        Write-Output "coturn listeners (owned by PID $($state.Turn.Pid)): $((Test-TurnPorts -ProcessId $state.Turn.Pid))"
        Test-TlsListener -HostName $state.PublicHost -Port 5349
        Write-Output "coturn TLS handshake: VERIFIED locally"
    }
    $rendezvousProcess = Get-ManagedProcess -Entry $state.Rendezvous
    if ($null -ne $rendezvousProcess) {
        $httpCode = Test-RendezvousEndpoint -HostName $state.PublicHost
        Write-Output "Rendezvous local TLS/HTTPS response: HTTP $httpCode"
    }
    return
}

if ($Action -eq "stop") {
    if ($Follow) { throw "-Follow is only valid with -Action logs." }
    $state = Read-State
    if ($null -eq $state) {
        Write-Output "No managed NOVA server processes are recorded."
        return
    }

    $rendezvousStopped = Stop-ManagedProcess -Entry $state.Rendezvous
    $turnStopped = Stop-ManagedProcess -Entry $state.Turn
    if (-not ($rendezvousStopped -and $turnStopped)) {
        throw "At least one process could not be safely stopped. State and coturn config were retained for inspection."
    }
    if ($state.TurnConfigPath -and (Test-Path -LiteralPath $state.TurnConfigPath)) {
        Remove-Item -LiteralPath $state.TurnConfigPath
    }
    $state.StoppedAt = [DateTime]::UtcNow.ToString("o")
    Write-State -State $state
    Write-Output "Stopped the NOVA server processes recorded by this manager. Logs were retained."
    return
}

if ($Action -eq "logs") {
    $state = Read-State
    if ($null -eq $state) { throw "No managed server log files are recorded yet." }
    $logFiles = @($state.Turn.Stdout, $state.Turn.Stderr, $state.Rendezvous.Stdout, $state.Rendezvous.Stderr) |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    if ($logFiles.Count -eq 0) { throw "No server logs were found at the recorded paths." }
    if ($Follow) {
        Get-Content -LiteralPath $logFiles -Tail 100 -Wait
    } else {
        Get-Content -LiteralPath $logFiles -Tail 100
    }
}
