using System.Text.Json;

namespace NfseApp;

public record NovaVersao(string Versao, string Notas, string Url);

/// <summary>
/// Aviso de versão nova: consulta a última versão publicada no GitHub. O app nunca baixa o .exe: a pessoa baixa pelo
/// navegador e abre o arquivo, que se instala no lugar da cópia antiga (Instalacao).
/// </summary>
public static class Atualizacao
{
    private const string Ultima = "https://api.github.com/repos/marcelopedroso/nfse-app/releases/latest";
    private const string PrefixoDownload = "https://github.com/marcelopedroso/nfse-app/releases/download/";

    // Testes: NFSEAPP_ATUALIZACAO é um JSON local no formato da API; NFSEAPP_VERSAO_ATUAL faz o app se achar antigo.
    private static string? ArquivoDeTeste => Environment.GetEnvironmentVariable("NFSEAPP_ATUALIZACAO") is { Length: > 0 } a ? a : null;
    private static Version Atual => Normalizar(Version.Parse(
        Environment.GetEnvironmentVariable("NFSEAPP_VERSAO_ATUAL") is { Length: > 0 } v ? v : Edicao.Versao));

    /// <summary>Nome do .exe desta edição nas versões publicadas e na pasta de instalação.</summary>
    public static string ArquivoDaEdicao => Edicao.TemHomologacao ? "NfseApp-Teste.exe" : "NfseApp.exe";

    /// <summary>O que "--versao" escreve: os testes conferem a cópia instalada com isto.</summary>
    public static string Identificacao(string versao) => $"{Instancia.Assinatura} {versao} {ArquivoDaEdicao}";

    private static (DateTime Quando, NovaVersao? Nova)? _consulta;

    /// <summary>Versão publicada mais nova que esta, ou null. Guarda a resposta por uma hora (a API do GitHub limita consultas).</summary>
    public static async Task<NovaVersao?> Verificar(bool forcar = false)
    {
        if (!forcar && _consulta is { } c && DateTime.Now - c.Quando < TimeSpan.FromHours(1)) return c.Nova;
        string json;
        if (ArquivoDeTeste is { } arquivo) json = await File.ReadAllTextAsync(arquivo);
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Instancia.Assinatura}/{Edicao.Versao}");
            json = await http.GetStringAsync(Ultima);
        }
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        NovaVersao? nova = null;
        if (Version.TryParse((raiz.GetProperty("tag_name").GetString() ?? "").TrimStart('v'), out var v) && Normalizar(v) > Atual)
            foreach (var a in raiz.GetProperty("assets").EnumerateArray())
                // Só o .exe desta edição e só do repositório do app: é o endereço que a pessoa vai abrir no navegador.
                if (a.GetProperty("name").GetString() == ArquivoDaEdicao
                    && a.GetProperty("browser_download_url").GetString() is { } url && url.StartsWith(PrefixoDownload))
                    nova = new NovaVersao(Normalizar(v).ToString(3), raiz.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", url);
        _consulta = (DateTime.Now, nova);
        return nova;
    }

    public static Version Normalizar(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
