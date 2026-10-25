namespace RobloxMultiManager.Core;

/// <summary>
/// Holds the compatibility mutex on one dedicated OS thread. Mutex ownership is
/// thread-affine, so acquisition and release must happen on the same thread.
/// </summary>
public sealed class MultiInstanceGuard : IAsyncDisposable
{
    public const string DefaultMutexName = "ROBLOX_singletonMutex";
    public const string DefaultEventName = "ROBLOX_singletonEvent";

    private readonly object _gate = new();
    private readonly string _mutexName;
    private readonly string? _eventName;
    private Thread? _thread;
    private ManualResetEventSlim? _stopSignal;
    private TaskCompletionSource<GuardResult>? _ready;
    private volatile GuardState _state = GuardState.Stopped;
    private volatile bool _ownsMutex;

    /// <summary>
    /// Whether the current activation insisted on an empty machine.
    /// </summary>
    private bool _requireNoPlayers;
    private bool _disposed;

    public MultiInstanceGuard(
        string mutexName = DefaultMutexName,
        string? eventName = null)
    {
        _mutexName = mutexName;
        _eventName = eventName
                     ?? (mutexName == DefaultMutexName ? DefaultEventName : null);
    }

    public GuardState State => _state;

    /// <summary>
    /// Whether the guard is holding the compatibility names.
    /// </summary>
    /// <remarks>
    /// Ownership of the mutex used to be part of this condition, and that was the
    /// reason restarting the manager while a client was open demanded that the
    /// client be closed.  Measured on this machine with one client running:
    /// <c>ROBLOX_singletonMutex</c> exists as a Mutex already owned by that client,
    /// and <c>ROBLOX_singletonEvent</c> exists as an Event.  A mutex cannot be taken
    /// from its owner, but ownership is not what the client consults either: what
    /// matters is the shape of those names when the NEXT client starts.  Holding a
    /// live handle is therefore the useful state, and <see cref="OwnsMutex"/>
    /// reports the ownership separately instead of gating everything on it.
    /// </remarks>
    public bool IsActive => _state == GuardState.Active;

    /// <summary>
    /// Whether this process is the owner of the compatibility mutex, as opposed to
    /// merely holding a handle to it.
    /// </summary>
    public bool OwnsMutex => _ownsMutex;

    /// <summary>
    /// Reports what a named Windows object currently is, without changing it.
    /// </summary>
    /// <remarks>
    /// Exposed because the object TYPE behind these names is the thing that decides
    /// whether a second client opens, and it was previously only visible from a
    /// test.  Putting it in the diagnostic report means the next occurrence carries
    /// its own evidence instead of needing a separate probe.
    /// </remarks>
    public static string DescribeNamedObject(string name)
    {
        try
        {
            using var asEvent = EventWaitHandle.OpenExisting(name);
            return "Event";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            return RobloxMultiManager.Core.L.T("existe, sem permissão de acesso");
        }

        try
        {
            using var asMutex = Mutex.OpenExisting(name);
            bool owned;
            try
            {
                owned = asMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                return RobloxMultiManager.Core.L.T("Mutex abandonado");
            }

            if (!owned)
            {
                return RobloxMultiManager.Core.L.T("Mutex de outro processo");
            }

            asMutex.ReleaseMutex();
            return RobloxMultiManager.Core.L.T("Mutex livre");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return RobloxMultiManager.Core.L.T("não existe");
        }
        catch (UnauthorizedAccessException)
        {
            return RobloxMultiManager.Core.L.T("existe, sem permissão de acesso");
        }
    }

    public event EventHandler<GuardResult>? StateChanged;

    public Task<GuardResult> StartAsync(bool requireNoRobloxProcesses = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsActive)
            {
                return Task.FromResult(new GuardResult(
                    GuardState.Active,
                    RobloxMultiManager.Core.L.T("Modo multi-instância já está ativo.")));
            }

            if (_thread?.IsAlive == true)
            {
                return _ready?.Task ?? Task.FromResult(new GuardResult(
                    _state,
                    RobloxMultiManager.Core.L.T("A ativação já está em andamento.")));
            }

            if (requireNoRobloxProcesses && RobloxProcessMonitor.CountPlayerProcesses() > 0)
            {
                return PublishAndReturn(new GuardResult(
                    GuardState.PlayersAlreadyRunning,
                    RobloxMultiManager.Core.L.T("Feche todas as janelas e processos do Roblox antes de ativar.")));
            }

            _requireNoPlayers = requireNoRobloxProcesses;
            _state = GuardState.Starting;
            _stopSignal?.Dispose();
            _stopSignal = new ManualResetEventSlim(false);
            _ready = new TaskCompletionSource<GuardResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(GuardThreadMain)
            {
                IsBackground = true,
                Name = "RobloxMultiManager.MutexGuard"
            };
            _thread.Start();
            return _ready.Task;
        }
    }

    /// <summary>
    /// Occupies the singleton name with a Mutex, which is the shape that lets a
    /// second client start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order here matters and it used to be backwards.  The client's own
    /// singleton check creates an Event over this name; when that succeeds or
    /// reports "already exists", the client concludes another instance owns the
    /// application and tries to hand the launch over through a window message.  That
    /// handoff is the measured <c>SingleInstanceGuard.cpp(131)</c> crash with
    /// <c>error: 1400</c> (ERROR_INVALID_WINDOW_HANDLE) that
    /// <see cref="RobloxClientFailureReader"/> reports.
    /// </para>
    /// <para>
    /// What makes a second client run instead is the name existing as the WRONG
    /// kind of object: the client's create then fails with
    /// <c>ERROR_INVALID_HANDLE</c> and it logs <c>Cannot create event to secure
    /// single process: Unknown error code 0x00000006</c> and carries on.  That line
    /// is documented as benign and appears in sessions that run perfectly.
    /// </para>
    /// <para>
    /// So a Mutex is attempted first.  The previous version tried an
    /// <see cref="EventWaitHandle"/> first, and measured on this machine
    /// (<c>ROBLOX_singletonEvent -&gt; Event</c> while a client ran) that meant the
    /// guard opened the client's own Event and held it alive — keeping the exact
    /// object shape that stops the next client from opening, even after the client
    /// that created it exited.  A foreign Event is therefore deliberately NOT held:
    /// letting it die frees the name, and the caller re-attempts as soon as the
    /// process list is empty.
    /// </para>
    /// </remarks>
    private static (WaitHandle? Handle, string? Note) HoldSingletonName(string name)
    {
        try
        {
            var mutex = new Mutex(false, name, out var createdNew);
            return (
                mutex,
                createdNew
                    ? null
                    : RobloxMultiManager.Core.L.F("'{0}' já existia como mutex; o gerenciador apenas o ",name)
                      + RobloxMultiManager.Core.L.T("mantém vivo, sem tomar a posse."));
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The name exists with another type, which for this name means the
            // client created its Event and is still running.
        }
        catch (UnauthorizedAccessException)
        {
            return (null, RobloxMultiManager.Core.L.F("sem permissão para ocupar '{0}'.",name));
        }

        return (
            null,
            RobloxMultiManager.Core.L.F("'{0}' existe como {1}, criado pelo Roblox ",name,DescribeNamedObject(name))
            + RobloxMultiManager.Core.L.T("que já está aberto. Não é possível trocar o tipo de um objeto ")
            + RobloxMultiManager.Core.L.T("existente, e manter um handle nele conservaria justamente o estado ")
            + RobloxMultiManager.Core.L.T("que impede a próxima janela de abrir. O nome será ocupado assim que ")
            + RobloxMultiManager.Core.L.T("esse cliente fechar."));
    }

    private void GuardThreadMain()
    {
        Mutex? mutex = null;
        WaitHandle? singletonEvent = null;
        var owns = false;
        var createdNew = false;
        var abandoned = false;
        string? aliasNote = null;

        try
        {
            if (_eventName is not null)
            {
                // Never fatal: failing to occupy this name degrades multi-window,
                // but the app still has to run so the user can see why.
                (singletonEvent, aliasNote) = HoldSingletonName(_eventName);
            }

            try
            {
                mutex = new Mutex(false, _mutexName, out createdNew);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Publish(new GuardResult(
                    GuardState.Contested,
                    RobloxMultiManager.Core.L.F("O nome '{0}' já existe no Windows como outro tipo ",_mutexName)
                    + RobloxMultiManager.Core.L.T("de objeto, provavelmente criado por outro gerenciador de ")
                    + RobloxMultiManager.Core.L.T("múltiplas instâncias. Feche esse programa (ou reinicie o ")
                    + RobloxMultiManager.Core.L.T("Windows) e ative novamente.")));
                return;
            }

            try
            {
                owns = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owns = true;
                abandoned = true;
            }

            // Not owning is no longer a dead end.
            //
            // Measured on this machine with one client running: this name exists as
            // a Mutex already owned by that client. Windows will not take a mutex
            // from its owner, and the old code turned that into "close the Roblox
            // you already have open". But ownership is not what the client consults
            // when it starts; the handle opened above is what keeps the name alive
            // once the current owner exits, and that is the part that matters for
            // the next client. So the guard proceeds and reports the shared state
            // instead of refusing.
            if (!owns)
            {
                aliasNote = aliasNote is null
                    ? RobloxMultiManager.Core.L.T("a posse do mutex pertence ao Roblox já aberto; o gerenciador ")
                      + RobloxMultiManager.Core.L.T("mantém o nome vivo sem tomá-la.")
                    : aliasNote
                      + RobloxMultiManager.Core.L.T(" A posse do mutex também pertence ao Roblox já aberto.");
            }

            // Only the caller that asked for an empty machine gets this check.
            //
            // It used to run unconditionally, and that was the reason restarting the
            // manager with clients already open demanded that they be closed. The
            // demand was never a Windows limitation: measured on a live machine with
            // a client running and this manager holding nothing,
            // ROBLOX_singletonMutex did not exist at all, so the name was free the
            // whole time. The client only consults the singleton while it starts, so
            // taking the name afterwards takes nothing away from it and is exactly
            // what lets the NEXT client open.
            if (_requireNoPlayers
                && _mutexName == DefaultMutexName
                && RobloxProcessMonitor.CountPlayerProcesses() > 0)
            {
                Publish(new GuardResult(
                    GuardState.PlayersAlreadyRunning,
                    RobloxMultiManager.Core.L.T("Um processo do Roblox apareceu durante a ativação. Feche-o e tente novamente.")));
                return;
            }

            _ownsMutex = owns;
            var baseMessage = abandoned
                ? RobloxMultiManager.Core.L.T("Modo multi-instância ativo; mutex recuperado e singletonEvent ocupado.")
                : singletonEvent is null
                    ? RobloxMultiManager.Core.L.T("Multi-janela indisponível: abrir outra janela fecharia a que já está aberta.")
                    : owns
                        ? RobloxMultiManager.Core.L.T("Modo multi-instância ativo: mutex + singletonEvent ocupados. Mantenha o app aberto.")
                        : RobloxMultiManager.Core.L.T("Modo multi-instância ativo em modo compartilhado. Mantenha o app aberto.");
            Publish(new GuardResult(
                GuardState.Active,
                aliasNote is null
                    ? baseMessage
                    : RobloxMultiManager.Core.L.F("{0} Observação: {1}",baseMessage,aliasNote),
                createdNew,
                abandoned,
                OwnsMutex: owns,
                // Degraded means the singleton name does not have the shape the
                // next client needs. Missing mutex ownership alone is not degraded:
                // the handle still keeps the name alive.
                Degraded: _eventName is not null && singletonEvent is null));

            _stopSignal!.Wait();
        }
        catch (Exception ex)
        {
            Publish(new GuardResult(
                GuardState.Faulted,
                RobloxMultiManager.Core.L.F("Não foi possível manter a trava: {0}: {1}",ex.GetType().Name,ex.Message)));
        }
        finally
        {
            // Only a mutex this thread actually acquired may be released; calling
            // ReleaseMutex on a handle we merely opened throws.
            if (owns && mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Ownership was already lost. State still becomes stopped.
                }
            }

            _ownsMutex = false;
            mutex?.Dispose();
            singletonEvent?.Dispose();

            if (_state == GuardState.Active)
            {
                Publish(new GuardResult(
                    GuardState.Stopped,
                    RobloxMultiManager.Core.L.T("Modo multi-instância desativado.")));
            }
        }
    }

    public async Task StopAsync()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            if (thread is null)
            {
                _state = GuardState.Stopped;
                return;
            }

            _stopSignal?.Set();
        }

        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        lock (_gate)
        {
            _thread = null;
            _ready = null;
        }
    }

    private Task<GuardResult> PublishAndReturn(GuardResult result)
    {
        Publish(result);
        return Task.FromResult(result);
    }

    private void Publish(GuardResult result)
    {
        _state = result.State;
        _ready?.TrySetResult(result);
        StateChanged?.Invoke(this, result);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _stopSignal?.Dispose();
        _disposed = true;
    }
}
