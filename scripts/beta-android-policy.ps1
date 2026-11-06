# Pure validation and bounded-path helpers used by the manual Android beta job.
Set-StrictMode -Version Latest

function Get-AndroidBetaDirectory([string]$WorkDirectory,[string]$RunnerDirectory) {
    if ([string]::IsNullOrWhiteSpace($RunnerDirectory)) { throw 'RUNNER_TEMP is required for beta packaging.' }
    $runner=[IO.Path]::GetFullPath($RunnerDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $work=[IO.Path]::GetFullPath($WorkDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    if (-not $work.StartsWith($runner+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Beta work must be inside RUNNER_TEMP, not its root.' }
    for ($cursor=$work; $cursor.Length -gt $runner.Length; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Beta work cannot use reparse points.' }
        }
    }
    return $work
}

function Get-AndroidBetaManifest([string]$ManifestPath) {
    $readerSettings=[Xml.XmlReaderSettings]::new()
    $readerSettings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create($ManifestPath,$readerSettings)
    try { $document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader) } finally { $reader.Dispose() }
    $manifest=$document.DocumentElement
    $namespace='http://schemas.android.com/apk/res/android'
    $versionCode=0
    if ($manifest.Name -ne 'manifest' -or $manifest.GetAttribute('package') -ne 'app.tabletdeck.studio' -or -not [int]::TryParse($manifest.GetAttribute('versionCode',$namespace),[ref]$versionCode) -or $versionCode -lt 1 -or $versionCode -gt 2100000000) { throw 'Unexpected Android package or version code.' }
    $versionName=$manifest.GetAttribute('versionName',$namespace)
    if ($versionName -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-part source version name is required.' }
    $sdk=$manifest.SelectSingleNode('uses-sdk')
    if ($null -eq $sdk -or $sdk.GetAttribute('targetSdkVersion',$namespace) -ne '36' -or $sdk.GetAttribute('minSdkVersion',$namespace) -ne '26') { throw 'Beta packaging expects the verified SDK 26/36 manifest.' }
    return [pscustomobject]@{package='app.tabletdeck.studio';versionCode=$versionCode;versionName=$versionName;minSdk=26;targetSdk=36}
}

function Assert-AndroidBetaSigning([string]$Fingerprint,[string]$Alias,[string]$LastPublishedVersion,[int]$SourceVersionCode,[string]$Password) {
    if ($Fingerprint -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Set ANDROID_UPLOAD_CERT_SHA256 to the expected upload certificate SHA-256.' }
    if ($Alias -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { throw 'Set a simple ANDROID_UPLOAD_KEY_ALIAS matching the existing key.' }
    $last=0
    if ($LastPublishedVersion -notmatch '^(0|[1-9][0-9]{0,9})$' -or -not [int]::TryParse($LastPublishedVersion,[ref]$last) -or $last -gt 2100000000 -or $SourceVersionCode -le $last) { throw 'The source versionCode must exceed ANDROID_LAST_PUBLISHED_VERSION_CODE; use 0 only for a first Play upload.' }
    if ([string]::IsNullOrEmpty($Password) -or $Password.Length -gt 4096 -or $Password.IndexOfAny([char[]]@([char]0,[char]10,[char]13)) -ge 0) { throw 'The upload-key password must be nonempty and fit on one line.' }
}

function Assert-AndroidBetaSigningContext([string]$Actions,[string]$EventName,[string]$Ref) {
    if ($Actions -ne 'true' -or $EventName -ne 'workflow_dispatch' -or $Ref -ne 'refs/heads/main') { throw 'Signed beta packaging requires a manual main-branch GitHub Actions run.' }
}

function Remove-AndroidBetaSecrets([string]$WorkDirectory,[string]$RunnerDirectory) {
    $work=Get-AndroidBetaDirectory $WorkDirectory $RunnerDirectory
    $secret=Join-Path $work '.signing'
    if (-not (Test-Path -LiteralPath $secret)) { return }
    if (((Get-Item -LiteralPath $secret -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing a linked signing directory.' }
    foreach ($name in @('upload.jks','password.txt')) {
        $file=Join-Path $secret $name
        if (Test-Path -LiteralPath $file) {
            if (((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing a linked signing file.' }
            Remove-Item -LiteralPath $file -Force
        }
    }
    # Never recursively remove a computed directory or unrelated runner files.
    if (@(Get-ChildItem -LiteralPath $secret -Force).Count -eq 0) { Remove-Item -LiteralPath $secret -Force }
}
