param(
 [Parameter(Mandatory=$true)][string]$Toolchain,
 [string]$OutputDirectory,
 [string]$JdkDirectory,
 [string]$AndroidSdkDirectory,
 [string]$IntermediateDirectory,
 [string]$SigningKey,
 [string]$SigningPasswordFile,
 [string]$SigningAlias='tabletdeck',
 [string]$ExpectedCertificateSha256,
 [switch]$Unsigned,
 [switch]$Development
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskTools=(Resolve-Path -LiteralPath $Toolchain).Path
if(!$JdkDirectory){$JdkDirectory=(Get-ChildItem -LiteralPath (Join-Path $taskTools 'jdk') -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'bin\javac.exe')} | Select-Object -First 1).FullName}
if(!$JdkDirectory){throw 'JDK 17 was not found. Set -JdkDirectory to an existing JDK root.'}
$taskJdk=(Resolve-Path -LiteralPath $JdkDirectory).Path
if(!$AndroidSdkDirectory){$AndroidSdkDirectory=Join-Path $taskTools 'sdk'}
$taskSdk=(Resolve-Path -LiteralPath $AndroidSdkDirectory).Path
$taskPlatformCandidates=@((Join-Path $taskSdk 'platforms\android-36\android.jar'),(Join-Path $taskSdk 'android-36\android.jar'))
$taskPlatform=$taskPlatformCandidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
if(!$taskPlatform){throw 'Android SDK platform 36 is required. Install the pinned platform archive or set -AndroidSdkDirectory.'}
$taskBuildCandidates=@((Join-Path $taskSdk 'build-tools\36.1.0'),(Join-Path $taskSdk 'build-tools\36.0.0'),(Join-Path $taskSdk 'android-16.1'),(Join-Path $taskSdk 'android-16'))
$taskBuildTools=$taskBuildCandidates | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'aapt2.exe')} | Select-Object -First 1
if(!$taskBuildTools){throw 'Android Build Tools 36.x are required. Install the pinned build-tools archive or set -AndroidSdkDirectory.'}
if($Development -and ($Unsigned -or $SigningKey -or $SigningPasswordFile -or $ExpectedCertificateSha256)){throw '-Development cannot be combined with release signing options or -Unsigned.'}
if($Development){
 $taskDevelopmentKeys=Join-Path $taskRoot '.test-output\android-development-signing'
 $SigningKey=Join-Path $taskDevelopmentKeys 'deskwing-development.jks'
 $SigningPasswordFile=Join-Path $taskDevelopmentKeys 'password.txt'
 $SigningAlias='deskwing-development'
}else{
 if(!$SigningKey){$SigningKey=Join-Path $taskTools 'signing\tabletdeck.jks'}
 if(!$SigningPasswordFile){$SigningPasswordFile=Join-Path $taskTools 'signing\password.txt'}
 if(!$Unsigned -and (!(Test-Path -LiteralPath $SigningKey) -or !(Test-Path -LiteralPath $SigningPasswordFile))){throw 'The existing signing key/password file is missing. Supply -SigningKey and -SigningPasswordFile. No replacement identity is generated; use -Unsigned only for compile checks.'}
}
if($ExpectedCertificateSha256 -and $ExpectedCertificateSha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'ExpectedCertificateSha256 must be a 64-character SHA-256 hex digest.'}
$taskAndroid=Join-Path $taskRoot 'android'
if(!$IntermediateDirectory){$IntermediateDirectory=Join-Path $taskRoot ('build\android-'+[guid]::NewGuid().ToString('N'))}
$taskBuild=[IO.Path]::GetFullPath($IntermediateDirectory)
if((Test-Path -LiteralPath $taskBuild) -and @(Get-ChildItem -LiteralPath $taskBuild -Force).Count){throw 'IntermediateDirectory must be new or empty so stale classes/assets cannot enter an APK.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskRoot 'dist'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[xml]$taskManifest=Get-Content -LiteralPath (Join-Path $taskAndroid 'AndroidManifest.xml') -Raw
$taskNamespace='http://schemas.android.com/apk/res/android'
$taskTarget=[int]$taskManifest.manifest.'uses-sdk'.GetAttribute('targetSdkVersion',$taskNamespace)
if($taskTarget -ne 36){throw 'The release manifest must target SDK 36; update the manifest and compile platform together.'}
New-Item -ItemType Directory -Force -Path $taskBuild,(Join-Path $taskBuild 'classes'),(Join-Path $taskBuild 'dex'),(Join-Path $taskBuild 'assets\www'),$OutputDirectory | Out-Null
$taskManifestPath=Join-Path $taskAndroid 'AndroidManifest.xml'
if($Development){
 $taskManifest.manifest.SetAttribute('package','app.tabletdeck.studio.development')
 $taskManifest.manifest.application.SetAttribute('label',$taskNamespace,'DeskWing Development') | Out-Null
 $taskManifestPath=Join-Path $taskBuild 'AndroidManifest-development.xml'
 $taskManifest.Save($taskManifestPath)
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\index.html'),(Join-Path $taskRoot 'companion\wwwroot\app.js'),(Join-Path $taskRoot 'companion\wwwroot\style.css') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\features.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\boards.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\desktop.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\studio.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\monitor.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\hardening.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\latency.js') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\computer.js'),(Join-Path $taskRoot 'companion\wwwroot\computer.css') -Destination (Join-Path $taskBuild 'assets\www')
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\media-touchbar.js'),(Join-Path $taskRoot 'companion\wwwroot\media-touchbar.css'),(Join-Path $taskRoot 'companion\wwwroot\soundpad-board.js') -Destination (Join-Path $taskBuild 'assets\www')
foreach($taskAsset in @('editor.js','editor.css','macro-editor.js','macro-editor.css','pen.js','pen.css','tablet-workflows.css')){
 Copy-Item -LiteralPath (Join-Path $taskRoot ('companion\wwwroot\'+$taskAsset)) -Destination (Join-Path $taskBuild 'assets\www')
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\fonts') -Destination (Join-Path $taskBuild 'assets\www') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\vendor') -Destination (Join-Path $taskBuild 'assets\www') -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $taskBuild 'assets\licenses') | Out-Null
Copy-Item -Path (Join-Path $taskRoot 'companion\Licenses\*') -Destination (Join-Path $taskBuild 'assets\licenses') -Recurse -Force
$taskUiSource=Get-Content -LiteralPath (Join-Path $taskAndroid 'src\app\tabletdeck\MainActivity.java') -Raw
$taskAllowedBlock=[regex]::Match($taskUiSource,'UI_ASSETS=new HashSet<>\(Arrays\.asList\((.*?)\)\)',[Text.RegularExpressions.RegexOptions]::Singleline).Groups[1].Value
$taskAllowedPaths=@([regex]::Matches($taskAllowedBlock,'"([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
$taskNotebookSource=Get-Content -LiteralPath (Join-Path $taskAndroid 'src\app\tabletdeck\NotebookUiAssets.java') -Raw
$taskNotebookBlock=[regex]::Match($taskNotebookSource,'PATHS=new HashSet<>\(Arrays\.asList\((.*?)\)\)',[Text.RegularExpressions.RegexOptions]::Singleline).Groups[1].Value
$taskNotebookPaths=@([regex]::Matches($taskNotebookBlock,'"([^"]+)"') | ForEach-Object {$_.Groups[1].Value})
if($taskNotebookPaths.Count -lt 3){throw 'The notebook asset allowlist is missing. Run scripts/sync-notebook-assets.py.'}
$taskAllowedPaths=@($taskAllowedPaths+$taskNotebookPaths | Sort-Object -Unique)
$taskPdfManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'scripts\notebook-pdf-vendor.json') -Raw | ConvertFrom-Json
$taskPdfBytes=0L
if(@($taskPdfManifest.assets).Count -lt 1 -or @($taskPdfManifest.assets).Count -gt 512){throw 'Invalid offline PDF asset count.'}
foreach($taskAssetEntry in $taskPdfManifest.assets){
 $taskAssetName=[string]$taskAssetEntry.path
 if($taskAssetName -notmatch '^vendor/(pdfjs|pdf-lib)/' -or $taskAssetName.Contains('\') -or $taskAssetName.Split('/') -contains '..' -or $taskAssetName -match '[:?#]' -or $taskAllowedPaths -notcontains ('/'+$taskAssetName)){throw 'Unsafe or unregistered offline PDF asset.'}
 $taskVendorFile=Join-Path (Join-Path $taskRoot 'companion\wwwroot') $taskAssetName
 $taskVendorInfo=Get-Item -LiteralPath $taskVendorFile
 if($taskVendorInfo.Length -ne $taskAssetEntry.bytes -or $taskVendorInfo.Length -gt 12MB -or (Get-FileHash -LiteralPath $taskVendorFile -Algorithm SHA256).Hash -ne $taskAssetEntry.sha256){throw 'Offline PDF dependency checksum or size mismatch.'}
 $taskPdfBytes+=$taskVendorInfo.Length
 if($taskPdfBytes -gt 64MB){throw 'Offline PDF dependencies exceed the bundle budget.'}
}
$taskEntry=Get-Content -LiteralPath (Join-Path $taskRoot 'companion\wwwroot\index.html') -Raw
$taskReferencedAssets=@([regex]::Matches($taskEntry,'(?:src|href)="([^"]+\.(?:js|css))"') | ForEach-Object {$_.Groups[1].Value})
foreach($taskAsset in $taskReferencedAssets){
 if($taskAsset.Contains('\') -or $taskAsset.Split('/') -contains '..' -or [IO.Path]::IsPathRooted($taskAsset)){throw 'Unsafe entry-point asset path.'}
 if($taskAllowedPaths -notcontains ('/'+$taskAsset)){throw ('Native WebView blocks a referenced UI asset: '+$taskAsset)}
 $taskAssetDestination=Join-Path (Join-Path $taskBuild 'assets\www') $taskAsset
 New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskAssetDestination) | Out-Null
 Copy-Item -LiteralPath (Join-Path (Join-Path $taskRoot 'companion\wwwroot') $taskAsset) -Destination $taskAssetDestination -Force
}
function Invoke-Checked([string]$Exe,[string[]]$Arguments){& $Exe @Arguments;if($LASTEXITCODE -ne 0){throw "Build command failed: $Exe"}}
Invoke-Checked (Join-Path $taskBuildTools 'aapt2.exe') @('compile','--dir',(Join-Path $taskAndroid 'res'),'-o',(Join-Path $taskBuild 'resources.zip'))
Invoke-Checked (Join-Path $taskBuildTools 'aapt2.exe') @('link','-o',(Join-Path $taskBuild 'unsigned.apk'),'--manifest',$taskManifestPath,'-I',$taskPlatform,'-R',(Join-Path $taskBuild 'resources.zip'),'--auto-add-overlay')
# The Windows aapt2 asset packer emits backslashes in ZIP entry names. Android's
# AssetManager requires forward slashes; jar normalizes filesystem paths for us.
Invoke-Checked (Join-Path $taskJdk 'bin\jar.exe') @('uf',(Join-Path $taskBuild 'unsigned.apk'),'-C',$taskBuild,'assets')
$taskArchive=[System.IO.Compression.ZipFile]::OpenRead((Join-Path $taskBuild 'unsigned.apk'))
try {
 $taskEntries=@($taskArchive.Entries | ForEach-Object FullName)
 if(@($taskEntries | Where-Object { $_.Contains('\') }).Count){throw 'APK contains Windows path separators.'}
 foreach($taskAsset in @('index.html','app.js','features.js','boards.js','desktop.js','studio.js','monitor.js','hardening.js','latency.js','computer.js','computer.css','media-touchbar.js','media-touchbar.css','soundpad-board.js','editor.js','editor.css','macro-editor.js','macro-editor.css','pen.js','pen.css','tablet-workflows.css','style.css','fonts/Manrope.ttf','vendor/purify.min.js')){
  if($taskEntries -notcontains ('assets/www/'+$taskAsset)){throw "APK asset is missing: $taskAsset"}
 }
 foreach($taskAsset in $taskReferencedAssets){if($taskEntries -notcontains ('assets/www/'+$taskAsset)){throw "Referenced APK asset is missing: $taskAsset"}}
 foreach($taskLicense in @('ZXING-LICENSE.txt','QRCODER-LICENSE.txt')){if($taskEntries -notcontains ('assets/licenses/'+$taskLicense)){throw "APK license is missing: $taskLicense"}}
 foreach($taskAsset in $taskNotebookPaths){if($taskEntries -notcontains ('assets/www'+$taskAsset)){throw "APK notebook asset is missing: $taskAsset"}}
} finally {$taskArchive.Dispose()}
$taskSources=@(Get-ChildItem -LiteralPath (Join-Path $taskAndroid 'src') -Filter '*.java' -Recurse | ForEach-Object FullName)
$taskQr=Join-Path $taskAndroid 'vendor\zxing-core-3.5.3.jar'
if((Get-FileHash -LiteralPath $taskQr -Algorithm SHA256).Hash -ne '8D8064C1636FDAEF7189DD9055C7D59950A8940A12F2293956446EC3C109FD82'){throw 'ZXing dependency hash mismatch.'}
Invoke-Checked (Join-Path $taskJdk 'bin\javac.exe') (@('-encoding','UTF-8','--release','8','-classpath',($taskPlatform+';'+$taskQr),'-d',(Join-Path $taskBuild 'classes'))+$taskSources)
Push-Location (Join-Path $taskBuild 'classes')
try {Invoke-Checked (Join-Path $taskJdk 'bin\jar.exe') @('cf',(Join-Path $taskBuild 'classes.jar'),'.')} finally {Pop-Location}
Invoke-Checked (Join-Path $taskJdk 'bin\java.exe') @('-cp',(Join-Path $taskBuildTools 'lib\d8.jar'),'com.android.tools.r8.D8','--release','--min-api','26','--lib',$taskPlatform,'--output',(Join-Path $taskBuild 'dex'),(Join-Path $taskBuild 'classes.jar'),$taskQr)
Push-Location (Join-Path $taskBuild 'dex')
try {Invoke-Checked (Join-Path $taskJdk 'bin\jar.exe') @('uf',(Join-Path $taskBuild 'unsigned.apk'),'classes.dex')} finally {Pop-Location}
Invoke-Checked (Join-Path $taskBuildTools 'zipalign.exe') @('-f','-p','4',(Join-Path $taskBuild 'unsigned.apk'),(Join-Path $taskBuild 'aligned.apk'))
$taskCertificate=''
if($Development -and !(Test-Path -LiteralPath $SigningKey)){
 if(Test-Path -LiteralPath $SigningPasswordFile){throw 'A development password exists without its key. Choose a fresh explicit development signing directory instead of replacing it.'}
 New-Item -ItemType Directory -Force -Path $taskDevelopmentKeys | Out-Null
 $taskRandom=[byte[]]::new(32);[Security.Cryptography.RandomNumberGenerator]::Fill($taskRandom)
 [Convert]::ToHexString($taskRandom) | Set-Content -LiteralPath $SigningPasswordFile -NoNewline
 Invoke-Checked (Join-Path $taskJdk 'bin\keytool.exe') @('-genkeypair','-keystore',$SigningKey,'-storepass:file',$SigningPasswordFile,'-keypass:file',$SigningPasswordFile,'-alias',$SigningAlias,'-keyalg','RSA','-keysize','3072','-validity','3650','-dname','CN=DeskWing Development Only','-noprompt')
}
if($Development -and !(Test-Path -LiteralPath $SigningPasswordFile)){throw 'Development signing password file is missing; no replacement key was generated.'}
if($Unsigned){
 $taskApk=Join-Path $OutputDirectory 'DeskWing-unsigned.apk'
 Copy-Item -LiteralPath (Join-Path $taskBuild 'aligned.apk') -Destination $taskApk -Force
}else{
 $taskApk=Join-Path $OutputDirectory $(if($Development){'DeskWing-development.apk'}else{'DeskWing.apk'})
 Invoke-Checked (Join-Path $taskJdk 'bin\java.exe') @('-jar',(Join-Path $taskBuildTools 'lib\apksigner.jar'),'sign','--ks',$SigningKey,'--ks-key-alias',$SigningAlias,'--ks-pass',('file:'+$SigningPasswordFile),'--out',$taskApk,(Join-Path $taskBuild 'aligned.apk'))
 $taskVerification=& (Join-Path $taskJdk 'bin\java.exe') '-jar' (Join-Path $taskBuildTools 'lib\apksigner.jar') 'verify' '--verbose' '--print-certs' $taskApk
 if($LASTEXITCODE -ne 0){throw 'APK signature verification failed.'}
 $taskCertificate=[regex]::Match(($taskVerification -join "`n"),'certificate SHA-256 digest: ([a-fA-F0-9]{64})').Groups[1].Value.ToLowerInvariant()
 if(!$taskCertificate){throw 'APK signer fingerprint was not reported.'}
 if($ExpectedCertificateSha256 -and $taskCertificate -ne $ExpectedCertificateSha256.ToLowerInvariant()){throw 'APK signer does not match the expected existing installation. Do not install this APK.'}
 Write-Output $taskVerification
}
Invoke-Checked (Join-Path $taskBuildTools 'aapt.exe') @('dump','badging',$taskApk)
[ordered]@{apk=$taskApk;signed=(!$Unsigned);development=[bool]$Development;sha256=(Get-FileHash -LiteralPath $taskApk -Algorithm SHA256).Hash.ToLowerInvariant();certificateSha256=$taskCertificate;package=$taskManifest.manifest.GetAttribute('package');versionName=$taskManifest.manifest.GetAttribute('versionName',$taskNamespace);versionCode=[int]$taskManifest.manifest.GetAttribute('versionCode',$taskNamespace);targetSdk=$taskTarget;manifestPath=$taskManifestPath;intermediateDirectory=$taskBuild;jdkDirectory=$taskJdk;buildToolsDirectory=$taskBuildTools;platformJar=$taskPlatform} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'android-build.json') -Encoding utf8
Write-Output "APK built: $taskApk"
