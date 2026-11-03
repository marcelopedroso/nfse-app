using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NfseApp;

public record DocDistribuido(long Nsu, string Chave, string Tipo, string Xml);

public record ResultadoEmissao(bool Ok, int Status, string? Chave, string? NfseXml, List<string> Erros, List<string> Alertas, string Bruto);

/// <summary>Cliente das APIs do Sistema Nacional NFS-e (Sefin e ADN), autenticado por mTLS com o certificado do usuário.</summary>
public class ApiNacional
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clientes = new();

    private readonly HttpClient _http;

    public ApiNacional(X509Certificate2 cert)
    {
        _http = Clientes.GetOrAdd(cert.Thumbprint, _ =>
        {
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            handler.ClientCertificates.Add(cert);
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        });
    }

    /// <summary>Documentos distribuídos ao CNPJ com NSU maior que <paramref name="ultimoNsu"/> (lotes de até 50). Vazio = acabou.</summary>
    public async Task<List<DocDistribuido>> BaixarDfe(Ambiente amb, string cnpj, long ultimoNsu)
    {
        var url = $"{amb.Adn}/contribuintes/DFe/{ultimoNsu}?cnpjConsulta={cnpj}&lote=true";
        using var resp = await ComRetentativa(() => _http.GetAsync(url));
        if (resp.StatusCode == HttpStatusCode.NotFound) return new();
        var corpo = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"ADN respondeu {(int)resp.StatusCode}: {Resumir(corpo)}");

        using var json = JsonDocument.Parse(corpo);
        var docs = new List<DocDistribuido>();
        if (!json.RootElement.TryGetProperty("LoteDFe", out var lote)) return docs;
        foreach (var d in lote.EnumerateArray())
            docs.Add(new DocDistribuido(
                d.GetProperty("NSU").GetInt64(),
                d.GetProperty("ChaveAcesso").GetString() ?? "",
                d.GetProperty("TipoDocumento").GetString() ?? "",
                Descompactar(d.GetProperty("ArquivoXml").GetString() ?? "")));
        return docs;
    }

    public async Task<ResultadoEmissao> Emitir(Ambiente amb, string dpsAssinada)
    {
        var corpoEnvio = JsonSerializer.Serialize(new { dpsXmlGZipB64 = Compactar(dpsAssinada) });
        using var resp = await _http.PostAsync($"{amb.Sefin}/nfse", new StringContent(corpoEnvio, Encoding.UTF8, "application/json"));
        var corpo = await resp.Content.ReadAsStringAsync();

        var erros = new List<string>();
        var alertas = new List<string>();
        string? chave = null, nfseXml = null;
        try
        {
            using var json = JsonDocument.Parse(corpo);
            var raiz = json.RootElement;
            chave = Texto(raiz, "chaveAcesso");
            var gz = Texto(raiz, "nfseXmlGZipB64");
            if (gz != null) nfseXml = Descompactar(gz);
            erros.AddRange(Mensagens(raiz, "erros"));
            alertas.AddRange(Mensagens(raiz, "alertas"));
        }
        catch (JsonException)
        {
            erros.Add(Resumir(corpo));
        }
        if (!resp.IsSuccessStatusCode && erros.Count == 0) erros.Add($"HTTP {(int)resp.StatusCode}: {Resumir(corpo)}");
        return new ResultadoEmissao(resp.IsSuccessStatusCode && nfseXml != null, (int)resp.StatusCode, chave, nfseXml, erros, alertas, corpo);
    }

    /// <summary>Chave da NFS-e que uma DPS já gerou, ou null se essa DPS nunca virou nota.</summary>
    public async Task<string?> ChaveDaDps(Ambiente amb, string idDps)
    {
        using var resp = await ComRetentativa(() => _http.GetAsync($"{amb.Sefin}/dps/{idDps}"));
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Texto(json.RootElement, "chaveAcesso");
    }

    public async Task<string?> ConsultarNfse(Ambiente amb, string chave)
    {
        using var resp = await ComRetentativa(() => _http.GetAsync($"{amb.Sefin}/nfse/{chave}"));
        if (!resp.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Texto(json.RootElement, "nfseXmlGZipB64") is { } gz ? Descompactar(gz) : null;
    }

    // O ADN às vezes derruba a conexão; uma segunda tentativa costuma passar.
    private static async Task<HttpResponseMessage> ComRetentativa(Func<Task<HttpResponseMessage>> chamada)
    {
        try { return await chamada(); }
        catch (HttpRequestException) { await Task.Delay(1500); return await chamada(); }
    }

    private static IEnumerable<string> Mensagens(JsonElement raiz, string nome)
    {
        foreach (var p in raiz.EnumerateObject())
        {
            if (!p.Name.Equals(nome, StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in p.Value.EnumerateArray())
            {
                var partes = new[] { Texto(e, "Codigo"), Texto(e, "Descricao"), Texto(e, "Complemento") }.Where(s => !string.IsNullOrWhiteSpace(s));
                yield return string.Join(" — ", partes);
            }
        }
    }

    private static string? Texto(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(nome, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    private static string Resumir(string s) => s.Length > 500 ? s[..500] + "…" : s;

    public static string Compactar(string xml)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes(xml));
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Descompactar(string b64)
    {
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        using var leitor = new StreamReader(gz, Encoding.UTF8);
        return leitor.ReadToEnd();
    }
}
