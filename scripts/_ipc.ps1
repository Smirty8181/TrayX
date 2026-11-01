param([string]$Command = 'system.stats', [string]$PayloadJson = '', [string]$PayloadFile = '')

if ($PayloadFile) { $PayloadJson = (Get-Content -Raw -Encoding UTF8 $PayloadFile) }

$identity = "$env:USERDOMAIN\$env:USERNAME"
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))
$suffix = [System.BitConverter]::ToString($hash).Replace('-', '').Substring(0, 12).ToLowerInvariant()
$pipeName = "TrayX.Core.$suffix"

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, 'InOut')
$pipe.Connect(2000)

$id = [Guid]::NewGuid().ToString()
$payload = $null
if ($PayloadJson) { $payload = $PayloadJson | ConvertFrom-Json }
$request = @{ type = 'request'; id = $id; name = $Command }
if ($payload) { $request.payload = $payload }
$line = ($request | ConvertTo-Json -Compress -Depth 20) + "`n"

$writer = New-Object System.IO.StreamWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
$writer.Write($line)

$reader = New-Object System.IO.StreamReader($pipe)
$deadline = [DateTime]::UtcNow.AddSeconds(8)
while ([DateTime]::UtcNow -lt $deadline) {
    $responseLine = $reader.ReadLine()
    if ($null -eq $responseLine) { break }
    $response = $responseLine | ConvertFrom-Json
    if ($response.type -eq 'response' -and $response.id -eq $id) {
        $response | ConvertTo-Json -Depth 20
        break
    }
}

try { $pipe.Dispose() } catch { }
