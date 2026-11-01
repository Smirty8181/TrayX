param([string]$Command = 'system.stats', [int]$Seconds = 12, [string]$OutFile = 'C:\Users\1\AppData\Local\Temp\ipc-events.txt')

$identity = "$env:USERDOMAIN\$env:USERNAME"
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))
$suffix = [System.BitConverter]::ToString($hash).Replace('-', '').Substring(0, 12).ToLowerInvariant()
$pipeName = "TrayX.Core.$suffix"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, 'InOut')
$pipe.Connect(2000)

$id = [Guid]::NewGuid().ToString()
$request = @{ type = 'request'; id = $id; name = $Command }
$line = ($request | ConvertTo-Json -Compress -Depth 20) + "`n"

$writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
$writer.Write($line)

$encoding = New-Object System.Text.UTF8Encoding($false)
$buffer = New-Object byte[] 65536
$memory = New-Object System.IO.MemoryStream
$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
$pending = $null

while ([DateTime]::UtcNow -lt $deadline) {
    if (-not $pipe.IsConnected) { break }
    if ($null -eq $pending) { $pending = $pipe.BeginRead($buffer, 0, $buffer.Length, $null, $null) }
    if ($pending.AsyncWaitHandle.WaitOne(250)) {
        try { $read = $pipe.EndRead($pending) } catch { $read = 0 }
        $pending = $null
        if ($read -le 0) { break }
        $memory.Write($buffer, 0, $read)
    }
}

$text = $encoding.GetString($memory.ToArray())
$lines = @($text -split "`r?`n" | Where-Object { $_ })
$out = @('lines=' + $lines.Count)
$out += foreach ($item in $lines) {
    if ($item.Length -gt 220) { $item.Substring(0, 220) } else { $item }
}
$out | Out-File -Encoding utf8 $OutFile

$memory.Dispose()
$writer.Dispose()
$pipe.Dispose()
