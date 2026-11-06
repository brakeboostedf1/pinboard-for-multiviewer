# Install test: silent install, checks, silent uninstall, checks. What a new user's machine gets from the setup file.
# Runs in the release workflow on a clean GitHub machine (administrator, as setup needs). Exit code = failed checks.
param([string]$Setup = 'Pinboard-for-MultiViewer-Setup.exe')
$name = 'Pinboard for MultiViewer'
$dir = Join-Path $env:ProgramFiles $name
$exe = Join-Path $dir 'Pinboard.exe'
$key = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PinboardForMultiViewer'
$links = @((Join-Path $env:PUBLIC "Desktop\$name.lnk"), (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$name.lnk"))
$data = Join-Path $env:LOCALAPPDATA $name
$setupPath = (Resolve-Path $Setup).Path
$version = (Get-Item $setupPath).VersionInfo.ProductVersion
$fails = 0
function Check($ok, $what) { if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:fails++ } }
function Run($file, $arguments) { (Start-Process $file -ArgumentList $arguments -Wait -PassThru).ExitCode }

"-- install $version"
Check ((Run $setupPath @('--install', '--desktop', '--menu', '--silent')) -eq 0) 'silent install: exit code 0'
Check ((Test-Path $exe) -and (Get-FileHash $exe).Hash -eq (Get-FileHash $setupPath).Hash) "installed in $dir, same file as setup"
$u = Get-ItemProperty $key -ErrorAction SilentlyContinue
Check ($u.DisplayName -eq $name) "Apps entry name: '$($u.DisplayName)'"
Check ($u.Publisher -eq $name) "Apps entry publisher: '$($u.Publisher)'"
Check ($u.DisplayVersion -eq $version) "Apps entry version: '$($u.DisplayVersion)'"
Check ($u.InstallLocation -eq $dir) "Apps entry folder: '$($u.InstallLocation)'"
$shell = New-Object -ComObject WScript.Shell
foreach ($l in $links) { Check ((Test-Path $l) -and $shell.CreateShortcut($l).TargetPath -eq $exe) "shortcut points at Pinboard: $l" }
Check ((Run $exe '--selftest') -eq 0) 'installed copy passes its self-test'

"-- uninstall"
Check ((Run $exe @('--uninstall', '--silent')) -eq 0) 'silent uninstall: exit code 0'
for ($i = 0; $i -lt 40 -and (Test-Path $dir); $i++) { Start-Sleep -Milliseconds 250 } # the exe deletes itself just after it exits
Check (-not (Test-Path $dir)) 'install folder removed'
Check (-not (Test-Path $key)) 'Apps entry removed'
foreach ($l in $links) { Check (-not (Test-Path $l)) "shortcut removed: $l" }
Check ((-not (Test-Path $data)) -or (-not (Get-ChildItem $data))) "user data removed: $((Get-ChildItem $data -ErrorAction SilentlyContinue).Name -join ', ')"

"$fails failed"
exit $fails
