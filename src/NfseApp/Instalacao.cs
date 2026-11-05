using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NfseApp;

/// <summary>
/// Atualização local: abrir um .exe mais novo oferece instalá-lo no lugar da cópia instalada (a que o atalho abre).
/// Só cópias de arquivo: a instalada vira backup ({nome}.anterior.exe) e este .exe é copiado por cima dela, que não pode
/// estar aberta. Nada é apagado nem renomeado, e o app nunca baixa .exe sozinho.
/// </summary>
public static class Instalacao
{
    // Pasta da cópia instalada (o atalho da área de trabalho aponta para ela). NFSEAPP_INSTALACAO troca a pasta nos testes.
    private static string Pasta => Environment.GetEnvironmentVariable("NFSEAPP_INSTALACAO") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NfseApp", "bin");
    private static string Alvo => Path.Combine(Pasta, Atualizacao.ArquivoDaEdicao);
    private static string Backup => Path.Combine(Pasta, Path.GetFileNameWithoutExtension(Atualizacao.ArquivoDaEdicao) + ".anterior.exe");
    private static Version Esta => Atualizacao.Normalizar(Version.Parse(Edicao.Versao));

    private enum Situacao { PodeInstalar, SemInstalacao, JaAtualizada, MaisNovaInstalada }

    // A versão da instalada vem do recurso de versão do arquivo: não precisa executar nada.
    private static (Situacao, Version?) Avaliar()
    {
        if (Environment.ProcessPath is not { } eu || !File.Exists(Alvo)) return (Situacao.SemInstalacao, null);
        if (string.Equals(Path.GetFullPath(eu), Path.GetFullPath(Alvo), StringComparison.OrdinalIgnoreCase)) return (Situacao.JaAtualizada, Esta);
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(Alvo).FileVersion, out var lida)) return (Situacao.SemInstalacao, null);
        var instalada = Atualizacao.Normalizar(lida);
        return instalada == Esta ? (Situacao.JaAtualizada, instalada)
            : instalada > Esta ? (Situacao.MaisNovaInstalada, instalada)
            : (Situacao.PodeInstalar, instalada);
    }

    // O Windows não deixa abrir com exclusividade um .exe que está rodando.
    private static bool AlvoAberto()
    {
        try
        {
            using (File.Open(Alvo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // sem permissão: a cópia falha e diz por quê
    }

    // Instalada -> backup; este .exe -> instalada; confere. Se algo falhar, a instalada volta do backup. Null = deu certo.
    private static string? Copiar()
    {
        var eu = Environment.ProcessPath!;
        try
        {
            File.Copy(Alvo, Backup, overwrite: true);
            try
            {
                File.Copy(eu, Alvo, overwrite: true);
                if (!MesmoConteudo(eu, Alvo)) throw new IOException("a cópia instalada não confere com o arquivo novo.");
                return null;
            }
            catch
            {
                File.Copy(Backup, Alvo, overwrite: true);
                throw;
            }
        }
        catch (Exception e) { return e.Message; }
    }

    private static bool MesmoConteudo(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// <summary>
    /// "--instalar" (testes): sem perguntas e sem abrir o app. Escreve o resultado numa linha e devolve o código de saída.
    /// Aqui só o arquivo aberto conta como "em uso" (nos testes, a cópia do dia a dia pode estar aberta na porta dela).
    /// </summary>
    public static int PorLinhaDeComando()
    {
        var (situacao, instalada) = Avaliar();
        switch (situacao)
        {
            case Situacao.SemInstalacao: Console.WriteLine("SEM_INSTALACAO"); return 2;
            case Situacao.JaAtualizada: Console.WriteLine("JA_ATUALIZADO"); return 3;
            case Situacao.MaisNovaInstalada: Console.WriteLine($"MAIS_NOVA_INSTALADA {instalada!.ToString(3)}"); return 5;
        }
        if (AlvoAberto()) { Console.WriteLine("EM_USO"); return 4; }
        if (Copiar() is { } erro) { Console.WriteLine($"FALHA {erro}"); return 1; }
        Console.WriteLine($"INSTALADO {Esta.ToString(3)} (antes {instalada!.ToString(3)})");
        return 0;
    }

    /// <summary>
    /// Aberto à mão um .exe mais novo que o instalado: pergunta se instala no lugar dele e, se sim, abre o instalado.
    /// true = este processo deve sair (instalou e abriu o instalado, ou a pessoa desistiu com o app antigo aberto).
    /// </summary>
    public static async Task<bool> Oferecer()
    {
        var (situacao, instalada) = Avaliar();
        if (situacao != Situacao.PodeInstalar) return false;
        var nova = Esta.ToString(3);
        var antiga = instalada!.ToString(3);
        if (Caixa($"Instalar o NFS-e {nova} no lugar da versão {antiga}?\n\nO atalho da área de trabalho passa a abrir a versão nova. " +
                  "Suas notas e configurações continuam como estão.", MB_YESNO | MB_ICONQUESTION) != IDYES)
            return false;
        // Também não pode haver outra cópia desta edição aberta na porta dela: o instalado só abriria uma janela para ela.
        while (AlvoAberto() || await Instancia.JaAberta(Edicao.Porta))
            if (Caixa("O NFS-e está aberto. Feche a janela dele e clique em Tentar novamente.\n\n" +
                      $"Se a janela já estiver fechada, finalize \"{Path.GetFileNameWithoutExtension(Atualizacao.ArquivoDaEdicao)}\" no Gerenciador de Tarefas.",
                      MB_RETRYCANCEL | MB_ICONINFORMATION) != IDRETRY)
                return true;
        if (Copiar() is { } erro)
        {
            Caixa($"Não consegui instalar a versão nova: {erro}\n\nA versão {antiga} continua instalada.", MB_OK | MB_ICONERROR);
            return true;
        }
        Process.Start(new ProcessStartInfo(Alvo) { UseShellExecute = false, WorkingDirectory = Pasta });
        return true;
    }

    private const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_RETRYCANCEL = 0x5, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20,
        MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    private const int IDYES = 6, IDRETRY = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr janela, string texto, string titulo, uint tipo);

    // Caixa de mensagem do próprio Windows: a instalação acontece antes de o app abrir a janela dele.
    private static int Caixa(string texto, uint tipo) => MessageBoxW(IntPtr.Zero, texto, Edicao.Nome, tipo | MB_SETFOREGROUND | MB_TOPMOST);
}
