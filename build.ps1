param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$appProject = Join-Path $projectRoot "src\RobloxMultiManager\RobloxMultiManager.csproj"
$testsProject = Join-Path $projectRoot "tests\RobloxMultiManager.Tests\RobloxMultiManager.Tests.csproj"
$fixtureProject = Join-Path $projectRoot "tests\RobloxWindowFixture\RobloxWindowFixture.csproj"

& $dotnetPath build $appProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o aplicativo." }

& $dotnetPath build $fixtureProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar a fixture." }

& $dotnetPath build $testsProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar os testes." }

$testsDll = Join-Path $projectRoot "tests\RobloxMultiManager.Tests\bin\$Configuration\net8.0-windows\RobloxMultiManager.Tests.dll"
$fixtureExe = Join-Path $projectRoot "tests\RobloxWindowFixture\bin\$Configuration\net8.0-windows\RobloxWindowFixture.exe"
& $dotnetPath $testsDll --fixture $fixtureExe
if ($LASTEXITCODE -ne 0) { throw "Um ou mais testes falharam." }

$loginProject = Join-Path $projectRoot "tests\RobloxMultiManager.LoginSmoke\RobloxMultiManager.LoginSmoke.csproj"
& $dotnetPath build $loginProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar a fixture de login." }
$loginDll = Join-Path $projectRoot "tests\RobloxMultiManager.LoginSmoke\bin\$Configuration\net8.0-windows\RobloxMultiManager.LoginSmoke.dll"
& $dotnetPath $loginDll $projectRoot
if ($LASTEXITCODE -ne 0) { throw "Falha na verificação offline do navegador de login." }
