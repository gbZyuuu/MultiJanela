namespace RobloxMultiManager.Core;

/// <summary>
/// Whether a pinned deployment can still update itself out from under you.
/// </summary>
public enum SelfUpdateState
{
    /// <summary>The folder has no updater and no disabled updater: nothing to say.</summary>
    NoUpdaterPresent,

    /// <summary>The updater is present, so this version will replace itself.</summary>
    Allowed,

    /// <summary>The updater was renamed by this app, so the version stays put.</summary>
    Blocked
}

/// <summary>
/// Keeps a pinned Roblox deployment from replacing itself with the current one.
/// </summary>
/// <remarks>
/// <para>
/// The problem, measured end to end on a live machine.  With
/// <c>version-ddf602d9cfe44005</c> (0.734.0.7340917) selected, the app started
/// exactly that executable and logged <c>match=true</c>, and two seconds later a
/// process on <c>version-17d504d2c9544583</c> (0.735.3.7351147) existed instead.
/// The client's own log says why:
/// </para>
/// <code>
/// [FLog::UpdateController] Checking if updater exists at
///     ...\version-ddf602d9cfe44005\RobloxPlayerInstaller.exe. Returning true
/// [FLog::UpdateController] version response: {"version":"0.735.3.7351147",
///     "clientVersionUpload":"version-17d504d2c9544583"}
/// [FLog::UpdateController] Update check thread: updateRequired TRUE
/// [FLog::UpdateController] Update thread: start
///     ""...\version-ddf602d9cfe44005\RobloxPlayerInstaller.exe" ...
/// </code>
/// <para>
/// The client asks <c>clientsettingscdn.roblox.com</c> which deployment is current,
/// sees that it is not it, and runs the <c>RobloxPlayerInstaller.exe</c> sitting in
/// its own folder.  That installer relaunches from Temp, notes that
/// <c>version-17d504d2c9544583</c> is already installed, starts that client with the
/// original launch arguments, and the pinned process exits.  None of this involves
/// this manager: it reproduces with the bare executable and no ticket.
/// </para>
/// <para>
/// The lever is the first line.  The existence check happens BEFORE the version
/// query, so with the updater absent the client never even asks the server.
/// Measured immediately after the run above, with the file renamed:
/// </para>
/// <code>
/// [FLog::UpdateController] Checking if updater exists at ... Returning false
/// [FLog::UpdateController] Update check thread: updateRequired FALSE
/// </code>
/// <para>
/// The pinned client stayed alive and no second client appeared.  So the block is a
/// single reversible rename inside one version folder.  Nothing is patched, no code
/// is injected, no network traffic is touched, and every other installed version
/// keeps its own updater and its normal behaviour.
/// </para>
/// <para>
/// What this does NOT promise: a pinned client can still be refused by Roblox when
/// it tries to join a place, because that is a server-side decision this cannot
/// reach.  Blocking the self-update only stops the client from replacing itself on
/// startup.  Reinstalling or repairing that version folder also puts the updater
/// back, and then the block is simply gone.
/// </para>
/// </remarks>
public sealed class RobloxSelfUpdateBlocker
{
    /// <summary>The file the client looks for before deciding to update.</summary>
    public const string UpdaterFileName = "RobloxPlayerInstaller.exe";

    /// <summary>
    /// Suffix that marks the rename as this app's doing, so a folder is never
    /// mistaken for a broken install and the change is obvious to a human.
    /// </summary>
    public const string DisabledSuffix = ".multijanela-desativado";

    /// <summary>
    /// Reports whether the deployment in <paramref name="versionFolder"/> can
    /// replace itself.
    /// </summary>
    public SelfUpdateState Inspect(string versionFolder)
    {
        if (string.IsNullOrWhiteSpace(versionFolder) || !Directory.Exists(versionFolder))
        {
            return SelfUpdateState.NoUpdaterPresent;
        }

        if (File.Exists(Path.Combine(versionFolder, UpdaterFileName)))
        {
            return SelfUpdateState.Allowed;
        }

        return File.Exists(Path.Combine(versionFolder, UpdaterFileName + DisabledSuffix))
            ? SelfUpdateState.Blocked
            : SelfUpdateState.NoUpdaterPresent;
    }

    /// <summary>
    /// Renames the updater away so the deployment stops replacing itself.
    /// </summary>
    /// <returns>
    /// The resulting state, and a message describing what happened, suitable for the
    /// activity log.
    /// </returns>
    /// <remarks>
    /// Idempotent: calling this on an already blocked folder reports success without
    /// touching anything.
    /// </remarks>
    public (SelfUpdateState State, string Message) Block(string versionFolder)
    {
        var state = Inspect(versionFolder);
        if (state != SelfUpdateState.Allowed)
        {
            return (
                state,
                state == SelfUpdateState.Blocked
                    ? RobloxMultiManager.Core.L.T("A atualização automática desta versão já estava bloqueada.")
                    : RobloxMultiManager.Core.L.F("Esta versão não tem {0}, então não há o que ",UpdaterFileName)
                      + RobloxMultiManager.Core.L.T("bloquear."));
        }

        var from = Path.Combine(versionFolder, UpdaterFileName);
        var to = from + DisabledSuffix;
        try
        {
            if (File.Exists(to))
            {
                File.Delete(to);
            }

            File.Move(from, to);
            return (
                SelfUpdateState.Blocked,
                RobloxMultiManager.Core.L.F("{0} renomeado para {1}{2}. ",UpdaterFileName,UpdaterFileName,DisabledSuffix)
                + RobloxMultiManager.Core.L.T("Esta versão deixa de se substituir sozinha ao abrir."));
        }
        catch (Exception ex)
        {
            return (
                Inspect(versionFolder),
                RobloxMultiManager.Core.L.F("Não foi possível renomear {0}: ",UpdaterFileName)
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts the updater back, restoring Roblox's own behaviour.
    /// </summary>
    public (SelfUpdateState State, string Message) Unblock(string versionFolder)
    {
        var state = Inspect(versionFolder);
        if (state != SelfUpdateState.Blocked)
        {
            return (
                state,
                state == SelfUpdateState.Allowed
                    ? RobloxMultiManager.Core.L.T("A atualização automática desta versão já está liberada.")
                    : RobloxMultiManager.Core.L.F("Não há {0}{1} para restaurar.",UpdaterFileName,DisabledSuffix));
        }

        var from = Path.Combine(versionFolder, UpdaterFileName + DisabledSuffix);
        var to = Path.Combine(versionFolder, UpdaterFileName);
        try
        {
            File.Move(from, to);
            return (
                SelfUpdateState.Allowed,
                RobloxMultiManager.Core.L.F("{0} restaurado. Esta versão volta a se atualizar ",UpdaterFileName)
                + RobloxMultiManager.Core.L.T("sozinha, como o Roblox faz por padrão."));
        }
        catch (Exception ex)
        {
            return (
                Inspect(versionFolder),
                RobloxMultiManager.Core.L.F("Não foi possível restaurar {0}: ",UpdaterFileName)
                + $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
