using System.Text.Json;

namespace NfseApp;

public record Configuracao(decimal? PTotTribSN);

/// <summary>
/// Cópia local das notas do contribuinte, em %LOCALAPPDATA%\NfseApp\{documento}\{ambiente}\.
/// A fonte da verdade é o ADN; aqui só fica o que já foi baixado ou emitido por este app.
/// </summary>
public class Repositorio
{
    private static readonly SemaphoreSlim TravaSync = new(1, 1);

    private readonly string _raizDoc;
    private readonly string _dir;

    // NFSEAPP_DADOS troca a pasta (os testes automatizados rodam numa pasta temporária, sem tocar nos dados reais).
    public static string PastaRaiz =>
        Environment.GetEnvironmentVariable("NFSEAPP_DADOS") is { Length: > 0 } pasta
            ? pasta
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NfseApp");

    public Repositorio(string documento, Ambiente amb)
    {
        _raizDoc = Path.Combine(PastaRaiz, documento);
        _dir = Path.Combine(_raizDoc, amb.Id);
        Directory.CreateDirectory(Path.Combine(_dir, "nfse"));
        Directory.CreateDirectory(Path.Combine(_dir, "eventos"));
        Directory.CreateDirectory(Path.Combine(_dir, "dps"));
    }

    private string ArquivoEstado => Path.Combine(_dir, "estado.json");
    private string ArquivoConfig => Path.Combine(_raizDoc, "config.json");

    public long UltimoNsu =>
        File.Exists(ArquivoEstado) ? JsonDocument.Parse(File.ReadAllText(ArquivoEstado)).RootElement.GetProperty("ultimoNsu").GetInt64() : 0;

    /// <summary>Baixa do ADN tudo que chegou depois do último NSU guardado. Devolve quantos documentos novos vieram.</summary>
    public async Task<int> Sincronizar(ApiNacional api, Ambiente amb, string cnpj)
    {
        await TravaSync.WaitAsync();
        try
        {
            var nsu = UltimoNsu;
            var novos = 0;
            for (var pagina = 0; pagina < 200; pagina++)
            {
                var docs = await api.BaixarDfe(amb, cnpj, nsu);
                if (docs.Count == 0) break;
                foreach (var d in docs)
                {
                    var pasta = d.Tipo == "NFSE" ? "nfse" : "eventos";
                    var nome = d.Tipo == "NFSE" ? $"{d.Chave}.xml" : $"{d.Nsu:D6}_{d.Chave}.xml";
                    File.WriteAllText(Path.Combine(_dir, pasta, nome), d.Xml);
                    novos++;
                }
                var maior = docs.Max(d => d.Nsu);
                if (maior <= nsu) break;
                nsu = maior;
                File.WriteAllText(ArquivoEstado, JsonSerializer.Serialize(new { ultimoNsu = nsu }));
            }
            return novos;
        }
        finally { TravaSync.Release(); }
    }

    public List<Nota> Todas()
    {
        var cancelamentos = new Dictionary<string, string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_dir, "eventos"), "*.xml"))
            if (Notas.LerCancelamento(File.ReadAllText(f)) is { } c) cancelamentos[c.Chave] = c.Motivo;

        return Directory.EnumerateFiles(Path.Combine(_dir, "nfse"), "*.xml")
            .Select(f => Notas.Ler(File.ReadAllText(f), cancelamentos))
            .OrderByDescending(n => n.DhProc)
            .ToList();
    }

    /// <summary>Marca d'água do DANFSe (NT 008, item 2.5): CANCELADA (e101101) ou SUBSTITUÍDA (e105102).</summary>
    public string? MarcaDagua(string chave)
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_dir, "eventos"), $"*_{Path.GetFileName(chave)}.xml"))
        {
            var xml = File.ReadAllText(f);
            if (xml.Contains("<e105102")) return "SUBSTITUÍDA";
            if (xml.Contains("<e101101")) return "CANCELADA";
        }
        return null;
    }

    public string? Xml(string chave)
    {
        var f = Path.Combine(_dir, "nfse", $"{Path.GetFileName(chave)}.xml");
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    /// <summary>Guarda na hora a DPS enviada e a NFS-e devolvida: o ADN demora alguns minutos para distribuí-la.</summary>
    public void GuardarEmissao(string idDps, string dpsAssinada, string? chave, string? nfseXml)
    {
        File.WriteAllText(Path.Combine(_dir, "dps", $"{idDps}.xml"), dpsAssinada);
        if (chave != null && nfseXml != null) GuardarNfse(chave, nfseXml);
    }

    public void GuardarNfse(string chave, string nfseXml) =>
        File.WriteAllText(Path.Combine(_dir, "nfse", $"{chave}.xml"), nfseXml);

    public Configuracao Config =>
        File.Exists(ArquivoConfig) ? JsonSerializer.Deserialize<Configuracao>(File.ReadAllText(ArquivoConfig)) ?? new(null) : new(null);

    public void SalvarConfig(Configuracao c) => File.WriteAllText(ArquivoConfig, JsonSerializer.Serialize(c));
}
