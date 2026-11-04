using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using QRCoder;

namespace NfseApp;

/// <summary>
/// DANFSe gerado pelo próprio app, no modelo da NT 008/2026 v1.02 (SE/CGNFS-e): a API nacional de DANFSe
/// foi suspensa em 03/08/2026 e a geração passou a ser do sistema emissor.
/// Monta um HTML A4 de uma página só com o que está no XML da NFS-e (campo ausente sai como "-", nota 12);
/// o PDF é impresso pelo Edge em modo headless.
/// Medidas, textos e formatos copiados do DANFSe que o portal nacional (Emissor Nacional) gera, onde a NT deixa
/// margem: códigos em vez de descrições no cabeçalho, telefone e IBGE formatados, " / -" para país ausente, etc.
/// </summary>
public static class Danfse
{
    private static readonly XNamespace N = Notas.N;
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private const string UrlConsulta = "https://www.nfse.gov.br/ConsultaPublica?tpc=1&chave=";

    // Descrições oficiais dos códigos, copiadas das anotações do XSD (tiposSimples_v1.01).
    private static readonly Dictionary<string, string> TpEmit = new() { ["1"] = "Prestador", ["2"] = "Tomador", ["3"] = "Intermediário" };
    private static readonly Dictionary<string, string> CStat = new() { ["100"] = "NFS-e Gerada", ["102"] = "NFS-e de Decisão Judicial", ["103"] = "NFS-e Avulsa", ["107"] = "NFS-e MEI" };
    private static readonly Dictionary<string, string> FinNFSe = new() { ["0"] = "NFS-e regular" };
    private static readonly Dictionary<string, string> OpSimpNac = new()
    {
        ["1"] = "Não Optante", ["2"] = "Optante - Microempreendedor Individual (MEI)",
        ["3"] = "Optante - Microempresa ou Empresa de Pequeno Porte (ME/EPP)",
    };
    private static readonly Dictionary<string, string> RegApTribSN = new()
    {
        ["1"] = "Regime de apuração dos tributos federais e municipal pelo Simples Nacional",
        ["2"] = "Regime de apuração dos tributos federais pelo Simples Nacional e ISSQN por fora do Simples Nacional conforme respectiva legislação municipal do tributo",
        ["3"] = "Regime de apuração dos tributos federais e municipal por fora do Simples Nacional conforme respectivas legislações federal e municipal de cada tributo",
    };
    private static readonly Dictionary<string, string> TribISSQN = new() { ["1"] = "Operação Tributável", ["2"] = "Imunidade", ["3"] = "Exportação de Serviço", ["4"] = "Não Incidência" };
    private static readonly Dictionary<string, string> RegEspTrib = new()
    {
        ["0"] = "Nenhum", ["1"] = "Ato Cooperado (Cooperativa)", ["2"] = "Estimativa", ["3"] = "Microempresa Municipal",
        ["4"] = "Notário ou Registrador", ["5"] = "Profissional Autônomo", ["6"] = "Sociedade de Profissionais", ["9"] = "Outros",
    };
    private static readonly Dictionary<string, string> TpImunidade = new()
    {
        ["0"] = "Imunidade (tipo não informado na nota de origem)",
        ["1"] = "Patrimônio, renda ou serviços, uns dos outros (CF88, Art 150, VI, a)",
        ["2"] = "Templos de qualquer culto (CF88, Art 150, VI, b)",
        ["3"] = "Patrimônio, renda ou serviços dos partidos políticos, inclusive suas fundações, das entidades sindicais dos trabalhadores, das instituições de educação e de assistência social, sem fins lucrativos (CF88, Art 150, VI, c)",
        ["4"] = "Livros, jornais, periódicos e o papel destinado a sua impressão (CF88, Art 150, VI, d)",
        ["5"] = "Fonogramas e videofonogramas musicais produzidos no Brasil (CF88, Art 150, VI, e)",
    };
    private static readonly Dictionary<string, string> TpSusp = new() { ["1"] = "Exigibilidade Suspensa por Decisão Judicial", ["2"] = "Exigibilidade Suspensa por Processo Administrativo" };
    private static readonly Dictionary<string, string> TpBM = new() { ["1"] = "Isenção", ["2"] = "Redução da BC em %", ["3"] = "Redução da BC em R$", ["4"] = "Alíquota Diferenciada" };
    private static readonly Dictionary<string, string> TpRetISSQN = new() { ["1"] = "Não Retido", ["2"] = "Retido pelo Tomador", ["3"] = "Retido pelo Intermediário" };
    private static readonly Dictionary<string, string> TpRetPisCofins = new()
    {
        ["0"] = "PIS/COFINS/CSLL Não Retidos", ["1"] = "PIS/COFINS Retidos", ["2"] = "PIS/COFINS Não Retidos",
        ["3"] = "PIS/COFINS/CSLL Retidos", ["4"] = "PIS/COFINS Retidos, CSLL Não Retido", ["5"] = "PIS Retido, COFINS/CSLL Não Retido",
        ["6"] = "COFINS Retido, PIS/CSLL Não Retido", ["7"] = "PIS Não Retido, COFINS/CSLL Retidos", ["8"] = "PIS/COFINS Não Retidos, CSLL Retido",
        ["9"] = "COFINS Não Retido, PIS/CSLL Retidos",
    };

    private static readonly Lazy<Dictionary<string, (string Nome, string Uf)>> Municipios = new(() =>
    {
        var tabela = new Dictionary<string, (string, string)>();
        using var leitor = new StreamReader(Recurso("assets/municipios.csv"), Encoding.UTF8);
        while (leitor.ReadLine() is { } linha)
            if (linha.Split(';') is [var cod, var nome, var uf]) tabela[cod] = (nome, uf);
        return tabela;
    });

    private static readonly Lazy<string> Logo = new(() =>
    {
        using var ms = new MemoryStream();
        Recurso("assets/logo-nfse.png").CopyTo(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    });

    private static Stream Recurso(string nome) =>
        typeof(Danfse).Assembly.GetManifestResourceStream(nome) ?? throw new InvalidOperationException($"Recurso {nome} não embutido.");

    /// <param name="marcaDagua">"CANCELADA", "SUBSTITUÍDA" ou null (item 2.5 da NT).</param>
    public static string Html(string nfseXml, string? marcaDagua)
    {
        var inf = XDocument.Parse(nfseXml).Root!.Element(N + "infNFSe")!;
        var dps = inf.Element(N + "DPS")!.Element(N + "infDPS")!;
        var emit = inf.Element(N + "emit");
        var prest = dps.Element(N + "prest");
        var toma = dps.Element(N + "toma");
        var interm = dps.Element(N + "interm");
        var ibscbsDps = dps.Element(N + "IBSCBS");
        var dest = ibscbsDps?.Element(N + "dest");
        var serv = dps.Element(N + "serv");
        var cServ = serv?.Element(N + "cServ");
        var valDps = dps.Element(N + "valores");
        var trib = valDps?.Element(N + "trib");
        var tribMun = trib?.Element(N + "tribMun");
        var tribFed = trib?.Element(N + "tribFed");
        var pisCofins = tribFed?.Element(N + "piscofins");
        var valNfse = inf.Element(N + "valores");
        var ibscbsNfse = inf.Element(N + "IBSCBS");

        var chave = (inf.Attribute("Id")?.Value ?? "")[3..];
        var tpAmb = T(dps, "tpAmb");
        var cTribNac = T(cServ, "cTribNac");
        var competencia = T(dps, "dCompet");

        var h = new StringBuilder();
        h.Append(Cabecalho());
        h.Append("<div class=\"borda\"></div><div class=\"conteudo\">");

        // ---------- cabeçalho ----------
        var ufEmit = T(emit?.Element(N + "enderNac"), "UF");
        var municipioEmit = cTribNac.StartsWith("99") ? "" : $"Município: {T(inf, "xLocEmi")}{(ufEmit != "" ? " - " + ufEmit : "")}";
        h.Append($"""
            <div class="cab sombra">
              <img class="logo" src="{Logo.Value}" alt="NFS-e">
              <div class="titulo">DANFSe v2.0<div class="l2">Documento Auxiliar da NFS-e{(tpAmb == "2" ? "<div class=\"semvalidade\">NFS-e SEM VALIDADE JURÍDICA</div>" : "")}</div></div>
              <div class="ambiente"><div class="mun">{E(municipioEmit)}</div>
                <div>Ambiente Gerador: {E(T(inf, "ambGer"))}</div><div>Tipo de Ambiente: {E(tpAmb)}</div></div>
            </div>
            """);

        // ---------- dados da NFS-e ----------
        var ident = new StringBuilder();
        ident.Append($"<div class=\"linha\">{Id("CHAVE DE ACESSO DA NFS-e", chave, 3)}</div>");
        ident.Append($"<div class=\"linha\">{Id("NÚMERO DA NFS-e", T(inf, "nNFSe"))}{Id("COMPETÊNCIA DA NFS-e", Data(competencia))}{Id("DATA E HORA DA EMISSÃO DA NFS-e", DataHora(T(inf, "dhProc")))}</div>");
        ident.Append($"<div class=\"linha\">{Id("NÚMERO DA DPS", T(dps, "nDPS"))}{Id("SÉRIE DA DPS", T(dps, "serie"))}{Id("DATA E HORA DA EMISSÃO DA DPS", DataHora(T(dps, "dhEmi")))}</div>");
        var fin = T(dps, "finNFSe") is { Length: > 0 } f ? f : T(ibscbsDps, "finNFSe");
        ident.Append($"<div class=\"linha\">{Id("EMITENTE DA NFS-e", D(TpEmit, T(dps, "tpEmit")), sombra: true)}{Id("SITUAÇÃO DA NFS-e", Corta(D(CStat, T(inf, "cStat")), 37))}{Id("FINALIDADE", Corta(D(FinNFSe, fin), 37))}</div>");
        h.Append($"""
            <div class="bloco ident">{ident}
              <img class="qr" data-qr="{E(UrlConsulta + chave)}" src="{QrPng(UrlConsulta + chave)}" alt="QR Code da consulta pública">
              <div class="qrtexto">A autenticidade desta NFS-e pode ser verificada<br>pela leitura deste código QR ou pela consulta da<br>chave de acesso no portal nacional da NFS-e</div>
            </div>
            """);

        // ---------- prestador ----------
        // Nome e endereço vêm do cadastro (emit) quando a DPS não traz; telefone e e-mail, só o que foi na DPS (como o portal).
        var endPrest = prest?.Element(N + "end");
        var enderEmit = emit?.Element(N + "enderNac");
        var regTrib = prest?.Element(N + "regTrib");
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("PRESTADOR / FORNECEDOR"), Campo("CNPJ / CPF / NIF", Doc(prest ?? emit)), Campo("Indicador Municipal (Inscrição)", Tx(prest, "IM", emit)),
            Campo("Telefone", Fone(T(prest, "fone")))));
        h.Append(Linha(Campo("Nome / Nome Empresarial", Corta(Tx(prest, "xNome", emit), 77), 2),
            Campo("Município / Sigla UF", Municipio(endPrest) is { Length: > 0 } mp ? mp : MunicipioNac(enderEmit)),
            Campo("Código IBGE / CEP", CodigoCep(endPrest) is { Length: > 0 } cp ? cp : CodigoCepNac(enderEmit))));
        h.Append(Linha(Campo("Endereço", Corta(Endereco(endPrest) is { Length: > 0 } ep ? ep : Endereco(enderEmit), 77), 2),
            Campo("E-mail", T(prest, "email"), 2)));
        h.Append(Linha(Campo("Simples Nacional na Data de Competência", Corta(D(OpSimpNac, T(regTrib, "opSimpNac")), 37)),
            Campo("Regime de Apuração Tributária pelo SN", Corta(D(RegApTribSN, T(regTrib, "regApTribSN")), 77), 3)));
        h.Append("</div>");

        // ---------- tomador / destinatário / intermediário ----------
        h.Append(toma == null ? Aviso("TOMADOR/ADQUIRENTE DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e") : Pessoa("TOMADOR / ADQUIRENTE", toma, comIm: true));
        h.Append(dest != null ? Pessoa("DESTINATÁRIO DA OPERAÇÃO", dest, comIm: false)
            : T(ibscbsDps, "indDest") == "0" ? Aviso("O DESTINATÁRIO É O PRÓPRIO TOMADOR/ADQUIRENTE DA OPERAÇÃO")
            : Aviso("DESTINATÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e"));
        h.Append(interm == null ? Aviso("INTERMEDIÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e") : Pessoa("INTERMEDIÁRIO DA OPERAÇÃO", interm, comIm: true));

        // ---------- serviço ----------
        var locPrest = serv?.Element(N + "locPrest");
        var localPrest = Local(T(inf, "xLocPrestacao"), T(locPrest, "cLocPrestacao"), T(locPrest, "cPaisPrestacao"));
        var codTrib = $"{CodTribNac(cTribNac)} / {(T(cServ, "cTribMun") is { Length: > 0 } ctm ? ctm : "-")}";
        var descCodigo = T(cServ, "xTribMun") is { Length: > 0 } xtm ? xtm : T(inf, "xTribNac");
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("SERVIÇO PRESTADO"), Campo("Código de Tributação Nacional/Municipal", codTrib),
            Campo("Código da NBS", Nbs(T(cServ, "cNBS"))), Campo("Local da Prestação / Sigla UF / País", Corta(localPrest, 42))));
        h.Append($"<div class=\"desccod\">{E(Corta(descCodigo, 167))}</div>");
        h.Append($"<div class=\"descricao\"><span class=\"r\">Descrição do Serviço</span><span class=\"v pre\">{E(Corta(T(cServ, "xDescServ"), 1297))}</span></div>");
        h.Append("</div>");

        // ---------- tributação municipal ----------
        if (tribMun == null)
            h.Append(Aviso("TRIBUTAÇÃO MUNICIPAL (ISSQN) - OPERAÇÃO NÃO SUJEITA AO ISSQN"));
        else
        {
            var exig = tribMun.Element(N + "exigSusp");
            var bm = tribMun.Element(N + "BM");
            var incid = Local(T(inf, "xLocIncid"), T(inf, "cLocIncid"), T(tribMun, "cPaisResult"));
            h.Append("<div class=\"bloco\">");
            h.Append(Linha(Titulo("TRIBUTAÇÃO MUNICIPAL (ISSQN)"), Campo("Tipo de Tributação do ISSQN", D(TribISSQN, T(tribMun, "tribISSQN"))),
                Campo("Município / Sigla UF / País de Incidência do ISSQN", Corta(incid, 42), 2)));
            // Nota 5: a linha some quando não há nada a dizer (regime especial "Nenhum" conta como vazio, como no portal).
            var regEsp = T(regTrib, "regEspTrib") is "0" or "" ? "" : D(RegEspTrib, T(regTrib, "regEspTrib"));
            var linha2 = new[] { regEsp, Corta(D(TpImunidade, T(tribMun, "tpImunidade")), 37), Corta(D(TpSusp, T(exig, "tpSusp")), 37), T(exig, "nProcesso") };
            if (linha2.Any(v => v != ""))
                h.Append(Linha(Campo("Regime Especial de Tributação do ISSQN", linha2[0]), Campo("Tipo de Imunidade do ISSQN", linha2[1]),
                    Campo("Suspensão da Exigibilidade do ISSQN", linha2[2]), Campo("Número Processo Suspensão", linha2[3])));
            var deducoes = T(valDps?.Element(N + "vDedRed"), "vDR") is { Length: > 0 } vdr ? vdr : T(valNfse, "vCalcDR");
            var linha3 = new[] { D(TpBM, T(bm, "tpBM")), Dinheiro(T(valNfse, "vCalcBM") is { Length: > 0 } cbm ? cbm : T(bm, "vRedBCBM")),
                Dinheiro(deducoes), Dinheiro(T(valDps?.Element(N + "vDescCondIncond"), "vDescIncond")) };
            if (linha3.Any(v => v != ""))
                h.Append(Linha(Campo("Benefício Municipal", linha3[0]), Campo("Cálculo do BM", linha3[1]),
                    Campo("Total Deduções/Reduções", linha3[2]), Campo("Desconto Incondicionado", linha3[3])));
            h.Append(Linha(Campo("BC ISSQN", Dinheiro(T(valNfse, "vBC"))), Campo("Alíquota Aplicada", Pct(T(valNfse, "pAliqAplic"))),
                Campo("Retenção do ISSQN", D(TpRetISSQN, T(tribMun, "tpRetISSQN"))), Campo("ISSQN Apurado", Dinheiro(T(valNfse, "vISSQN")))));
            h.Append("</div>");
        }

        // ---------- tributação federal ----------
        var tpRetPc = T(pisCofins, "tpRetPisCofins");
        var vCsll = Dec(T(tribFed, "vRetCSLL"));
        var contribRetidas = tpRetPc == "1"
            ? (vCsll ?? 0) + (Dec(T(pisCofins, "vPis")) ?? 0) + (Dec(T(pisCofins, "vCofins")) ?? 0)
            : vCsll;
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("TRIBUTAÇÃO FEDERAL (EXCETO CBS)"), Campo("IRRF", Dinheiro(T(tribFed, "vRetIRRF"))),
            Campo("Contribuição Previdenciária - Retida", Dinheiro(T(tribFed, "vRetCP"))),
            Campo("Contribuições Sociais - Retidas", contribRetidas is { } cr ? Dinheiro(cr) : "")));
        if (competencia.Length >= 4 && int.TryParse(competencia[..4], out var anoComp) && anoComp <= 2026) // nota 6
            h.Append(Linha(Campo("PIS - Débito Apuração Própria", tpRetPc == "1" ? Dinheiro(0m) : Dinheiro(T(pisCofins, "vPis"))),
                Campo("COFINS - Débito Apuração Própria", tpRetPc == "1" ? Dinheiro(0m) : Dinheiro(T(pisCofins, "vCofins"))),
                Campo("Descrição Contrib. Sociais - Retidas", tpRetPc == "" ? "" : Corta($"{tpRetPc} - {D(TpRetPisCofins, tpRetPc)}", 39), 2)));
        h.Append("</div>");

        // ---------- IBS / CBS ----------
        var gIbsCbs = ibscbsDps?.Element(N + "valores")?.Element(N + "trib")?.Element(N + "gIBSCBS");
        var valIbs = ibscbsNfse?.Element(N + "valores");
        var totC = ibscbsNfse?.Element(N + "totCIBS");
        var gIbs = totC?.Element(N + "gIBS");
        var cLocIncIbs = T(ibscbsNfse, "cLocalidadeIncid");
        // Como no portal: as exclusões da base (descontos, ISS, PIS e COFINS) saem calculadas mesmo sem o grupo IBSCBS.
        var exclusoes = new[] { T(valDps?.Element(N + "vDescCondIncond"), "vDescIncond"), T(valIbs, "vCalcReeRepRes"), T(valNfse, "vISSQN"), T(pisCofins, "vPis"), T(pisCofins, "vCofins") }
            .Sum(v => Dec(v) ?? 0);
        string Barra(params string[] partes) => string.Join(" / ", partes.Select(p => p == "" ? "-" : p));
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("TRIBUTAÇÃO IBS/CBS"), Campo("CST / cClassTrib", Barra(T(gIbsCbs, "CST"), T(gIbsCbs, "cClassTrib"))),
            Campo("Indicador de Operação / Código IBGE Incidência / Município Incidência / Sigla UF",
                Corta(Barra(T(ibscbsDps, "cIndOp"), cLocIncIbs, T(ibscbsNfse, "xLocalidadeIncid"), Municipios.Value.TryGetValue(cLocIncIbs, out var mib) ? mib.Uf : ""), 56), 2)));
        h.Append(Linha(Campo("Exclusões e Reduções da Base de Cálculo", Dinheiro(exclusoes)),
            Campo("Base de Cálculo Após Exclusões e Reduções", Dinheiro(T(valIbs, "vBC"))),
            Campo("Red. Alíquota IBS / Red. Alíquota CBS", Barra(Pct(T(valIbs?.Element(N + "uf"), "pRedAliqUF")), Pct(T(valIbs?.Element(N + "mun"), "pRedAliqMun")), Pct(T(valIbs?.Element(N + "fed"), "pRedAliqCBS")))),
            Campo("Alíquota - IBS UF / IBS Mun", Barra(Pct(T(valIbs?.Element(N + "uf"), "pIBSUF")), Pct(T(valIbs?.Element(N + "mun"), "pIBSMun"))))));
        h.Append(Linha(Campo("Alíq. Efetiva Municipal - IBS", Pct(T(valIbs?.Element(N + "mun"), "pAliqEfetMun"))),
            Campo("Valor Apurado Municipal - IBS", Dinheiro(T(gIbs?.Element(N + "gIBSMunTot"), "vIBSMun"))),
            Campo("Alíq. Efetiva Estadual - IBS", Pct(T(valIbs?.Element(N + "uf"), "pAliqEfetUF"))),
            Campo("Valor Apurado Estadual - IBS", Dinheiro(T(gIbs?.Element(N + "gIBSUFTot"), "vIBSUF")))));
        h.Append(Linha(Campo("Valor Total Apurado - IBS", Dinheiro(T(gIbs, "vIBSTot"))), Campo("Alíquota - CBS", Pct(T(valIbs?.Element(N + "fed"), "pCBS"))),
            Campo("Alíquota Efetiva - CBS", Pct(T(valIbs?.Element(N + "fed"), "pAliqEfetCBS"))),
            Campo("Valor Total Apurado - CBS", Dinheiro(T(totC?.Element(N + "gCBS"), "vCBS")))));
        h.Append("</div>");

        // ---------- valor total ----------
        // Sem o grupo IBSCBS, o portal mostra R$ 0,00 no total do IBS/CBS e no líquido + IBS/CBS; aqui também.
        var descCI = valDps?.Element(N + "vDescCondIncond");
        var totalIbsCbs = (Dec(T(gIbs, "vIBSTot")) ?? 0) + (Dec(T(totC?.Element(N + "gCBS"), "vCBS")) ?? 0);
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("VALOR TOTAL DA NFS-e"), Campo("VALOR DA OPERAÇÃO / SERVIÇO", Dinheiro(T(valDps?.Element(N + "vServPrest"), "vServ"))),
            Campo("Desconto Incondicionado", Dinheiro(T(descCI, "vDescIncond"))), Campo("Desconto Condicionado", Dinheiro(T(descCI, "vDescCond")))));
        h.Append(Linha(Campo("Total das Retenções (ISSQN / Federais)", Dinheiro(T(valNfse, "vTotalRet"))),
            Campo("VALOR LÍQUIDO DA NFS-e", Dinheiro(T(valNfse, "vLiq"))),
            Campo("Total do IBS/CBS", Dinheiro(totalIbsCbs)),
            Campo("VALOR LÍQUIDO DA NFS-e + IBS/CBS", Dinheiro(Dec(T(totC, "vTotNF")) ?? 0), sombra: true)));
        h.Append("</div>");

        // ---------- informações complementares ----------
        h.Append($"""
            <div class="bloco info">
              <div class="titulo-bloco">INFORMAÇÕES COMPLEMENTARES</div>
              <div class="v pre">{E(Corta(InfoComplementares(inf, dps, serv), 1997))}</div>
              <div class="v">{E(TotaisTributos(trib?.Element(N + "totTrib")))}</div>
            </div>
            """);
        h.Append("</div>");

        // ---------- canhoto (opcional) ----------
        h.Append($"""
            <div class="canhoto k1"><span class="r">DATA CIENTIFICAÇÃO:</span></div>
            <div class="canhoto k2"><span class="r">IDENTIFICAÇÃO E ASSINATURA</span></div>
            <div class="canhoto k3"><span class="r">N° NFS-e / CHAVE NFS-e</span><span class="v">{E(T(inf, "nNFSe"))} / {E(chave)}</span></div>
            """);

        if (marcaDagua != null) h.Append($"<div class=\"marca\">{E(marcaDagua)}</div>");
        h.Append("</body></html>");
        return h.ToString();
    }

    /// <summary>Imprime o HTML em PDF com o Edge headless (perfil temporário próprio). Null se não houver Edge.</summary>
    public static async Task<byte[]?> Pdf(string html)
    {
        if (Janela.CaminhoEdge() is not { } edge) return null;
        var pasta = Path.Combine(Path.GetTempPath(), "nfseapp-danfse-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(pasta);
        try
        {
            var arquivoHtml = Path.Combine(pasta, "danfse.html");
            var arquivoPdf = Path.Combine(pasta, "danfse.pdf");
            await File.WriteAllTextAsync(arquivoHtml, html, Encoding.UTF8);
            var psi = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         $"--user-data-dir={Path.Combine(pasta, "perfil")}", "--no-pdf-header-footer", "--print-to-pdf-no-header",
                         $"--print-to-pdf={arquivoPdf}", new Uri(arquivoHtml).AbsoluteUri })
                psi.ArgumentList.Add(a);
            using var processo = Process.Start(psi)!;
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await processo.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException) { processo.Kill(true); throw new TimeoutException("O Edge demorou demais para gerar o PDF."); }
            return File.Exists(arquivoPdf) ? await File.ReadAllBytesAsync(arquivoPdf) : throw new InvalidOperationException("O Edge não gerou o PDF.");
        }
        finally
        {
            try { Directory.Delete(pasta, true); } catch { /* o Edge às vezes ainda segura o perfil; sobra só uma pasta temporária */ }
        }
    }

    // ------------------------------------------------------------------ blocos

    private static string Pessoa(string titulo, XElement p, bool comIm)
    {
        var end = p.Element(N + "end");
        var sb = new StringBuilder("<div class=\"bloco\">");
        sb.Append(Linha(Titulo(titulo), Campo("CNPJ / CPF / NIF", Doc(p)),
            comIm ? Campo("Indicador Municipal (Inscrição)", T(p, "IM")) : "<div class=\"c\"></div>",
            Campo("Telefone", Fone(T(p, "fone")))));
        sb.Append(Linha(Campo("Nome / Nome Empresarial", Corta(T(p, "xNome"), 77), 2), Campo("Município / Sigla UF", Municipio(end)),
            Campo("Código IBGE / CEP", CodigoCep(end))));
        sb.Append(Linha(Campo("Endereço", Corta(Endereco(end), 77), 2), Campo("E-mail", T(p, "email"), 2)));
        return sb.Append("</div>").ToString();
    }

    private static string Aviso(string texto) => $"<div class=\"bloco aviso\"><span>{E(texto)}</span></div>";

    private static string Linha(params string[] celulas) => $"<div class=\"linha\">{string.Concat(celulas)}</div>";

    private static string Titulo(string texto) => $"<div class=\"c titulo-bloco sombra\">{E(texto)}</div>";

    private static string Campo(string rotulo, string valor, int colunas = 1, bool sombra = false) =>
        $"<div class=\"c{(colunas > 1 ? " s" + colunas : "")}{(sombra ? " sombra" : "")}\"><span class=\"r\">{E(rotulo)}</span>" +
        $"<span class=\"v\">{E(valor == "" ? "-" : valor)}</span></div>";

    private static string Id(string rotulo, string valor, int colunas = 1, bool sombra = false) =>
        $"<div class=\"c{(colunas > 1 ? " s" + colunas : "")}{(sombra ? " sombra" : "")}\"><span class=\"ri\">{E(rotulo)}</span><span class=\"v\">{E(valor == "" ? "-" : valor)}</span></div>";

    private static string InfoComplementares(XElement inf, XElement dps, XElement? serv)
    {
        var info = serv?.Element(N + "infoCompl");
        var obra = serv?.Element(N + "obra");
        var evento = serv?.Element(N + "atvEvento");
        var imovel = dps.Element(N + "IBSCBS")?.Element(N + "imovel");
        var partes = new List<string>();
        void Add(string rotulo, string valor) { if (valor != "") partes.Add(rotulo + " " + valor); }
        Add("Inf. Cont.:", T(info, "xInfComp"));
        Add("NFS-e Subst.:", T(dps.Element(N + "subst"), "chSubstda"));
        Add("Doc. Ref.:", T(info, "docRef"));
        Add("Cod. Obra:", T(obra, "cObra"));
        Add("Insc. Imob.:", T(obra, "inscImobFisc") is { Length: > 0 } io ? io : T(imovel, "inscImobFisc"));
        Add("Cod. Evt.:", T(evento, "idAtvEvt"));
        Add("Doc. Tec.:", T(info, "idDocTec"));
        Add("Núm. Ped.:", T(info, "xPed"));
        Add("Item Ped.:", string.Join(", ", info?.Element(N + "gItemPed")?.Elements(N + "xItemPed").Select(e => e.Value) ?? []));
        Add("Inf. A. T. Mun.:", T(inf, "xOutInf"));
        return string.Join(" | ", partes);
    }

    // Nota 10: linha obrigatória. Como no portal, só valores (vTotTrib) ou percentuais (pTotTrib) por esfera;
    // pTotTribSN (Simples) e indTotTrib saem como "-".
    private static string TotaisTributos(XElement? totTrib)
    {
        var v = totTrib?.Element(N + "vTotTrib");
        var p = totTrib?.Element(N + "pTotTrib");
        string Esfera(string nome) => v != null ? Dinheiro(T(v, "vTotTrib" + nome)) : p != null ? Pct(T(p, "pTotTrib" + nome)) : "";
        string Ou(string s) => s == "" ? "-" : s;
        return $"Totais aproximados dos Tributos cfe. Lei n° 12.741/2012: Federais: {Ou(Esfera("Fed"))}; Estaduais: {Ou(Esfera("Est"))}; Municipais: {Ou(Esfera("Mun"))};";
    }

    // ------------------------------------------------------------------ formatação

    private static string T(XElement? e, string nome) => e?.Element(N + nome)?.Value.Trim() ?? "";
    private static string Tx(XElement? e, string nome, XElement? alternativa) => T(e, nome) is { Length: > 0 } v ? v : T(alternativa, nome);
    private static string D(Dictionary<string, string> tabela, string codigo) => codigo == "" ? "" : tabela.TryGetValue(codigo, out var d) ? d : codigo;
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Corta(string s, int max) => s.Length > max ? s[..max] + "..." : s;
    private static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string Dinheiro(string s) => Dec(s) is { } v ? Dinheiro(v) : "";
    private static string Dinheiro(decimal v) => "R$ " + v.ToString("#,##0.00", Br);
    private static string Pct(string s) => Dec(s) is { } v ? v.ToString("0.00", Br) + "%" : "";

    private static string Data(string iso) => DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy") : iso;
    // Hora do próprio documento (fuso que veio no XML), sem converter para o fuso da máquina.
    private static string DataHora(string iso) => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy HH:mm:ss") : iso;

    private static string Doc(XElement? p)
    {
        if (T(p, "CNPJ") is { Length: 14 } cnpj) return $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}";
        if (T(p, "CPF") is { Length: 11 } cpf) return $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";
        return T(p, "NIF");
    }

    private static string CodTribNac(string c) => c.Length == 6 ? $"{c[..2]}.{c[2..4]}.{c[4..]}" : c;
    private static string Nbs(string c) => c.Length == 9 ? $"{c[..1]}.{c[1..5]}.{c[5..7]}.{c[7..]}" : c;
    private static string Cep(string c) => c.Length == 8 ? $"{c[..2]}.{c[2..5]}-{c[5..]}" : c;
    private static string CodIbge(string c) => c.Length == 7 ? $"{c[..2]}.{c[2..]}" : c;

    private static string Fone(string f) => f.Length switch
    {
        10 => $"({f[..2]}) {f[2..6]}-{f[6..]}",
        11 => $"({f[..2]}) {f[2..7]}-{f[7..]}",
        _ => f,
    };

    // "Município / UF / País" do local da prestação e da incidência; sem país no XML, "-" (como no portal).
    private static string Local(string nome, string cMun, string pais) =>
        nome == "" ? "" : $"{nome}{(Municipios.Value.TryGetValue(cMun, out var m) ? " / " + m.Uf : "")} / {(pais == "" ? "-" : pais)}";

    // end/endNac (tomador, intermediário) ou end/endExt (exterior)
    private static string Municipio(XElement? end)
    {
        if (end?.Element(N + "endNac") is { } nac) return NomeMunicipio(T(nac, "cMun"));
        if (end?.Element(N + "endExt") is { } ext) return T(ext, "xCidade") is { Length: > 0 } cid ? $"{cid} / {T(ext, "cPais")}" : "";
        return "";
    }

    private static string CodigoCep(XElement? end)
    {
        if (end?.Element(N + "endNac") is { } nac) return T(nac, "cMun") is { Length: > 0 } cm ? $"{CodIbge(cm)} / {Cep(T(nac, "CEP"))}" : "";
        if (end?.Element(N + "endExt") is { } ext) return T(ext, "cEndPost");
        return "";
    }

    // emit/enderNac (prestador, quando a DPS não traz o endereço): cMun, UF e CEP no mesmo grupo
    private static string MunicipioNac(XElement? enderNac) => T(enderNac, "cMun") is { Length: > 0 } cm ? NomeMunicipio(cm) : "";
    private static string CodigoCepNac(XElement? enderNac) => T(enderNac, "cMun") is { Length: > 0 } cm ? $"{CodIbge(cm)} / {Cep(T(enderNac, "CEP"))}" : "";

    private static string NomeMunicipio(string cMun) =>
        cMun == "" ? "" : Municipios.Value.TryGetValue(cMun, out var m) ? $"{m.Nome} / {m.Uf}" : cMun;

    private static string Endereco(XElement? end) =>
        string.Join(", ", new[] { T(end, "xLgr"), T(end, "nro"), T(end, "xCpl"), T(end, "xBairro") }.Where(s => s != ""));

    // forceUtf8: tudo em modo byte, como o portal. Sem isso o QRCoder compacta a chave em modo numérico e o desenho
    // (e o tamanho: 37 módulos em vez de 41) fica diferente do oficial, embora leve ao mesmo endereço.
    // PNG de 10 px por módulo, como o do portal: em SVG o Edge alinha cada módulo à grade de pixels e o QR deforma.
    private static string QrPng(string conteudo)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(conteudo, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(dados).GetGraphic(10, drawQuietZones: false));
    }

    // Medidas em pontos tiradas do DANFSe do portal (A4 = 595 x 842 pt): quatro colunas de 144,6 pt a partir de x = 8,5,
    // linhas de 19,1 pt (20,2 na identificação), texto a 3,4 pt da borda da coluna. Fontes do item 2.4 da NT:
    // Arial negrito nos rótulos, Microsoft Sans Serif nos conteúdos. Altura de linha = altura natural da fonte.
    private static string Cabecalho() => """
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>DANFSe</title><style>
        @page { size: A4 portrait; margin: 0; }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body { width: 210mm; height: 297mm; background: #fff; color: #000; }
        body { position: relative; -webkit-print-color-adjust: exact; print-color-adjust: exact;
               font-family: "Microsoft Sans Serif", Arial, sans-serif; font-size: 7pt; line-height: 7.9pt; }
        .borda { position: absolute; left: 4.5pt; top: 4.5pt; width: 586pt; height: 833pt; border: 1pt solid #000; }
        .conteudo { position: absolute; left: 8.5pt; top: 5.7pt; width: 578.3pt; height: 788pt; overflow: hidden; }
        .sombra { background: #f2f2f2; }
        .cab { position: relative; height: 34pt; }
        .logo { position: absolute; left: 3.4pt; top: 4.6pt; width: 115.7pt; height: 22.9pt; }
        .titulo { position: absolute; left: 0; right: 0; top: 7.2pt; text-align: center; font: bold 9pt/10.4pt Arial, sans-serif; }
        .titulo .l2 { position: absolute; left: 0; right: 0; top: 10.35pt; }
        .semvalidade { color: #f00; }
        .ambiente { position: absolute; left: 437.1pt; top: 5.7pt; font-size: 6pt; line-height: 6.8pt; white-space: nowrap; }
        .ambiente .mun { font-size: 8pt; line-height: 9pt; }
        /* A linha de cima é desenhada por fora do layout: como borda, o Edge arredonda 0,5 pt para 1 px e cada bloco desce um pouco. */
        .bloco { padding-top: .5pt; position: relative; }
        .bloco::before { content: ""; position: absolute; left: 0; right: 0; top: .6pt; height: .5pt; background: #000; z-index: 1; }
        .linha { display: grid; grid-template-columns: repeat(4, 1fr); height: 19.1pt; }
        .c { padding: .2pt 3.4pt 0; overflow: hidden; white-space: nowrap; }
        .c.s2 { grid-column: span 2; } .c.s3 { grid-column: span 3; } .c.s4 { grid-column: span 4; }
        .r { display: block; font: bold 6pt/6.7pt Arial, sans-serif; }
        .ri { display: block; font: bold 7pt/7.8pt Arial, sans-serif; }
        .titulo-bloco { font: bold 7pt/7.8pt Arial, sans-serif; }
        .v { display: block; overflow: hidden; text-overflow: ellipsis; }
        /* O Edge alinha cada linha de texto à grade de pixels (0,75 pt) e arredonda as métricas da fonte; estes ajustes
           põem os textos onde o portal põe (conferido com o PDF oficial, diferença de até ~0,5 pt). */
        .r, .v, .ri, .titulo-bloco, .desccod, .aviso span { position: relative; }
        .r { top: 1.4pt; } .v { top: 1pt; } .ri { top: .7pt; } .titulo-bloco { top: .9pt; } .desccod { top: 1.2pt; } .aviso span { top: 1pt; }
        .ident { padding-top: 4.3pt; }
        .ident .linha { height: 20.2pt; }
        /* 410 px reduzidos por escala para 41 pt: com width: 41pt o Edge arredonda a caixa para 54 px (40,5 pt). */
        .qr { position: absolute; left: 485.5pt; top: 7.1pt; width: 410px; height: 410px; transform: scale(.1333333); transform-origin: 0 0;
              image-rendering: pixelated; }
        .qrtexto { position: absolute; left: 437.1pt; top: 52.4pt; font-size: 6pt; line-height: 6.8pt; white-space: nowrap; }
        .aviso { text-align: center; white-space: nowrap; overflow: hidden; }
        .desccod { padding: 0 3.4pt 4.4pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .descricao { padding: 0 3.4pt 4.4pt; }
        .pre { white-space: pre-wrap; word-break: break-word; }
        .info { padding: .2pt 3.4pt 0; }
        .info .pre { min-height: 7.9pt; margin-bottom: 4.4pt; }
        .canhoto { position: absolute; top: 795.3pt; height: 21.1pt; border: 1pt solid #000; padding: 0 3.65pt 0; overflow: hidden; white-space: nowrap; }
        .canhoto.k1 { left: 8.5pt; width: 145.6pt; } .canhoto.k2 { left: 153.1pt; width: 145.5pt; } .canhoto.k3 { left: 297.6pt; width: 290.2pt; }
        .marca { position: absolute; left: 0; right: 0; top: 40%; text-align: center; transform: rotate(-35deg);
                 font: 64pt Arial, sans-serif; color: #a6a6a6; opacity: .55; pointer-events: none; letter-spacing: .1em; }
        </style></head><body>
        """;
}
