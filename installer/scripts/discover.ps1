param(
    [string]$OutputFile = "discovered.txt",
    [int]$TimeoutMs = 1500
)

$client = $null
try {
    $client = New-Object System.Net.Sockets.UdpClient
    $client.EnableBroadcast = $true
    $client.Client.ReceiveTimeout = $TimeoutMs
    $ep = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Broadcast, 38899)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("IDLEBOARD_DISCOVER")
    [void]$client.Send($bytes, $bytes.Length, $ep)

    $remoteEp = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $respBytes = $client.Receive([ref]$remoteEp)
    $respStr = [System.Text.Encoding]::UTF8.GetString($respBytes)

    $json = ConvertFrom-Json $respStr -ErrorAction SilentlyContinue
    if ($json -and $json.service -eq "idleboard-server") {
        $port = if ($json.httpPort) { $json.httpPort } else { 3000 }
        $rawIp = $remoteEp.Address.ToString().Replace("::ffff:", "").Trim()
        $ip = if ($json.primaryIp -and $json.primaryIp -ne "127.0.0.1") { $json.primaryIp } else { $rawIp }
        $url = if ($json.eventsUrl -and !$json.eventsUrl.Contains("127.0.0.1")) { $json.eventsUrl } else { "http://$ip`:$port/events" }
        [System.IO.File]::WriteAllText($OutputFile, $url)
        exit 0
    }
} catch {
} finally {
    if ($client) {
        $client.Close()
    }
}
exit 1
