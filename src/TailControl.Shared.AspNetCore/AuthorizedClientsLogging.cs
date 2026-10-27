using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TailControl.Shared.Security;

namespace TailControl.Shared.AspNetCore;

public static partial class AuthorizedClientsLogging
{
    /// <summary>
    /// Spune la pornire cati clienti sunt autorizati sau de ce fisierul nu poate fi citit.
    /// Un fisier lipsa sau invalid nu autorizeaza pe nimeni, iar fara mesajul asta toate
    /// cererile ar primi 401 fara explicatie.
    /// </summary>
    public static void LogAuthorizedClientsState(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IAuthorizedClientStore>() is not AuthorizedClientsFile file)
        {
            return;
        }

        var active = file.Current().Clients.Count(c => !c.IsRevoked);
        if (file.LastError is { } error)
        {
            LogUnreadable(app.Logger, file.Path, error);
        }
        else
        {
            LogLoaded(app.Logger, active, file.Path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} clienti autorizati activi ({Path})")]
    private static partial void LogLoaded(ILogger logger, int count, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lista de clienti autorizati nu poate fi citita ({Path}): {Error} Nicio cerere nu va fi acceptata.")]
    private static partial void LogUnreadable(ILogger logger, string path, string error);
}
