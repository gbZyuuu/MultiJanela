# Building and validating

Use Windows x64, PowerShell, the .NET 8 SDK and Python 3.11+ on PATH. Restore requires access to NuGet. Git is needed only to clone and track the source. Do not run the production manager or use real accounts for the automated fixtures.

```powershell
git clone https://github.com/gbZyuuu/MultiJanela.git
cd MultiJanela
./build.ps1
./tests/RobloxMultiManager.UiSmoke/verify.ps1
python tools/verify_localization.py
```

`build.ps1` builds the app and native fixture and runs the functional checks, followed by the offline WebView2 login fixture. The browser fixture requires WebView2 Runtime; it intercepts login/identity requests in a fresh test profile, never using real credentials. UI validation constructs the real WPF controls with synthetic accounts and offscreen windows. It does not run production startup, authenticate accounts, launch Roblox or modify existing Roblox sessions. Previews are generated under `dist/_ui-preview/release-1.0.0` and the corresponding `-pt` directory.

To create a self-contained x64 distribution:

```powershell
./package-public.ps1 -Version 1.0.0
```

The version must match the application project. The packaging script refuses to overwrite an existing delivery. It publishes the executable, includes license notices and the bilingual user guide, validates an exact six-file allowlist and SHA256 manifest, then creates the ZIP. Nothing from local account storage is copied. `publish.ps1` is a lower-level publish helper; it is not the complete audited distribution workflow.

The public package check is portable and has no parent-repository dependency. Build output can vary by SDK, dependencies and source revision; a locally built file need not have the release checksum. This project does not have a code-signing certificate. Online vulnerability-feed failures must not be presented as a successful dependency-security audit.

## Português

Instale SDK .NET 8, Python 3.11+ e PowerShell no Windows. Execute os comandos acima na raiz. Os testes usam contas sintéticas e janelas de teste; não autenticam contas reais ou iniciam Roblox. O script de pacote exige a versão correta, inclui licenças e verifica a lista de arquivos e seus hashes. Nenhum teste certifica aceitação do Roblox ou proteção contra punições.

## Website

The GitHub Pages site in `docs/` is static HTML, CSS and JavaScript. No package installation or build step is required.

```powershell
python -m http.server 4177 --bind 127.0.0.1 --directory docs
node --test tests/website-demo.test.mjs
```

Open `http://127.0.0.1:4177`. The demo uses fictional in-memory data and persists only appearance/language preferences. It never authenticates to Roblox or launches processes. The desktop screenshot fixture supports `--studio --theme-gallery` to capture all nine dashboard palettes with synthetic profiles.
