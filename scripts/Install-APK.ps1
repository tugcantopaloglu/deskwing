$ErrorActionPreference='Stop'
try {
 $taskAdb=Join-Path $PSScriptRoot 'platform-tools\adb.exe'
 $taskPackageRoot=Split-Path -Parent $PSScriptRoot
 $taskApk=Join-Path $taskPackageRoot 'DeskWing.apk'
 # Retain direct-install support for an older extracted release.
 if(!(Test-Path -LiteralPath $taskApk)){$taskApk=Join-Path $taskPackageRoot 'TabletDeck-Studio.apk'}
 if(!(Test-Path -LiteralPath $taskApk)){throw 'APK not found. Extract the complete DeskWing package.'}
 $taskDevices=@(& $taskAdb devices | Where-Object {$_ -match '^\S+\s+device$'} | ForEach-Object {($_ -split '\s+')[0]})
 if($taskDevices.Count -ne 1){throw 'Connect exactly one Android tablet with USB debugging enabled and authorize this PC.'}
 & $taskAdb -s $taskDevices[0] install -r $taskApk
 if($LASTEXITCODE -ne 0){throw 'Installation failed. Confirm Android 8.0 or later and allow app installation on the tablet.'}
 Write-Host 'DeskWing installed successfully.' -ForegroundColor Cyan
} catch {Write-Host $_.Exception.Message -ForegroundColor Red;exit 1}
