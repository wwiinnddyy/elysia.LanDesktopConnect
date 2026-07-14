[CmdletBinding()]
param(
    [string]$GatewayDirectory,
    [string]$BunPath = "bun",
    [int]$TimeoutSeconds = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    Write-Host "Gateway named-pipe smoke test is skipped on non-Windows hosts."
    return
}

if ([string]::IsNullOrWhiteSpace($GatewayDirectory)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $GatewayDirectory = (Resolve-Path (Join-Path $scriptRoot "..\elysia-gateway")).Path
}
else {
    $GatewayDirectory = (Resolve-Path $GatewayDirectory).Path
}

function Wait-TaskResult($Task, [string]$Description, [int]$Timeout) {
    if (-not $Task.Wait([TimeSpan]::FromSeconds($Timeout))) {
        throw "Timed out waiting for $Description."
    }
    return $Task.GetAwaiter().GetResult()
}

function Send-WebSocketJson(
    [System.Net.WebSockets.ClientWebSocket]$Socket,
    [string]$Json
) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $segment = [ArraySegment[byte]]::new($bytes)
    [void]$Socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Receive-WebSocketJson(
    [System.Net.WebSockets.ClientWebSocket]$Socket,
    [int]$Timeout
) {
    $buffer = New-Object byte[] 8192
    $stream = [System.IO.MemoryStream]::new()
    try {
        do {
            $segment = [ArraySegment[byte]]::new($buffer)
            $receiveTask = $Socket.ReceiveAsync($segment, [Threading.CancellationToken]::None)
            $result = Wait-TaskResult $receiveTask "WebSocket message" $Timeout
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw "Gateway closed the WebSocket during smoke test."
            }
            $stream.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)

        return [System.Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
    }
    finally {
        $stream.Dispose()
    }
}

$pipeName = "LMD_Elysia_Smoke_$([Guid]::NewGuid().ToString('N'))"
$pipeEndpoint = "\\.\pipe\$pipeName"
$pipe = [System.IO.Pipes.NamedPipeServerStream]::new(
    $pipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte,
    [System.IO.Pipes.PipeOptions]::Asynchronous)

$process = $null
$webSocket = $null
try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $BunPath
    $startInfo.Arguments = "run src/index.ts"
    $startInfo.WorkingDirectory = $GatewayDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["LMD_PLUGIN_PIPE"] = $pipeEndpoint
    $startInfo.Environment["LMD_PLUGIN_DATA_DIR"] = [System.IO.Path]::GetTempPath()
    $startInfo.Environment["LMD_GATEWAY_PORT"] = "0"
    $startInfo.Environment["LMD_GATEWAY_HOST"] = "127.0.0.1"
    $startInfo.Environment["LMD_LOG_LEVEL"] = "debug"

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start Bun gateway smoke process."
    }

    $connectTask = $pipe.WaitForConnectionAsync()
    $stdoutTask = $process.StandardOutput.ReadLineAsync()
    $port = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline -and ($null -eq $port -or -not $connectTask.IsCompleted)) {
        if ($process.HasExited) {
            $stderr = $process.StandardError.ReadToEnd()
            throw "Gateway smoke process exited with code $($process.ExitCode): $stderr"
        }

        if ($stdoutTask.IsCompleted) {
            $line = $stdoutTask.GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            if ($line -match 'running\s+on\s+port\s+(?<port>\d+)') {
                $port = [int]$Matches.port
            }
            $stdoutTask = $process.StandardOutput.ReadLineAsync()
        }

        Start-Sleep -Milliseconds 50
    }

    if ($null -eq $port) { throw "Gateway did not report a listening port." }
    Wait-TaskResult $connectTask "gateway named-pipe connection" $TimeoutSeconds | Out-Null

    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8, $true, 1024, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.AutoFlush = $true

    $webSocket = [System.Net.WebSockets.ClientWebSocket]::new()
    $connectWebSocketTask = $webSocket.ConnectAsync(
        [Uri]"ws://127.0.0.1:$port/bridge",
        [Threading.CancellationToken]::None)
    Wait-TaskResult $connectWebSocketTask "gateway WebSocket connection" $TimeoutSeconds | Out-Null

    $registerMessage = [ordered]@{
        id = "smoke-register"
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        type = "register"
        sender = "smoke-client"
        payload = [ordered]@{
            id = "smoke-client"
            name = "Gateway Smoke Test"
            type = "test"
            capabilities = @("smoke.echo")
        }
    } | ConvertTo-Json -Depth 10 -Compress
    Send-WebSocketJson $webSocket $registerMessage

    $pipeMessage = Wait-TaskResult ($reader.ReadLineAsync()) "register message on named pipe" $TimeoutSeconds
    $parsedPipeMessage = $pipeMessage | ConvertFrom-Json
    if ($parsedPipeMessage.type -ne "register" -or $parsedPipeMessage.sender -ne "smoke-client") {
        throw "Gateway did not forward the registration message to the plugin transport."
    }

    $registerResponse = Receive-WebSocketJson $webSocket $TimeoutSeconds
    if ($registerResponse.type -ne "register:success") {
        throw "Gateway did not acknowledge client registration."
    }

    $hostBroadcast = [ordered]@{
        id = "smoke-broadcast"
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        type = "host:broadcast"
        sender = "lanmountain-desktop.smoke"
        payload = [ordered]@{
            type = "smoke:message"
            payload = [ordered]@{ ok = $true }
        }
    } | ConvertTo-Json -Depth 10 -Compress
    $writer.WriteLine($hostBroadcast)

    $broadcastResponse = Receive-WebSocketJson $webSocket $TimeoutSeconds
    if ($broadcastResponse.type -ne "smoke:message" -or -not $broadcastResponse.payload.ok) {
        throw "Gateway did not forward the host broadcast to the WebSocket client."
    }

    Write-Host "Gateway transport smoke test passed on 127.0.0.1:$port."
}
finally {
    if ($webSocket) {
        try { $webSocket.Dispose() } catch { }
    }
    if ($process) {
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            $process.WaitForExit(5000) | Out-Null
        }
        catch { }
        $process.Dispose()
    }
    $pipe.Dispose()
}
