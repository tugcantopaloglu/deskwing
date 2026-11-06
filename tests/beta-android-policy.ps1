$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\beta-android-policy.ps1')
$passed=0
function Check([bool]$Condition,[string]$Label) { if (-not $Condition) { throw $Label }; $script:passed++;Write-Output ('PASS '+$Label) }
function Reject([scriptblock]$Action,[string]$Label) { $rejected=$false;try { & $Action | Out-Null } catch { $rejected=$true };Check $rejected $Label }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('deskwing-beta-policy-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $work=Join-Path $fixture 'job'
    Check ((Get-AndroidBetaDirectory $work $fixture) -eq $work) 'work is admitted only below the runner temporary root'
    Reject { Get-AndroidBetaDirectory $fixture $fixture } 'the runner root itself is never a cleanup target'
    Reject { Get-AndroidBetaDirectory ($fixture+'-other\job') $fixture } 'a sibling with the same textual prefix is rejected'
    Reject { Get-AndroidBetaDirectory (Join-Path $fixture '..\escape') $fixture } 'normalized parent traversal is rejected'
    Reject { Get-AndroidBetaDirectory $work '' } 'missing runner root fails closed'
    $manifestPath=Join-Path $fixture 'AndroidManifest.xml'
    $valid='<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="app.tabletdeck.studio" android:versionCode="18" android:versionName="1.15.0"><uses-sdk android:minSdkVersion="26" android:targetSdkVersion="36" /></manifest>'
    [IO.File]::WriteAllText($manifestPath,$valid)
    $manifest=Get-AndroidBetaManifest $manifestPath
    Check ($manifest.package -eq 'app.tabletdeck.studio' -and $manifest.versionCode -eq 18 -and $manifest.targetSdk -eq 36) 'source identity and SDK are retained'
    foreach ($bad in @(
        $valid.Replace('app.tabletdeck.studio','example.other'),
        $valid.Replace('versionCode="18"','versionCode="0"'),
        $valid.Replace('versionCode="18"','versionCode="2100000001"'),
        $valid.Replace('targetSdkVersion="36"','targetSdkVersion="35"'),
        $valid.Replace('minSdkVersion="26"','minSdkVersion="25"'),
        $valid.Replace('versionName="1.15.0"','versionName="next"'),
        ('<!DOCTYPE manifest [<!ENTITY remote SYSTEM "file:///unavailable">]>'+$valid)
    )) {
        [IO.File]::WriteAllText($manifestPath,$bad)
        Reject { Get-AndroidBetaManifest $manifestPath } 'wrong identity, version, SDK or external XML declarations are rejected'
    }
    $fingerprint='A'*64
    Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '17' 18 'fixture-password'
    Check $true 'an existing key with a strictly newer source version is accepted'
    Assert-AndroidBetaSigning $fingerprint 'upload_key-2' '0' 18 'fixture-password'
    Check $true 'first Play upload is explicit through a zero published-code floor'
    Reject { Assert-AndroidBetaSigning 'bad' 'tabletdeck' '17' 18 'fixture-password' } 'missing expected certificate cannot silently select another signer'
    Reject { Assert-AndroidBetaSigning $fingerprint '../key' '17' 18 'fixture-password' } 'unsafe key alias is rejected'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '18' 18 'fixture-password' } 'same published version code is rejected'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '19' 18 'fixture-password' } 'downgrade code is rejected'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '' 18 'fixture-password' } 'unknown Play version history requires an owner decision'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '9999999999' 18 'fixture-password' } 'overflowing version history is rejected'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '17' 18 '' } 'missing password is rejected'
    Reject { Assert-AndroidBetaSigning $fingerprint 'tabletdeck' '17' 18 "line1`nline2" } 'multiline password cannot disagree between signing tools'
    Assert-AndroidBetaSigningContext 'true' 'workflow_dispatch' 'refs/heads/main'
    Check $true 'signed packages require an explicit manual main run'
    Reject { Assert-AndroidBetaSigningContext 'true' 'push' 'refs/heads/main' } 'a main push cannot force signing through the helper'
    Reject { Assert-AndroidBetaSigningContext 'true' 'push' 'refs/heads/codex/workspace-expansion' } 'the bounded unsigned validation push cannot sign'
    Reject { Assert-AndroidBetaSigningContext 'true' 'pull_request' 'refs/heads/main' } 'pull-request events cannot sign'
    Reject { Assert-AndroidBetaSigningContext 'true' 'workflow_dispatch' 'refs/heads/feature' } 'manual feature-branch dispatch cannot sign'
    Reject { Assert-AndroidBetaSigningContext 'false' 'workflow_dispatch' 'refs/heads/main' } 'an unmarked local process cannot sign'
    $secret=Join-Path $work '.signing'
    New-Item -ItemType Directory -Path $secret | Out-Null
    [IO.File]::WriteAllText((Join-Path $secret 'upload.jks'),'fixture')
    [IO.File]::WriteAllText((Join-Path $secret 'password.txt'),'fixture')
    [IO.File]::WriteAllText((Join-Path $work 'approved-package.aab'),'retained')
    Remove-AndroidBetaSecrets $work $fixture
    Check (-not (Test-Path -LiteralPath $secret)) 'owned key and password are removed after packaging'
    Check (Test-Path -LiteralPath (Join-Path $work 'approved-package.aab')) 'cleanup preserves approved packages and other runner files'
    Remove-AndroidBetaSecrets $work $fixture
    Check $true 'cleanup can safely run again from an always step'
    New-Item -ItemType Directory -Path $secret | Out-Null
    [IO.File]::WriteAllText((Join-Path $secret 'unexpected.txt'),'retained')
    Remove-AndroidBetaSecrets $work $fixture
    Check (Test-Path -LiteralPath (Join-Path $secret 'unexpected.txt')) 'cleanup does not recursively delete unexpected content'
    if ($IsWindows) {
        $target=Join-Path $fixture 'outside-signing'
        New-Item -ItemType Directory -Path $target | Out-Null
        $linked=Join-Path $fixture 'linked-job'
        New-Item -ItemType Junction -Path $linked -Target $target | Out-Null
        Reject { Get-AndroidBetaDirectory $linked $fixture } 'a junction cannot redirect the beta work directory'
        Remove-Item -LiteralPath $linked -Force
    }
    Write-Output ($passed.ToString()+' Android beta policy checks passed; no key, SDK download, package install or Play upload used.')
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if ($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('deskwing-beta-policy-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
