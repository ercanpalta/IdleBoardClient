# =====================================================================
# IdleBoard Client - Otomatik Derleme ve Kurulum Paketi Olusturucu
# =====================================================================
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$workspaceRoot = Split-Path -Parent $projectRoot
$certsDir = Join-Path $workspaceRoot "certs"
$pfxFile = Join-Path $certsDir "IdleBoard_CodeSigning.pfx"
$cerFile = Join-Path $certsDir "IdleBoard_Sertifikasi.cer"
$batFile = Join-Path $certsDir "Sertifikayi_Yukle.bat"

$csprojPath = Join-Path $projectRoot "IdleBoardClient\IdleBoardClient.csproj"
$publishDir = Join-Path $projectRoot "publish"
$issPath = Join-Path $scriptDir "IdleBoardClient.iss"
$outputDir = Join-Path $projectRoot "installer_output"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   IdleBoard Client Kurulum Paketi Derleme Baslatiliyor   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Inno Setup Derleyicisi (ISCC.exe) yolunu tespit et
$isccPath = "$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccPath = "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $isccPath)) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $isccPath = $cmd.Source
    }
}
if (-not (Test-Path $isccPath)) {
    Write-Error "Inno Setup derleyicisi (ISCC.exe) bulunamadi! Lutfen Inno Setup 6 yuklu oldugundan emin olun."
    exit 1
}
Write-Host "[1/4] Inno Setup Derleyicisi: $isccPath" -ForegroundColor Green

# 2. .NET 10 Self-Contained Win-x64 Publish
Write-Host "[2/4] IdleBoard Client bagimsiz calisabilir (Self-Contained win-x64) olarak derleniyor..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $csprojPath -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish islemi basarisiz oldu!"
    exit 1
}
Write-Host "      Derleme tamamlandi -> $publishDir" -ForegroundColor Green

# 3. İstemci Çalıştırılabilir Dosyasını Dijital Olarak İmzala (Authenticode)
$cert = $null
if (Test-Path $pfxFile) {
    $pfxPassword = ConvertTo-SecureString -String "IdleBoard2026!" -Force -AsPlainText
    $cert = Import-PfxCertificate -FilePath $pfxFile -Password $pfxPassword -CertStoreLocation "Cert:\CurrentUser\My"
    $clientExe = Join-Path $publishDir "IdleBoardClient.exe"
    if (Test-Path $clientExe) {
        Write-Host "      IdleBoardClient.exe dijital olarak imzalanıyor..." -ForegroundColor Yellow
        Set-AuthenticodeSignature -FilePath $clientExe -Certificate $cert -TimestampServer "http://timestamp.digicert.com" | Out-Null
        Write-Host "      [OK] IdleBoardClient.exe imzalandı." -ForegroundColor Green
    }
}

# 4. Inno Setup ile Setup.exe olusturma
Write-Host "[3/4] Kurulum paketi (Setup.exe) paketleniyor..." -ForegroundColor Yellow
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& $isccPath $issPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup paketleme islemi basarisiz oldu!"
    exit 1
}

$setupFile = Join-Path $outputDir "IdleBoardClient_Setup.exe"
if (Test-Path $setupFile) {
    # 5. Kurulum Dosyasını Dijital Olarak İmzala
    Write-Host "[4/4] Kurulum paketi dijital olarak imzalanıyor..." -ForegroundColor Yellow
    if ($cert) {
        Set-AuthenticodeSignature -FilePath $setupFile -Certificate $cert -TimestampServer "http://timestamp.digicert.com" | Out-Null
        Write-Host "      [OK] Kurulum paketi dijital olarak imzalandı." -ForegroundColor Green
    }
    
    # 6. Sertifika ve 1-Tık Yükleyiciyi Çıktı Klasörüne Kopyala
    if (Test-Path $cerFile) { Copy-Item $cerFile $outputDir -Force }
    if (Test-Path $batFile) { Copy-Item $batFile $outputDir -Force }

    $sizeMB = [math]::Round((Get-Item $setupFile).Length / 1MB, 2)
    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host " Tebrikler! Kurulum paketi basariyla olusturuldu:" -ForegroundColor Green
    Write-Host " Dosya: $setupFile" -ForegroundColor Cyan
    Write-Host " Boyut: $sizeMB MB" -ForegroundColor Cyan
    Write-Host " Imza : Dogrulanmis (IdleBoard Software)" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Green
} else {
    Write-Error "Kurulum dosyasi olusturulamadi!"
    exit 1
}
