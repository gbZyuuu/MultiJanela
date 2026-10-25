using System.Diagnostics.CodeAnalysis;

namespace RobloxMultiManager.Core;

public sealed record SavedLaunchDestination(Guid Id, string Name, string Link)
{
    public static bool TryCreate(string name, string link, Guid? id,
        [NotNullWhen(true)] out SavedLaunchDestination? destination, out string error)
    {
        destination = null;
        name = name.Trim(); link = link.Trim();
        if (name.Length is 0 or > 80)
        { error = RobloxMultiManager.Core.L.T("Informe um nome de até 80 caracteres."); return false; }
        if (!RobloxLinkParser.TryParse(link, out _, out error)) return false;
        if (id == Guid.Empty) { error = RobloxMultiManager.Core.L.T("O destino salvo possui identificação inválida."); return false; }
        destination = new(id ?? Guid.NewGuid(), name, link);
        error = string.Empty;
        return true;
    }
}
