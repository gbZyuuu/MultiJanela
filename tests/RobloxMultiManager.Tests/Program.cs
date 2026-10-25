using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (CpuQuotaWatchdog.RunIfRequested(args) is { } workerExit) return workerExit;
        if (args.FirstOrDefault() == "--cpu-fixture-worker")
        {
            Console.WriteLine("ready"); Console.ReadLine(); return args.Length > 1 ? int.Parse(args[1]) : 0;
        }
        if (args.FirstOrDefault() == "--cpu-fixture-orphan")
        {
            var service = new CpuLimitService(); // Deliberately abandoned to test crash recovery on our own fixture only.
            var result = service.Reconcile([new(int.Parse(args[1]), new DateTime(long.Parse(args[2]), DateTimeKind.Utc), int.Parse(args[3]))]).Single();
            Console.WriteLine(result.Code); Console.ReadLine(); return result.Success ? 0 : 1;
        }
        if (args is ["--verify-payload", var package])
        {
            await ManagerUpdateService.VerifyPayloadAsync(Path.GetFullPath(package));
            Console.WriteLine("[PASS] Installer payload inventory, hashes and executable signature bytes.");
            return 0;
        }
        _watchdogEntry = ReadArgument(args, "--watchdog-entry");
        L.Language = "pt-BR";
        await RunAsync("Account batches: identity, CSRF, verification, receipts and cancellation", TestAccountBatchesAsync);
        await RunAsync("Avatar distribution: owned assets, no paid purchases and verified appearance", TestAvatarDistributionAsync);
        await RunAsync("Macros: TinyTask records, cancellation and focus safety", TestMacroExpansionAsync);
        await RunAsync("Audio: account inheritance, focus, restoration and process identity", TestAudioExpansionAsync);
        await RunAsync("Vault: encrypted persistence, backup restore, lock and password rotation", TestVaultExpansionAsync);
        await RunAsync("Nexus: authenticated loopback, scoped commands and text values", TestNexusExpansionAsync);
        if(args.Contains("--expansion-only"))return _failed==0?0:1;
        await RunAsync("Manager activation: same version, busy work, acknowledged transfer and cancellation", TestManagerActivationAsync);
        await RunAsync("Manager workspace: queue, destination, selection and reconnect progress", TestManagerWorkspaceAsync);
        if (args.Contains("--activation-only")) return _failed == 0 ? 0 : 1;
        await RunAsync("Games: search, cache, server capacity, cancellation and rate limiting", TestGameServicesAsync);
        await RunAsync("Theme upgrade: preserve accessibility and explicit choices", TestThemeMigrationAsync);
        await RunAsync("Studio discovery: paging, sort, cache and global search session", TestDiscoveryStudioAsync);
        await RunAsync("Studio graphics: inheritance, staged launches, cancellation and restore", TestLaunchGraphicsStudioAsync);
        await RunAsync("Libraries: favorites, private links, groups, presets and notes", TestFeatureLibrariesAsync);
        await RunAsync("Backups: encryption, authenticated restore, safety copy and path validation", TestEncryptedBackupsAsync);
        await RunAsync("Appearance: persistence, theme validation and masked identities", TestAppearancePreferencesAsync);
        await RunAsync("Restore restart: exact owner wait without losing guard continuity", TestRestartWaitAsync);
        await RunAsync("Automation and history: permissions, cancellation, bounds and private CSV", TestAutomationHistoryAsync);
        await RunAsync("Graphics and tuning: reversible native settings, launch races and process identity", TestGraphicsAndTuningAsync);
        await RunAsync("Social and secrets: verified account, private presence and DPAPI", TestSocialIdentityAsync);
        await RunAsync("Native social: private presence, thumbnails, outfit cursors and verified item application", TestNativeSocialAsync);
        await RunAsync("Polish: Roblox aliases, friendship receipts, avatar editor, nullable servers and Retry-After", TestPolishedSocialAsync);
        await RunAsync("Local API: authentication, browser isolation and scoped actions", TestLocalApiAsync);
        await RunAsync("Updates and installation: cache binding, source validation and retained user files", TestVerifiedInstallAsync);
        await RunAsync("Avatar studio: categories, read-only preview, body validation and receipts", TestAvatarStudioAsync);
        await RunAsync("Communities: discovery, permissions and mutation receipts", TestCommunitiesAsync);
        await RunAsync("Account loading: coalescing, cache, refresh, cancellation and identity isolation", TestAccountLoadingAsync);
        if (args.Contains("--features-only")) return _failed == 0 ? 0 : 1;
        await RunAsync("Reconnect queue: cooldown, busy launches, retries and cancellation", TestReconnectQueueAsync);
        await RunAsync("Reconnect destination: account-bound tail and server links", TestReconnectSessionAsync);
        await RunAsync("Reconnect exit: retained native handle after process termination", TestRetainedExitAsync);
        await RunAsync("CPU: herança, override e persistência legada", TestCpuPolicyAsync);
        await RunAsync("CPU: confirmação, falha e liberação segura", TestCpuServiceAsync);
        await RunAsync("CPU: limites nativos em processo de teste e recuperação", TestNativeCpuAsync);
        await RunAsync("RAM/VRAM: leitura, identidade e counters opcionais", TestResourcesAsync);
        if (args.Contains("--resources-only")) return _failed == 0 ? 0 : 1;
        await RunAsync("Grade 1..30", TestGridAsync);
        await RunAsync("Grade 5: linhas completas 3 + 2", TestFiveWindowGridAsync);
        await RunAsync("Verificação: origens e diagnóstico sem segredos", TestLoginBrowserPolicyAsync);
        await RunAsync("Bordas visíveis e mínimos medidos", TestVisibleFrameLayoutAsync);
        await RunAsync("URLs oficiais", TestUrlsAsync);
        await RunAsync("Links de jogo e servidor privado", TestRobloxLinkParserAsync);
        await RunAsync("Persistência local", TestStoreAsync);
        await RunAsync("Destinos salvos: edição e reabertura", TestSavedDestinationsAsync);
        await RunAsync("Catálogo de versões Roblox", TestVersionCatalogAsync);
        await RunAsync("Assinatura do catálogo local", TestLocalVersionSignatureAsync);
        await RunAsync("Checker de versão Roblox", TestVersionCheckerAsync);
        await RunAsync("Correlação de PID e deployment", TestLaunchCorrelationAsync);
        await RunAsync(
            "Atualizador transitório não é divergência",
            TestLaunchObservationStateAsync);
        await RunAsync(
            "Pipeline de abertura rápida",
            TestFastLaunchPipelineAsync);
        await RunAsync("Lifecycle do mutex + singletonEvent", TestGuardAsync);
        await RunAsync("Identidade do login", TestIdentityAsync);
        await RunAsync("URI da home sem PlaceId", TestHomeLaunchUriAsync);
        await RunAsync("URI de jogo com launcher oficial", TestGameLaunchUriAsync);
        await RunAsync("Finalização protege contra PID reutilizado", TestForceTerminateIdentityAsync);
        await RunAsync("Handshake do ticket", TestTicketHandshakeAsync);
        await RunAsync("Native authentication rejection", TestAuthenticationFailureAsync);
        await RunAsync("Reversible native session recovery", TestNativeSessionRecoveryAsync);
        await RunAsync("Ordenação por versão do cliente", TestClientVersionOrderAsync);
        await RunAsync("Manifesto de deployment", TestDeploymentManifestAsync);
        await RunAsync("Índice de deployments", TestDeploymentCatalogAsync);
        await RunAsync("Extração de pacote", TestPackageExtractionAsync);
        await RunAsync("Convite /share de servidor privado", TestShareLinkAsync);
        await RunAsync(
            "Todas as formas de link de servidor privado",
            TestPrivateServerLinkShapesAsync);
        await RunAsync("Layout determinístico de janelas", TestWindowLayoutAsync);
        await RunAsync("Janelas confinadas à tela", TestWindowContainmentAsync);
        await RunAsync("Motivo real da morte do cliente", TestClientFailureReaderAsync);
        await RunAsync("Bloqueio da autoatualização da versão", TestSelfUpdateBlockerAsync);
        await RunAsync("Identificação de conta em execução", TestAccountResolverAsync);
        await RunAsync("Rótulos de instância", TestInstanceLabelsAsync);
        await RunAsync("Nome do perfil vindo da conta", TestProfileNamingAsync);
        await RunAsync(
            "Reconexão só depois de queda, nunca de saída limpa",
            TestReconnectDecisionAsync);

        var fixture = ReadArgument(args, "--fixture");
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            await RunAsync("Integração com 5 processos e janelas", () =>
                TestFiveWindowsAsync(fixture));
            await RunAsync("Janelas reais teimosas ficam na tela", () =>
                TestStubbornWindowsStayOnScreenAsync(fixture));
        }
        else
        {
            Console.WriteLine("[SKIP] Integração de janelas (informe --fixture <exe>)");
        }

        // Opt-in: reads this machine's real Roblox artifacts, which a normal test
        // pass must not depend on.
        if (args.Contains("--resolve-live", StringComparer.OrdinalIgnoreCase))
        {
            await RunAsync(
                "Identificação real desta máquina",
                TestLiveAccountResolveAsync);
        }
        else
        {
            Console.WriteLine(
                "[SKIP] Identificação real desta máquina (informe --resolve-live)");
        }

        // Opt-in: takes the real ROBLOX_* names on this machine, so it must not run
        // alongside a live client or another manager during a normal test pass.
        if (args.Contains("--guard-live", StringComparer.OrdinalIgnoreCase))
        {
            await RunAsync("Trava real desta máquina", TestLiveGuardAsync);
        }
        else
        {
            Console.WriteLine("[SKIP] Trava real desta máquina (informe --guard-live)");
        }

        // Opt-in: a real deployment is a few hundred megabytes over the network,
        // which does not belong in the default run.
        var download = ReadArgument(args, "--download");
        if (!string.IsNullOrWhiteSpace(download))
        {
            await RunAsync(
                $"Download real de {download}",
                () => TestRealDownloadAsync(
                    download,
                    ReadArgument(args, "--channel")));
        }
        else
        {
            Console.WriteLine(
                "[SKIP] Download real (informe --download version-<hash>)");
        }

        Console.WriteLine();
        Console.WriteLine($"Resultado: {_passed} passou/passaram; {_failed} falhou/falharam.");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Downloads a real deployment into a temporary root and checks that the
    /// result is a usable, correctly laid out, Roblox-signed client.
    /// </summary>
    private static async Task TestRealDownloadAsync(
        string versionId,
        string? channel)
    {
        var normalized = RobloxDeploymentDownloader.NormalizeVersionId(versionId);
        Assert(normalized is not null, $"Hash inválido: {versionId}.");

        var root = Path.Combine(
            Path.GetTempPath(),
            "RobloxMultiManager.Tests.Download",
            Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var downloader = new RobloxDeploymentDownloader(http, root);
        try
        {
            var manifest = await downloader.GetManifestAsync(normalized!, channel);
            Console.WriteLine(
                $"       manifesto: {manifest.Packages.Count} pacotes, "
                + $"{manifest.DownloadSize / 1024 / 1024} MB para baixar, "
                + $"{manifest.InstalledSize / 1024 / 1024} MB instalados");
            Assert(manifest.Packages.Count > 0, "Manifesto sem pacotes.");
            Assert(
                manifest.Packages.Any(package =>
                    package.Name.Equals(
                        "RobloxApp.zip",
                        StringComparison.OrdinalIgnoreCase)),
                "Manifesto sem RobloxApp.zip.");

            var lastStage = string.Empty;
            var progress = new Progress<RobloxDownloadProgress>(report =>
            {
                if (report.Stage == lastStage)
                {
                    return;
                }

                lastStage = report.Stage;
                Console.WriteLine(
                    $"       {report.Stage} · {report.SizeText} "
                    + $"({report.Fraction:P0})");
            });

            var executable = await downloader.DownloadAsync(
                normalized!,
                channel,
                progress);

            Assert(File.Exists(executable), "RobloxPlayerBeta.exe não foi criado.");
            var directory = Path.GetDirectoryName(executable)!;

            // AppSettings.xml is what lets the client find its content, and the
            // measured file is exactly 144 bytes.
            var appSettings = Path.Combine(directory, "AppSettings.xml");
            Assert(File.Exists(appSettings), "AppSettings.xml não foi gravado.");
            var appSettingsBytes = await File.ReadAllBytesAsync(appSettings);
            Assert(
                appSettingsBytes.Length == 144,
                $"AppSettings.xml tem {appSettingsBytes.Length} bytes, esperado 144.");
            Assert(
                !appSettingsBytes.Contains((byte)'\r'),
                "AppSettings.xml foi gravado com CRLF.");

            foreach (var expected in new[]
                     {
                         "RobloxPlayerBeta.dll",
                         Path.Combine("content", "fonts"),
                         Path.Combine("content", "textures"),
                         Path.Combine("PlatformContent", "pc", "terrain"),
                         Path.Combine("ExtraContent", "translations"),
                         "shaders",
                         "ssl"
                     })
            {
                var path = Path.Combine(directory, expected);
                Assert(
                    File.Exists(path) || Directory.Exists(path),
                    $"Pacote não extraído no lugar certo: {expected}.");
            }

            // The bootstrapper is listed in the manifest but is not client content.
            Assert(
                !File.Exists(Path.Combine(directory, "RobloxPlayerInstaller.exe")),
                "O instalador bootstrapper foi gravado na pasta da versão.");
            Assert(
                !Directory.Exists(Path.Combine(root, ".staging")),
                "A pasta de staging sobrou depois do sucesso.");

            var (publisher, signed) =
                RobloxInstallationLocator.VerifyRobloxSignature(executable, null);
            Assert(
                signed && publisher == "Roblox Corporation",
                $"Assinatura não confirmada (publicador: {publisher}).");

            // The locator must now see it through the app-owned root.
            var locator = new RobloxInstallationLocator(
                Path.Combine(root, "official-absent"),
                () => null,
                () => null,
                () => "LIVE",
                null,
                root);
            var found = locator.DiscoverInstalled().SingleOrDefault(item =>
                item.VersionId.Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
            Assert(found is not null, "A versão baixada não entrou no catálogo.");
            Assert(found!.IsManaged, "A versão baixada não foi marcada como do app.");
            Assert(found.AppearsSigned, "O catálogo não confirmou a assinatura.");
            Assert(
                RobloxClientExecutableInspector.TryInspect(
                    executable,
                    out var inspected,
                    root)
                && inspected.VersionId == normalized,
                "O inspetor de lançamento rejeitou a versão baixada.");

            Console.WriteLine(
                $"       versão do arquivo: {found.FileVersion ?? "n/d"}");
        }
        finally
        {
            var safeParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "RobloxMultiManager.Tests.Download"));
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(
                    safeParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Runs the resolver against this machine's real Roblox artifacts.
    /// </summary>
    /// <remarks>
    /// The fixture test above proves the parsing against formats this file asserts.
    /// This one proves it against what Roblox actually wrote, which is the only way
    /// to know the patterns match reality.
    /// </remarks>
    private static Task TestLiveAccountResolveAsync()
    {
        var robloxRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox");
        var storage = Path.Combine(robloxRoot, "LocalStorage");
        var logs = Path.Combine(robloxRoot, "logs");
        Console.WriteLine($"       LocalStorage existe: {Directory.Exists(storage)}");
        Console.WriteLine($"       logs existe: {Directory.Exists(logs)}");
        if (!Directory.Exists(storage))
        {
            Console.WriteLine("       (sem artefatos do Roblox; nada a verificar)");
            return Task.CompletedTask;
        }

        var resolver = new RobloxAccountResolver();
        var resolvedAny = false;

        // Exact route: every memProfStorage<pid>.json on disk.
        foreach (var file in Directory.GetFiles(storage, "memProfStorage*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var digits = name["memProfStorage".Length..];
            if (!int.TryParse(digits, out var pid))
            {
                continue;
            }

            var account = resolver.Resolve(pid, DateTime.UtcNow);
            Console.WriteLine(
                $"       pid={pid} -> "
                + (account is null
                    ? "sem identidade neste arquivo"
                    : $"userId={account.UserId} usuario=@{account.Username ?? "n/d"} "
                      + $"exato={account.IsExact}"));
            if (account is not null)
            {
                Assert(account.IsExact, "Rota por PID deveria ser exata.");
                Assert(account.UserId > 0, "UserId inválido na rota por PID.");
                resolvedAny = true;
            }
        }

        // Inferred route: use each real log's creation time as if it were a process
        // start, which is exactly what the resolver does in production.
        if (Directory.Exists(logs))
        {
            var recent = new DirectoryInfo(logs)
                .GetFiles("*_last.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(6)
                .ToArray();
            foreach (var file in recent)
            {
                var account = resolver.Resolve(
                    int.MaxValue - 1,
                    file.CreationTimeUtc);
                Console.WriteLine(
                    $"       log {file.Name} ({file.Length} B) -> "
                    + (account is null
                        ? "sem identidade"
                        : $"userId={account.UserId} usuario=@{account.Username ?? "n/d"} "
                          + $"nome={account.DisplayName ?? "n/d"}"));
                if (account is not null)
                {
                    Assert(account.UserId > 0, "UserId inválido na rota por log.");
                    Assert(!account.IsExact, "Rota por log não é exata.");
                    resolvedAny = true;
                }
            }
        }

        Assert(
            resolvedAny,
            "Nenhuma conta foi identificada nos artefatos reais; os padrões não "
            + "correspondem ao que o Roblox grava nesta máquina.");
        return Task.CompletedTask;
    }

    private static Task TestAccountResolverAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RobloxMultiManager.Tests.Account",
            Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        var storage = Path.Combine(root, "LocalStorage");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(storage);
        try
        {
            // The roster Roblox keeps of accounts that signed in on this machine.
            File.WriteAllText(
                Path.Combine(storage, "appStorage.json"),
                """
                {
                  "UserId": "720100002",
                  "Username": "fixture_account",
                  "DisplayName": "Fixture Display",
                  "PlayerHydrationSignature": "must-never-be-read",
                  "PreviousAccountsList": "{\"720100001\":{\"username\":\"atlas_demo\",\"displayName\":\"atlas_demo\",\"userId\":\"720100001\"}}"
                }
                """);

            // Exact path: Roblox names this file by process id.
            File.WriteAllText(
                Path.Combine(storage, "memProfStorage4242.json"),
                """
                {"DebugUserId":"720100001","UserId":"720100001","LastPlaceId":"1"}
                """);

            var resolver = new RobloxAccountResolver(root);
            var exact = resolver.Resolve(4242, DateTime.UtcNow);
            Assert(exact is not null, "Arquivo por PID não foi resolvido.");
            Assert(
                exact!.UserId == 720100001 && exact.IsExact,
                "Resolução por PID deveria ser exata.");
            Assert(
                exact.Username == "atlas_demo",
                "Nome não foi obtido da lista de contas conhecidas.");

            // Inferred path: no per-pid file, so the log nearest the process start
            // is used. The join payload is percent-encoded in the real logs.
            var started = new DateTime(2026, 8, 22, 21, 22, 47, DateTimeKind.Utc);
            var chosen = Path.Combine(
                logs,
                "0.734.0.7340917_20260822T212250Z_Player_CF8D3_last.log");
            File.WriteAllText(
                chosen,
                "linha inicial\n"
                + "UgcExperienceController: doTeleport: joinScriptUrl "
                + "https://assetgame.roblox.com/Game/Join.ashx?ticketVersion=2&ticket="
                + "{\"UserId\"%3a720100002%2c\"UserName\"%3a\"fixture_account\"%2c"
                + "\"DisplayName\"%3a\"Fixture Display\"%2c\"SegredoQueNaoDeveSerLido\"%3a\"x\"}\n");
            File.SetCreationTimeUtc(chosen, started.AddSeconds(3));

            // A decoy created before the process started must be ignored.
            var decoy = Path.Combine(
                logs,
                "0.734.0.7340917_20260822T211558Z_Player_25926_last.log");
            File.WriteAllText(
                decoy,
                "UgcExperienceController: doTeleport: joinScriptUrl "
                + "https://assetgame.roblox.com/Game/Join.ashx?ticket="
                + "{\"UserId\"%3a720100002%2c\"UserName\"%3a\"aurora_demo\"%2c"
                + "\"DisplayName\"%3a\"aurora_demo\"%2c}\n");
            File.SetCreationTimeUtc(decoy, started.AddMinutes(-7));

            var inferred = resolver.Resolve(9999, started);
            Assert(inferred is not null, "Log não foi correlacionado ao processo.");
            Assert(
                inferred!.UserId == 720100002,
                $"Log errado escolhido: userId={inferred.UserId}.");
            Assert(
                inferred.Username == "fixture_account"
                && inferred.DisplayName == "Fixture Display",
                "Usuário/nome de exibição não foram lidos do payload de join.");
            Assert(!inferred.IsExact, "Resolução por horário não é exata.");

            // The telemetry line carries the id but no name; it is the fallback.
            var telemetryOnly = Path.Combine(logs, "telemetry_last.log");
            File.WriteAllText(
                telemetryOnly,
                "[FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:1, "
                + "join_time:0.5, universeid:2, clienttime:3, userid:720100001,\n");
            var telemetryStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(telemetryOnly, telemetryStart.AddSeconds(1));
            var fromTelemetry = new RobloxAccountResolver(root)
                .Resolve(8888, telemetryStart);
            Assert(
                fromTelemetry?.UserId == 720100001,
                "Linha de telemetria deveria servir de fallback.");
            Assert(
                fromTelemetry!.Username == "atlas_demo",
                "Fallback deveria buscar o nome na lista conhecida.");

            // A session that never joined a place has no identity, and that must
            // read as "not yet" rather than as a wrong guess.
            var anonymous = Path.Combine(logs, "anon_last.log");
            File.WriteAllText(anonymous, "sem identidade aqui\n");
            var anonymousStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(anonymous, anonymousStart.AddSeconds(1));
            Assert(
                new RobloxAccountResolver(root).Resolve(7777, anonymousStart) is null,
                "Log sem identidade deveria retornar nulo.");

            // rbxuid is NOT the playing account: it was measured disagreeing with
            // the join payload, so it must never be used as a source.
            var misleading = Path.Combine(logs, "rbxuid_last.log");
            File.WriteAllText(
                misleading,
                "[FLog::LogWin32BTId] cookie Native => Engine set RBXEventTrackerV2 "
                + "as CreateDate=08/22/2026&rbxid=1&rbxuid=720100002&browserid=2\n");
            var misleadingStart = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(misleading, misleadingStart.AddSeconds(1));
            Assert(
                new RobloxAccountResolver(root).Resolve(6666, misleadingStart) is null,
                "rbxuid jamais deveria ser aceito como a conta em jogo.");

            Assert(
                new RobloxAccountResolver(root).Resolve(0, DateTime.UtcNow) is null,
                "PID inválido deveria retornar nulo.");
            Assert(
                new RobloxAccountResolver(
                        Path.Combine(root, "inexistente"))
                    .Resolve(4242, DateTime.UtcNow) is null,
                "Raiz inexistente deveria retornar nulo.");
        }
        finally
        {
            var safeParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "RobloxMultiManager.Tests.Account"));
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(
                    safeParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestWindowLayoutAsync()
    {
        var work = new System.Drawing.Rectangle(0, 0, 1920, 1040);

        // Every rectangle has to stay inside the work area, or a window lands
        // partly or fully off-screen, which is the "not all of them are visible"
        // complaint.
        for (var count = 1; count <= 24; count++)
        {
            var cells = GridLayoutCalculator.Calculate(work, count);
            Assert(cells.Count == count, $"Contagem de células errada para {count}.");
            foreach (var cell in cells)
            {
                Assert(
                    cell.Width > 0 && cell.Height > 0,
                    $"Célula degenerada com {count} janelas.");
                Assert(
                    cell.Left >= work.Left
                    && cell.Top >= work.Top
                    && cell.Right <= work.Right
                    && cell.Bottom <= work.Bottom,
                    $"Célula fora da área de trabalho com {count} janelas: {cell}.");
            }
        }

        // The old version produced one-pixel cells once the gaps exceeded the work
        // area, because it clamped the available space to the row count.
        var many = GridLayoutCalculator.Calculate(work, 40);
        Assert(
            many.All(cell => cell.Height >= 8 && cell.Width >= 8),
            "Layout voltou a gerar células de poucos pixels.");

        // Equal widths apply within each row; an incomplete row is expanded.
        var five = GridLayoutCalculator.Calculate(work, 5);
        foreach (var row in five.GroupBy(cell => cell.Top))
            Assert(row.Max(cell => cell.Width) - row.Min(cell => cell.Width) <= 1,
                "O resto dos pixels não foi distribuído igualmente na linha.");
        for (var count = 1; count <= 30; count++)
        {
            var cells = GridLayoutCalculator.Calculate(work, count, gap: 0);
            Assert(cells.Sum(cell => (long)cell.Width * cell.Height) == (long)work.Width * work.Height,
                $"A grade deixou uma célula vazia com {count} janelas.");
        }

        // No two cells may overlap while the count still fits the grid.
        for (var count = 2; count <= 12; count++)
        {
            var cells = GridLayoutCalculator.Calculate(work, count);
            for (var a = 0; a < cells.Count; a++)
            {
                for (var b = a + 1; b < cells.Count; b++)
                {
                    Assert(
                        !cells[a].IntersectsWith(cells[b]),
                        $"Células sobrepostas com {count} janelas.");
                }
            }
        }

        // Deterministic: the same input must give the same output every time.
        var first = GridLayoutCalculator.Calculate(work, 7);
        var second = GridLayoutCalculator.Calculate(work, 7);
        Assert(first.SequenceEqual(second), "Layout não é determinístico.");

        // A degenerate monitor must not throw or divide by zero.
        Assert(
            GridLayoutCalculator.Calculate(
                new System.Drawing.Rectangle(0, 0, 0, 0),
                3).Count == 3,
            "Área de trabalho vazia não foi tratada.");
        Assert(
            GridLayoutCalculator.Calculate(work, 0).Count == 0,
            "Contagem zero deveria retornar lista vazia.");

        // A tall monitor should prefer rows, a wide one columns.
        var wide = GridLayoutCalculator.Calculate(
            new System.Drawing.Rectangle(0, 0, 3840, 1080),
            4);
        Assert(
            wide.Select(cell => cell.Top).Distinct().Count()
            <= wide.Select(cell => cell.Left).Distinct().Count(),
            "Monitor largo não preferiu colunas.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing the arrange path produces may cross the edge of the monitor.
    /// </summary>
    /// <remarks>
    /// Two separate guarantees are checked here, because the old code had a hole in
    /// each.  The calculator must never emit a cell outside the work area, on any
    /// monitor geometry including the degenerate ones that used to overflow through
    /// its two <c>Math.Max</c> guards.  And <c>Contain</c> must keep a window that
    /// refuses to shrink inside the screen, which the old placement code explicitly
    /// declined to do: it verified only the origin, so an oversized client hung over
    /// the edge and was still counted as organised.
    /// </remarks>
    private static Task TestWindowContainmentAsync()
    {
        System.Drawing.Rectangle[] areas =
        [
            new(0, 0, 1920, 1040),        // primário comum
            new(0, 0, 1366, 728),         // notebook
            new(-1920, 0, 1920, 1040),    // monitor à esquerda
            new(0, -1080, 1920, 1040),    // monitor acima
            new(-2560, -1440, 2560, 1400),// à esquerda e acima
            new(0, 0, 3840, 2120),        // 4K
            new(0, 0, 3840, 1080),        // ultrawide
            new(0, 0, 800, 600),          // pequeno
            new(0, 0, 40, 30),            // absurdo, mas não pode estourar
            new(0, 0, 2, 2)               // degenerado
        ];
        int[] gaps = [0, 6, 8, 24];

        foreach (var area in areas)
        {
            foreach (var gap in gaps)
            {
                for (var count = 1; count <= 30; count++)
                {
                    var cells = GridLayoutCalculator.Calculate(area, count, gap);
                    Assert(
                        cells.Count == count,
                        $"Contagem errada em {area} com {count} janelas.");
                    foreach (var cell in cells)
                    {
                        Assert(
                            area.Contains(cell),
                            $"Célula fora da tela em {area}, gap {gap}, "
                            + $"{count} janelas: {cell}.");
                    }
                }
            }
        }

        // A client that refuses to shrink into its cell. The cell is the bottom
        // right one, so an oversized window is exactly the case that used to hang
        // off the corner of the screen.
        var work = new System.Drawing.Rectangle(0, 0, 1920, 1040);
        var grid = GridLayoutCalculator.Calculate(work, 9, 8);
        var corner = grid[^1];
        var stubborn = GridLayoutCalculator.Contain(
            corner,
            new System.Drawing.Size(1024, 768),
            work);
        Assert(
            work.Contains(stubborn),
            $"Janela teimosa saiu da tela: {stubborn} fora de {work}.");
        Assert(
            stubborn.Width == 1024 && stubborn.Height == 768,
            "Contain não deveria inventar um tamanho que a janela recusou.");
        Assert(
            stubborn.Right == work.Right && stubborn.Bottom == work.Bottom,
            $"Janela teimosa deveria encostar na borda: {stubborn}.");

        // A window larger than the whole work area: capped and pinned, never
        // negative-sized and never outside.
        var huge = GridLayoutCalculator.Contain(
            new System.Drawing.Rectangle(1500, 900, 200, 100),
            new System.Drawing.Size(4000, 3000),
            work);
        Assert(huge == work, $"Janela maior que a tela não foi limitada: {huge}.");

        // Monitors at negative coordinates are the regression-prone case: pulling
        // the origin back must not walk off the left or top edge.
        var left = new System.Drawing.Rectangle(-1920, -1080, 1920, 1040);
        var onLeft = GridLayoutCalculator.Contain(
            new System.Drawing.Rectangle(-200, -200, 400, 300),
            new System.Drawing.Size(1200, 900),
            left);
        Assert(
            left.Contains(onLeft),
            $"Contenção falhou em monitor negativo: {onLeft} fora de {left}.");
        Assert(
            onLeft.Right == left.Right && onLeft.Bottom == left.Bottom,
            $"Deveria encostar no canto do monitor negativo: {onLeft}.");

        // A window that already fits must be left exactly where the grid put it.
        var fits = GridLayoutCalculator.Contain(
            grid[0],
            grid[0].Size,
            work);
        Assert(fits == grid[0], $"Janela que já cabia foi movida: {fits}.");

        // A degenerate area carries no containment information, so the request has
        // to pass through untouched rather than collapse to nothing.
        var noArea = new System.Drawing.Rectangle(10, 20, 300, 200);
        Assert(
            GridLayoutCalculator.Contain(
                noArea,
                new System.Drawing.Size(300, 200),
                new System.Drawing.Rectangle(0, 0, 0, 0)) == noArea,
            "Área degenerada não deveria alterar o retângulo pedido.");

        return Task.CompletedTask;
    }

    private static Task TestInstanceLabelsAsync()
    {
        var verified = new RobloxInstanceInfo(
            100,
            DateTime.UtcNow,
            nint.Zero,
            "t",
            RobloxInstanceState.Running,
            true,
            @"C:\x\Versions\version-aaaaaaaaaaaaaaa1\RobloxPlayerBeta.exe",
            "version-aaaaaaaaaaaaaaa1",
            "0.734.0.7340917");
        Assert(verified.IsVerified, "Instância verificada não foi reconhecida.");
        Assert(
            verified.EffectiveDeploymentId == "version-aaaaaaaaaaaaaaa1",
            "Deployment efetivo incorreto para instância verificada.");

        // A client started by another bootstrapper: real deployment, unverified
        // path. It used to read "Não verificado" and block every launch.
        var external = new RobloxInstanceInfo(
            101,
            DateTime.UtcNow,
            nint.Zero,
            "t",
            RobloxInstanceState.Running,
            true,
            @"C:\Fishstrap\Versions\version-bbbbbbbbbbbbbbb2\RobloxPlayerBeta.exe",
            null,
            "0.734.0.7340917",
            DeploymentId: "version-bbbbbbbbbbbbbbb2");
        Assert(!external.IsVerified, "Instância externa não deveria contar como verificada.");
        Assert(
            external.EffectiveDeploymentId == "version-bbbbbbbbbbbbbbb2",
            "Deployment de instância externa foi perdido.");
        Assert(
            external.ClientText.Contains(
                "version-bbbbbbbbbbbbbbb2",
                StringComparison.Ordinal),
            "ClientText escondeu o deployment de uma instância externa.");
        Assert(
            !external.ClientText.Contains("Não verificado", StringComparison.Ordinal),
            "Instância externa continuou marcada como não verificada.");

        // Two clients on the same deployment must compare equal even when only one
        // of them sits in a folder this app validates.
        Assert(
            external.EffectiveDeploymentId!.Equals(
                "VERSION-BBBBBBBBBBBBBBB2",
                StringComparison.OrdinalIgnoreCase),
            "Comparação de deployment deveria ignorar caixa.");

        var unreadable = external with { ExecutablePath = null, DeploymentId = null };
        Assert(
            unreadable.EffectiveDeploymentId is null,
            "Instância ilegível não deveria inventar deployment.");
        Assert(
            unreadable.ClientText == "caminho não legível",
            "Instância ilegível recebeu texto errado.");

        // Attribution: an unattributed row must not claim to be managed.
        Assert(
            external.ProfileText == "Aberto fora do app",
            "Rótulo de instância não atribuída incorreto.");
        Assert(
            (external with { ProfileName = "atlas_demo" }).ProfileText == "atlas_demo",
            "Rótulo de instância atribuída incorreto.");

        // Version match now works off the effective deployment.
        Assert(
            (external with { RequestedVersionId = "version-bbbbbbbbbbbbbbb2" })
                .VersionMatchText == "Corresponde",
            "Compatibilidade de instância externa deveria corresponder.");
        Assert(
            (external with { RequestedVersionId = "version-ccccccccccccccc3" })
                .VersionMatchText == "Divergente",
            "Compatibilidade divergente não foi detectada.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Auto reconnect must reopen a crash and never reopen a deliberate close.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the whole design: reopening a window the player closed on
    /// purpose is worse than leaving a crashed one closed, so every uncertain case
    /// resolves to "do nothing".  A clean exit code is what Roblox reports when the
    /// player quits, and an unreadable code falls back to the client's own log, where
    /// the absence of a reason is documented to mean "no reason recorded" rather than
    /// "it crashed".
    /// </remarks>
    private static Task TestReconnectDecisionAsync()
    {
        // The case the feature exists for.
        Assert(
            MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: -1073741819,
                clientFailureKind: null,
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Queda com código diferente de zero deveria reabrir.");

        // The player quit. Roblox exits cleanly, and this must never be reopened.
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: 0,
                clientFailureKind: null,
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Saída limpa é o jogador fechando e não pode reabrir.");

        // Closed from this app, by the row X or by the lock cleanup.
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: true,
                exitCode: -1,
                clientFailureKind: "crash",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Encerramento pedido no app nunca deveria reabrir.");

        Assert(
            !MainWindow.ShouldReconnect(
                enabled: false,
                launchedByApp: true,
                closedByApp: false,
                exitCode: -1,
                clientFailureKind: "crash",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Desligado não deveria reabrir nada.");

        // A client this app did not open has no known account or destination.
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: false,
                closedByApp: false,
                exitCode: -1,
                clientFailureKind: "crash",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Cliente aberto fora do app não deveria ser reaberto.");

        // Roblox's own installer already relaunches the client it replaced.
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: null,
                clientFailureKind: "self-update",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Autoatualização não deveria gerar uma segunda janela.");
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: 1,
                clientFailureKind: "self-update",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Autoatualização vence até um código diferente de zero.");

        // Unreadable code: the client log decides, and silence means do nothing.
        Assert(
            MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: null,
                clientFailureKind: "crash",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Crash registrado pelo cliente deveria reabrir sem o código.");
        Assert(
            MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: null,
                clientFailureKind: "single-instance-handoff",
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Crash da trava de instância única deveria reabrir.");
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: null,
                clientFailureKind: null,
                attemptsSoFar: 0,
                maxAttempts: 3),
            "Sem código e sem motivo no log, nada deveria ser presumido.");

        // The budget is what stops a client that crashes on startup from being
        // reopened forever.
        Assert(
            !MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: -1,
                clientFailureKind: null,
                attemptsSoFar: 3,
                maxAttempts: 3),
            "O limite de tentativas não foi respeitado.");
        Assert(
            MainWindow.ShouldReconnect(
                enabled: true,
                launchedByApp: true,
                closedByApp: false,
                exitCode: -1,
                clientFailureKind: null,
                attemptsSoFar: 2,
                maxAttempts: 3),
            "A última tentativa dentro do limite deveria ser permitida.");

        return Task.CompletedTask;
    }

    private static Task TestProfileNamingAsync()
    {
        var identity = new AccountIdentity(123, "atlas_demo", "ATLAS");

        // Placeholders this app produced may be replaced by the real username.
        Assert(
            MainWindow.ShouldAdoptAccountName(
                AccountProfile.Create("Nova conta"),
                identity),
            "Placeholder 'Nova conta' deveria adotar o usuário.");
        Assert(
            MainWindow.ShouldAdoptAccountName(
                AccountProfile.Create("Conta 3"),
                identity),
            "Placeholder 'Conta 3' deveria adotar o usuário.");
        Assert(
            MainWindow.ShouldAdoptAccountName(
                AccountProfile.Create("   "),
                identity),
            "Nome vazio deveria adotar o usuário.");

        // A label the user chose is left alone.
        Assert(
            !MainWindow.ShouldAdoptAccountName(
                AccountProfile.Create("minha alt principal"),
                identity),
            "Nome escolhido pelo usuário não deveria ser sobrescrito.");
        Assert(
            !MainWindow.ShouldAdoptAccountName(
                AccountProfile.Create("Conta do irmão"),
                identity),
            "Nome que só começa com 'Conta ' não deveria ser sobrescrito.");

        // A label that already mirrored the old username follows a rename.
        var renamed = AccountProfile.Create("antigo_nome") with
        {
            RobloxUsername = "antigo_nome"
        };
        Assert(
            MainWindow.ShouldAdoptAccountName(renamed, identity),
            "Nome espelhando o usuário antigo deveria acompanhar a troca.");
        var unchanged = AccountProfile.Create("atlas_demo") with
        {
            RobloxUsername = "atlas_demo"
        };
        Assert(
            !MainWindow.ShouldAdoptAccountName(unchanged, identity),
            "Nome já igual ao usuário não precisa ser reescrito.");

        return Task.CompletedTask;
    }

    private static Task TestShareLinkAsync()
    {
        // The newer format carries neither a place id nor a link code, so the
        // parser can only record the share code and mark the place id unknown.
        var target = RobloxLinkParser.Parse(
            "https://www.roblox.com/share?code=0f89515e0509da4b8fbf69ca0028eda7&type=Server");
        Assert(
            target.Kind == RobloxLaunchTargetKind.PrivateServer
            && target.PlaceId is null
            && target.PrivateServerCode == "0f89515e0509da4b8fbf69ca0028eda7",
            "Link /share?type=Server não foi interpretado como convite pendente.");

        // Roblox reports invite state in the body with HTTP 200, so each state has
        // to become its own reason instead of a generic authorization failure.
        var valid = RobloxLaunchWindow.EvaluateShareResolution(
            "Valid",
            "2753915549",
            "ce65b2c80e8dae48866fe2f919966557");
        Assert(
            valid.Ok
            && valid.PlaceId == 2753915549
            && valid.LinkCode == "ce65b2c80e8dae48866fe2f919966557",
            "Convite válido não produziu placeId e código.");

        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("Expired", "1", "abc").Ok,
            "Convite expirado foi aceito.");
        Assert(
            RobloxLaunchWindow.EvaluateShareResolution("Expired", "1", "abc")
                .Code.Contains("expirado", StringComparison.OrdinalIgnoreCase),
            "Convite expirado não foi identificado como expirado.");
        Assert(
            RobloxLaunchWindow.EvaluateShareResolution("Invalid", "1", "abc")
                .Code.Contains("inválido", StringComparison.OrdinalIgnoreCase),
            "Convite inválido não foi identificado como inválido.");
        Assert(
            RobloxLaunchWindow.EvaluateShareResolution("http-403", null, null)
                .Message.Contains("403", StringComparison.Ordinal),
            "Status HTTP não foi repassado na mensagem.");
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("missing", null, null).Ok,
            "Resposta sem privateServerInviteData foi aceita.");
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution(null, null, null).Ok,
            "Ausência de resolução foi aceita.");

        // An unrecognised state must be surfaced verbatim, not guessed at.
        var unexpected = RobloxLaunchWindow.EvaluateShareResolution(
            "SenderBlockedRecipient",
            "1",
            "abc");
        Assert(
            !unexpected.Ok
            && unexpected.Message.Contains(
                "SenderBlockedRecipient",
                StringComparison.Ordinal),
            "Estado desconhecido não foi relatado literalmente.");

        // A Valid state with unusable payload must not launch.
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("Valid", "0", "abc").Ok,
            "PlaceId zero foi aceito.");
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("Valid", "abc", "abc").Ok,
            "PlaceId não numérico foi aceito.");
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("Valid", "1", "a/b").Ok,
            "Código com barra foi aceito.");
        Assert(
            !RobloxLaunchWindow.EvaluateShareResolution("Valid", "1", null).Ok,
            "Código ausente foi aceito.");

        // The resolved invite must build the classic private-server launcher.
        var resolved = RobloxLinkParser.Parse(
            "https://www.roblox.com/games/start?placeId=2753915549"
            + "&privateServerLinkCode=ce65b2c80e8dae48866fe2f919966557");
        var uri = RobloxLaunchWindow.BuildGameLaunchUri(
            new string('T', 64),
            123456789012,
            1770000000000,
            resolved,
            "LIVE");
        Assert(
            uri.Contains("RequestPrivateGame", StringComparison.Ordinal),
            "Convite resolvido não usou RequestPrivateGame.");

        // The script must refuse an unsafe code rather than interpolate it.
        AssertThrows(
            () => RobloxLaunchWindow.BuildTicketFetchScript(
                Guid.NewGuid().ToString("N"),
                123,
                "\" + fetch('http://evil') + \""),
            "Código de convite malicioso foi aceito no script.");

        var script = RobloxLaunchWindow.BuildTicketFetchScript(
            Guid.NewGuid().ToString("N"),
            123,
            "0f89515e0509da4b8fbf69ca0028eda7");
        Assert(
            script.Contains("sharelinks/v1/resolve-link", StringComparison.Ordinal)
            && script.Contains("linkType: \"Server\"", StringComparison.Ordinal)
            && script.Contains(
                "0f89515e0509da4b8fbf69ca0028eda7",
                StringComparison.Ordinal),
            "O script não monta a chamada de resolução do convite.");
        Assert(
            RobloxLaunchWindow.BuildTicketFetchScript(
                    Guid.NewGuid().ToString("N"),
                    123)
                .Contains("shareCode = null", StringComparison.Ordinal),
            "Sem convite, o script deveria desativar a resolução.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every shape of private-server link must reach the client, and none of them may
    /// send the protected session to a page that hands off to the protocol handler.
    /// </summary>
    /// <remarks>
    /// The reported failure was
    /// <c>https://www.roblox.com/games/2753915549?privateServerLinkCode=...</c>, which
    /// parsed correctly (the log recorded
    /// <c>target=private-server;placeId=2753915549</c>) and then died with "a
    /// autorização tentou sair do domínio oficial do Roblox ... Destino recusado:
    /// roblox://experiences".  The launch window was navigating to the target's
    /// canonical <c>/games/start</c> URL, and that page exists precisely to jump to
    /// <c>roblox://experiences</c>.
    /// </remarks>
    private static Task TestPrivateServerLinkShapesAsync()
    {
        const long placeId = 2753915549;
        const string code = "09564311317829059653148615420483";

        // Every one of these is a link Roblox itself produces or accepts.
        foreach (var link in new[]
                 {
                     // The exact reported form: place id in the path, no slug.
                     $"https://www.roblox.com/games/{placeId}?privateServerLinkCode={code}",
                     $"https://www.roblox.com/games/{placeId}/Blox-Fruits?privateServerLinkCode={code}",
                     $"https://www.roblox.com/games/start?placeId={placeId}&privateServerLinkCode={code}",
                     // Query keys are case-insensitive on the site.
                     $"https://www.roblox.com/games/{placeId}?privateserverlinkcode={code}",
                     $"https://www.roblox.com/games/{placeId}?PrivateServerLinkCode={code}",
                     // A harmless extra parameter must not defeat the match.
                     $"https://www.roblox.com/games/{placeId}/Blox-Fruits?privateServerLinkCode={code}&refPageId=abc",
                     "https://roblox.com/games/"
                     + placeId
                     + "?privateServerLinkCode="
                     + code
                 })
        {
            Assert(
                RobloxLinkParser.TryParse(link, out var target, out var error),
                $"Link de servidor privado recusado: {link} ({error})");
            Assert(
                target.Kind == RobloxLaunchTargetKind.PrivateServer,
                $"Link não foi classificado como servidor privado: {link}");
            Assert(
                target.PlaceId == placeId,
                $"PlaceId perdido em: {link}");
            Assert(
                target.PrivateServerCode == code,
                $"Código do servidor privado perdido em: {link}");

            // The launcher URL the client receives must request a private game and
            // carry the invite code.
            var uri = RobloxLaunchWindow.BuildGameLaunchUri(
                new string('P', 64),
                123456789012,
                1770000000000,
                target,
                "LIVE");
            Assert(
                uri.Contains(
                    Uri.EscapeDataString("request=RequestPrivateGame"),
                    StringComparison.Ordinal),
                $"RequestPrivateGame ausente para: {link}");
            Assert(
                uri.Contains(
                    Uri.EscapeDataString($"linkCode={code}"),
                    StringComparison.Ordinal),
                $"linkCode ausente para: {link}");

            // The regression under test: a target that already knows its place id
            // must never be visited, because /games/start jumps to roblox://.
            Assert(
                RobloxLaunchWindow.ResolveInitialNavigationUrl(target, null)
                == "https://www.roblox.com/home",
                $"A sessão iria visitar a página que dispara roblox:// para: {link}");
        }

        // A public game with a concrete place id is the same story.
        var publicTarget = RobloxLinkParser.Parse($"{placeId}");
        Assert(
            RobloxLaunchWindow.ResolveInitialNavigationUrl(publicTarget, null)
            == "https://www.roblox.com/home",
            "Jogo público com placeId concreto não deveria ser visitado.");

        // No destination at all: the home screen, as before.
        Assert(
            RobloxLaunchWindow.ResolveInitialNavigationUrl(null, null)
            == "https://www.roblox.com/home",
            "Sem destino deveria abrir a tela inicial.");

        // A /share SERVER invite is resolved through the API, so it also stays home.
        var shareServer = RobloxLinkParser.Parse(
            $"https://www.roblox.com/share?code={code}&type=Server");
        Assert(
            shareServer.PlaceId is null,
            "Convite /share não deveria inventar um placeId.");
        Assert(
            RobloxLaunchWindow.ResolveInitialNavigationUrl(shareServer, code)
            == "https://www.roblox.com/home",
            "Convite /share de servidor é resolvido pela API e deveria ficar na home.");

        // The one case that still needs the official page: a /share EXPERIENCE link
        // carries neither a place id nor an invite code, so only Roblox's own redirect
        // can reveal the place id.
        var shareExperience = RobloxLinkParser.Parse(
            $"https://www.roblox.com/share?code={code}&type=Experience");
        Assert(
            RobloxLaunchWindow.ResolveInitialNavigationUrl(shareExperience, null)
            == shareExperience.LauncherUrl,
            "Convite /share de experiência ainda precisa da página oficial.");

        return Task.CompletedTask;
    }

    private static Task TestPackageExtractionAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RobloxMultiManager.Tests.Extract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Entry shapes measured on real packages: backslash separators, a bare
            // "\" root entry, and directory entries carrying a LEADING separator
            // while their files do not.  Path.Combine treats that leading
            // separator as rooted and discards the destination, so extraction has
            // to strip it — this is what made content-avatar.zip fail.
            var archive = Path.Combine(root, "content-avatar.zip");
            WriteZip(archive, new (string Name, string? Content)[]
            {
                ("\\", null),
                ("character.rbxm", "root file"),
                ("\\abilities\\", null),
                ("abilities\\AbilityManagerActor.rbxm", "nested file")
            });

            var versionRoot = Path.Combine(root, "version");
            Directory.CreateDirectory(versionRoot);
            RobloxDeploymentDownloader.ExtractPackage(
                archive,
                versionRoot,
                Path.Combine("content", "avatar"));

            var target = Path.Combine(versionRoot, "content", "avatar");
            Assert(
                File.ReadAllText(Path.Combine(target, "character.rbxm"))
                == "root file",
                "Arquivo na raiz do pacote não foi extraído.");
            Assert(
                File.ReadAllText(Path.Combine(
                    target,
                    "abilities",
                    "AbilityManagerActor.rbxm")) == "nested file",
                "Arquivo em subpasta não foi extraído.");
            Assert(
                Directory.Exists(Path.Combine(target, "abilities")),
                "Subpasta declarada no pacote não foi criada.");

            // The leading separator must not have escaped to the drive root.
            Assert(
                !Directory.Exists(Path.Combine(
                    Path.GetPathRoot(versionRoot)!,
                    "abilities")),
                "Entrada de diretório escapou para a raiz do disco.");

            var hostile = Path.Combine(root, "hostile.zip");
            WriteZip(hostile, new (string Name, string? Content)[]
            {
                ("..\\..\\escape.txt", "should never be written")
            });
            AssertThrows(
                () => RobloxDeploymentDownloader.ExtractPackage(
                    hostile,
                    versionRoot,
                    Path.Combine("content", "avatar")),
                "Pacote com traversal foi extraído.");
            Assert(
                !File.Exists(Path.Combine(versionRoot, "escape.txt")),
                "Traversal gravou fora da pasta de destino.");
        }
        finally
        {
            var safeParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "RobloxMultiManager.Tests.Extract"));
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(
                    safeParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static void WriteZip(
        string path,
        IReadOnlyList<(string Name, string? Content)> entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new System.IO.Compression.ZipArchive(
            stream,
            System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (content is null)
            {
                continue;
            }

            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    /// <summary>
    /// Runs the guard against the real compatibility names on this machine.
    /// </summary>
    /// <remarks>
    /// This is the scenario a user actually hits: whatever type those names
    /// currently have, the guard must reach <see cref="GuardState.Active"/>,
    /// because every launch path returns early when it does not.
    /// </remarks>
    private static async Task TestLiveGuardAsync()
    {
        foreach (var name in new[]
                 {
                     MultiInstanceGuard.DefaultEventName,
                     MultiInstanceGuard.DefaultMutexName
                 })
        {
            Console.WriteLine($"       {name}: {DescribeNamedObject(name)}");
        }

        await using var guard = new MultiInstanceGuard();
        var result = await guard.StartAsync(requireNoRobloxProcesses: true);
        Console.WriteLine($"       estado={result.State}");
        Console.WriteLine($"       mensagem={result.Message}");
        if (result.State == GuardState.PlayersAlreadyRunning)
        {
            Console.WriteLine(
                "       (feche o Roblox e repita; isto não é uma falha do guard)");
            return;
        }

        Assert(
            result.IsActive,
            $"O guard não ativou nesta máquina: {result.State} - {result.Message}");
        Assert(guard.IsActive, "O guard relatou ativo mas não detém o mutex.");
        await guard.StopAsync();
    }

    private static string DescribeNamedObject(string name)
    {
        try
        {
            using var asEvent = EventWaitHandle.OpenExisting(name);
            return "existe como Event";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            return "existe, sem permissão de acesso";
        }

        try
        {
            using var asMutex = Mutex.OpenExisting(name);
            return "existe como Mutex";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return "não existe";
        }
        catch (UnauthorizedAccessException)
        {
            return "existe, sem permissão de acesso";
        }
    }

    private static Task TestClientVersionOrderAsync()
    {
        Assert(
            RobloxVersionChecker.TryParseClientVersion(
                "0.734.0.7340917",
                out var dotted)
            && dotted!.Revision == 7340917,
            "Versão pontuada não foi interpretada.");
        Assert(
            RobloxVersionChecker.TryParseClientVersion(
                "0, 733, 0, 7330989",
                out var resource)
            && resource!.Revision == 7330989,
            "Versão no formato do recurso PE não foi interpretada.");

        // The fourth component of a real Roblox build overflows the 16-bit Win32
        // field (7330989 truncates to 56493), so the comparison must come from the
        // string and never from FileVersionInfo.FilePrivatePart.
        Assert(
            resource!.Revision != 7330989 % 65536,
            "Quarta parte truncada em 16 bits.");
        Assert(dotted > resource, "Build mais nova não ficou acima da antiga.");

        Assert(
            !RobloxVersionChecker.TryParseClientVersion("0.734.0", out _),
            "Versão com três partes foi aceita.");
        Assert(
            !RobloxVersionChecker.TryParseClientVersion("hidden", out _),
            "Texto arbitrário foi aceito como versão.");
        Assert(
            !RobloxVersionChecker.TryParseClientVersion(null, out _),
            "Nulo foi aceito como versão.");

        var ordered = new[]
            {
                "0.733.0.7330989",
                "0.734.0.7340917",
                "0.729.24.7290100",
                "0.734.0.7340915"
            }
            .OrderByDescending(value =>
                RobloxVersionChecker.TryParseClientVersion(value, out var parsed)
                    ? parsed
                    : null)
            .ToArray();
        Assert(
            ordered[0] == "0.734.0.7340917"
            && ordered[1] == "0.734.0.7340915"
            && ordered[2] == "0.733.0.7330989"
            && ordered[3] == "0.729.24.7290100",
            "Ordenação da mais recente para a mais antiga incorreta.");

        return Task.CompletedTask;
    }

    private static Task TestDeploymentManifestAsync()
    {
        // Byte shape taken from a real manifest: "v0" header, then four lines per
        // package (name, MD5, downloaded size, uncompressed size), CRLF throughout
        // including a trailing CRLF.
        const string manifest =
            "v0\r\n"
            + "RobloxApp.zip\r\naeaa4432a6005a7cead977db05447511\r\n"
            + "134901428\r\n173551147\r\n"
            + "ssl.zip\r\n8afa4daa8041a80deef7748c1c5509b7\r\n130569\r\n228725\r\n"
            + "RobloxPlayerInstaller.exe\r\nd444176101c29a0b4a4edaee7215443a\r\n"
            + "13646288\r\n13646288\r\n";

        var packages = RobloxDeploymentDownloader.ParseManifest(manifest);
        Assert(packages.Count == 3, "Contagem de pacotes incorreta.");
        Assert(
            packages[0].Name == "RobloxApp.zip"
            && packages[0].Md5 == "aeaa4432a6005a7cead977db05447511"
            && packages[0].CompressedSize == 134901428
            && packages[0].UncompressedSize == 173551147,
            "Ordem dos campos do manifesto incorreta.");
        Assert(
            packages[1].IsArchive && !packages[2].IsArchive,
            "Instalador não-arquivo foi tratado como pacote extraível.");

        // A stray CR would break both the MD5 comparison and the integer parses.
        Assert(
            !packages[0].Md5.Contains('\r') && !packages[0].Name.Contains('\r'),
            "Campos mantiveram o CR do CRLF.");

        AssertThrows(
            () => RobloxDeploymentDownloader.ParseManifest("v1\r\nRobloxApp.zip\r\n"),
            "Manifesto com cabeçalho desconhecido foi aceito.");
        AssertThrows(
            () => RobloxDeploymentDownloader.ParseManifest(
                "v0\r\nRobloxApp.zip\r\nnothex\r\n1\r\n2\r\n"),
            "MD5 inválido foi aceito.");
        AssertThrows(
            () => RobloxDeploymentDownloader.ParseManifest(
                "v0\r\nRobloxApp.zip\r\naeaa4432a6005a7cead977db05447511\r\n1\r\n"),
            "Registro incompleto foi aceito.");
        AssertThrows(
            () => RobloxDeploymentDownloader.ParseManifest(
                "v0\r\n..\\evil.zip\r\naeaa4432a6005a7cead977db05447511\r\n1\r\n2\r\n"),
            "Nome de pacote com traversal foi aceito.");
        AssertThrows(
            () => RobloxDeploymentDownloader.ParseManifest(string.Empty),
            "Manifesto vazio foi aceito.");

        // LIVE lives at the CDN root and every other channel under channel/<name>/;
        // the two are not interchangeable, so the prefix must follow the channel.
        Assert(
            RobloxDeploymentDownloader.BuildBaseUrl("LIVE")
            == "https://setup.rbxcdn.com/",
            "URL base do canal LIVE incorreta.");
        Assert(
            RobloxDeploymentDownloader.BuildBaseUrl("ZIntegration")
            == "https://setup.rbxcdn.com/channel/zintegration/",
            "URL base de canal não-LIVE incorreta.");
        AssertThrows(
            () => RobloxDeploymentDownloader.BuildBaseUrl("LIVE/../evil"),
            "Canal com barra foi aceito na URL base.");

        Assert(
            RobloxDeploymentDownloader.NormalizeVersionId("d584fb6c717a43d9")
            == "version-d584fb6c717a43d9",
            "Hash sem prefixo não foi normalizado.");
        Assert(
            RobloxDeploymentDownloader.NormalizeVersionId(
                "  VERSION-D584FB6C717A43D9 ")
            == "version-d584fb6c717a43d9",
            "Hash com espaços e maiúsculas não foi normalizado.");
        Assert(
            RobloxDeploymentDownloader.NormalizeVersionId(
                "../version-d584fb6c717a43d9") is null,
            "Hash com traversal foi aceito.");
        Assert(
            RobloxDeploymentDownloader.NormalizeVersionId("version-zzzz") is null,
            "Hash fora do formato foi aceito.");
        Assert(
            RobloxDeploymentDownloader.NormalizeVersionId(null) is null,
            "Nulo foi aceito como hash.");

        return Task.CompletedTask;
    }

    private static Task TestDeploymentCatalogAsync()
    {
        const string history =
            """
            {
              "version-aaaaaaaaaaaaaaa1": "0.733.0.7330989",
              "version-aaaaaaaaaaaaaaa2": "0.734.0.7340917",
              "version-aaaaaaaaaaaaaaa3": "version-hidden",
              "version-not-a-deployment": "0.999.0.9990000",
              "version-aaaaaaaaaaaaaaa4": 12345
            }
            """;
        const string metadata =
            """
            {
              "Windows": {
                "Studio64": { "version-bbbbbbbbbbbbbbb1": "2026-08-10T22:01:22" },
                "WindowsPlayer": {
                  "version-aaaaaaaaaaaaaaa2": "2026-08-11T18:30:54+00:00",
                  "version-aaaaaaaaaaaaaaa3": "2026-08-05T10:24:28",
                  "version-aaaaaaaaaaaaaaa9": "2026-07-01T00:00:00"
                }
              }
            }
            """;

        var entries = RobloxDeploymentCatalog.Parse(history, metadata);
        Assert(
            entries.All(entry =>
                RobloxInstallationLocator.IsValidVersionId(entry.VersionId)),
            "Índice aceitou um identificador inválido.");
        Assert(
            entries.All(entry => entry.VersionId != "version-not-a-deployment"),
            "Identificador fora do formato entrou no índice.");
        Assert(entries.Count == 5, $"Contagem do índice incorreta: {entries.Count}.");

        Assert(
            entries[0].VersionId == "version-aaaaaaaaaaaaaaa2"
            && entries[0].Version == "0.734.0.7340917",
            "Índice não começou pelo deployment mais recente.");
        Assert(
            entries[1].VersionId == "version-aaaaaaaaaaaaaaa1",
            "Segunda posição do índice incorreta.");

        // A row Roblox redacted keeps its identifier and loses only the version
        // string, so it must stay in the list and sort after the known ones.
        var order = entries.Select(entry => entry.VersionId).ToList();
        var hidden = entries.Single(entry =>
            entry.VersionId == "version-aaaaaaaaaaaaaaa3");
        Assert(
            hidden.Version is null && hidden.DeployedAtUtc is not null,
            "Deployment sem versão publicada perdeu o carimbo de data.");
        Assert(
            order.IndexOf("version-aaaaaaaaaaaaaaa3")
            > order.IndexOf("version-aaaaaaaaaaaaaaa1"),
            "Deployment sem versão não foi para o fim da lista.");
        Assert(
            order.IndexOf("version-aaaaaaaaaaaaaaa9")
            > order.IndexOf("version-aaaaaaaaaaaaaaa3"),
            "Empate sem versão não usou o carimbo mais recente primeiro.");

        // The Studio timestamps belong to a different binary and must not leak in.
        Assert(
            entries.All(entry => entry.VersionId != "version-bbbbbbbbbbbbbbb1"),
            "Deployment de Studio entrou na lista de WindowsPlayer.");
        Assert(
            entries.Any(entry => entry.VersionId == "version-aaaaaaaaaaaaaaa9"),
            "Deployment presente só nos carimbos foi descartado.");
        Assert(
            entries.Single(entry =>
                entry.VersionId == "version-aaaaaaaaaaaaaaa4").Version is null,
            "Versão não textual foi aceita.");

        Assert(
            entries.Single(entry => entry.VersionId == "version-aaaaaaaaaaaaaaa2")
                .DeployedAtUtc!.Value.Kind == DateTimeKind.Utc,
            "Carimbo não foi convertido para UTC.");

        // A missing timestamp file must not cost the history rows.
        Assert(
            RobloxDeploymentCatalog.Parse(history, null).Count == 4,
            "Índice sem carimbos perdeu as linhas do histórico.");
        Assert(
            RobloxDeploymentCatalog.Parse("{ not json", metadata).Count == 3,
            "Histórico corrompido não caiu para os carimbos.");
        Assert(
            RobloxDeploymentCatalog.Parse(null, null).Count == 0,
            "Índice vazio não retornou lista vazia.");

        // The published index refreshes on a timer, so a deployment observed live
        // has to be mergeable without losing what the index already knew.
        var merged = RobloxDeploymentCatalog.Merge(
            entries,
            [
                new RobloxDeploymentEntry(
                    "version-ccccccccccccccc1",
                    "0.735.0.7350001",
                    null),
                new RobloxDeploymentEntry(
                    "version-aaaaaaaaaaaaaaa3",
                    "0.733.0.7330985",
                    null)
            ]);
        Assert(
            merged[0].VersionId == "version-ccccccccccccccc1",
            "Deployment recém-observado não ficou no topo.");
        Assert(
            merged.Single(entry => entry.VersionId == "version-aaaaaaaaaaaaaaa3")
                .DeployedAtUtc is not null,
            "Merge descartou o carimbo já conhecido.");
        Assert(
            merged.Count == entries.Count + 1,
            "Merge duplicou um deployment já presente.");

        return Task.CompletedTask;
    }

    private static Task TestGridAsync()
    {
        var workingArea = new Rectangle(-1920, 0, 1920, 1040);
        for (var count = 1; count <= 30; count++)
        {
            var cells = GridLayoutCalculator.Calculate(workingArea, count, 6);
            Assert(cells.Count == count, $"Contagem incorreta para {count}.");
            foreach (var cell in cells)
            {
                Assert(cell.Width > 0 && cell.Height > 0, "Célula sem área.");
                Assert(workingArea.Contains(cell), $"Célula fora da área para {count}: {cell}.");
            }

            for (var first = 0; first < cells.Count; first++)
            {
                for (var second = first + 1; second < cells.Count; second++)
                {
                    Assert(
                        !cells[first].IntersectsWith(cells[second]),
                        $"Sobreposição em {count}: {first}/{second}.");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestFiveWindowGridAsync()
    {
        var cells = GridLayoutCalculator.Calculate(new Rectangle(0, 0, 1920, 1040), 5, 6);
        var rows = cells.GroupBy(cell => cell.Y).ToArray();
        Assert(rows.Length == 2 && rows[0].Count() == 3 && rows[1].Count() == 2,
            "Cinco janelas devem usar linhas de 3 + 2, sem reserva vazia.");
        Assert(rows.All(row => row.First().Left == 6 && row.Last().Right == 1914),
            "Uma linha incompleta não preencheu a largura do monitor.");

        return Task.CompletedTask;
    }

    private static Task TestLoginBrowserPolicyAsync()
    {
        foreach (var url in new[] { "https://www.roblox.com/login", "https://roblox.com", "about:blank" })
            Assert(LoginBrowserPolicy.IsRobloxPage(url), "Página oficial bloqueada.");
        foreach (var domain in new[] { "arkoselabs.com", "client-api.arkoselabs.com", "funcaptcha.com", "roblox-api.funcaptcha.com" })
        {
            Assert(LoginBrowserPolicy.IsLoginPopup("https://" + domain + "/"), "Fornecedor documentado bloqueado.");
            Assert(!LoginBrowserPolicy.IsRobloxPage("https://" + domain + "/"), "Verificação tomou a página principal de credenciais.");
        }
        foreach (var url in new[] { "https://roblox.com.evil.example", "https://arkoselabs.com.evil.example", "http://www.roblox.com", "https://user@www.roblox.com", "https://www.roblox.com:444/", "file:///C:/test", "javascript:alert(1)", "https://evilfuncaptcha.com" })
            Assert(!LoginBrowserPolicy.IsLoginPopup(url), "Origem inválida permitida: " + url);
        var report = LoginBrowserPolicy.DescribeHttpFailure("https://auth.roblox.com/login/SECRET_PATH?token=SECRET_TOKEN#SECRET_FRAGMENT", 429);
        Assert(report == "auth.roblox.com · HTTP 429", "Diagnóstico vazou URL ou perdeu status.");
        Assert(LoginBrowserPolicy.DescribeHttpFailure("https://evil.example/?secret=value", 500) is null, "Diagnóstico registrou origem não permitida.");
        Assert(LoginBrowserPolicy.DescribeHttpFailure("https://auth.roblox.com/login", 200) is null, "Resposta normal foi descrita como erro.");
        return Task.CompletedTask;
    }

    private static Task TestVisibleFrameLayoutAsync()
    {
        var target = new Rectangle(-1900, 20, 800, 600);
        var outer = new Rectangle(100, 100, 816, 608);
        var visible = new Rectangle(108, 100, 800, 600);
        Assert(VisibleFrameLayout.ToOuter(target, outer, visible) == new Rectangle(-1908, 20, 816, 608),
            "Borda invisível não foi compensada em monitor negativo.");
        Assert(VisibleFrameLayout.ToOuter(target, outer, outer) == target, "Janela sem borda foi alterada.");
        Assert(VisibleFrameLayout.ToOuter(target, outer, new Rectangle(0, 0, 2000, 2000)) == target,
            "Geometria de outro espaço DPI foi usada como borda.");
        var minimum = GridLayoutCalculator.Calculate(new Rectangle(0, 0, 1920, 1040), 6, 8, 640, 400);
        Assert(minimum.All(cell => cell.Width >= 640 && cell.Height >= 400),
            "O gap não cedeu para acomodar um mínimo de cliente que cabe na tela.");
        return Task.CompletedTask;
    }

    private static Task TestUrlsAsync()
    {
        var numeric = BrowserProfileLauncher.NormalizeRobloxUrl("2753915549");
        Assert(numeric == "https://www.roblox.com/games/2753915549", "PlaceId não normalizado.");
        var launch = BrowserProfileLauncher.BuildOfficialLaunchUrl(
            "https://www.roblox.com/games/2753915549/Blox-Fruits");
        Assert(
            launch == "https://www.roblox.com/games/start?placeId=2753915549",
            "Deep link oficial incorreto.");

        var rejected = false;
        try
        {
            _ = BrowserProfileLauncher.NormalizeRobloxUrl("https://roblox.com.evil.example/game");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert(rejected, "Domínio semelhante malicioso deveria ser rejeitado.");
        return Task.CompletedTask;
    }

    private static Task TestRobloxLinkParserAsync()
    {
        Assert(
            RobloxLinkParser.TryParse(
                "https://www.roblox.com/games/2753915549/Blox-Fruits?foo=ignored",
                out var publicGame,
                out _),
            "Link público deveria ser aceito.");
        Assert(publicGame.Kind == RobloxLaunchTargetKind.Game, "Tipo público incorreto.");
        Assert(publicGame.PlaceId == 2753915549, "PlaceId público incorreto.");
        Assert(
            publicGame.LauncherUrl ==
            "https://www.roblox.com/games/start?placeId=2753915549",
            "URL pública canônica incorreta.");

        Assert(
            RobloxLinkParser.TryParse(
                "https://www.roblox.com/games/2753915549/Blox-Fruits?privateServerLinkCode=abc_123-xyz",
                out var privateGame,
                out _),
            "Link privado de jogo deveria ser aceito.");
        Assert(
            privateGame.Kind == RobloxLaunchTargetKind.PrivateServer
            && privateGame.PlaceId == 2753915549
            && privateGame.PrivateServerCode == "abc_123-xyz",
            "Destino privado não foi identificado.");
        Assert(
            privateGame.LauncherUrl.Contains(
                "privateServerLinkCode=abc_123-xyz",
                StringComparison.Ordinal),
            "Código privado não foi preservado na URL canônica.");

        Assert(
            RobloxLinkParser.TryParse(
                "https://www.roblox.com/share?code=invite_123&type=Server",
                out var share,
                out _),
            "Link share de servidor deveria ser aceito.");
        Assert(
            share.Kind == RobloxLaunchTargetKind.PrivateServer
            && share.PlaceId is null
            && share.PrivateServerCode == "invite_123",
            "Link share não foi classificado como servidor privado.");

        Assert(
            RobloxLinkParser.TryParse(
                "https://www.roblox.com/share?code=experience_123&type=Experience",
                out var sharedExperience,
                out _),
            "Link share de experiência deveria ser aceito.");
        Assert(
            sharedExperience.Kind == RobloxLaunchTargetKind.Game
            && sharedExperience.PlaceId is null,
            "Link share de experiência não foi classificado como jogo.");

        Assert(
            RobloxLinkParser.TryParse("2753915549", out var numeric, out _)
            && numeric.PlaceId == 2753915549,
            "PlaceId numérico deveria continuar sendo aceito.");
        Assert(
            numeric.Kind == RobloxLaunchTargetKind.Game,
            "PlaceId numérico deveria ser classificado como jogo.");
        Assert(
            numeric.PrivateServerCode is null,
            "PlaceId numérico não deveria carregar código de servidor privado.");
        Assert(
            numeric.LauncherUrl ==
            "https://www.roblox.com/games/start?placeId=2753915549",
            "URL canônica do PlaceId numérico incorreta.");
        Assert(
            RobloxLinkParser.Parse("2753915549")
            == RobloxLinkParser.Parse(
                "https://www.roblox.com/games/2753915549/Blox-Fruits"),
            "PlaceId puro e link completo deveriam gerar o mesmo alvo de launch.");

        foreach (var rejected in new[]
                 {
                     "http://www.roblox.com/games/123",
                     "https://roblox.com.evil.example/games/123",
                     "https://evil.roblox.com/games/123",
                     "https://www.roblox.com:444/games/123",
                     "https://user@www.roblox.com/games/123",
                     "https://www.roblox.com/share?code=&type=Server",
                     "https://www.roblox.com/share?code=abc&type=Game",
                     "https://www.roblox.com/games/start?placeId=0",
                     "https://www.roblox.com/games/123?placeId=456&placeId=789",
                     "-2753915549",
                     "2753915549abc",
                     "0",
                     "2 753 915 549",
                     "+2753915549",
                     "2.753.915.549",
                     "9223372036854775808"
                 })
        {
            Assert(
                !RobloxLinkParser.TryParse(rejected, out _, out _),
                $"Link inválido foi aceito: {rejected}");
        }

        foreach (var malformedId in new[]
                 {
                     "2 753 915 549",
                     "2.753.915.549",
                     "-2753915549",
                     "2753915549abc",
                     "0",
                     "9223372036854775808"
                 })
        {
            Assert(
                !RobloxLinkParser.TryParse(malformedId, out _, out var idError),
                $"ID malformado foi aceito: {malformedId}");
            Assert(
                !idError.Contains("HTTPS", StringComparison.OrdinalIgnoreCase),
                $"Erro de ID malformado não deveria falar de HTTPS: {malformedId}");
            Assert(
                idError.Contains("ID", StringComparison.Ordinal),
                $"Erro de ID malformado deveria mencionar o ID: {malformedId}");
        }

        return Task.CompletedTask;
    }

    private static async Task TestSavedDestinationsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "RobloxMultiManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SavedDestinationStore(new AppPaths(root));
            Assert((await store.LoadAsync()).Count == 0, "Cadastro novo deveria estar vazio.");
            Assert(SavedLaunchDestination.TryCreate("  Meu servidor  ", "https://www.roblox.com/games/2753915549?privateServerLinkCode=fixture-private", null, out var first, out _), "Link privado válido foi recusado.");
            Assert(SavedLaunchDestination.TryCreate("Home do jogo", "1818", null, out var second, out _), "PlaceId válido foi recusado.");
            await store.SaveAsync([first!, second!]);
            var reopened = await new SavedDestinationStore(new AppPaths(root)).LoadAsync();
            Assert(reopened.Count == 2 && reopened[0].Name == "Meu servidor" && reopened[0].Link == first!.Link, "Nome/link não persistiram ao reabrir.");
            Assert(SavedLaunchDestination.TryCreate("Novo nome", "https://www.roblox.com/share?code=fixture-share&type=Server", first!.Id, out var edited, out _), "Edição válida foi recusada.");
            await store.SaveAsync([edited!, second!]);
            reopened = await new SavedDestinationStore(new AppPaths(root)).LoadAsync();
            Assert(reopened.Count == 2 && reopened[0] == edited && reopened[1] == second, "Edição duplicou ou alterou outro destino.");
            Assert(!SavedLaunchDestination.TryCreate("", "1818", null, out _, out _), "Nome vazio foi aceito.");
            Assert(!SavedLaunchDestination.TryCreate("Externo", "https://example.com/game", null, out _, out _), "Link externo foi aceito.");
            var before = await File.ReadAllTextAsync(store.FilePath);
            var refused = false;
            try { await store.SaveAsync([edited!, edited!]); } catch (ArgumentException) { refused = true; }
            Assert(refused && await File.ReadAllTextAsync(store.FilePath) == before, "Cadastro inválido substituiu o arquivo válido.");
            await File.WriteAllTextAsync(store.FilePath, "{invalid-json");
            refused = false;
            try { await store.LoadAsync(); } catch (InvalidDataException) { refused = true; }
            Assert(refused && await File.ReadAllTextAsync(store.FilePath) == "{invalid-json", "Arquivo ilegível foi apagado ou tratado como cadastro vazio.");
            Assert(!Directory.EnumerateFiles(root, "*.tmp").Any(), "Sobrou arquivo temporário após salvar.");
        }
        finally
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RobloxMultiManager.Tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Pasta de teste fora do diretório esperado.");
            Directory.Delete(root, true);
        }
    }

    private static async Task TestStoreAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RobloxMultiManager.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var store = new LocalStore(paths);
            var profiles = new[]
            {
                AccountProfile.Create("Conta teste", "https://www.roblox.com/home")
                    .WithIdentity(new AccountIdentity(123456, "ContaTeste", "Conta Teste"))
            };
            await store.SaveProfilesAsync(profiles);
            await store.SaveSettingsAsync(new AppSettings(
                TargetInstances: 9,
                SelectedRobloxVersionId: "version-1111111111111111",
                SelectedRobloxChannel: "LIVE"));

            var loadedProfiles = await store.LoadProfilesAsync();
            var settings = await store.LoadSettingsAsync();
            Assert(loadedProfiles.Count == 1, "Perfil não persistiu.");
            Assert(loadedProfiles[0].Name == "Conta teste", "Nome do perfil mudou.");
            Assert(loadedProfiles[0].RobloxUserId == 123456, "Identidade não persistiu.");
            Assert(loadedProfiles[0].RobloxUsername == "ContaTeste", "Username não persistiu.");
            Assert(loadedProfiles[0].BrowserTrackerId > 0, "BrowserTrackerId não foi criado.");
            Assert(settings.TargetInstances == 9, "Configuração não persistiu.");
            Assert(
                settings.SelectedRobloxVersionId == "version-1111111111111111",
                "Seleção de versão não persistiu.");
            Assert(
                settings.SelectedRobloxChannel == "LIVE",
                "Canal fixado não persistiu.");

            var serialized = await File.ReadAllTextAsync(paths.ProfilesFile);
            foreach (var secretName in new[]
                     {
                         ".ROBLOSECURITY",
                         "\"password\"",
                         "\"cookie\"",
                         "\"ticket\"",
                         "\"csrf\"",
                         "\"AccountDisplay\"",
                         "\"SessionStatus\""
                     })
            {
                Assert(
                    !serialized.Contains(secretName, StringComparison.OrdinalIgnoreCase),
                    $"Cadastro contém campo secreto: {secretName}");
            }

            await File.WriteAllTextAsync(
                paths.SettingsFile,
                """{"TargetInstances":4,"AutoArrange":false}""");
            var legacySettings = await store.LoadSettingsAsync();
            Assert(
                legacySettings.TargetInstances == 4
                && legacySettings.SelectedRobloxVersionId is null,
                "Settings antigos não permaneceram compatíveis.");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RobloxMultiManager.Tests"));
            if (fullRoot.StartsWith(safeParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static Task TestVersionCatalogAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RobloxMultiManager.Tests.Versions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string firstId = "version-1111111111111111";
            const string secondId = "version-2222222222222222";
            var firstDirectory = Path.Combine(root, firstId);
            var secondDirectory = Path.Combine(root, secondId);
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var firstExe = Path.Combine(firstDirectory, "RobloxPlayerBeta.exe");
            var secondExe = Path.Combine(secondDirectory, "RobloxPlayerBeta.exe");
            File.WriteAllBytes(firstExe, [0]);
            File.WriteAllBytes(secondExe, [0]);

            var nestedDirectory = Path.Combine(
                root,
                "version-3333333333333333",
                "nested");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllBytes(
                Path.Combine(nestedDirectory, "RobloxPlayerBeta.exe"),
                [0]);
            var invalidDirectory = Path.Combine(root, "version-not-a-deployment");
            Directory.CreateDirectory(invalidDirectory);
            File.WriteAllBytes(
                Path.Combine(invalidDirectory, "RobloxPlayerBeta.exe"),
                [0]);

            var locator = new RobloxInstallationLocator(
                root,
                () => $"\"{firstExe}\" %1",
                () => secondId,
                () => "LIVE",
                (_, _) => ("Roblox Corporation", true));
            var installed = locator.DiscoverInstalled();
            Assert(installed.Count == 2, "Catálogo aceitou candidato aninhado ou inválido.");
            Assert(
                installed.Single(item => item.VersionId == firstId).IsProtocolTarget,
                "Alvo do handler não foi identificado.");
            Assert(
                installed.Single(item => item.VersionId == secondId).IsEnvironmentCurrent,
                "Versão do ambiente não foi identificada.");

            var automatic = locator.Resolve(null);
            Assert(
                automatic.VersionId == firstId
                && automatic.Source == "Protocolo roblox-player",
                "Modo automático não priorizou o handler oficial.");
            var selected = locator.Resolve(secondId);
            Assert(
                selected.IsFound && selected.VersionId == secondId,
                "Seleção fixa não venceu o modo automático.");
            Assert(
                !locator.Resolve("version-aaaaaaaaaaaaaaaa").IsFound,
                "Versão ausente recebeu fallback silencioso.");
            Assert(
                !RobloxInstallationLocator.IsValidVersionId("../version-1111111111111111"),
                "VersionId com traversal foi aceito.");
            Assert(
                RobloxClientExecutableInspector.TryInspect(
                    firstExe,
                    out var inspected,
                    root)
                && inspected.VersionId == firstId
                && inspected.SafeDisplayPath.EndsWith(
                    $"Versions{Path.DirectorySeparatorChar}{firstId}"
                    + $"{Path.DirectorySeparatorChar}RobloxPlayerBeta.exe",
                    StringComparison.OrdinalIgnoreCase),
                "Inspector não reconheceu o executável oficial direto.");

            var untrusted = new RobloxInstallationLocator(
                root,
                () => $"\"{firstExe}\" %1",
                () => secondId,
                () => "LIVE",
                (_, _) => ("Roblox Corporation", false));
            Assert(
                !untrusted.Resolve(secondId).IsFound,
                "Arquivo sem assinatura válida foi selecionado.");

            var externalProtocol = new RobloxInstallationLocator(
                root,
                () => "\"C:\\Fishstrap\\Fishstrap.exe\" %1",
                () => secondId,
                () => "LIVE",
                (_, _) => ("Roblox Corporation", true));
            Assert(
                externalProtocol.Resolve(null).VersionId == secondId,
                "Handler externo ocultou o catálogo oficial.");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var safeParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "RobloxMultiManager.Tests.Versions"));
            if (fullRoot.StartsWith(
                    safeParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestLaunchCorrelationAsync()
    {
        var started = new DateTime(
            2026,
            7,
            29,
            17,
            20,
            0,
            DateTimeKind.Utc);
        const string requestedId = "version-1111111111111111";
        const string requestedPath =
            @"C:\Users\Test\AppData\Local\Roblox\Versions\version-1111111111111111\RobloxPlayerBeta.exe";
        var actual = new RobloxClientExecutableMetadata(
            requestedPath,
            requestedId,
            "0.732.23.7321040",
            @"Versions\version-1111111111111111\RobloxPlayerBeta.exe");
        var receipt = new RobloxLaunchReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            4321,
            started,
            requestedPath,
            requestedId,
            "0.732.23.7321040",
            "LIVE",
            actual);
        var observed = new RobloxInstanceInfo(
            4321,
            started,
            (nint)123,
            "Roblox",
            RobloxInstanceState.Running,
            true,
            requestedPath,
            requestedId,
            "0.732.23.7321040");
        Assert(receipt.InitialClientMatches, "PID inicial não correspondeu ao solicitado.");
        Assert(
            MainWindow.IsSameProcessIdentity(receipt, observed),
            "PID/start exatos não foram correlacionados.");
        Assert(
            MainWindow.DoesReceiptMatchObservedClient(receipt, observed),
            "Deployment solicitado e observado não foram confirmados.");

        var reusedPid = observed with
        {
            StartTimeUtc = started.AddSeconds(1)
        };
        Assert(
            !MainWindow.IsSameProcessIdentity(receipt, reusedPid),
            "PID reutilizado foi atribuído à tentativa antiga.");
        var divergent = observed with
        {
            ExecutablePath =
                @"C:\Users\Test\AppData\Local\Roblox\Versions\version-2222222222222222\RobloxPlayerBeta.exe",
            VersionId = "version-2222222222222222",
            FileVersion = "0.732.518.7320306"
        };
        Assert(
            !MainWindow.DoesReceiptMatchObservedClient(receipt, divergent),
            "Outro deployment foi confirmado para o perfil.");

        var sanitized = MainWindow.SanitizeLogValue(
            "perfil\r\n[forjado]\t" + new string('x', 300));
        Assert(
            !sanitized.Any(char.IsControl)
            && sanitized.Length == 160,
            "Valor de log não foi higienizado ou limitado.");
        foreach (var forbidden in new[]
                 {
                     "gameinfo:",
                     "roblox-player:",
                     ".ROBLOSECURITY",
                     "csrf",
                     "ticket="
                 })
        {
            Assert(
                !sanitized.Contains(
                    forbidden,
                    StringComparison.OrdinalIgnoreCase),
                $"Log seguro contém marcador proibido: {forbidden}");
        }

        return Task.CompletedTask;
    }

    private static Task TestLaunchObservationStateAsync()
    {
        var started = new DateTime(
            2026,
            7,
            29,
            17,
            37,
            27,
            DateTimeKind.Utc);
        const string requestedId = "version-145f189a6a974303";
        const string requestedPath =
            @"C:\Users\Test\AppData\Local\Roblox\Versions\version-145f189a6a974303\RobloxPlayerBeta.exe";
        var actual = new RobloxClientExecutableMetadata(
            requestedPath,
            requestedId,
            "0.732.23.7321040",
            @"Versions\version-145f189a6a974303\RobloxPlayerBeta.exe");
        var receipt = new RobloxLaunchReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            2196,
            started,
            requestedPath,
            requestedId,
            "0.732.23.7321040",
            "LIVE",
            actual);
        var matching = new RobloxInstanceInfo(
            2196,
            started,
            (nint)123,
            "Roblox",
            RobloxInstanceState.Running,
            true,
            requestedPath,
            requestedId,
            "0.732.23.7321040");

        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                matching,
                initialProcessAlive: true,
                updaterRunning: true,
                readyStableFor: TimeSpan.FromSeconds(10),
                timedOut: false)
            == LaunchObservationDecision.WaitingForUpdater,
            "Updater com PID/path/versão correspondentes foi tratado como divergência.");
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                matching,
                initialProcessAlive: true,
                updaterRunning: false,
                readyStableFor: TimeSpan.FromSeconds(4.9),
                timedOut: false)
            == LaunchObservationDecision.Stabilizing,
            "Janela foi confirmada antes dos cinco segundos sem updater.");
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                matching,
                initialProcessAlive: true,
                updaterRunning: false,
                readyStableFor: TimeSpan.FromSeconds(5),
                timedOut: false)
            == LaunchObservationDecision.Confirmed,
            "PID correspondente não foi confirmado depois da janela estável.");

        var divergent = matching with
        {
            ExecutablePath =
                @"C:\Users\Test\AppData\Local\Roblox\Versions\version-78cc77b7a8164761\RobloxPlayerBeta.exe",
            VersionId = "version-78cc77b7a8164761"
        };
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                divergent,
                initialProcessAlive: true,
                updaterRunning: true,
                readyStableFor: TimeSpan.Zero,
                timedOut: false)
            == LaunchObservationDecision.Diverged,
            "Troca real de path/deployment não foi classificada como divergência.");
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                observed: null,
                initialProcessAlive: false,
                updaterRunning: true,
                readyStableFor: TimeSpan.Zero,
                timedOut: false)
            == LaunchObservationDecision.InitialProcessExited,
            "Encerramento do PID inicial não foi detectado.");
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                matching,
                initialProcessAlive: true,
                updaterRunning: true,
                readyStableFor: TimeSpan.Zero,
                timedOut: true)
            == LaunchObservationDecision.UpdaterStillRunning,
            "Updater persistente não recebeu resultado próprio.");
        Assert(
            MainWindow.ClassifyLaunchObservation(
                receipt,
                matching,
                initialProcessAlive: true,
                updaterRunning: false,
                readyStableFor: TimeSpan.Zero,
                timedOut: true)
            == LaunchObservationDecision.ReadinessTimedOut,
            "Timeout de responsividade foi confundido com divergência.");

        return Task.CompletedTask;
    }

    private static Task TestFastLaunchPipelineAsync()
    {
        Assert(
            MainWindow.HasBatchVerificationCapacity(0),
            "Pipeline vazio deveria aceitar a primeira validação.");
        Assert(
            MainWindow.HasBatchVerificationCapacity(1),
            "Uma validação pendente deveria liberar a próxima conta.");
        Assert(
            !MainWindow.HasBatchVerificationCapacity(2),
            "Pipeline permitiu mais de duas validações pendentes.");
        Assert(
            MainWindow.CalculateRemainingBatchLaunches(
                target: 5,
                matchingRunningCount: 2,
                availableProfiles: 10) == 3,
            "Alvo 5 com duas instâncias abertas deveria iniciar somente três.");
        Assert(
            MainWindow.CalculateRemainingBatchLaunches(
                target: 5,
                matchingRunningCount: 5,
                availableProfiles: 10) == 0,
            "Fila iniciou contas mesmo com o alvo já alcançado.");
        Assert(
            MainWindow.CalculateRemainingBatchLaunches(
                target: 10,
                matchingRunningCount: 1,
                availableProfiles: 2) == 2,
            "Fila ultrapassou a quantidade de perfis disponíveis.");
        return Task.CompletedTask;
    }

    private static Task TestVersionCheckerAsync()
    {
        var checkedAt = new DateTime(
            2026,
            7,
            29,
            15,
            0,
            0,
            DateTimeKind.Utc);
        const string json =
            """{"version":"0.732.23.7321040","clientVersionUpload":"version-145f189a6a974303","bootstrapperVersion":""}""";
        Assert(
            RobloxVersionChecker.TryParseResponse(
                json,
                "live",
                checkedAt,
                out var remote),
            "Resposta oficial válida não foi interpretada.");
        Assert(remote.Channel == "LIVE", "Canal LIVE não foi normalizado.");
        Assert(remote.Version == "0.732.23.7321040", "Versão numérica mudou.");
        Assert(
            remote.VersionId == "version-145f189a6a974303",
            "Deployment oficial mudou.");
        Assert(
            !RobloxVersionChecker.TryParseResponse(
                """{"version":"732","clientVersionUpload":"../bad"}""",
                "LIVE",
                checkedAt,
                out _),
            "Resposta oficial malformada foi aceita.");

        var rdd = RobloxVersionChecker.BuildRddUrl(
            "LIVE",
            remote.VersionId);
        Assert(
            rdd.StartsWith(
                "https://rdd.whatexpsare.online/",
                StringComparison.Ordinal),
            "Atalho RDD não usa HTTPS esperado.");
        Assert(
            rdd.Contains(
                "version=version-145f189a6a974303",
                StringComparison.Ordinal),
            "Atalho RDD não preservou o deployment.");

        var invalidChannelRejected = false;
        try
        {
            _ = RobloxVersionChecker.BuildRddUrl("LIVE\r\nmalicioso");
        }
        catch (ArgumentException)
        {
            invalidChannelRejected = true;
        }

        Assert(invalidChannelRejected, "Canal com controles foi aceito.");
        return Task.CompletedTask;
    }

    private static Task TestLocalVersionSignatureAsync()
    {
        var versionsRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions");
        if (!Directory.Exists(versionsRoot)
            || !Directory.EnumerateFiles(
                    versionsRoot,
                    "RobloxPlayerBeta.exe",
                    SearchOption.AllDirectories)
                .Any())
        {
            return Task.CompletedTask;
        }

        var locator = new RobloxInstallationLocator();
        var installed = locator.DiscoverInstalled();
        Assert(installed.Count > 0, "Nenhuma instalação local segura foi catalogada.");
        Assert(
            installed.Any(item => item.AppearsSigned),
            "Nenhuma assinatura Authenticode válida da Roblox foi confirmada.");
        Assert(
            locator.Resolve(null).IsFound,
            "Modo automático não resolveu um cliente Roblox assinado.");
        return Task.CompletedTask;
    }

    private static async Task TestGuardAsync()
    {
        var mutexName = $"RobloxMultiManager.Tests.{Guid.NewGuid():N}";
        var eventName = $"Local\\RobloxMultiManager.Tests.Event.{Guid.NewGuid():N}";
        await using var first = new MultiInstanceGuard(mutexName, eventName);
        var firstResult = await first.StartAsync(requireNoRobloxProcesses: false);
        Assert(firstResult.IsActive, "Primeiro guard deveria adquirir.");

        // The singleton name must be occupied as a MUTEX, not an Event. That wrong
        // type is the entire mechanism: the client's own check creates an Event over
        // this name, fails with ERROR_INVALID_HANDLE, logs "Cannot create event to
        // secure single process" and carries on. Holding it as an Event instead makes
        // the client's create succeed, which it reads as "another instance owns the
        // app" and answers with the SingleInstanceGuard handoff crash.
        Assert(
            MultiInstanceGuard.DescribeNamedObject(eventName).StartsWith(
                "Mutex",
                StringComparison.Ordinal),
            "O nome do singleton deveria estar ocupado como mutex, não como Event.");
        var eventProbeThrew = false;
        try
        {
            using var eventProbe = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                eventName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            eventProbeThrew = true;
        }

        Assert(
            eventProbeThrew,
            "Criar um Event sobre o nome ocupado deveria falhar; é isso que faz o "
            + "cliente seguir em frente em vez de tentar o repasse.");

        Assert(firstResult.OwnsMutex, "Primeiro guard deveria deter a posse.");
        Assert(firstResult.IsFullyHeld, "Primeiro guard deveria estar completo.");

        // Measured on this machine with one client running: ROBLOX_singletonMutex is
        // a Mutex already owned by that client. Refusing there is what demanded the
        // client be closed before the manager would even try. Not owning is now a
        // shared, usable state: the handle still keeps the name alive, which is the
        // part that matters for the next client.
        await using var second = new MultiInstanceGuard(mutexName, eventName);
        var secondResult = await second.StartAsync(requireNoRobloxProcesses: false);
        Assert(
            secondResult.IsActive,
            $"Segundo guard deveria ativar em modo compartilhado: {secondResult.State}.");
        Assert(
            !secondResult.OwnsMutex,
            "Segundo guard não pode alegar posse de um mutex de outro dono.");
        Assert(
            secondResult.Message.Contains("posse", StringComparison.OrdinalIgnoreCase),
            "O modo compartilhado deveria ser explicado na mensagem.");
        Assert(first.OwnsMutex, "O primeiro guard perdeu a posse para o segundo.");

        await first.StopAsync();
        await second.StopAsync();
        Assert(
            MultiInstanceGuard.DescribeNamedObject(eventName) == "não existe",
            "O nome do singleton deveria ser liberado no shutdown.");

        await using var third = new MultiInstanceGuard(mutexName, eventName);
        var thirdResult = await third.StartAsync(requireNoRobloxProcesses: false);
        Assert(thirdResult.IsActive, "Guard deveria readquirir após liberação.");

        // Measured on a live machine: ROBLOX_singletonEvent existed as a MUTEX
        // while ROBLOX_singletonMutex did not exist at all.  Creating an event
        // over that name throws, and letting it abort the guard blocked every
        // launch, because all three launch paths return early when the guard is
        // not active.  Holding the name in whatever type it already has must keep
        // the guard usable.
        var aliasEventName =
            $"Local\\RobloxMultiManager.Tests.Alias.{Guid.NewGuid():N}";
        var aliasMutexName = $"RobloxMultiManager.Tests.{Guid.NewGuid():N}";
        using (var squatter = new Mutex(false, aliasEventName))
        {
            await using var aliased = new MultiInstanceGuard(
                aliasMutexName,
                aliasEventName);
            var aliasResult = await aliased.StartAsync(
                requireNoRobloxProcesses: false);
            Assert(
                aliasResult.IsActive,
                "Guard não ativou com o nome do evento ocupado por um mutex.");
            // A Mutex is the shape that lets a second client start, so a name that is
            // already one is not degraded at all.
            Assert(
                aliasResult.IsFullyHeld,
                "Nome do evento já sendo mutex é o formato desejado, não degradado.");
            Assert(
                aliasResult.Message.Contains("mutex", StringComparison.OrdinalIgnoreCase),
                "O estado compartilhado não foi relatado na mensagem.");
            // The squatter must keep its own object: this guard only opens a
            // handle and must never take the name away from its creator.
            Assert(
                squatter.WaitOne(TimeSpan.Zero),
                "O guard tomou a posse do mutex de outro processo.");
            squatter.ReleaseMutex();
        }

        // The measured live shape: the client created the singleton name as an EVENT.
        // Holding that Event alive is actively harmful, because it preserves exactly
        // the object shape the client reads as "another instance owns the app", even
        // after its creator exits. So the guard must NOT hold it, must report the
        // degraded state, and must still activate.
        var foreignEventName =
            $"Local\\RobloxMultiManager.Tests.ForeignEvt.{Guid.NewGuid():N}";
        var withForeignMutexName = $"RobloxMultiManager.Tests.{Guid.NewGuid():N}";
        using (var foreignEvent = new EventWaitHandle(
                   false,
                   EventResetMode.ManualReset,
                   foreignEventName))
        {
            await using var partial = new MultiInstanceGuard(
                withForeignMutexName,
                foreignEventName);
            var partialResult = await partial.StartAsync(
                requireNoRobloxProcesses: false);
            Assert(
                partialResult.IsActive,
                "Guard deveria ativar mesmo sem poder ocupar o nome do evento.");
            Assert(
                partialResult.Degraded,
                "Nome do evento ocupado por um Event deveria ser reportado degradado.");
            Assert(
                !partialResult.IsFullyHeld,
                "Estado degradado não pode ser reportado como completo.");
            Assert(
                partialResult.Message.Contains("Event", StringComparison.OrdinalIgnoreCase),
                "A mensagem deveria dizer que o nome existe como Event.");

            // This is the regression that cost a live client. Measured on
            // 2026-08-26 16:42: with the name still a foreign Event, launching a
            // second client made Roblox hand the session over and the client that
            // had been open for 51 minutes exited by itself. So Degraded must never
            // be treated as launchable, and the guard must not claim the name.
            Assert(
                MultiInstanceGuard.DescribeNamedObject(foreignEventName) == "Event",
                "O guard não deveria ter trocado o tipo do objeto alheio.");
            Assert(
                !partialResult.IsFullyHeld,
                "Estado degradado é destrutivo para o cliente aberto e não pode "
                + "contar como pronto para abrir.");
        }

        // With the foreign Event gone the name must become claimable as a Mutex, which
        // is what the app's upgrade path depends on.
        await using (var upgraded = new MultiInstanceGuard(
                         $"RobloxMultiManager.Tests.{Guid.NewGuid():N}",
                         foreignEventName))
        {
            var upgradedResult = await upgraded.StartAsync(
                requireNoRobloxProcesses: false);
            Assert(
                upgradedResult.IsFullyHeld,
                "Depois do Event morrer o nome deveria ser ocupável como mutex.");
        }

        Assert(
            MultiInstanceGuard.DescribeNamedObject(
                $"RobloxMultiManager.Tests.Ausente.{Guid.NewGuid():N}")
            == "não existe",
            "Nome inexistente deveria ser descrito como inexistente.");
        var describedName = $"Local\\RobloxMultiManager.Tests.Desc.{Guid.NewGuid():N}";
        using (var describedEvent = new EventWaitHandle(
                   false,
                   EventResetMode.ManualReset,
                   describedName))
        {
            Assert(
                MultiInstanceGuard.DescribeNamedObject(describedName) == "Event",
                "Event não foi descrito corretamente.");
        }

        var ownedName = $"RobloxMultiManager.Tests.Owned.{Guid.NewGuid():N}";
        using (var owned = new Mutex(false, ownedName))
        {
            Assert(
                MultiInstanceGuard.DescribeNamedObject(ownedName) == "Mutex livre",
                "Mutex sem dono deveria ser descrito como livre.");

            // Ownership has to be taken on ANOTHER thread. Mutex acquisition is
            // recursive for the owning thread, so taking it here would let the probe
            // below acquire it again and report the name as free.
            using var acquired = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var holder = new Thread(() =>
            {
                using var handle = Mutex.OpenExisting(ownedName);
                handle.WaitOne();
                acquired.Set();
                release.Wait();
                handle.ReleaseMutex();
            })
            {
                IsBackground = true
            };
            holder.Start();
            Assert(acquired.Wait(TimeSpan.FromSeconds(5)), "A posse não foi tomada.");
            Assert(
                MultiInstanceGuard.DescribeNamedObject(ownedName)
                == "Mutex de outro processo",
                "Mutex possuído deveria ser descrito como de outro dono.");
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }

        // The symmetric case is fatal for the core lock, but it must report a
        // usable reason instead of surfacing a raw exception.
        var takenMutexName =
            $"Local\\RobloxMultiManager.Tests.Taken.{Guid.NewGuid():N}";
        using (var wrongType = new EventWaitHandle(
                   false,
                   EventResetMode.ManualReset,
                   takenMutexName))
        {
            await using var blocked = new MultiInstanceGuard(
                takenMutexName,
                $"Local\\RobloxMultiManager.Tests.Evt.{Guid.NewGuid():N}");
            var blockedResult = await blocked.StartAsync(
                requireNoRobloxProcesses: false);
            Assert(
                blockedResult.State == GuardState.Contested,
                "Nome principal ocupado por outro tipo deveria virar contenção.");
            Assert(
                !blockedResult.Message.Contains(
                    "WaitHandleCannotBeOpened",
                    StringComparison.OrdinalIgnoreCase),
                "A mensagem expôs a exceção crua em vez de explicar o problema.");
        }
    }

    private static Task TestIdentityAsync()
    {
        const string wrapped =
            """{"authenticated":true,"id":987654321,"name":"Conta_2","displayName":"Minha Conta"}""";
        Assert(
            AccountLoginWindow.TryParseIdentity(wrapped, out var identity),
            "Identidade encapsulada válida não foi reconhecida.");
        Assert(identity.UserId == 987654321, "UserId incorreto.");
        Assert(identity.Username == "Conta_2", "Username incorreto.");
        const string direct =
            """{"id":123456789,"name":"ContaDireta","displayName":"Conta Direta"}""";
        Assert(
            AccountLoginWindow.TryParseIdentity(direct, out var directIdentity),
            "Resposta direta do endpoint não foi reconhecida.");
        Assert(directIdentity.UserId == 123456789, "UserId direto incorreto.");
        Assert(
            !AccountLoginWindow.TryParseIdentity(
                """{"authenticated":false,"status":401}""",
                out _),
            "Sessão não autenticada foi aceita.");
        Assert(
            !AccountLoginWindow.TryParseIdentity(
                """{"authenticated":"true","id":"123","name":7,"displayName":{}}""",
                out _),
            "Tipos inválidos foram aceitos.");
        Assert(
            AccountLoginWindow.IsAllowedRobloxUri("https://users.roblox.com/v1/users/authenticated"),
            "Subdomínio oficial foi bloqueado.");
        Assert(
            !AccountLoginWindow.IsAllowedRobloxUri("https://roblox.com.evil.example/"),
            "Domínio semelhante malicioso foi aceito.");
        Assert(
            !AccountLoginWindow.IsAllowedRobloxUri("https://www.roblox.com:444/login"),
            "Porta não padrão foi aceita no login.");
        Assert(
            !AccountLoginWindow.IsAllowedRobloxUri("https://user@www.roblox.com/login"),
            "URL com userinfo foi aceita no login.");
        return Task.CompletedTask;
    }

    private static Task TestHomeLaunchUriAsync()
    {
        var ticket = new string('A', 64) + "_safe-ticket";
        var uri = RobloxLaunchWindow.BuildHomeLaunchUri(
            ticket,
            123456789012,
            1770000000000);
        Assert(uri.StartsWith("roblox-player:1+launchmode:app+", StringComparison.Ordinal),
            "URI não usa launchmode:app.");
        Assert(uri.Contains("+gameinfo:", StringComparison.Ordinal), "URI não contém autorização.");
        Assert(!uri.Contains("placeId", StringComparison.OrdinalIgnoreCase), "URI contém PlaceId.");
        Assert(!uri.Contains("placelauncherurl", StringComparison.OrdinalIgnoreCase),
            "URI contém launcher de jogo.");
        Assert(uri.Contains("+browsertrackerid:123456789012", StringComparison.Ordinal),
            "BrowserTrackerId ausente.");
        var reservedTicket = new string('R', 40) + ":/?&%#+";
        var reservedUri = RobloxLaunchWindow.BuildHomeLaunchUri(
            reservedTicket,
            123456789012,
            1770000000000);
        Assert(
            reservedUri.Contains(
                "+gameinfo:" + Uri.EscapeDataString(reservedTicket),
                StringComparison.Ordinal),
            "Ticket não foi codificado como no launcher web.");
        Assert(
            !reservedUri.Contains(reservedTicket, StringComparison.Ordinal),
            "Ticket reservado permaneceu cru no protocolo.");
        var liveChannelUri = RobloxLaunchWindow.BuildHomeLaunchUri(
            ticket,
            123456789012,
            1770000000000,
            "LIVE");
        Assert(
            liveChannelUri.EndsWith(
                "+channel:",
                StringComparison.Ordinal),
            "Canal LIVE não foi fixado como production no protocolo.");
        var testChannelUri = RobloxLaunchWindow.BuildHomeLaunchUri(
            ticket,
            123456789012,
            1770000000000,
            "ztestremovingendianmodeargument");
        Assert(
            testChannelUri.EndsWith(
                "+channel:ztestremovingendianmodeargument",
                StringComparison.Ordinal),
            "Canal de teste não foi preservado no protocolo.");
        return Task.CompletedTask;
    }

    private static Task TestGameLaunchUriAsync()
    {
        var target = RobloxLinkParser.Parse(
            "https://www.roblox.com/games/2753915549/Blox-Fruits?privateServerLinkCode=abc_123");
        var ticket = new string('G', 64) + ":/?&%#+";
        var uri = RobloxLaunchWindow.BuildGameLaunchUri(
            ticket,
            123456789012,
            1770000000000,
            target,
            "LIVE");
        Assert(
            uri.StartsWith("roblox-player:1+launchmode:play+", StringComparison.Ordinal),
            "URI de jogo não usa launchmode:play.");
        var expectedLauncher =
            "https://assetgame.roblox.com/game/PlaceLauncher.ashx"
            + "?request=RequestPrivateGame&browserTrackerId=123456789012"
            + "&placeId=2753915549&isPlayTogetherGame=false&linkCode=abc_123";
        Assert(
            uri.Contains(
                "+placelauncherurl:" + Uri.EscapeDataString(expectedLauncher),
                StringComparison.Ordinal),
            "Launcher URL oficial não foi codificada.");
        Assert(
            !uri.Contains("https://www.roblox.com", StringComparison.Ordinal),
            "URL oficial permaneceu crua no protocolo.");
        Assert(
            !uri.Contains(ticket, StringComparison.Ordinal),
            "Ticket reservado permaneceu cru no protocolo.");
        Assert(
            uri.EndsWith("+channel:", StringComparison.Ordinal),
            "Canal LIVE não foi normalizado.");
        return Task.CompletedTask;
    }

    private static Task TestForceTerminateIdentityAsync()
    {
        using var current = Process.GetCurrentProcess();
        var start = current.StartTime.ToUniversalTime();
        var reusedPid = new RobloxInstanceInfo(
            current.Id,
            start.AddMilliseconds(-1),
            nint.Zero,
            string.Empty,
            RobloxInstanceState.Running,
            false);
        var outcome = new WindowLayoutService().ForceTerminate(reusedPid);
        Assert(
            outcome.Code == "identity-mismatch" && !outcome.TerminationRequested,
            "PID reutilizado não foi bloqueado antes do encerramento.");
        return Task.CompletedTask;
    }

    private static Task TestTicketHandshakeAsync()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        const long expectedUserId = 720100002;
        var script = RobloxLaunchWindow.BuildTicketFetchScript(
            nonce,
            expectedUserId);
        Assert(
            script.Contains("window.chrome.webview.postMessage", StringComparison.Ordinal),
            "Script não retorna o resultado por WebMessage.");
        Assert(
            script.Contains("return true;", StringComparison.Ordinal),
            "Script não termina sincronamente após iniciar o trabalho.");
        Assert(
            script.Contains("meta[name=\"csrf-token\"]", StringComparison.Ordinal),
            "Script não usa o CSRF inicial publicado pela página oficial.");
        Assert(
            script.Contains("localStorage.getItem(\"x-csrf-token\")", StringComparison.Ordinal),
            "Script não reutiliza o CSRF mantido pela página oficial.");
        Assert(
            !script.Contains("document.cookie", StringComparison.OrdinalIgnoreCase),
            "Script tentou ler cookies diretamente.");
        var identityIndex = script.IndexOf(
            "users.roblox.com/v1/users/authenticated",
            StringComparison.Ordinal);
        var assertionIndex = script.IndexOf(
            "auth.roblox.com/v1/client-assertion",
            StringComparison.Ordinal);
        Assert(
            identityIndex >= 0 && identityIndex < assertionIndex,
            "Ticket é solicitado antes de confirmar o UserId da sessão.");

        var ticket = new string('T', 96) + "_safe";
        var message = $$"""
            {
              "source": "RobloxMultiManager.TicketResult",
              "nonce": "{{nonce}}",
              "kind": "ticket-result",
              "session": "verified",
              "authenticatedUserId": "{{expectedUserId}}",
              "status": 200,
              "ticket": "{{ticket}}"
            }
            """;
        Assert(
            RobloxLaunchWindow.TryParseTicketMessage(
                message,
                nonce,
                expectedUserId,
                out var result),
            "Mensagem válida do ticket foi rejeitada.");
        Assert(result.SessionState == "verified", "Estado da sessão mudou.");
        Assert(result.StatusCode == 200, "Status da mensagem mudou.");
        Assert(result.Ticket == ticket, "Ticket da mensagem mudou.");

        Assert(
            !RobloxLaunchWindow.TryParseTicketMessage(
                message.Replace(nonce, "fedcba9876543210fedcba9876543210"),
                nonce,
                expectedUserId,
                out _),
            "Mensagem com nonce incorreto foi aceita.");
        Assert(
            !RobloxLaunchWindow.TryParseTicketMessage(
                message.Replace(
                    expectedUserId.ToString(),
                    "720100001",
                    StringComparison.Ordinal),
                nonce,
                expectedUserId,
                out _),
            "Mensagem verificada para outra conta foi aceita.");
        Assert(
            RobloxLaunchWindow.IsRequestedHomePage("https://www.roblox.com/home"),
            "Navegação inicial oficial foi rejeitada.");
        Assert(
            !RobloxLaunchWindow.IsRequestedHomePage("https://www.roblox.com/pt/home"),
            "Redirect localizado substituiu a navegação inicial correlacionada.");
        Assert(
            RobloxLaunchWindow.IsTrustedLaunchOrigin("https://www.roblox.com/pt/home"),
            "Home localizada foi rejeitada.");
        Assert(
            RobloxLaunchWindow.IsTrustedLaunchOrigin(
                "https://www.roblox.com/pt-br/home?source=redirect"),
            "Home localizada com query foi rejeitada.");
        Assert(
            RobloxLaunchWindow.IsTrustedLaunchOrigin("https://www.roblox.com/login"),
            "Origem do redirect de login foi rejeitada antes do probe.");
        Assert(
            !RobloxLaunchWindow.IsTrustedLaunchOrigin(
                "https://www.roblox.com.evil.example/home"),
            "Origem semelhante maliciosa foi aceita.");
        Assert(
            !RobloxLaunchWindow.IsTrustedLaunchOrigin("https://www.roblox.com:444/home"),
            "Porta não padrão foi aceita como origem oficial.");
        Assert(
            !RobloxLaunchWindow.IsTrustedLaunchOrigin(
                "https://user@www.roblox.com/home"),
            "Origem com userinfo foi aceita.");

        var accepted = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "verified",
            200,
            ticket);
        Assert(
            accepted.Action == RobloxLaunchWindow.TicketFetchAction.LaunchPlayer,
            "Ticket válido não liberou o Player.");
        Assert(accepted.Ticket == ticket, "Ticket mudou durante o handshake.");

        var expired = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "not-authenticated",
            401,
            null);
        Assert(
            expired.Action == RobloxLaunchWindow.TicketFetchAction.Fail,
            "Sessão expirada não falhou.");
        Assert(
            expired.ErrorCode?.Contains("401", StringComparison.Ordinal) == true,
            "Código de sessão expirada não preservou o status HTTP.");

        var malformed = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "verified",
            200,
            "quebrado\r\nheader: ruim");
        Assert(
            malformed.Action == RobloxLaunchWindow.TicketFetchAction.Fail,
            "Ticket malformado foi aceito.");

        var mismatch = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "account-mismatch",
            409,
            null);
        Assert(
            mismatch.ErrorCode == "Conta diferente no perfil",
            "Conta divergente não foi diferenciada de sessão expirada.");

        var throttled = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "verified",
            429,
            null);
        Assert(
            throttled.Action == RobloxLaunchWindow.TicketFetchAction.Fail,
            "Limite HTTP não terminou em erro visível.");

        var directTicket = RobloxLaunchWindow.EvaluateTicketFetchResponse(
            "verified",
            200,
            "RBX1:opaque.ticket/value=accepted");
        Assert(
            directTicket.Action == RobloxLaunchWindow.TicketFetchAction.LaunchPlayer,
            "Ticket opaco foi rejeitado.");
        return Task.CompletedTask;
    }

    private static async Task TestFiveWindowsAsync(string fixturePath)
    {
        Assert(File.Exists(fixturePath), $"Fixture inexistente: {fixturePath}");
        var launched = new List<Process>();
        await using var monitor = new RobloxProcessMonitor("RobloxWindowFixture");
        try
        {
            monitor.Start();
            for (var index = 0; index < 5; index++)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fixturePath,
                    UseShellExecute = false,
                    Arguments = "--lifetime 30"
                });
                Assert(process is not null, $"Fixture {index + 1} não iniciou.");
                launched.Add(process!);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            IReadOnlyList<RobloxInstanceInfo> snapshot = [];
            while (DateTime.UtcNow < deadline)
            {
                snapshot = await monitor.RefreshNowAsync();
                if (snapshot.Count >= 5
                    && snapshot.Count(item => item.HasWindow && item.IsResponsive) >= 5)
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert(snapshot.Count >= 5, $"Somente {snapshot.Count}/5 processos detectados.");
            Assert(
                snapshot.Count(item => item.HasWindow && item.IsResponsive) >= 5,
                "Nem todas as cinco janelas ficaram prontas.");

            var layout = new WindowLayoutService();
            var moved = layout.Arrange(snapshot, selectedMonitor: 0);
            Assert(moved >= 5, $"Somente {moved}/5 janelas foram organizadas.");
            var placed = snapshot.OrderBy(item => item.StartTimeUtc).ThenBy(item => item.ProcessId).ToArray();
            var expected = GridLayoutCalculator.Calculate(layout.GetMonitors()[0].WorkingArea, placed.Length);
            for (var index = 0; index < placed.Length; index++)
            {
                var actual = ReadWindowRectangle(placed[index].WindowHandle);
                Assert(actual is not null && Math.Abs(actual.Value.Left - expected[index].Left) <= 2
                    && Math.Abs(actual.Value.Top - expected[index].Top) <= 2
                    && Math.Abs(actual.Value.Width - expected[index].Width) <= 2
                    && Math.Abs(actual.Value.Height - expected[index].Height) <= 2,
                    $"A borda visível não coincidiu com a célula {index}: {actual}, esperado {expected[index]}.");
            }
            foreach (var instance in snapshot.Take(5))
            {
                _ = layout.RequestClose(instance);
            }
        }
        finally
        {
            foreach (var process in launched)
            {
                try
                {
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// The reason a client died has to come from the client's own log.
    /// </summary>
    /// <remarks>
    /// The crash text below is copied from a real 1 KiB log left by a client that
    /// died 0.16 s into startup on this machine, seven times in a row.  The benign
    /// case matters just as much: a client that logs
    /// <c>Cannot create event to secure single process</c> and then runs normally
    /// must not be reported as a failure, because that line is the compatibility
    /// lock working and it appears in healthy sessions.
    /// </remarks>
    private static Task TestClientFailureReaderAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rmm-failure-" + Guid.NewGuid().ToString("N")[..8]);
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var start = DateTime.UtcNow;

            var crash = Path.Combine(logs, "0.734.0_20260824T173209Z_Player_01043_last.log");
            File.WriteAllLines(crash, [
                "2026-08-24T17:32:09.156Z,0.156728,4370,6,Warning [FLog::RobloxStarter] Starting module: Logging",
                "2026-08-24T17:32:09.158Z,0.158718,4370,6,Warning [FLog::Output] NoReload Detector: WebPlay NoReload with Tray only!",
                "2026-08-24T17:32:09.161Z,0.161703,4370,6 [LOGCHANNELS + 1] RBXCRASH: UnhandledException (class wil::ResultException B:\\Client\\WindowsClient\\SingleInstanceGuard.cpp(131)\\RobloxPlayerBeta.exe!00007FF6100673B7: (caller: 000",
                "",
                "    Msg:[Failed to send message ID 49600, result: 0, error: 1400] ",
                ")"
            ]);
            File.SetCreationTimeUtc(crash, start.AddSeconds(2));

            var reader = new RobloxClientFailureReader(root);
            var failure = reader.Read(start);
            Assert(failure is not null, "O crash da trava de instância única não foi lido.");
            Assert(
                failure!.Kind == "single-instance-handoff",
                $"Tipo de falha errado: {failure.Kind}.");
            Assert(failure.Win32Error == 1400, $"Erro do Windows errado: {failure.Win32Error}.");
            Assert(
                failure.ClosingAllClientsMayFix,
                "O remédio medido deveria ser oferecido para esta falha.");
            Assert(
                failure.LogFileName.Contains("01043", StringComparison.Ordinal),
                "A falha deveria citar o log de origem.");

            // Um cliente saudável: a falha de criar o event é a trava funcionando.
            var benignRoot = Path.Combine(root, "benigno");
            var benignLogs = Path.Combine(benignRoot, "logs");
            Directory.CreateDirectory(benignLogs);
            var benign = Path.Combine(benignLogs, "0.734.0_20260824T174239Z_Player_25D64_last.log");
            File.WriteAllLines(benign, [
                "2026-08-24T17:42:39.341Z,0.341198,2dc4,6,Error [FLog::WndProcessCheck] Cannot create event to secure single process: Unknown error code 0x00000006.",
                "2026-08-24T17:42:39.341Z,0.341198,2dc4,6,Info [FLog::WndProcessCheck] WindowsAppReloadTelemetry CreateEventFailure",
                "2026-08-24T17:42:40.000Z,1.000000,2dc4,6,Info [FLog::Systray] No new version available"
            ]);
            File.SetCreationTimeUtc(benign, start.AddSeconds(1));
            Assert(
                new RobloxClientFailureReader(benignRoot).Read(start) is null,
                "A falha de criar o event é a trava funcionando e não pode virar erro.");

            // Sem log, nada é inventado.
            var emptyRoot = Path.Combine(root, "vazio");
            Directory.CreateDirectory(Path.Combine(emptyRoot, "logs"));
            Assert(
                new RobloxClientFailureReader(emptyRoot).Read(start) is null,
                "Sem log do cliente não deveria haver motivo.");

            // Um log muito antigo não pertence a este processo.
            var staleRoot = Path.Combine(root, "antigo");
            var staleLogs = Path.Combine(staleRoot, "logs");
            Directory.CreateDirectory(staleLogs);
            var stale = Path.Combine(staleLogs, "0.734.0_20260101T000000Z_Player_AAAAA_last.log");
            File.Copy(crash, stale);
            File.SetCreationTimeUtc(stale, start.AddHours(-3));
            Assert(
                new RobloxClientFailureReader(staleRoot).Read(start) is null,
                "Um log de horas antes não pode ser atribuído a este processo.");

            // A versão fixada se substituindo sozinha. Não é crash: o cliente sai
            // limpo depois de entregar a abertura ao próprio instalador, e por isso
            // isso era reportado como "sem motivo registrado".
            var updRoot = Path.Combine(root, "autoatualizou");
            var updLogs = Path.Combine(updRoot, "logs");
            Directory.CreateDirectory(updLogs);
            var upd = Path.Combine(updLogs, "0.734.0_20260826T163228Z_Player_56D5C_last.log");
            File.WriteAllLines(upd, [
                "2026-08-26T16:32:28.129Z,0.129691,0f20,6,Info [FLog::UpdateController] UpdateController: versionQueryUrl: https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer",
                "2026-08-26T16:32:28.129Z,0.129691,45f4,6,Info [FLog::UpdateController] Checking if updater exists at C:\\Users\\Example\\AppData\\Local\\Roblox\\Versions\\version-ddf602d9cfe44005\\RobloxPlayerInstaller.exe. Returning true",
                "2026-08-26T16:32:28.214Z,0.214708,45f4,6,Debug [FLog::UpdateController] version response: {\"version\":\"0.735.3.7351147\",\"clientVersionUpload\":\"version-17d504d2c9544583\",\"bootstrapperVersion\":\"\"}",
                "2026-08-26T16:32:28.214Z,0.214708,45f4,6,Info [FLog::UpdateController] Update check thread: updateRequired TRUE",
                "2026-08-26T16:32:28.278Z,0.278736,0f20,6,Info [FLog::UpdateController] Update thread: start \"\"C:\\Users\\Example\\AppData\\Local\\Roblox\\Versions\\version-ddf602d9cfe44005\\RobloxPlayerInstaller.exe\" roblox-player:1+launchmode:app"
            ]);
            File.SetCreationTimeUtc(upd, start.AddSeconds(2));
            var auto = new RobloxClientFailureReader(updRoot).Read(start);
            Assert(auto is not null, "A autoatualização da versão fixada não foi lida.");
            Assert(auto!.Kind == "self-update", $"Tipo errado: {auto.Kind}.");
            Assert(
                auto.ReplacedByDeployment == "version-17d504d2c9544583",
                $"Deployment substituto errado: {auto.ReplacedByDeployment}.");
            Assert(
                auto.BlockingSelfUpdateMayFix,
                "Bloquear a autoatualização deveria ser oferecido.");
            Assert(
                !auto.ClosingAllClientsMayFix,
                "Fechar todo Roblox não é o remédio para autoatualização.");

            // Um veredito de atualizar sem o lançamento do instalador não encerrou
            // nada, então não pode ser apontado como causa da falha.
            var soVeredito = Path.Combine(root, "soveredito");
            var soVereditoLogs = Path.Combine(soVeredito, "logs");
            Directory.CreateDirectory(soVereditoLogs);
            var sv = Path.Combine(soVereditoLogs, "0.734.0_20260826T170000Z_Player_CCCCC_last.log");
            File.WriteAllLines(sv, [
                "2026-08-26T17:00:00.100Z,0.1,1111,6,Info [FLog::UpdateController] Update check thread: updateRequired TRUE",
                "2026-08-26T17:00:05.100Z,5.1,1111,6,Info [FLog::Systray] checkForUpdateTimer Fired"
            ]);
            File.SetCreationTimeUtc(sv, start.AddSeconds(2));
            Assert(
                new RobloxClientFailureReader(soVeredito).Read(start) is null,
                "Veredito de atualizar sem lançar o instalador não é causa de falha.");

            // Uma exceção não tratada qualquer é relatada, mas sem oferecer o remédio.
            var otherRoot = Path.Combine(root, "outro");
            var otherLogs = Path.Combine(otherRoot, "logs");
            Directory.CreateDirectory(otherLogs);
            var other = Path.Combine(otherLogs, "0.734.0_20260824T180000Z_Player_BBBBB_last.log");
            File.WriteAllLines(other, [
                "2026-08-24T18:00:00.100Z,0.1,1111,6 [LOGCHANNELS + 1] RBXCRASH: UnhandledException (class std::bad_alloc)",
                "    Msg:[out of memory]"
            ]);
            File.SetCreationTimeUtc(other, start.AddSeconds(3));
            var outra = new RobloxClientFailureReader(otherRoot).Read(start);
            Assert(outra is not null && outra.Kind == "crash", "Crash genérico não foi lido.");
            Assert(
                !outra!.ClosingAllClientsMayFix,
                "Fechar tudo não é remédio para um crash qualquer.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Blocking a pinned deployment's self-update is one reversible rename.
    /// </summary>
    /// <remarks>
    /// Measured on a live machine: with <c>RobloxPlayerInstaller.exe</c> present the
    /// pinned 0.734 client logged <c>updateRequired TRUE</c> and was replaced by
    /// 0.735.3; with the same file renamed the existence check returned false, the
    /// client never queried the server, and it stayed open.  This test covers the
    /// file handling around that, including that it is reversible and idempotent.
    /// </remarks>
    private static Task TestSelfUpdateBlockerAsync()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "rmm-selfupdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var blocker = new RobloxSelfUpdateBlocker();
            var updater = Path.Combine(folder, RobloxSelfUpdateBlocker.UpdaterFileName);
            var disabled = updater + RobloxSelfUpdateBlocker.DisabledSuffix;

            Assert(
                blocker.Inspect(folder) == SelfUpdateState.NoUpdaterPresent,
                "Pasta sem instalador deveria ser NoUpdaterPresent.");

            File.WriteAllText(updater, "instalador falso");
            Assert(
                blocker.Inspect(folder) == SelfUpdateState.Allowed,
                "Com o instalador presente a versão se atualiza sozinha.");

            var (blocked, _) = blocker.Block(folder);
            Assert(blocked == SelfUpdateState.Blocked, "Bloqueio não foi aplicado.");
            Assert(!File.Exists(updater), "O instalador deveria ter saído do lugar.");
            Assert(File.Exists(disabled), "O arquivo renomeado deveria existir.");
            Assert(
                blocker.Inspect(folder) == SelfUpdateState.Blocked,
                "Estado após bloquear deveria ser Blocked.");

            // Idempotente: bloquear de novo não pode apagar nem duplicar nada.
            var (again, _) = blocker.Block(folder);
            Assert(again == SelfUpdateState.Blocked, "Bloquear duas vezes deveria ser inócuo.");
            Assert(File.Exists(disabled), "O arquivo renomeado não pode desaparecer.");

            var (restored, _) = blocker.Unblock(folder);
            Assert(restored == SelfUpdateState.Allowed, "Restauração falhou.");
            Assert(File.Exists(updater), "O instalador deveria estar de volta.");
            Assert(!File.Exists(disabled), "O arquivo renomeado deveria ter sumido.");
            Assert(
                File.ReadAllText(updater) == "instalador falso",
                "O conteúdo do instalador não pode ser alterado; é só um rename.");

            var (noop, _) = blocker.Unblock(folder);
            Assert(noop == SelfUpdateState.Allowed, "Restaurar duas vezes deveria ser inócuo.");

            Assert(
                blocker.Inspect(Path.Combine(folder, "inexistente")) == SelfUpdateState.NoUpdaterPresent,
                "Pasta inexistente não deveria lançar.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Real windows that refuse to shrink into their cell must still end up
    /// completely inside the monitor.
    /// </summary>
    /// <remarks>
    /// This is the only test that can prove the requirement, because the defect was
    /// never in the arithmetic: the calculator's cells were already inside the work
    /// area.  The old placement code measured only the origin and accepted whatever
    /// size the window insisted on, so an oversized client hung over the edge of the
    /// screen and was still counted as organised.  The fixture is given a minimum
    /// size larger than its cell so the window manager genuinely refuses the
    /// requested size, which is what a Roblox client does.
    /// </remarks>
    private static async Task TestStubbornWindowsStayOnScreenAsync(string fixturePath)
    {
        Assert(File.Exists(fixturePath), $"Fixture inexistente: {fixturePath}");

        const int windowCount = 8;
        var layout = new WindowLayoutService();
        var monitors = layout.GetMonitors();
        Assert(monitors.Count > 0, "Nenhum monitor foi enumerado.");
        var work = monitors[0].WorkingArea;

        // Large enough to overflow the cell for this count, small enough to still
        // fit the monitor even at 150% scaling, so a refusal to fit the screen at
        // all cannot be confused with the defect under test.
        var minimumWidth = Math.Max(320, Math.Min(700, work.Width / 2));
        var minimumHeight = Math.Max(240, Math.Min(520, work.Height / 2));

        var launched = new List<Process>();
        await using var monitorService = new RobloxProcessMonitor("RobloxWindowFixture");
        try
        {
            monitorService.Start();
            for (var index = 0; index < windowCount; index++)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fixturePath,
                    UseShellExecute = false,
                    Arguments =
                        $"--lifetime 40 --min-width {minimumWidth} "
                        + $"--min-height {minimumHeight}"
                });
                Assert(process is not null, $"Fixture {index + 1} não iniciou.");
                launched.Add(process!);
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            IReadOnlyList<RobloxInstanceInfo> snapshot = [];
            while (DateTime.UtcNow < deadline)
            {
                snapshot = await monitorService.RefreshNowAsync();
                if (snapshot.Count(item => item.HasWindow && item.IsResponsive)
                    >= windowCount)
                {
                    break;
                }

                await Task.Delay(250);
            }

            var ready = snapshot
                .Where(item => item.HasWindow && item.IsResponsive)
                .ToArray();
            Assert(
                ready.Length >= windowCount,
                $"Somente {ready.Length}/{windowCount} janelas ficaram prontas.");

            var report = layout.ArrangeWithReport(ready, selectedMonitor: 0);
            Assert(
                report.Moved >= windowCount,
                $"Somente {report.Moved}/{windowCount} janelas foram posicionadas.");

            // The windows keep settling after the posted placements, so the
            // invariant is polled rather than sampled once. Polling only delays the
            // verdict; it never weakens it.
            var offenders = Array.Empty<string>();
            var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            do
            {
                offenders = ready
                    .Select(instance => new
                    {
                        instance.ProcessId,
                        Rectangle = ReadWindowRectangle(instance.WindowHandle)
                    })
                    .Where(item =>
                        item.Rectangle is null || !work.Contains(item.Rectangle.Value))
                    .Select(item =>
                        $"PID {item.ProcessId} em "
                        + (item.Rectangle?.ToString() ?? "retângulo ilegível"))
                    .ToArray();
                if (offenders.Length == 0)
                {
                    break;
                }

                await Task.Delay(200);
            }
            while (DateTime.UtcNow < settleDeadline);

            Assert(
                offenders.Length == 0,
                $"Janela(s) fora da tela {work}: {string.Join("; ", offenders)}.");
            Assert(
                report.Overflowing == 0,
                $"{report.Overflowing} janela(s) foram relatadas como fora da tela.");
            Assert(
                report.Contained >= windowCount,
                $"Somente {report.Contained}/{windowCount} janelas foram confirmadas "
                + "dentro da tela.");

            foreach (var instance in ready)
            {
                _ = layout.RequestClose(instance);
            }
        }
        finally
        {
            foreach (var process in launched)
            {
                try
                {
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Reads a live window's rectangle, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Check visible DWM bounds. Invisible resize handles may extend beyond the
    /// monitor when a visible edge is correctly flush with it. Checking the outer
    /// rectangle would incorrectly reject that placement.
    /// </remarks>
    private static System.Drawing.Rectangle? ReadWindowRectangle(nint window)
    {
        if (window == nint.Zero || !NativeTestWindows.IsWindow(window))
        {
            return null;
        }

        if (!NativeTestWindows.GetWindowRect(window, out var rect)) return null;
        if (NativeTestWindows.DwmGetWindowAttribute(window, 9, out var visible, Marshal.SizeOf<NativeTestWindows.Rect>()) == 0)
            rect = visible;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static class NativeTestWindows
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect rect, int size);
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Asserts that <paramref name="action"/> rejects its input.  The assertion
    /// message itself is allowed through so a failing Assert inside the action is
    /// not silently reported as the expected rejection.
    /// </summary>
    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException failure) when (failure.Message == message)
        {
            throw;
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
