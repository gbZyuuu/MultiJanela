param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão inválida.' }
$projectRoot = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path $dotnet -Parent
$raw = Join-Path $projectRoot "dist\_public-publish-$Version"
$delivery = Join-Path $projectRoot "dist\MultiJanela-$Version-win-x64"
$archive = "$delivery.zip"
[xml]$projectVersion = Get-Content -LiteralPath (Join-Path $projectRoot 'src\RobloxMultiManager\RobloxMultiManager.csproj')
if ($projectVersion.Project.PropertyGroup.Version -ne $Version) { throw 'A versão do pacote deve coincidir com a versão do projeto.' }
if ((Test-Path -LiteralPath $delivery) -or (Test-Path -LiteralPath $archive)) {
    throw 'O pacote já existe. Verifique-o antes de criar outra entrega; nenhuma versão será sobrescrita.'
}
$build = (Join-Path $projectRoot 'dist\_bilingual-ui-build').Replace('\', '/') + '/'
& $dotnet publish (Join-Path $projectRoot 'src\RobloxMultiManager\RobloxMultiManager.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -p:PublishTrimmed=false `
    "-p:BaseOutputPath=$build" -o $raw --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o pacote público.' }
$sdkLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$sdkNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
$webViewLicense = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4078.44\LICENSE.txt'
foreach ($path in @($sdkLicense, $sdkNotices, $webViewLicense)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Licença não encontrada: $path" }
}
New-Item -ItemType Directory -Path $delivery | Out-Null
foreach ($name in @('RobloxMultiManager.exe', 'README.md', 'LICENSE', 'NOTICE.md')) {
    Copy-Item -LiteralPath (Join-Path $raw $name) -Destination (Join-Path $delivery $name)
}
$thirdParty = @('Microsoft .NET', (Get-Content -LiteralPath $sdkLicense -Raw),
    'Microsoft .NET - Third Party Notices', (Get-Content -LiteralPath $sdkNotices -Raw),
    'Microsoft Edge WebView2 SDK', (Get-Content -LiteralPath $webViewLicense -Raw)) -join "`r`n`r`n"
[IO.File]::WriteAllText((Join-Path $delivery 'THIRD-PARTY-NOTICES.txt'), $thirdParty, [Text.UTF8Encoding]::new($false))
$hashes = Get-ChildItem -LiteralPath $delivery -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $delivery 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
& python (Join-Path $projectRoot 'tools\check_public_package.py') $delivery
if ($LASTEXITCODE -ne 0) { throw 'O pacote foi recusado pela verificação pública.' }
Compress-Archive -LiteralPath $delivery -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Host "Pacote para distribuição: $archive"
