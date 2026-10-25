$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$outputRoot = (Join-Path $projectRoot 'dist\_bilingual-ui-build').Replace('\', '/') + '/'
$previews = Join-Path $projectRoot 'dist\_ui-preview\focus-2.8.0'
& $dotnetPath build $PSScriptRoot -c Release "-p:BaseOutputPath=$outputRoot"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar a validação de UI.' }
$smoke = Join-Path $outputRoot 'Release\net8.0-windows\RobloxMultiManager.UiSmoke.dll'
& $dotnetPath $smoke $previews --studio
if ($LASTEXITCODE -ne 0) { throw "English UI validation failed." }
& $dotnetPath $smoke ($previews + "-pt") --pt --studio
if ($LASTEXITCODE -ne 0) { throw 'Falha na validação de UI.' }
