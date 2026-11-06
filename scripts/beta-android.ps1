# Builds artifacts only. It never uploads to Play, installs an APK, or changes a board.
param(
    [Parameter(Mandatory=$true)][ValidateSet('unsigned-validation','signed-beta','cleanup')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$WorkDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'beta-android-policy.ps1')
$taskWork=Get-AndroidBetaDirectory $WorkDirectory $env:RUNNER_TEMP
if ($Mode -eq 'cleanup') { Remove-AndroidBetaSecrets $taskWork $env:RUNNER_TEMP;return }
if (Test-Path -LiteralPath $taskWork) { throw 'Choose a fresh beta work directory to prevent stale artifacts.' }
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskManifest=Get-AndroidBetaManifest (Join-Path $taskRoot 'android\AndroidManifest.xml')
$taskRevision=(& git -C $taskRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $taskRevision -notmatch '^(?:[a-f0-9]{40}|[a-f0-9]{64})$') { throw 'A committed source revision is required.' }
if ($Mode -eq 'signed-beta') {
    Assert-AndroidBetaSigningContext $env:GITHUB_ACTIONS $env:GITHUB_EVENT_NAME $env:GITHUB_REF
    Assert-AndroidBetaSigning $env:ANDROID_UPLOAD_CERT_SHA256 $env:ANDROID_UPLOAD_KEY_ALIAS $env:ANDROID_LAST_PUBLISHED_VERSION_CODE $taskManifest.versionCode $env:ANDROID_UPLOAD_KEYSTORE_PASSWORD
    if ([string]::IsNullOrWhiteSpace($env:ANDROID_UPLOAD_KEYSTORE_BASE64)) { throw 'Configure ANDROID_UPLOAD_KEYSTORE_BASE64 in the protected android-beta environment.' }
}
New-Item -ItemType Directory -Path $taskWork | Out-Null
$taskTools=Join-Path $taskWork 'tools'
$taskArchives=Join-Path $taskWork 'downloads'
$taskBuildOutput=Join-Path $taskWork 'build-output'
$taskApproved=Join-Path $taskWork 'approved'
New-Item -ItemType Directory -Path $taskTools,$taskArchives,$taskBuildOutput,$taskApproved | Out-Null
$taskSigning=Join-Path $taskWork '.signing'
try {
    if ($Mode -eq 'signed-beta') {
        New-Item -ItemType Directory -Path $taskSigning | Out-Null
        $taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls.exe $taskSigning '/inheritance:r' '/grant:r' "${taskIdentity}:(OI)(CI)F" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot protect temporary signing material.' }
        $taskKeyBytes=$null;$taskPasswordBytes=$null
        try {
            $taskKeyBytes=[Convert]::FromBase64String($env:ANDROID_UPLOAD_KEYSTORE_BASE64)
            if ($taskKeyBytes.Length -lt 1024 -or $taskKeyBytes.Length -gt 1024*1024) { throw 'Upload keystore size is outside the allowed range.' }
            [IO.File]::WriteAllBytes((Join-Path $taskSigning 'upload.jks'),$taskKeyBytes)
            $taskPasswordBytes=[Text.UTF8Encoding]::new($false).GetBytes($env:ANDROID_UPLOAD_KEYSTORE_PASSWORD)
            [IO.File]::WriteAllBytes((Join-Path $taskSigning 'password.txt'),$taskPasswordBytes)
        } finally {
            if ($null -ne $taskKeyBytes) { [Array]::Clear($taskKeyBytes,0,$taskKeyBytes.Length) }
            if ($null -ne $taskPasswordBytes) { [Array]::Clear($taskPasswordBytes,0,$taskPasswordBytes.Length) }
            [Environment]::SetEnvironmentVariable('ANDROID_UPLOAD_KEYSTORE_BASE64',$null,'Process')
            [Environment]::SetEnvironmentVariable('ANDROID_UPLOAD_KEYSTORE_PASSWORD',$null,'Process')
        }
    }
    $taskWanted=@('JDK17','platform-36_r02.zip','build-tools_r36.1_windows.zip')
    $taskPins=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain-downloads.json') -Raw | ConvertFrom-Json | Where-Object { $_.component -in $taskWanted })
    if ($taskPins.Count -ne 3 -or @($taskPins.component | Select-Object -Unique).Count -ne 3) { throw 'Pinned JDK/SDK archives are incomplete.' }
    foreach ($taskPin in $taskPins) {
        $taskUri=[Uri]$taskPin.url
        if ($taskUri.Scheme -ne 'https' -or $taskUri.Host -notin @('github.com','dl.google.com') -or $taskUri.UserInfo -or $taskUri.Port -ne 443 -or $taskPin.sha256 -notmatch '^[a-f0-9]{64}$' -or $taskPin.extractTo -notin @('jdk','sdk')) { throw 'Invalid official toolchain pin.' }
        $taskArchive=Join-Path $taskArchives ($taskPin.component+'.zip')
        Invoke-WebRequest -Uri $taskUri -OutFile $taskArchive
        if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskPin.sha256) { throw 'Toolchain checksum mismatch; archive was not extracted or executed.' }
        $taskDestination=Join-Path $taskTools $taskPin.extractTo
        New-Item -ItemType Directory -Force -Path $taskDestination | Out-Null
        $taskZip=[IO.Compression.ZipFile]::OpenRead($taskArchive)
        try {
            foreach ($taskEntry in $taskZip.Entries) {
                $taskEntryPath=[IO.Path]::GetFullPath((Join-Path $taskDestination $taskEntry.FullName))
                if (-not $taskEntryPath.StartsWith($taskDestination.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Toolchain archive has an escaping path.' }
            }
        } finally { $taskZip.Dispose() }
        Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskDestination -Force
    }
    $taskArguments=@{Toolchain=$taskTools;OutputDirectory=$taskBuildOutput;DownloadTools=$true}
    if ($Mode -eq 'signed-beta') {
        $taskArguments.SigningKey=Join-Path $taskSigning 'upload.jks'
        $taskArguments.SigningPasswordFile=Join-Path $taskSigning 'password.txt'
        $taskArguments.SigningAlias=$env:ANDROID_UPLOAD_KEY_ALIAS
        $taskArguments.ExpectedCertificateSha256=$env:ANDROID_UPLOAD_CERT_SHA256
    } else { $taskArguments.Unsigned=$true }
    & (Join-Path $PSScriptRoot 'Build-AndroidBundle.ps1') @taskArguments
    $taskApk=Get-Content -LiteralPath (Join-Path $taskBuildOutput 'android-build.json') -Raw | ConvertFrom-Json
    $taskBundle=Get-Content -LiteralPath (Join-Path $taskBuildOutput 'android-bundle.json') -Raw | ConvertFrom-Json
    $taskSigned=$Mode -eq 'signed-beta'
    if ($taskApk.package -ne $taskManifest.package -or $taskBundle.package -ne $taskManifest.package -or $taskApk.versionCode -ne $taskManifest.versionCode -or $taskBundle.versionCode -ne $taskManifest.versionCode -or $taskApk.versionName -ne $taskManifest.versionName -or $taskBundle.versionName -ne $taskManifest.versionName -or $taskApk.targetSdk -ne 36 -or $taskBundle.targetSdk -ne 36 -or [bool]$taskApk.signed -ne $taskSigned -or [bool]$taskBundle.signed -ne $taskSigned) { throw 'Packaged identity, SDK or signing mode differs from the source.' }
    if ($taskSigned -and ($taskApk.certificateSha256 -ine $env:ANDROID_UPLOAD_CERT_SHA256 -or $taskBundle.certificateSha256 -ine $env:ANDROID_UPLOAD_CERT_SHA256)) { throw 'Upload certificate verification failed.' }
    $taskFiles=if ($taskSigned) {@('DeskWing.apk','DeskWing.aab','DeskWing-universal.apk')} else {@('DeskWing-unsigned.apk','DeskWing-unsigned.aab')}
    $taskDigests=@()
    foreach ($taskName in $taskFiles) {
        $taskFile=Join-Path $taskBuildOutput $taskName
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw 'An approved package artifact is missing.' }
        if (((Get-Item -LiteralPath $taskFile -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Approved packages cannot be links to other runner files.' }
        Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskApproved $taskName)
        $taskDigests += [ordered]@{file=$taskName;sha256=(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $taskFile).Length}
    }
    $taskProvenance=[ordered]@{sourceRevision=$taskRevision;package=$taskManifest.package;versionName=$taskManifest.versionName;versionCode=$taskManifest.versionCode;minSdk=26;targetSdk=36;mode=$Mode;signed=$taskSigned;uploadCertificateSha256=$(if($taskSigned){$env:ANDROID_UPLOAD_CERT_SHA256.ToLowerInvariant()}else{''});apkPurpose='bundle-validation';playUploadPerformed=$false;apkInstallPerformed=$false;artifacts=$taskDigests}
    $taskProvenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskApproved 'beta-provenance.json') -Encoding utf8
    $taskDigests | ForEach-Object { $_.sha256+'  '+$_.file } | Set-Content -LiteralPath (Join-Path $taskApproved 'SHA256SUMS.txt') -Encoding utf8
    if (-not $taskSigned) {
        'Unsigned validation artifacts cannot be installed or submitted to Play.' | Set-Content -LiteralPath (Join-Path $taskApproved 'UNSIGNED-VALIDATION.txt') -Encoding utf8
    } else {
        'This job prepared an upload-key-signed AAB; it did not upload to Play. The APKs validate bundle content and use that same upload key. Play normally signs delivered APKs with the separately enrolled app-signing key. Do not use these APKs to update existing installations unless the signing certificate is the same. Review Play App Signing continuity and the next version code before distribution.' | Set-Content -LiteralPath (Join-Path $taskApproved 'DISTRIBUTION-NOTES.txt') -Encoding utf8
    }
    Write-Output ('Validated '+$Mode+' packages. No upload or device installation was performed.')
} finally {
    [Environment]::SetEnvironmentVariable('ANDROID_UPLOAD_KEYSTORE_BASE64',$null,'Process')
    [Environment]::SetEnvironmentVariable('ANDROID_UPLOAD_KEYSTORE_PASSWORD',$null,'Process')
    Remove-AndroidBetaSecrets $taskWork $env:RUNNER_TEMP
}
