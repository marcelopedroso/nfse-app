using System.Diagnostics;

namespace NfseApp;

/// <summary>Abre a página numa janela própria do Edge em modo aplicativo (sem barra de endereço nem abas).</summary>
public static class Janela
{
    private static readonly string[] CaminhosEdge =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    };

    public static string? CaminhoEdge() => CaminhosEdge.FirstOrDefault(File.Exists);

    /// <summary>Devolve o processo da janela, ou null se caiu no navegador padrão (sem Edge).</summary>
    public static Process? Abrir(string url)
    {
        if (CaminhoEdge() is not { } edge)
        {
            AbrirNavegador(url);
            return null;
        }
        // Perfil separado: a janela é um processo só nosso, e fechar ela não mexe no Edge normal do usuário.
        var perfil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NfseApp", Edicao.PerfilJanela);
        var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
        foreach (var a in new[] { $"--app={url}", $"--user-data-dir={perfil}", "--window-size=1280,860",
                     "--no-first-run", "--no-default-browser-check", "--disable-background-mode" })
            psi.ArgumentList.Add(a);
        try { return Process.Start(psi); }
        catch { AbrirNavegador(url); return null; }
    }

    private static void AbrirNavegador(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* sem navegador: nada a fazer */ }
    }
}
