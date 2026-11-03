using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NfseApp;

var iAutoteste = Array.IndexOf(args, "--autoteste");
if (iAutoteste >= 0) return Autoteste.Rodar(args.ElementAtOrDefault(iAutoteste + 1) ?? "autoteste-dps.xml", args.ElementAtOrDefault(iAutoteste + 2));

var portaPreferida = args.FirstOrDefault(a => int.TryParse(a, out _)) is { } p ? int.Parse(p) : Edicao.Porta;
var abrirNavegador = !args.Contains("--sem-navegador");

// Esta edição já está aberta: só abre outra janela para ela.
if (await Instancia.JaAberta(portaPreferida))
{
    Console.WriteLine($"O app já está aberto em http://127.0.0.1:{portaPreferida}");
    if (abrirNavegador) Janela.Abrir($"http://127.0.0.1:{portaPreferida}");
    return 0;
}

// Usa a porta de sempre quando dá (o navegador guarda o tema por porta); se outro programa está nela, o sistema escolhe uma livre.
var porta = Instancia.PortaLivre(portaPreferida) ? portaPreferida : 0;

// Pasta base fixa na do .exe: aberto de uma pasta de rede (ex.: \\wsl.localhost) o .NET travava vigiando arquivos de configuração.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://127.0.0.1:{porta}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var sessoes = new ConcurrentDictionary<string, string>(); // token → thumbprint
const string Cookie = "nfse_sessao";

// Só atende a própria máquina, e os POSTs exigem um cabeçalho que outro site não consegue mandar sem CORS.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-NfseApp"] != "1")
    { ctx.Response.StatusCode = 403; return; }
    await next();
});

app.MapGet("/", () => Embutido("index.html", "text/html; charset=utf-8"));
app.MapGet("/icone.png", () => Embutido("icone.png", "image/png"));

app.MapGet("/api/info", () => new
{
    app = Instancia.Assinatura,
    nome = Edicao.Nome,
    versao = Edicao.Versao,
    temHomologacao = Edicao.TemHomologacao,
    pastaDados = Repositorio.PastaRaiz,
});

app.MapGet("/api/certificados", () => Certificados.Listar());

app.MapPost("/api/login", (LoginReq req, HttpContext ctx) =>
{
    var cert = Certificados.Abrir(req.Thumbprint);
    var info = cert == null ? null : Certificados.Descrever(cert);
    if (cert == null || info == null) return Erro(404, "Certificado não encontrado.");
    if (info.TipoDocumento != "CNPJ") return Erro(400, "Por enquanto o app só emite com e-CNPJ.");
    try { Certificados.TestarChave(cert); }
    catch (Exception e) { return Erro(400, "Não consegui usar a chave do certificado: " + e.Message); }

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    sessoes[token] = cert.Thumbprint;
    ctx.Response.Cookies.Append(Cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
    return Results.Ok(info);
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies[Cookie] is { } t) sessoes.TryRemove(t, out _);
    ctx.Response.Cookies.Delete(Cookie);
    return Results.Ok();
});

app.MapGet("/api/sessao", (HttpContext ctx) => Sessao(ctx) is { } s ? Results.Ok(s.Info) : Erro(401, "Não logado."));

app.MapPost("/api/sincronizar", async (string? amb, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var ambiente = Ambiente.De(amb);
    try { return Results.Ok(new { novos = await new Repositorio(s.Info.Documento, ambiente).Sincronizar(new ApiNacional(s.Cert), ambiente, s.Info.Documento) }); }
    catch (Exception e) { return Erro(502, "Falha ao consultar o ADN: " + e.Message); }
});

app.MapGet("/api/notas", (string? amb, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var notas = new Repositorio(s.Info.Documento, Ambiente.De(amb)).Todas();
    return Results.Ok(new
    {
        emitidas = notas.Where(n => n.PrestDoc == s.Info.Documento),
        recebidas = notas.Where(n => n.TomaDoc == s.Info.Documento),
    });
});

// Tomadores e notas-base vêm sempre da produção: são os clientes reais. O ambiente só decide para onde vai a nota nova.
app.MapGet("/api/tomadores", (HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    return Results.Ok(Notas.Tomadores(Emitidas(s.Info, Ambiente.Producao)));
});

app.MapGet("/api/nova", async (string? amb, string? tomador, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var destino = Ambiente.De(amb);
    var aviso = await SincronizarParaEmitir(s, destino);

    var baseNota = BaseParaCopiar(s.Info, tomador);
    if (baseNota == null) return Erro(404, "Não achei nota emitida (não cancelada) para copiar" + (tomador != null ? " para esse tomador." : "."));

    var repoProd = new Repositorio(s.Info.Documento, Ambiente.Producao);
    var xmlBase = repoProd.Xml(baseNota.Chave)!;
    var simples = XDocument.Parse(xmlBase).Descendants(Notas.N + "opSimpNac").FirstOrDefault()?.Value is "2" or "3";
    var pct = repoProd.Config.PTotTribSN
              ?? (decimal.TryParse(XDocument.Parse(xmlBase).Descendants(Notas.N + "pTotTribSN").FirstOrDefault()?.Value,
                  NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null);

    var tributacao = Notas.LerTributacao(xmlBase);
    return Results.Ok(new
    {
        @base = baseNota,
        ambiente = destino.Nome,
        proximoNDps = ProximoNDps(s.Info, destino, baseNota.Serie),
        serie = baseNota.Serie,
        simples,
        tributacao,
        emailTomadorRemovido = destino.TpAmb == 2,
        sugestao = new
        {
            dCompet = DateTime.Today.ToString("yyyy-MM-dd"),
            vServ = baseNota.VServ,
            vDescIncond = baseNota.VDesc,
            descricao = baseNota.Descricao,
            pTotTribSN = pct,
            // Em homologação o e-mail da nota real não vem sugerido, para não chegar ao cliente.
            emailTomador = destino.TpAmb == 2 ? "" : tributacao.EmailTomador,
            foneTomador = tributacao.FoneTomador,
        },
        aviso,
    });
});

app.MapPost("/api/emitir", async (EmitirReq req, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var destino = Ambiente.De(req.Amb);
    if (destino == Ambiente.Producao && !req.ConfirmoProducao)
        return Erro(400, "Emissão em produção precisa da confirmação marcada.");
    if (req.VServ <= 0) return Erro(400, "Valor do serviço tem que ser maior que zero.");
    if (req.VDescIncond < 0 || req.VDescIncond >= req.VServ) return Erro(400, "Desconto inválido.");
    if (!DateTime.TryParseExact(req.DCompet, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var comp) || comp > DateTime.Today)
        return Erro(400, "Competência inválida (não pode ser futura).");
    if (string.IsNullOrWhiteSpace(req.Descricao)) return Erro(400, "Descrição do serviço é obrigatória.");
    var email = req.EmailTomador?.Trim() ?? "";
    if (email != "" && (email.Length > 80 || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
        return Erro(400, "E-mail do tomador inválido.");
    var fone = Regex.Replace(req.FoneTomador ?? "", @"\D", "");
    if (fone != "" && fone.Length is < 6 or > 20) return Erro(400, "Telefone do tomador deve ter de 6 a 20 dígitos.");

    await SincronizarParaEmitir(s, destino);
    var repoProd = new Repositorio(s.Info.Documento, Ambiente.Producao);
    var baseNota = Emitidas(s.Info, Ambiente.Producao).FirstOrDefault(n => n.Chave == req.BaseChave);
    if (baseNota == null || baseNota.Cancelada) return Erro(400, "A nota-base não existe ou está cancelada.");

    var nDps = ProximoNDps(s.Info, destino, baseNota.Serie);
    var xmlBase = repoProd.Xml(baseNota.Chave)!;
    // Homologação nunca usa o e-mail da nota real (só um que a pessoa digitou para testar).
    if (destino.TpAmb == 2 && email == Notas.LerTributacao(xmlBase).EmailTomador) email = "";
    var dadosNota = new DadosNovaNota(req.DCompet, req.VServ, req.VDescIncond, req.Descricao.Trim(), req.PTotTribSN, fone, email);

    var api = new ApiNacional(s.Cert);
    var repoDestino = new Repositorio(s.Info.Documento, destino);
    var leiaute = repoDestino.Leiaute ?? Leiaute.Padrao(destino);
    var ajustes = new List<string>();
    string idDps = "", assinada = "";
    ResultadoEmissao? resultado = null;

    // Recusa de leiaute (E1235 finNFSe, E0625, E0712) não cria nota: ajusta a DPS e reenvia com o mesmo número.
    for (var tentativa = 1; tentativa <= 4; tentativa++)
    {
        try
        {
            var (id, dps) = Dps.Montar(xmlBase, dadosNota, destino, nDps, leiaute);
            idDps = id;
            assinada = Dps.Assinar(dps, id, s.Cert);
        }
        catch (Exception e) { return Erro(400, e.Message); }

        try { resultado = await api.Emitir(destino, assinada); }
        catch (Exception e)
        {
            // A resposta se perdeu, mas a nota pode ter sido autorizada. Só a Sefin sabe: pergunta antes de deixar tentar de novo.
            repoDestino.GuardarEmissao(idDps, assinada, null, null);
            Nota? recuperada;
            try { recuperada = await Recuperar(api, destino, repoDestino, idDps); }
            catch
            {
                return Results.Ok(new { ok = false, nDps, erros = new[] { "Falha de comunicação com a Sefin e não consegui confirmar se a nota saiu. NÃO emita de novo ainda: em alguns minutos vá em Notas → Atualizar do ADN e confira. (" + e.Message + ")" }, alertas = Array.Empty<string>(), ajustes, ambiente = destino.Nome });
            }
            if (recuperada == null)
                return Results.Ok(new { ok = false, nDps, erros = new[] { "Falha de comunicação com a Sefin, e ela confirma que essa DPS não virou nota. Pode tentar de novo. (" + e.Message + ")" }, alertas = Array.Empty<string>(), ajustes, ambiente = destino.Nome });
            repoDestino.Leiaute = leiaute;
            return Results.Ok(new { ok = true, chave = recuperada.Chave, numero = recuperada.Numero.ToString(), nDps, erros = Array.Empty<string>(), alertas = new[] { "A resposta se perdeu na rede, mas a Sefin confirma que a nota foi autorizada." }, ajustes, ambiente = destino.Nome });
        }
        if (resultado.Ok || leiaute.AjustarPara(resultado.Erros, ajustes) is not { } ajustado) break;
        leiaute = ajustado;
    }

    repoDestino.GuardarEmissao(idDps, assinada, resultado!.Chave, resultado.NfseXml);
    if (resultado.Ok)
    {
        repoDestino.Leiaute = leiaute; // a próxima nota deste ambiente já sai no formato que passou
        if (req.PTotTribSN is { } pct) repoProd.SalvarConfig(repoProd.Config with { PTotTribSN = pct });
    }

    // E0014: esse número de DPS já virou nota. Pode ser uma tentativa anterior que deu certo: mostra qual é em vez de pular número sozinho.
    Nota? jaExistia = null;
    if (resultado.Erros.Any(e => e.StartsWith("E0014")))
        try { jaExistia = await Recuperar(api, destino, repoDestino, idDps); } catch { /* sem a nota, o erro E0014 sozinho já explica */ }

    var numero = resultado.NfseXml != null ? XDocument.Parse(resultado.NfseXml).Descendants(Notas.N + "nNFSe").First().Value : null;
    return Results.Ok(new { resultado.Ok, resultado.Status, resultado.Chave, numero, nDps, resultado.Erros, resultado.Alertas, ajustes, ambiente = destino.Nome, jaExistia });
});

app.MapGet("/api/notas/{chave}/xml", (string chave, string? amb, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var xml = new Repositorio(s.Info.Documento, Ambiente.De(amb)).Xml(chave);
    return xml == null ? Erro(404, "Nota não encontrada.") : Results.File(Encoding.UTF8.GetBytes(xml), "application/xml", $"NFSe_{chave}.xml");
});

// DANFSe gerado pelo app (a API nacional de PDF foi suspensa em 03/08/2026, NT 008/2026). Sem rede: só o XML guardado.
app.MapGet("/api/notas/{chave}/danfse", (string chave, string? amb, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var repo = new Repositorio(s.Info.Documento, Ambiente.De(amb));
    return repo.Xml(chave) is { } xml
        ? Results.Text(Danfse.Html(xml, repo.MarcaDagua(chave)), "text/html; charset=utf-8")
        : Erro(404, "Nota não encontrada.");
});

app.MapGet("/api/notas/{chave}/pdf", async (string chave, string? amb, HttpContext ctx) =>
{
    if (Sessao(ctx) is not { } s) return Erro(401, "Não logado.");
    var repo = new Repositorio(s.Info.Documento, Ambiente.De(amb));
    if (repo.Xml(chave) is not { } xml) return Erro(404, "Nota não encontrada.");
    var html = Danfse.Html(xml, repo.MarcaDagua(chave));
    try
    {
        // Sem Edge na máquina: devolve o HTML do DANFSe, que dá para imprimir em PDF pelo navegador.
        if (await Danfse.Pdf(html) is not { } pdf) return Results.Text(html, "text/html; charset=utf-8");
        ctx.Response.Headers.ContentDisposition = $"inline; filename=\"DANFSe_{chave}.pdf\"";
        return Results.File(pdf, "application/pdf");
    }
    catch (Exception e) { return Results.Text("Não consegui gerar o PDF: " + e.Message, "text/plain; charset=utf-8", statusCode: 500); }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Com porta 0, só depois de subir dá para saber qual porta o sistema escolheu.
    var url = app.Urls.First();
    Console.WriteLine($"NFS-e rodando em {url}");
    if (!abrirNavegador || Janela.Abrir(url) is not { } janela) return;
    // Fechar a janela encerra o app. Se o Edge devolveu o controle na hora (repassou para uma janela já aberta), o app segue no ar.
    var inicio = DateTime.Now;
    Task.Run(async () =>
    {
        await janela.WaitForExitAsync();
        if (DateTime.Now - inicio > TimeSpan.FromSeconds(5)) app.Lifetime.StopApplication();
    });
});

try { app.Run(); }
catch (IOException e)
{
    // Só acontece se outro programa pegar a porta entre a checagem e a abertura.
    Console.WriteLine($"Não consegui abrir a porta {porta}: {e.Message}");
    return 1;
}
return 0;

// ---------------------------------------------------------------------------------------------

Sess? Sessao(HttpContext ctx)
{
    if (ctx.Request.Cookies[Cookie] is not { } t || !sessoes.TryGetValue(t, out var thumb)) return null;
    var cert = Certificados.Abrir(thumb);
    var info = cert == null ? null : Certificados.Descrever(cert);
    return cert == null || info == null ? null : new Sess(cert, info);
}

List<Nota> Emitidas(CertInfo info, Ambiente amb) =>
    new Repositorio(info.Documento, amb).Todas().Where(n => n.PrestDoc == info.Documento).ToList();

Nota? BaseParaCopiar(CertInfo info, string? tomador) =>
    Emitidas(info, Ambiente.Producao)
        .Where(n => !n.Cancelada && (tomador == null || n.TomaDoc == tomador))
        .OrderByDescending(n => n.DhProc)
        .FirstOrDefault();

// Canceladas também consomem número. Em homologação sem histórico, parte de 900000 para não se confundir com a numeração real.
long ProximoNDps(CertInfo info, Ambiente amb, int serie)
{
    var usados = Emitidas(info, amb).Where(n => n.Serie == serie).Select(n => n.NDps).DefaultIfEmpty(amb == Ambiente.Producao ? 0 : 900000).Max();
    return usados + 1;
}

async Task<string?> SincronizarParaEmitir(Sess s, Ambiente destino)
{
    var api = new ApiNacional(s.Cert);
    try
    {
        await new Repositorio(s.Info.Documento, Ambiente.Producao).Sincronizar(api, Ambiente.Producao, s.Info.Documento);
        if (destino != Ambiente.Producao) await new Repositorio(s.Info.Documento, destino).Sincronizar(api, destino, s.Info.Documento);
        return null;
    }
    catch (Exception e) { return "Não consegui atualizar as notas do ADN agora; o número da DPS pode estar desatualizado. (" + e.Message + ")"; }
}

// Busca na Sefin a NFS-e que a DPS gerou e guarda localmente, para o próximo número já levar ela em conta.
// null = a Sefin confirma que a DPS não virou nota; exceção = não deu para perguntar.
static async Task<Nota?> Recuperar(ApiNacional api, Ambiente amb, Repositorio repo, string idDps)
{
    if (await api.ChaveDaDps(amb, idDps) is not { } chave) return null;
    var xml = await api.ConsultarNfse(amb, chave) ?? throw new InvalidOperationException($"A DPS virou a nota {chave}, mas não consegui baixá-la.");
    repo.GuardarNfse(chave, xml);
    return Notas.Ler(xml, new Dictionary<string, string>());
}

static IResult Embutido(string arquivo, string tipo)
{
    using var s = typeof(Ambiente).Assembly.GetManifestResourceStream("wwwroot/" + arquivo)!;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return Results.File(ms.ToArray(), tipo);
}

static IResult Erro(int status, string mensagem) => Results.Json(new { erro = mensagem }, statusCode: status);


record Sess(X509Certificate2 Cert, CertInfo Info);
record LoginReq(string Thumbprint);
record EmitirReq(string Amb, string BaseChave, string DCompet, decimal VServ, decimal VDescIncond, string Descricao, decimal? PTotTribSN, bool ConfirmoProducao,
    string? EmailTomador = null, string? FoneTomador = null);
