dotnet build "d:\Project\DesktopKomik\Yomic.sln" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dest = 'd:\Project\DesktopKomik\PackedExtensions'
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$extensions = @('Kiryuu', 'KomikCast', 'Komiku', 'Mangabats', 'Weebcentral', 'Softkomik')
foreach ($ext in $extensions) {
    echo "Copying $ext..."
    $src = "d:\Project\DesktopKomik\Yomic\Extensions\$ext\bin\Release\net10.0\Yomic.Extensions.$ext.dll"
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dest
    } else {
        Write-Error "Could not find DLL for $ext at $src"
    }
}

$zipPath = 'd:\Project\DesktopKomik\Extensions.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$dest\*" -DestinationPath $zipPath -Force
Write-Host "Zip created at $zipPath"
