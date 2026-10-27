namespace TailControl.Agent;

/// <summary>Pasi de instalare apelati de installer (ca administrator).</summary>
public static class AgentSetup
{
    /// <summary>
    /// Creeaza <c>config\</c> (doar administratorii scriu, serviciul citeste) si <c>logs\</c>
    /// (serviciul scrie), cu ACL-uri protejate. Ruleaza la fiecare instalare/actualizare.
    /// </summary>
    public static int PrepareDirectories(AgentOptions options, bool isDevelopment, TextWriter output)
    {
        if (!isDevelopment && !Environment.IsPrivilegedProcess)
        {
            output.WriteLine("Eroare: comanda trebuie rulata ca administrator.");
            return 3;
        }

        if (isDevelopment)
        {
            Directory.CreateDirectory(options.ConfigDirectory);
            Directory.CreateDirectory(options.LogDirectory);
        }
        else
        {
            SecureDirectories.EnsureConfigDirectory(options.ConfigDirectory);
            SecureDirectories.EnsureLogDirectory(options.LogDirectory);
        }

        output.WriteLine($"Pregatit: {options.ConfigDirectory}");
        output.WriteLine($"Pregatit: {options.LogDirectory}");
        return 0;
    }
}
