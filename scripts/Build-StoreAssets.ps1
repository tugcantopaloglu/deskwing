param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=Join-Path $taskRoot 'assets\app-icon.svg'
$taskAssets=Join-Path $taskRoot 'release\store-assets'
$taskCatalog=Join-Path $taskRoot 'ios\Resources\Assets.xcassets\AppIcon.appiconset'
[xml]$taskSvg=Get-Content -LiteralPath $taskSource -Raw
New-Item -ItemType Directory -Path $taskAssets,$taskCatalog -Force | Out-Null
function Write-StoreIcon([int]$Size,[string]$Path){
    # The square opaque canvas lets each platform apply its own icon mask.
    $bitmap=[Drawing.Bitmap]::new($Size,$Size,[Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#101318'))
        $graphics.ScaleTransform($Size/256.0,$Size/256.0)
        foreach($rect in $taskSvg.svg.rect){
            $x=if($rect.x){[single]$rect.x}else{0};$y=if($rect.y){[single]$rect.y}else{0}
            $width=[single]$rect.width;$height=[single]$rect.height;$diameter=2*[single]$rect.rx
            $shape=[Drawing.Drawing2D.GraphicsPath]::new()
            $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($rect.fill))
            try {
                $shape.AddArc($x,$y,$diameter,$diameter,180,90)
                $shape.AddArc($x+$width-$diameter,$y,$diameter,$diameter,270,90)
                $shape.AddArc($x+$width-$diameter,$y+$height-$diameter,$diameter,$diameter,0,90)
                $shape.AddArc($x,$y+$height-$diameter,$diameter,$diameter,90,90)
                $shape.CloseFigure();$graphics.FillPath($brush,$shape)
            } finally {$shape.Dispose();$brush.Dispose()}
        }
        $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
    } finally {$graphics.Dispose();$bitmap.Dispose()}
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($bytes.Length -lt 33 -or $bytes[25] -ne 2){throw 'Store artwork must be an opaque RGB PNG.'}
}
$taskIos=Join-Path $taskAssets 'deskwing-app-store-1024.png'
$taskPlay=Join-Path $taskAssets 'deskwing-play-512.png'
Write-StoreIcon 1024 $taskIos
Write-StoreIcon 512 $taskPlay
Copy-Item -LiteralPath $taskIos -Destination (Join-Path $taskCatalog 'AppIcon.png') -Force
$taskManifest=@{
    source='assets/app-icon.svg'
    sourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant()
    artwork='Original DeskWing project artwork; platform masking is applied by the operating system.'
    files=@(
        @{path='store-assets/deskwing-app-store-1024.png';width=1024;height=1024;alpha=$false;sha256=(Get-FileHash -LiteralPath $taskIos -Algorithm SHA256).Hash.ToLowerInvariant()},
        @{path='store-assets/deskwing-play-512.png';width=512;height=512;alpha=$false;sha256=(Get-FileHash -LiteralPath $taskPlay -Algorithm SHA256).Hash.ToLowerInvariant()}
    )
}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'release\store-assets.json') -Encoding utf8
Write-Output 'Built opaque 1024px App Store and 512px Play artwork from the original SVG.'
