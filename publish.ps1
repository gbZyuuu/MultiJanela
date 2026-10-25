param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$appProject = Join-Path $projectRoot "src\RobloxMultiManager\RobloxMultiManager.csproj"
[xml]$projectXml = Get-Content -LiteralPath $appProject
$appVersion = $projectXml.Project.PropertyGroup.Version
$output = Join-Path $projectRoot "dist\RobloxMultiManager-$appVersion-$Runtime"

& $dotnetPath publish $appProject `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishTrimmed=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Falha ao publicar o aplicativo."
}

Write-Host "Executável publicado em: $output"
