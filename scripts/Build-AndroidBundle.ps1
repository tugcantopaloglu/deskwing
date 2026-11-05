# Build a Play-format bundle with the same source/assets and signing identity as the APK.
# No Play Console upload, key creation, or key replacement is performed.
param(
 [Parameter(Mandatory=$true)][string]$Toolchain,
 [string]$OutputDirectory,
 [string]$JdkDirectory,
 [string]$AndroidSdkDirectory,
 [string]$ReleaseToolsDirectory,
 [string]$SigningKey,
 [string]$SigningPasswordFile,
 [string]$SigningAlias='tabletdeck',
 [string]$ExpectedCertificateSha256,
 [switch]$DownloadTools,
 [switch]$Unsigned,
 [switch]$Development
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskTools=(Resolve-Path -LiteralPath $Toolchain).Path
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskRoot 'dist'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(!$ReleaseToolsDirectory){$ReleaseToolsDirectory=Join-Path $taskTools 'release-tools'}
$ReleaseToolsDirectory=[IO.Path]::GetFullPath($ReleaseToolsDirectory)
if($Development -and ($Unsigned -or $SigningKey -or $SigningPasswordFile -or $ExpectedCertificateSha256)){throw '-Development cannot be combined with release signing options or -Unsigned.'}
if($Development){
 $SigningKey=Join-Path $taskRoot '.test-output\android-development-signing\deskwing-development.jks'
 $SigningPasswordFile=Join-Path $taskRoot '.test-output\android-development-signing\password.txt'
 $SigningAlias='deskwing-development'
}else{
 if(!$SigningKey){$SigningKey=Join-Path $taskTools 'signing\tabletdeck.jks'}
 if(!$SigningPasswordFile){$SigningPasswordFile=Join-Path $taskTools 'signing\password.txt'}
 if(!$Unsigned -and (!(Test-Path -LiteralPath $SigningKey) -or !(Test-Path -LiteralPath $SigningPasswordFile))){throw 'Original signing material is required. Set -SigningKey and -SigningPasswordFile; no replacement key will be generated. -Unsigned is only for compile/structure checks.'}
}
function Invoke-BundleChecked([string]$Exe,[string[]]$Arguments){& $Exe @Arguments;if($LASTEXITCODE -ne 0){throw "Android bundle command failed: $Exe"}}
New-Item -ItemType Directory -Force -Path $OutputDirectory,$ReleaseToolsDirectory | Out-Null
$taskPins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'android-bundle-tools.json') -Raw | ConvertFrom-Json
$taskToolFiles=@{}
foreach($taskPin in $taskPins){
 if($taskPin.file -notmatch '^[a-zA-Z0-9._-]+\.jar$' -or $taskPin.sha256 -notmatch '^[a-f0-9]{64}$'){throw 'Invalid release-tool pin.'}
 $taskUri=[Uri]$taskPin.url
 if($taskUri.Scheme -ne 'https' -or $taskUri.Host -notin @('github.com','dl.google.com')){throw 'Release tools must come from the pinned official HTTPS source.'}
 $taskFile=Join-Path $ReleaseToolsDirectory $taskPin.file
 if(!(Test-Path -LiteralPath $taskFile)){
  if(!$DownloadTools){throw "Missing pinned tool $($taskPin.file). Run again with -DownloadTools to download and verify it."}
  Invoke-WebRequest -Uri $taskUri -OutFile $taskFile
 }
 if((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskPin.sha256){throw "Checksum mismatch for $($taskPin.file). The file will not be executed."}
 $taskToolFiles[$taskPin.component]=$taskFile
}

# A fresh intermediate directory prevents stale classes/assets from becoming release content.
$taskBuild=Join-Path $taskRoot ('build\bundle-'+[guid]::NewGuid().ToString('N'))
$taskApkArgs=@{Toolchain=$taskTools;OutputDirectory=$OutputDirectory;IntermediateDirectory=(Join-Path $taskBuild 'apk');Unsigned=$Unsigned;Development=$Development}
if(!$Development){$taskApkArgs.SigningKey=$SigningKey;$taskApkArgs.SigningPasswordFile=$SigningPasswordFile;$taskApkArgs.SigningAlias=$SigningAlias}
if($JdkDirectory){$taskApkArgs.JdkDirectory=$JdkDirectory}
if($AndroidSdkDirectory){$taskApkArgs.AndroidSdkDirectory=$AndroidSdkDirectory}
if($ExpectedCertificateSha256){$taskApkArgs.ExpectedCertificateSha256=$ExpectedCertificateSha256}
& (Join-Path $PSScriptRoot 'Build-Android.ps1') @taskApkArgs
$taskApk=Get-Content -LiteralPath (Join-Path $OutputDirectory 'android-build.json') -Raw | ConvertFrom-Json
$taskJava=Join-Path $taskApk.jdkDirectory 'bin\java.exe'
$taskBundletool=$taskToolFiles.bundletool
$taskAaptDir=Join-Path $taskBuild 'aapt2'
New-Item -ItemType Directory -Path $taskAaptDir | Out-Null
$taskAaptArchive=[IO.Compression.ZipFile]::OpenRead($taskToolFiles.aapt2)
try{
 $taskAaptEntry=$taskAaptArchive.GetEntry('aapt2.exe')
 if(!$taskAaptEntry){throw 'The pinned Maven AAPT2 archive has no aapt2.exe.'}
 [IO.Compression.ZipFileExtensions]::ExtractToFile($taskAaptEntry,(Join-Path $taskAaptDir 'aapt2.exe'))
}finally{$taskAaptArchive.Dispose()}
$taskAapt=Join-Path $taskAaptDir 'aapt2.exe'
$taskResources=Join-Path $taskBuild 'resources.zip'
$taskProto=Join-Path $taskBuild 'proto.apk'
Invoke-BundleChecked $taskAapt @('compile','--dir',(Join-Path $taskRoot 'android\res'),'-o',$taskResources)
Invoke-BundleChecked $taskAapt @('link','--proto-format','-o',$taskProto,'--manifest',$taskApk.manifestPath,'-I',$taskApk.platformJar,'-R',$taskResources,'--auto-add-overlay')

# Only known AAPT2 output roots enter the base module. Never extract arbitrary ZIP paths.
$taskModule=Join-Path $taskBuild 'base'
New-Item -ItemType Directory -Path $taskModule,(Join-Path $taskModule 'manifest'),(Join-Path $taskModule 'dex') | Out-Null
$taskProtoArchive=[IO.Compression.ZipFile]::OpenRead($taskProto)
try{
 foreach($taskEntry in $taskProtoArchive.Entries){
  if($taskEntry.FullName.EndsWith('/')){continue}
  $taskRelative=if($taskEntry.FullName -eq 'AndroidManifest.xml'){'manifest/AndroidManifest.xml'}elseif($taskEntry.FullName -eq 'resources.pb' -or $taskEntry.FullName.StartsWith('res/')){$taskEntry.FullName}else{throw "Unexpected proto resource entry: $($taskEntry.FullName)"}
  if($taskRelative.Contains('\') -or $taskRelative.Split('/') -contains '..' -or [IO.Path]::IsPathRooted($taskRelative)){throw 'Unsafe proto resource path.'}
  $taskDestination=[IO.Path]::GetFullPath((Join-Path $taskModule $taskRelative))
  if(!$taskDestination.StartsWith(([IO.Path]::GetFullPath($taskModule).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Proto resource path escaped the module.'}
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskDestination) | Out-Null
  [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,$taskDestination)
 }
}finally{$taskProtoArchive.Dispose()}
Copy-Item -LiteralPath (Join-Path $taskApk.intermediateDirectory 'assets') -Destination $taskModule -Recurse
Get-ChildItem -LiteralPath (Join-Path $taskApk.intermediateDirectory 'dex') -Filter 'classes*.dex' -File | Copy-Item -Destination (Join-Path $taskModule 'dex')
$taskBaseZip=Join-Path $taskBuild 'base.zip'
Invoke-BundleChecked (Join-Path $taskApk.jdkDirectory 'bin\jar.exe') @('--create','--file',$taskBaseZip,'--no-manifest','-C',$taskModule,'.')
$taskUnsignedBundle=Join-Path $taskBuild 'unsigned.aab'
Invoke-BundleChecked $taskJava @('-jar',$taskBundletool,'build-bundle',('--modules='+$taskBaseZip),('--output='+$taskUnsignedBundle))
$taskBundle=Join-Path $OutputDirectory $(if($Unsigned){'DeskWing-unsigned.aab'}elseif($Development){'DeskWing-development.aab'}else{'DeskWing.aab'})
if($Unsigned){Copy-Item -LiteralPath $taskUnsignedBundle -Destination $taskBundle -Force}else{
 Invoke-BundleChecked (Join-Path $taskApk.jdkDirectory 'bin\jarsigner.exe') @('-J-Duser.language=en','-keystore',$SigningKey,'-storepass:file',$SigningPasswordFile,'-digestalg','SHA-256','-signedjar',$taskBundle,$taskUnsignedBundle,$SigningAlias)
 $taskVerify=& (Join-Path $taskApk.jdkDirectory 'bin\jarsigner.exe') '-J-Duser.language=en' '-verify' $taskBundle
 if($LASTEXITCODE -ne 0 -or ($taskVerify -join "`n") -notmatch 'jar verified\.'){throw 'AAB signature verification failed.'}
 Write-Output $taskVerify
}
Invoke-BundleChecked $taskJava @('-jar',$taskBundletool,'validate',('--bundle='+$taskBundle))
$taskManifestText=& $taskJava '-jar' $taskBundletool 'dump' 'manifest' ('--bundle='+$taskBundle) '--module=base'
if($LASTEXITCODE -ne 0){throw 'Cannot inspect bundle manifest.'}
[xml]$taskManifest=$taskManifestText -join "`n"
$taskNs='http://schemas.android.com/apk/res/android'
if($taskManifest.manifest.GetAttribute('package') -ne $taskApk.package -or [int]$taskManifest.manifest.GetAttribute('versionCode',$taskNs) -ne $taskApk.versionCode -or [int]$taskManifest.manifest.'uses-sdk'.GetAttribute('targetSdkVersion',$taskNs) -ne 36){throw 'AAB package/version/SDK differs from the compiled APK.'}
$taskUniversal=''
if(!$Unsigned){
 $taskApks=Join-Path $taskBuild 'universal.apks'
 Invoke-BundleChecked $taskJava @('-jar',$taskBundletool,'build-apks',('--bundle='+$taskBundle),('--output='+$taskApks),'--mode=universal',('--aapt2='+$taskAapt),('--ks='+$SigningKey),('--ks-pass=file:'+$SigningPasswordFile),('--ks-key-alias='+$SigningAlias))
 $taskUniversal=Join-Path $OutputDirectory $(if($Development){'DeskWing-development-universal.apk'}else{'DeskWing-universal.apk'})
 $taskApksArchive=[IO.Compression.ZipFile]::OpenRead($taskApks)
 try{$taskUniversalEntry=$taskApksArchive.GetEntry('universal.apk');if(!$taskUniversalEntry){throw 'Bundletool produced no universal APK.'};[IO.Compression.ZipFileExtensions]::ExtractToFile($taskUniversalEntry,$taskUniversal,$true)}finally{$taskApksArchive.Dispose()}
 $taskVerification=& $taskJava '-jar' (Join-Path $taskApk.buildToolsDirectory 'lib\apksigner.jar') 'verify' '--print-certs' $taskUniversal
 if($LASTEXITCODE -ne 0){throw 'Bundle-derived APK signature verification failed.'}
 $taskFingerprint=[regex]::Match(($taskVerification -join "`n"),'certificate SHA-256 digest: ([a-fA-F0-9]{64})').Groups[1].Value.ToLowerInvariant()
 if($taskFingerprint -ne $taskApk.certificateSha256){throw 'Bundle-derived APK signer differs from the source APK.'}
 $taskExpected=[IO.Compression.ZipFile]::OpenRead($taskApk.apk);$taskActual=[IO.Compression.ZipFile]::OpenRead($taskUniversal)
 try{
  foreach($taskEntry in $taskExpected.Entries | Where-Object {$_.FullName.StartsWith('assets/') -and !$_.FullName.EndsWith('/') -or $_.FullName -match '^classes[0-9]*\.dex$'}){
   $taskOther=$taskActual.GetEntry($taskEntry.FullName);if(!$taskOther){throw "Bundle-derived APK is missing $($taskEntry.FullName)"}
   $taskFirst=$taskEntry.Open();$taskSecond=$taskOther.Open()
   try{$taskHashOne=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskFirst));$taskHashTwo=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskSecond));if($taskHashOne -ne $taskHashTwo){throw "Bundle-derived APK content mismatch: $($taskEntry.FullName)"}}finally{$taskFirst.Dispose();$taskSecond.Dispose()}
  }
 }finally{$taskExpected.Dispose();$taskActual.Dispose()}
}
[ordered]@{bundle=$taskBundle;signed=(!$Unsigned);development=[bool]$Development;sha256=(Get-FileHash -LiteralPath $taskBundle -Algorithm SHA256).Hash.ToLowerInvariant();package=$taskApk.package;versionName=$taskApk.versionName;versionCode=$taskApk.versionCode;targetSdk=36;certificateSha256=$taskApk.certificateSha256;universalApk=$taskUniversal;bundletoolVersion='1.18.3';aapt2Version='9.5.1-16666295';intermediateDirectory=$taskBuild} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'android-bundle.json') -Encoding utf8
Write-Output "AAB built and validated: $taskBundle"
