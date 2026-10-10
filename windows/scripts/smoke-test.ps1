# Launch test for a released ConduitHost ZIP, exactly as a user would run it:
#   extract the ZIP → start ConduitHost.exe → require a real window, every page visited,
#   /v1/ping answered by *this* process, and a second launch handing off to the first one.
param([Parameter(Mandatory)][string]$Zip)
$ErrorActionPreference = "Stop"

$root = Join-Path $env:RUNNER_TEMP ("conduit-smoke-" + [guid]::NewGuid().ToString("n"))
$app = Join-Path $root "app"
$data = Join-Path $root "data"
New-Item -ItemType Directory -Force $app, $data | Out-Null
Expand-Archive -Path $Zip -DestinationPath $app
$exe = Join-Path $app "ConduitHost.exe"
foreach ($f in "ConduitHost.exe", "resources.pri", "Microsoft.ui.xaml.dll") {
  if (!(Test-Path (Join-Path $app $f))) { throw "$f missing from the extracted ZIP" }
}
"Extracted $((Get-ChildItem -Recurse $app -File).Count) files; resources.pri = $((Get-Item (Join-Path $app 'resources.pri')).Length) bytes"

$env:CONDUIT_NO_DIALOG = "1"
$env:CONDUIT_SMOKE = "1"
$env:CONDUIT_DATA_DIR = $data
$marker = Join-Path $root "tour.ok"
$env:CONDUIT_SMOKE_MARKER = $marker
$crash = Join-Path $env:LOCALAPPDATA "Conduit/crash.log"
Remove-Item $crash -ErrorAction SilentlyContinue

$p = Start-Process $exe -PassThru
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 2
  if ($p.HasExited) { if (Test-Path $crash) { Get-Content $crash }; throw "ConduitHost exited during startup (code $($p.ExitCode))" }
  $p.Refresh()
  if ($p.MainWindowHandle -ne 0 -and (Test-Path $marker)) { break }
}
if (Test-Path $crash) { Get-Content $crash; throw "ConduitHost wrote a crash log" }
if ($p.MainWindowHandle -eq 0) { throw "No main window was created" }
if (!(Test-Path $marker)) { throw "Page tour did not complete" }
"Window: '$($p.MainWindowTitle)' handle=$($p.MainWindowHandle); tour: $(Get-Content $marker)"

$ping = Invoke-RestMethod http://127.0.0.1:47821/v1/ping
"Ping: $($ping | ConvertTo-Json -Compress)"
if ($ping.pid -ne $p.Id) { throw "Port 47821 answered from PID $($ping.pid), expected $($p.Id)" }

# Second launch must hand off to the running instance and exit, leaving the first one alive.
$second = Start-Process $exe -PassThru
if (!$second.WaitForExit(15000)) { $second.Kill(); throw "Second launch did not exit" }
if ($p.HasExited) { throw "First instance died after a second launch" }
"Second launch handed off (exit $($second.ExitCode))"

Stop-Process -Id $p.Id -Force
"Smoke test passed"
