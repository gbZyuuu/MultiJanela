using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

/// <summary>
/// Why a Roblox client died, taken from the client's own log.
/// </summary>
/// <param name="Kind">
/// Stable identifier for the failure, for logs and tests rather than for display.
/// </param>
/// <param name="Summary">Sentence to show the user, already in Portuguese.</param>
/// <param name="ClosingAllClientsMayFix">
/// <see langword="true"/> only for the single-instance handoff crash, where closing
/// every Roblox process was measured to clear the state.
/// </param>
public sealed record RobloxClientFailure(
    string Kind,
    string LogFileName,
    int? Win32Error,
    string Summary,
    bool ClosingAllClientsMayFix,
    string? ReplacedByDeployment = null,
    bool BlockingSelfUpdateMayFix = false);

/// <summary>
/// Reads the reason a client process exited out of the log the client itself wrote.
/// </summary>
/// <remarks>
/// <para>
/// Without this the app could only report what it observed from outside, "the
/// initial pid exited before confirmation", which is true and useless: it names no
/// cause and suggests no action.  The client, meanwhile, writes the real reason to
/// its own log before dying.
/// </para>
/// <para>
/// The case this was built for was measured on a live machine.  Seven consecutive
/// launches failed, and every dead client had left the same 1 KiB log:
/// </para>
/// <code>
/// NoReload Detector: WebPlay NoReload with Tray only!
/// RBXCRASH: UnhandledException (class wil::ResultException
///   B:\Client\WindowsClient\SingleInstanceGuard.cpp(131)
///   Msg:[Failed to send message ID 49600, result: 0, error: 1400]
/// </code>
/// <para>
/// The client decided another instance already owned the app, tried to hand the
/// launch over to that instance through a window message, and got Win32 error 1400,
/// <c>ERROR_INVALID_WINDOW_HANDLE</c>: the target window no longer existed.  The
/// exception is unhandled, so the process dies about 0.16 s in, before it ever asks
/// for an account.  Reproduced with the bare executable and no ticket, and
/// reproduced with this manager fully closed, so it is not caused by holding the
/// compatibility lock.  Closing every Roblox process, including
/// <c>RobloxCrashHandler</c>, cleared it and launching worked again.
/// </para>
/// <para>
/// One line is deliberately NOT treated as a failure:
/// <c>Cannot create event to secure single process: Unknown error code 0x00000006</c>.
/// That is <c>ERROR_INVALID_HANDLE</c> from Roblox trying to create an event over a
/// name that already exists as a mutex, and it appears in clients that go on to run
/// perfectly.  It is the compatibility lock working, not a crash.
/// </para>
/// </remarks>
public sealed class RobloxClientFailureReader
{
    /// <summary>The unhandled-exception marker Roblox writes before dying.</summary>
    private const string CrashMarker = "RBXCRASH";

    /// <summary>The source file of the single-instance handoff crash.</summary>
    private const string SingleInstanceMarker = "SingleInstanceGuard";

    private static readonly Regex Win32Error = new(
        @"\berror:\s*(?<code>\d{1,10})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExceptionClass = new(
        @"UnhandledException\s*\((?<what>[^)]{1,120})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The client's own verdict that it must replace itself.</summary>
    private const string UpdateRequiredMarker = "updateRequired TRUE";

    /// <summary>The line where it actually launches its installer.</summary>
    private const string UpdateStartMarker = "Update thread: start";

    /// <summary>The deployment the server says is current.</summary>
    private static readonly Regex CurrentDeployment = new(
        @"""clientVersionUpload""\s*:\s*""(?<id>version-[0-9a-f]{16})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Cap on how much of a log is read.  A crash is the last thing a process does,
    /// so the tail is what matters, but the cap keeps a huge log bounded.
    /// </summary>
    private const int MaxScanBytes = 8 * 1024 * 1024;

    /// <summary>How many trailing lines are kept for analysis.</summary>
    private const int TailLines = 240;

    /// <summary>
    /// Roblox creates the log a fraction of a second after the process starts; the
    /// negative side absorbs clock skew between the two readings.
    /// </summary>
    private static readonly TimeSpan StartsBefore = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after the process start a log may still belong to it.  A client
    /// that dies in the single-instance guard writes its log within a second, so
    /// this only needs to tolerate a slow disk.
    /// </summary>
    private static readonly TimeSpan StartsAfter = TimeSpan.FromSeconds(90);

    private readonly string _logsRoot;

    public RobloxClientFailureReader(string? robloxLocalRootOverride = null)
    {
        var root = robloxLocalRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox");
        _logsRoot = Path.Combine(root, "logs");
    }

    /// <summary>
    /// Returns the failure recorded by the client that started at
    /// <paramref name="processStartUtc"/>, or <see langword="null"/> when its log
    /// cannot be found or records no failure.
    /// </summary>
    /// <remarks>
    /// A null result means "the client left no reason", never "the client was
    /// fine".  Nothing here is inferred from the absence of a log.
    /// </remarks>
    public RobloxClientFailure? Read(DateTime processStartUtc)
    {
        if (processStartUtc == default || !Directory.Exists(_logsRoot))
        {
            return null;
        }

        FileInfo[] candidates;
        try
        {
            candidates = new DirectoryInfo(_logsRoot)
                .GetFiles("*_last.log", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var best = candidates
            .Where(file =>
                file.CreationTimeUtc >= processStartUtc - StartsBefore
                && file.CreationTimeUtc <= processStartUtc + StartsAfter)
            .OrderBy(file => Math.Abs(
                (file.CreationTimeUtc - processStartUtc).TotalMilliseconds))
            .FirstOrDefault();
        return best is null ? null : ReadFailureFromLog(best);
    }

    private static RobloxClientFailure? ReadFailureFromLog(FileInfo file)
    {
        var tail = ReadTail(file.FullName);
        if (tail is null)
        {
            return null;
        }

        // Checked before the crash markers because this is not a crash at all: the
        // client exits cleanly after handing the launch to its own installer, which
        // is why it used to be reported as "no reason recorded".
        if (ReadSelfUpdate(tail, file) is { } selfUpdate)
        {
            return selfUpdate;
        }

        var crashAt = tail.FindLastIndex(line =>
            line.Contains(CrashMarker, StringComparison.Ordinal));
        if (crashAt < 0)
        {
            return null;
        }

        // The message and the Win32 error land on the lines after the marker, so the
        // block is analysed together rather than line by line.
        var block = string.Join(" ", tail.Skip(crashAt).Take(8));
        var code = Win32Error.Match(block) is { Success: true } hit
                   && int.TryParse(hit.Groups["code"].Value, out var parsed)
            ? parsed
            : (int?)null;

        if (block.Contains(SingleInstanceMarker, StringComparison.Ordinal))
        {
            return new RobloxClientFailure(
                "single-instance-handoff",
                file.Name,
                code,
                RobloxMultiManager.Core.L.T("O cliente Roblox travou na trava de instância única dele mesmo. Ele ")
                + RobloxMultiManager.Core.L.T("concluiu que já existia outra instância dona do aplicativo e tentou ")
                + RobloxMultiManager.Core.L.T("repassar a abertura para a janela dessa instância, que não existe ")
                + RobloxMultiManager.Core.L.T("mais")
                + (code is null
                    ? ""
                    : RobloxMultiManager.Core.L.F(" (erro {0} do Windows: handle de janela inválido)",code))
                + RobloxMultiManager.Core.L.T(". Ele morre antes de pedir a conta, por isso não aparece erro de ")
                + RobloxMultiManager.Core.L.T("login. Medido nesta máquina: fechar todos os processos Roblox, ")
                + RobloxMultiManager.Core.L.T("inclusive o RobloxCrashHandler, limpa esse estado e a abertura ")
                + RobloxMultiManager.Core.L.T("volta a funcionar."),
                ClosingAllClientsMayFix: true);
        }

        var what = ExceptionClass.Match(block) is { Success: true } cls
            ? cls.Groups["what"].Value.Trim()
            : null;
        return new RobloxClientFailure(
            "crash",
            file.Name,
            code,
            RobloxMultiManager.Core.L.T("O cliente Roblox encerrou por uma exceção não tratada")
            + (what is null ? "" : $" ({what})")
            + (code is null ? "" : RobloxMultiManager.Core.L.F(", erro {0} do Windows",code))
            + RobloxMultiManager.Core.L.T(". O motivo está no log do próprio cliente, ")
            + $"{file.Name}.",
            ClosingAllClientsMayFix: false);
    }

    /// <summary>
    /// Detects the client replacing itself with the deployment the server calls
    /// current.
    /// </summary>
    /// <remarks>
    /// Both markers are required.  <c>updateRequired TRUE</c> alone is only a
    /// verdict; the launch of the installer is what actually ends this process, and
    /// demanding both keeps a client that decided to update but kept running from
    /// being reported as the cause of a failure.
    /// </remarks>
    private static RobloxClientFailure? ReadSelfUpdate(List<string> tail, FileInfo file)
    {
        var verdict = tail.FindLastIndex(line =>
            line.Contains(UpdateRequiredMarker, StringComparison.Ordinal));
        if (verdict < 0
            || !tail.Any(line => line.Contains(UpdateStartMarker, StringComparison.Ordinal)))
        {
            return null;
        }

        var target = CurrentDeployment.Match(string.Join(" ", tail)) is { Success: true } hit
            ? hit.Groups["id"].Value
            : null;
        return new RobloxClientFailure(
            "self-update",
            file.Name,
            Win32Error: null,
            RobloxMultiManager.Core.L.T("A versão que você fixou se substituiu sozinha. O cliente perguntou ao ")
            + RobloxMultiManager.Core.L.T("servidor da Roblox qual é a versão atual, viu que não é ele")
            + (target is null ? "" : RobloxMultiManager.Core.L.F(" e que a atual é {0}",target))
            + RobloxMultiManager.Core.L.T(", e executou o RobloxPlayerInstaller.exe da própria pasta, que abriu a ")
            + RobloxMultiManager.Core.L.T("versão atual com os mesmos argumentos e encerrou o processo fixado. ")
            + RobloxMultiManager.Core.L.T("Isso é o atualizador do Roblox, não este gerenciador: reproduz com o ")
            + RobloxMultiManager.Core.L.T("executável puro, sem conta e sem ticket. Medido nesta máquina: ")
            + RobloxMultiManager.Core.L.T("renomear o RobloxPlayerInstaller.exe de dentro da pasta da versão ")
            + RobloxMultiManager.Core.L.T("fixada faz a checagem falhar antes mesmo de consultar o servidor, e o ")
            + RobloxMultiManager.Core.L.T("cliente fixado permanece aberto."),
            ClosingAllClientsMayFix: false,
            ReplacedByDeployment: target,
            BlockingSelfUpdateMayFix: true);
    }

    /// <summary>
    /// Reads the last <see cref="TailLines"/> lines, bounded by
    /// <see cref="MaxScanBytes"/>.
    /// </summary>
    private static List<string>? ReadTail(string path)
    {
        try
        {
            // Shared read: the client may still hold its own log open.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>(TailLines);
            var budget = MaxScanBytes;
            while (reader.ReadLine() is { } line)
            {
                budget -= line.Length + 2;
                if (budget <= 0)
                {
                    break;
                }

                if (tail.Count == TailLines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            return [.. tail];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
