using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RobloxMultiManager.Core;

public sealed record RobloxGraphicsPreferences(int FpsCap, int Quality);

public sealed class RobloxGraphicsSettingsService(string? settingsFile = null, Func<bool>? isRunning = null)
{
    private readonly SemaphoreSlim _launchGate = new(1);
    private readonly string _path = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");
    private readonly Func<bool> _isRunning = isRunning ?? (() =>
    {
        var clients = Process.GetProcessesByName("RobloxPlayerBeta");
        try { return clients.Length > 0; } finally { foreach (var p in clients) p.Dispose(); }
    });
    private static void Validate(RobloxGraphicsPreferences value)
    { if (value.FpsCap is not (-1 or 30 or 60 or 120 or 144 or 165 or 240) || value.Quality is < 0 or > 10) throw new ArgumentException(L.T("Selecione FPS e qualidade válidos.")); }
    private XDocument Read()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length > 2 * 1024 * 1024 || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(L.T("O arquivo oficial de preferências gráficas não está disponível."));
        using var reader = XmlReader.Create(_path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static XElement Properties(XDocument document) => document.Descendants("Item")
        .FirstOrDefault(e => (string?)e.Attribute("class") == "UserGameSettings")?.Element("Properties")
        ?? throw new InvalidDataException(L.T("O formato das preferências do Roblox não foi reconhecido."));
    public RobloxGraphicsPreferences ReadCurrent()
    {
        var values = Properties(Read());
        int Get(string name, int fallback) => int.TryParse(values.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name)?.Value, out var value) ? value : fallback;
        return new(Get("FramerateCap", -1), Get("SavedQualityLevel", 0));
    }
    public async Task ApplyAsync(RobloxGraphicsPreferences preferences)
    {
        Validate(preferences);
        if (_isRunning()) throw new InvalidOperationException(L.T("Feche os jogos antes de aplicar FPS e qualidade globais. As preferências serão usadas nas próximas aberturas."));
        var document = Read(); var properties = Properties(document);
        void Set(string type, string name, int value)
        {
            var property = properties.Elements().SingleOrDefault(e => (string?)e.Attribute("name") == name);
            if (property is null || property.Name != type) throw new InvalidDataException(L.T("O formato das preferências do Roblox mudou. Nenhum valor foi alterado."));
            property.Value = value.ToString(CultureInfo.InvariantCulture);
        }
        Set("int", "FramerateCap", preferences.FpsCap);
        Set("token", "SavedQualityLevel", preferences.Quality);
        if (_isRunning()) throw new InvalidOperationException(L.T("Um cliente do Roblox foi aberto. Feche-o antes de aplicar estas preferências."));
        var backupRoot = Path.Combine(Path.GetDirectoryName(_path)!, "MultiJanelaGraphicsBackups"); Directory.CreateDirectory(backupRoot);
        if ((File.GetAttributes(backupRoot) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        File.Copy(_path, Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".xml"), false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await document.SaveAsync(output, SaveOptions.DisableFormatting, CancellationToken.None);
            if (_isRunning()) throw new InvalidOperationException(L.T("Um cliente do Roblox foi aberto. Feche-o antes de aplicar estas preferências."));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Stages shared native preferences for a serialized launch, then restores only if no other writer changed them.</summary>
    public async Task<IAsyncDisposable> BeginLaunchAsync(RobloxGraphicsPreferences preferences, CancellationToken token = default)
    {
        Validate(preferences); await _launchGate.WaitAsync(token);
        try
        {
            var document = Read(); var original = await SecureJsonFiles.ReadAllBytesAsync(_path, token);
            // Read the exact snapshot being backed up; never write over a concurrent preference update.
            using var input = new MemoryStream(original);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var properties = Properties(document);
            foreach (var (name, type, value) in new[] { ("FramerateCap", "int", preferences.FpsCap), ("SavedQualityLevel", "token", preferences.Quality) })
            {
                var property = properties.Elements().SingleOrDefault(e => (string?)e.Attribute("name") == name);
                if (property is null || property.Name != type) throw new InvalidDataException(L.T("O formato das preferências do Roblox mudou. Nenhum valor foi alterado."));
                property.Value = value.ToString(CultureInfo.InvariantCulture);
            }
            var backupRoot = Path.Combine(Path.GetDirectoryName(_path)!, "MultiJanelaGraphicsBackups");
            ManagerUpdateService.RejectRedirects(backupRoot); Directory.CreateDirectory(backupRoot);
            await SecureJsonFiles.WriteAllBytesAsync(Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".xml"), original, token);
            using var staged = new MemoryStream(); document.Save(staged, SaveOptions.DisableFormatting); var applied = staged.ToArray();
            if (!(await SecureJsonFiles.ReadAllBytesAsync(_path, token)).SequenceEqual(original)) throw new IOException(L.T("As preferências do Roblox mudaram durante a preparação. Tente abrir novamente."));
            await ReplaceAsync(applied, token);
            return new LaunchLease(async () =>
            {
                try
                {
                    if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(_path).Length <= 2 * 1024 * 1024
                        && (await SecureJsonFiles.ReadAllBytesAsync(_path)).SequenceEqual(applied)) await ReplaceAsync(original, CancellationToken.None);
                }
                finally { _launchGate.Release(); }
            });
        }
        catch { _launchGate.Release(); throw; }
    }
    private async Task ReplaceAsync(byte[] data, CancellationToken token)
    {
        ManagerUpdateService.RejectRedirects(_path);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllBytesAsync(temporary, data, token); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class LaunchLease(Func<Task> release) : IAsyncDisposable
    {
        private Func<Task>? _release = release;
        public async ValueTask DisposeAsync() { var action = Interlocked.Exchange(ref _release, null); if (action is not null) await action(); }
    }

}
