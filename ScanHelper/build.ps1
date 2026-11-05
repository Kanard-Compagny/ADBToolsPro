# Compile l'appli de test du lecteur (ADBToolsScan.apk) sans Gradle, avec le SDK Android installé :
# aapt2 (manifeste) → javac → d8 (dex) → zipalign → apksigner. Utilisé par la compilation GitHub (build.yml) ;
# fonctionne aussi en local si ANDROID_HOME (ou ANDROID_SDK_ROOT) et un JDK 11+ sont présents.
param([string]$Output = "ADBToolsScan.apk")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
if (-not $sdk -or -not (Test-Path $sdk)) { throw "SDK Android introuvable (ANDROID_HOME)" }
$bt = Get-ChildItem "$sdk/build-tools" -Directory | Sort-Object { [version]($_.Name -replace '[^\d.].*$', '') } | Select-Object -Last 1
# Plateforme stable la plus récente (android-35, android-36…), pas les préversions (android-37.2-beta3).
$platform = Get-ChildItem "$sdk/platforms" -Directory | Where-Object { $_.Name -match '^android-\d+$' -and (Test-Path "$($_.FullName)/android.jar") } |
            Sort-Object { [int]($_.Name -replace '\D', '') } | Select-Object -Last 1
$androidJar = "$($platform.FullName)/android.jar"
Write-Host "build-tools $($bt.Name), $($platform.Name)"

# JDK 17 de préférence (d8 et apksigner demandent Java 11+).
foreach ($v in "JAVA_HOME_17_X64", "JAVA_HOME_21_X64", "JAVA_HOME_11_X64") {
    $p = [Environment]::GetEnvironmentVariable($v)
    if ($p) { $env:JAVA_HOME = $p; $env:PATH = "$p/bin;$env:PATH"; break }
}

& java -version 2>&1 | Select-Object -First 1 | Write-Host
$out = "build"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out/classes", "$out/dex" | Out-Null

& "$($bt.FullName)/aapt2" link -o "$out/base.apk" --manifest AndroidManifest.xml -I $androidJar
if ($LASTEXITCODE) { throw "aapt2" }

# Fichiers d'arguments (@fichier) : chemins Windows passés tels quels, sans interprétation par PowerShell.
function Q([string]$p) { '"' + ($p -replace '\\', '/') + '"' }
$javacArgs = @("-source", "8", "-target", "8", "-encoding", "UTF-8", "-nowarn",
               "-bootclasspath", (Q $androidJar), "-classpath", (Q $androidJar), "-d", (Q "$out/classes"))
$javacArgs += Get-ChildItem src -Recurse -Filter *.java | ForEach-Object { Q $_.FullName }
Set-Content -Path "$out/javac.args" -Value $javacArgs -Encoding ascii
& javac "@$out/javac.args"
if ($LASTEXITCODE) { throw "javac" }

& jar cf "$out/classes.jar" -C "$out/classes" .
if ($LASTEXITCODE) { throw "jar" }
& "$($bt.FullName)/d8.bat" --release --min-api 21 --lib $androidJar --output "$out/dex" "$out/classes.jar"
if ($LASTEXITCODE) { throw "d8" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open((Resolve-Path "$out/base.apk"), 'Update')
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path "$out/dex/classes.dex"), "classes.dex") | Out-Null
$zip.Dispose()

& "$($bt.FullName)/zipalign" -f -p 4 "$out/base.apk" "$out/aligned.apk"
if ($LASTEXITCODE) { throw "zipalign" }

# Clé de signature propre à chaque compilation : ADB Tools désinstalle l'appli de test avant de la réinstaller.
& keytool -genkeypair -keystore "$out/test.keystore" -storepass adbtools -keypass adbtools -alias scan `
    -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=ADB Tools Scan" | Out-Null
if ($LASTEXITCODE) { throw "keytool" }
& "$($bt.FullName)/apksigner.bat" sign --ks "$out/test.keystore" --ks-pass pass:adbtools --key-pass pass:adbtools --out $Output "$out/aligned.apk"
if ($LASTEXITCODE) { throw "apksigner" }
& "$($bt.FullName)/apksigner.bat" verify $Output
if ($LASTEXITCODE) { throw "apksigner verify" }
Write-Host "OK : $Output ($((Get-Item $Output).Length) octets)"
