# Tiny test rig: serves a fake update manifest on http://127.0.0.1:8723/latest.json
# so we can exercise the in-app Updater without uploading anything. Run from
# the project root in a separate terminal:
#   powershell -ExecutionPolicy Bypass -File installer\test-update-server.ps1
# Then in the app's terminal:
#   $env:GWDETECTIVE_UPDATE_URL = "http://127.0.0.1:8723/latest.json"
#   dotnet run --project GatewayTracer.Desktop.csproj
# You should see the "GW Detective 9.9.9.9 is available" prompt within ~6s.
# Click "No" \u2014 we don't actually have a hosted installer for this test.

$ErrorActionPreference = 'Stop'

$prefix = 'http://127.0.0.1:8723/'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
$listener.Start()
Write-Host "Manifest server listening on $prefix  (Ctrl+C to stop)"

$manifest = @{
    version = '9.9.9.9'
    notes   = 'TEST manifest \u2014 served by test-update-server.ps1'
    x64     = @{ url = 'http://127.0.0.1:8723/fake-setup-x64.exe';   sha256 = ('00' * 32) }
    arm64   = @{ url = 'http://127.0.0.1:8723/fake-setup-arm64.exe'; sha256 = ('00' * 32) }
} | ConvertTo-Json -Depth 5

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request
        $res = $ctx.Response
        Write-Host ("{0} {1}" -f $req.HttpMethod, $req.Url.AbsolutePath)

        switch ($req.Url.AbsolutePath) {
            '/latest.json' {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($manifest)
                $res.ContentType = 'application/json'
                $res.ContentLength64 = $bytes.Length
                $res.OutputStream.Write($bytes, 0, $bytes.Length)
            }
            { $_ -like '/fake-setup-*.exe' } {
                # 1 MB of zero bytes \u2014 stands in for a real installer so the
                # download path completes and we can exercise the SHA-256
                # verifier. The manifest publishes 00..00 as the expected
                # hash, which will NOT match zeroed bytes, so the updater
                # should surface a clean "SHA-256 mismatch" error. That's
                # the success criterion for this test.
                $bytes = New-Object byte[] (1 * 1024 * 1024)
                $res.ContentType = 'application/octet-stream'
                $res.ContentLength64 = $bytes.Length
                $res.OutputStream.Write($bytes, 0, $bytes.Length)
            }
            default {
                $res.StatusCode = 404
            }
        }
        $res.Close()
    }
} finally {
    $listener.Stop()
    $listener.Close()
}
