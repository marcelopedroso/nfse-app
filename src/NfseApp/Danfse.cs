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
/// </summary>
public static class Danfse
{
    private static readonly XNamespace N = Notas.N;
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private const string UrlConsulta = "https://www.nfse.gov.br/ConsultaPublica/?tpc=1&chave=";

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
        ["1"] = "Regime de apuração dos tributos federais e municipal pelo SN",
        ["2"] = "Regime de apuração dos tributos federais pelo SN e ISSQN por fora do SN conforme respectiva legislação municipal do tributo",
        ["3"] = "Regime de apuração dos tributos federais e municipal por fora do SN conforme respectivas legislações federal e municipal de cada tributo",
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
    private static readonly Dictionary<string, string> TpAmb = new() { ["1"] = "Produção", ["2"] = "Homologação" };
    private static readonly Dictionary<string, string> AmbGer = new() { ["1"] = "Prefeitura", ["2"] = "Sistema Nacional da NFS-e" };

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
        h.Append("<div class=\"pagina\">");

        // ---------- cabeçalho ----------
        var ufEmit = T(emit?.Element(N + "enderNac"), "UF");
        var municipioEmit = cTribNac.StartsWith("99") ? "" : $"Município: {T(inf, "xLocEmi")}{(ufEmit != "" ? " / " + ufEmit : "")}";
        h.Append($"""
            <div class="cab sombra">
              <div class="logo"><img src="{Logo.Value}" alt="NFS-e"></div>
              <div class="titulo">DANFSe v2.0<br>Documento Auxiliar da NFS-e{(tpAmb == "2" ? "<div class=\"semvalidade\">NFS-e SEM VALIDADE JURÍDICA</div>" : "")}</div>
              <div class="ambiente"><div class="mun">{E(municipioEmit)}</div>
                <div>Ambiente Gerador: {E(D(AmbGer, T(inf, "ambGer")))}</div><div>Tipo de Ambiente: {E(D(TpAmb, tpAmb))}</div></div>
            </div>
            """);

        // ---------- dados da NFS-e ----------
        var ident = new StringBuilder();
        ident.Append($"<div class=\"linha l-chave\">{Id("Chave de Acesso da NFS-e", chave, 3)}</div>");
        ident.Append($"<div class=\"linha l-id\">{Id("Número da NFS-e", T(inf, "nNFSe"))}{Id("Competência da NFS-e", Data(competencia))}{Id("Data e Hora da Emissão da NFS-e", DataHora(T(inf, "dhProc")))}</div>");
        ident.Append($"<div class=\"linha l-id\">{Id("Número da DPS", T(dps, "nDPS"))}{Id("Série da DPS", T(dps, "serie"))}{Id("Data e Hora da Emissão da DPS", DataHora(T(dps, "dhEmi")))}</div>");
        var fin = T(dps, "finNFSe") is { Length: > 0 } f ? f : T(ibscbsDps, "finNFSe");
        ident.Append($"<div class=\"linha l-id\">{Id("Emitente da NFS-e", D(TpEmit, T(dps, "tpEmit")), sombra: true)}{Id("Situação da NFS-e", Corta(D(CStat, T(inf, "cStat")), 37))}{Id("Finalidade", Corta(D(FinNFSe, fin), 37))}</div>");
        h.Append($"""
            <div class="bloco ident">{ident}
              <div class="qr" data-qr="{E(UrlConsulta + chave)}">{QrSvg(UrlConsulta + chave)}</div>
              <div class="qrtexto">A autenticidade desta NFS-e pode ser verificada pela leitura deste código QR ou pela consulta da chave de acesso no portal nacional da NFS-e</div>
            </div>
            """);

        // ---------- prestador ----------
        var endPrest = prest?.Element(N + "end");
        var enderEmit = emit?.Element(N + "enderNac");
        var regTrib = prest?.Element(N + "regTrib");
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("Prestador / Fornecedor"), Campo("CNPJ / CPF / NIF", Doc(prest ?? emit)), Campo("Indicador Municipal (Inscrição)", Tx(prest, "IM", emit)),
            Campo("Telefone", Tx(prest, "fone", emit))));
        h.Append(Linha(Campo("Nome / Nome Empresarial", Corta(Tx(prest, "xNome", emit), 77), 2),
            Campo("Município / Sigla UF", Municipio(endPrest) is { Length: > 0 } mp ? mp : MunicipioNac(enderEmit)),
            Campo("Código IBGE / CEP", CodigoCep(endPrest) is { Length: > 0 } cp ? cp : CodigoCepNac(enderEmit))));
        h.Append(Linha(Campo("Endereço", Corta(Endereco(endPrest) is { Length: > 0 } ep ? ep : Endereco(enderEmit), 77), 2),
            Campo("E-mail", Tx(prest, "email", emit), 2)));
        h.Append(Linha(Campo("Simples Nacional na Data de Competência", Corta(D(OpSimpNac, T(regTrib, "opSimpNac")), 37)),
            Campo("Regime de Apuração Tributária pelo SN", Corta(D(RegApTribSN, T(regTrib, "regApTribSN")), 77), 3)));
        h.Append("</div>");

        // ---------- tomador / destinatário / intermediário ----------
        h.Append(toma == null ? Aviso("TOMADOR/ADQUIRENTE DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e") : Pessoa("Tomador / Adquirente", toma, comIm: true));
        h.Append(dest != null ? Pessoa("Destinatário da Operação", dest, comIm: false)
            : T(ibscbsDps, "indDest") == "0" ? Aviso("O DESTINATÁRIO É O PRÓPRIO TOMADOR/ADQUIRENTE DA OPERAÇÃO")
            : Aviso("DESTINATÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e"));
        h.Append(interm == null ? Aviso("INTERMEDIÁRIO DA OPERAÇÃO NÃO IDENTIFICADO NA NFS-e") : Pessoa("Intermediário da Operação", interm, comIm: true));

        // ---------- serviço ----------
        var locPrest = serv?.Element(N + "locPrest");
        var cLocPrest = T(locPrest, "cLocPrestacao");
        var localPrest = T(inf, "xLocPrestacao") is { Length: > 0 } xl
            ? $"{xl}{(Municipios.Value.TryGetValue(cLocPrest, out var mpr) ? " / " + mpr.Uf : "")} / {(T(locPrest, "cPaisPrestacao") is { Length: > 0 } pais ? pais : "BR")}"
            : "";
        var codTrib = CodTribNac(cTribNac) + (T(cServ, "cTribMun") is { Length: > 0 } ctm ? " / " + ctm : "");
        var descCodigo = T(cServ, "xTribMun") is { Length: > 0 } xtm ? xtm : T(inf, "xTribNac");
        h.Append("<div class=\"bloco servico\">");
        h.Append(Linha(Titulo("Serviço Prestado"), Campo("Código de Tributação Nacional / Municipal", codTrib),
            Campo("Código da NBS", Nbs(T(cServ, "cNBS"))), Campo("Local da Prestação / Sigla UF / País", Corta(localPrest, 42))));
        h.Append($"<div class=\"linha l-desccod\"><div class=\"c s4\"><span class=\"v\">{E(Corta(descCodigo, 167))}</span></div></div>");
        h.Append($"<div class=\"descricao\"><span class=\"r\">Descrição do Serviço</span><span class=\"v pre\">{E(Corta(T(cServ, "xDescServ"), 1297))}</span></div>");
        h.Append("</div>");

        // ---------- tributação municipal ----------
        if (tribMun == null)
            h.Append(Aviso("TRIBUTAÇÃO MUNICIPAL (ISSQN) - OPERAÇÃO NÃO SUJEITA AO ISSQN"));
        else
        {
            var exig = tribMun.Element(N + "exigSusp");
            var bm = tribMun.Element(N + "BM");
            var cLocIncid = T(inf, "cLocIncid");
            var incid = T(inf, "xLocIncid") is { Length: > 0 } xi
                ? $"{xi}{(Municipios.Value.TryGetValue(cLocIncid, out var mi) ? " / " + mi.Uf : "")} / {(T(tribMun, "cPaisResult") is { Length: > 0 } pr ? pr : "BR")}"
                : "";
            h.Append("<div class=\"bloco\">");
            h.Append(Linha(Titulo("Tributação Municipal (ISSQN)"), Campo("Tipo de Tributação do ISSQN", D(TribISSQN, T(tribMun, "tribISSQN"))),
                Campo("Município / Sigla UF / País de Incidência do ISSQN", Corta(incid, 42), 2)));
            var linha2 = new[] { D(RegEspTrib, T(regTrib, "regEspTrib")), Corta(D(TpImunidade, T(tribMun, "tpImunidade")), 37), Corta(D(TpSusp, T(exig, "tpSusp")), 37), T(exig, "nProcesso") };
            if (linha2.Any(v => v != "")) // nota 5: some se a linha toda estiver vazia
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
        h.Append(Linha(Titulo("Tributação Federal (Exceto CBS)"), Campo("IRRF", Dinheiro(T(tribFed, "vRetIRRF"))),
            Campo("Contribuição Previdenciária - Retida", Dinheiro(T(tribFed, "vRetCP"))),
            Campo("Contribuições Sociais - Retidas", contribRetidas is { } cr ? Dinheiro(cr) : "")));
        if (competencia.Length >= 4 && int.TryParse(competencia[..4], out var anoComp) && anoComp <= 2026) // nota 6
            h.Append(Linha(Campo("PIS - Débito Apuração Própria", tpRetPc == "1" ? Dinheiro(0m) : Dinheiro(T(pisCofins, "vPis"))),
                Campo("COFINS - Débito Apuração Própria", tpRetPc == "1" ? Dinheiro(0m) : Dinheiro(T(pisCofins, "vCofins"))),
                Campo("Descrição Contrib. Sociais - Retidas", Corta(D(TpRetPisCofins, tpRetPc), 35), 2)));
        h.Append("</div>");

        // ---------- IBS / CBS ----------
        var gIbsCbs = ibscbsDps?.Element(N + "valores")?.Element(N + "trib")?.Element(N + "gIBSCBS");
        var valIbs = ibscbsNfse?.Element(N + "valores");
        var totC = ibscbsNfse?.Element(N + "totCIBS");
        var gIbs = totC?.Element(N + "gIBS");
        var cstClass = T(gIbsCbs, "CST") is { Length: > 0 } cst ? $"{cst} / {T(gIbsCbs, "cClassTrib")}" : "";
        var cLocIncIbs = T(ibscbsNfse, "cLocalidadeIncid");
        var indOp = T(ibscbsDps, "cIndOp") is { Length: > 0 } io
            ? $"{io} / {cLocIncIbs} / {T(ibscbsNfse, "xLocalidadeIncid")}{(Municipios.Value.TryGetValue(cLocIncIbs, out var mib) ? " / " + mib.Uf : "")}"
            : "";
        decimal? exclusoes = ibscbsNfse == null ? null
            : new[] { T(valDps?.Element(N + "vDescCondIncond"), "vDescIncond"), T(valIbs, "vCalcReeRepRes"), T(valNfse, "vISSQN"), T(pisCofins, "vPis"), T(pisCofins, "vCofins") }
                .Sum(v => Dec(v) ?? 0);
        string Barra(params string[] partes) => partes.All(p => p == "") ? "" : string.Join(" / ", partes.Select(p => p == "" ? "-" : p));
        h.Append("<div class=\"bloco\">");
        h.Append(Linha(Titulo("Tributação IBS / CBS"), Campo("CST / cClassTrib", cstClass),
            Campo("Indicador de Operação / Código IBGE Incidência / Município Incidência / Sigla UF", Corta(indOp, 56), 2)));
        h.Append(Linha(Campo("Exclusões e Reduções da Base de Cálculo", exclusoes is { } ex ? Dinheiro(ex) : ""),
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
        var descCI = valDps?.Element(N + "vDescCondIncond");
        var totalIbsCbs = Dec(T(gIbs, "vIBSTot")) is { } vi ? vi + (Dec(T(totC?.Element(N + "gCBS"), "vCBS")) ?? 0) : (decimal?)null;
        h.Append("<div class=\"bloco total\">");
        h.Append(Linha(Titulo("Valor Total da NFS-e"), Campo("Valor da Operação / Serviço", Dinheiro(T(valDps?.Element(N + "vServPrest"), "vServ")), destaque: true),
            Campo("Desconto Incondicionado", Dinheiro(T(descCI, "vDescIncond"))), Campo("Desconto Condicionado", Dinheiro(T(descCI, "vDescCond")))));
        h.Append(Linha(Campo("Total das Retenções (ISSQN / Federais)", Dinheiro(T(valNfse, "vTotalRet"))),
            Campo("Valor Líquido da NFS-e", Dinheiro(T(valNfse, "vLiq")), destaque: true),
            Campo("Total do IBS/CBS", totalIbsCbs is { } tic ? Dinheiro(tic) : ""),
            Campo("Valor Líquido da NFS-e + IBS/CBS", Dinheiro(T(totC, "vTotNF")), sombra: true, destaque: true)));
        h.Append("</div>");

        // ---------- informações complementares ----------
        h.Append($"""
            <div class="bloco info">
              <div class="titulo-bloco sombra">INFORMAÇÕES COMPLEMENTARES</div>
              <div class="v pre">{E(Corta(InfoComplementares(inf, dps, serv), 1997))}</div>
              <div class="v totais">{E(TotaisTributos(trib?.Element(N + "totTrib")))}</div>
            </div>
            """);

        // ---------- canhoto (opcional) ----------
        h.Append(Linha(Campo("Data Cientificação", " ", rotuloBloco: true), Campo("Identificação e Assinatura", " ", rotuloBloco: true),
            Campo("Nº NFS-e / Chave NFS-e", $"{T(inf, "nNFSe")} / {chave}", 2, rotuloBloco: true)).Replace("class=\"linha\"", "class=\"linha canhoto\""));

        if (marcaDagua != null) h.Append($"<div class=\"marca\">{E(marcaDagua)}</div>");
        h.Append("</div></body></html>");
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
            Campo("Telefone", T(p, "fone"))));
        sb.Append(Linha(Campo("Nome / Nome Empresarial", Corta(T(p, "xNome"), 77), 2), Campo("Município / Sigla UF", Municipio(end)),
            Campo("Código IBGE / CEP", CodigoCep(end))));
        sb.Append(Linha(Campo("Endereço", Corta(Endereco(end), 77), 2), Campo("E-mail", T(p, "email"), 2)));
        return sb.Append("</div>").ToString();
    }

    private static string Aviso(string texto) => $"<div class=\"bloco aviso\"><div class=\"titulo-bloco sombra\">{E(texto)}</div></div>";

    private static string Linha(params string[] celulas) => $"<div class=\"linha\">{string.Concat(celulas)}</div>";

    private static string Titulo(string texto) => $"<div class=\"c titulo-bloco sombra\">{E(texto.ToUpperInvariant())}</div>";

    private static string Campo(string rotulo, string valor, int colunas = 1, bool sombra = false, bool destaque = false, bool rotuloBloco = false) =>
        $"<div class=\"c{(colunas > 1 ? " s" + colunas : "")}{(sombra ? " sombra" : "")}\"><span class=\"{(rotuloBloco ? "rb" : "r")}\">{E(rotuloBloco ? rotulo.ToUpperInvariant() : rotulo)}</span>" +
        $"<span class=\"v{(destaque ? " forte" : "")}\">{E(valor == "" ? "-" : valor)}</span></div>";

    private static string Id(string rotulo, string valor, int colunas = 1, bool sombra = false) =>
        $"<div class=\"c{(colunas > 1 ? " s" + colunas : "")}{(sombra ? " sombra" : "")}\"><span class=\"ri\">{E(rotulo.ToUpperInvariant())}</span><span class=\"v\">{E(valor == "" ? "-" : valor)}</span></div>";

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

    // Nota 10: linha obrigatória. pTotTribSN (Simples) e indTotTrib não estão na lista da NT; mostramos o que o XML traz.
    private static string TotaisTributos(XElement? totTrib)
    {
        const string prefixo = "Totais Aproximados dos Tributos cfe. Lei nº 12.741/2012: ";
        var v = totTrib?.Element(N + "vTotTrib");
        var p = totTrib?.Element(N + "pTotTrib");
        if (v != null) return prefixo + $"Federais: {Dinheiro(T(v, "vTotTribFed"))}; Estaduais: {Dinheiro(T(v, "vTotTribEst"))}; Municipais: {Dinheiro(T(v, "vTotTribMun"))}";
        if (p != null) return prefixo + $"Federais: {Pct(T(p, "pTotTribFed"))}; Estaduais: {Pct(T(p, "pTotTribEst"))}; Municipais: {Pct(T(p, "pTotTribMun"))}";
        if (T(totTrib, "pTotTribSN") is { Length: > 0 } sn) return prefixo + $"Simples Nacional: {Pct(sn)}";
        return prefixo + "não informados pelo emitente (Decreto nº 8.264/2014)";
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

    // end/endNac (tomador, intermediário) ou end/endExt (exterior)
    private static string Municipio(XElement? end)
    {
        if (end?.Element(N + "endNac") is { } nac) return NomeMunicipio(T(nac, "cMun"));
        if (end?.Element(N + "endExt") is { } ext) return T(ext, "xCidade") is { Length: > 0 } cid ? $"{cid} / {T(ext, "cPais")}" : "";
        return "";
    }

    private static string CodigoCep(XElement? end)
    {
        if (end?.Element(N + "endNac") is { } nac) return T(nac, "cMun") is { Length: > 0 } cm ? $"{cm} / {Cep(T(nac, "CEP"))}" : "";
        if (end?.Element(N + "endExt") is { } ext) return T(ext, "cEndPost");
        return "";
    }

    // emit/enderNac (prestador, quando a DPS não traz o endereço): cMun, UF e CEP no mesmo grupo
    private static string MunicipioNac(XElement? enderNac) => T(enderNac, "cMun") is { Length: > 0 } cm ? NomeMunicipio(cm) : "";
    private static string CodigoCepNac(XElement? enderNac) => T(enderNac, "cMun") is { Length: > 0 } cm ? $"{cm} / {Cep(T(enderNac, "CEP"))}" : "";

    private static string NomeMunicipio(string cMun) =>
        cMun == "" ? "" : Municipios.Value.TryGetValue(cMun, out var m) ? $"{m.Nome} / {m.Uf}" : cMun;

    private static string Endereco(XElement? end) =>
        string.Join(", ", new[] { T(end, "xLgr"), T(end, "nro"), T(end, "xCpl"), T(end, "xBairro") }.Where(s => s != ""));

    private static string QrSvg(string conteudo)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(conteudo, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(dados).GetGraphic(1, "#000000", "#ffffff", drawQuietZones: false, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    // Medidas da NT 008, item 2.4.5 (cm). Fontes: Arial nos rótulos, Microsoft Sans Serif nos conteúdos (item 2.4).
    private static string Cabecalho() => """
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>DANFSe</title><style>
        @page { size: A4 portrait; margin: 0; }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body { width: 21cm; height: 29.7cm; background: #fff; color: #000; }
        body { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        .pagina { position: absolute; left: .3cm; top: .3cm; width: 20.4cm; height: 29.1cm; border: 1pt solid #000;
                  display: flex; flex-direction: column; overflow: hidden; font-family: "Microsoft Sans Serif", Arial, sans-serif; font-size: 7pt; }
        .sombra { background: #f2f2f2; }
        .cab { height: 1.16cm; display: grid; grid-template-columns: 5.11cm 10.19cm 5.1cm; align-items: center; flex: none; }
        .logo img { width: 4cm; height: .85cm; object-fit: contain; margin-left: .19cm; display: block; }
        .titulo { text-align: center; font: bold 9pt Arial, sans-serif; line-height: 1.25; }
        .semvalidade { color: #ff0000; font: bold 9pt Arial, sans-serif; }
        .ambiente { font-size: 6pt; line-height: 1.3; padding-right: .1cm; }
        .ambiente .mun { font-size: 8pt; }
        .bloco { border-top: .5pt solid #000; flex: none; position: relative; }
        .linha { display: grid; grid-template-columns: repeat(4, 1fr); height: .63cm; }
        .ident .l-chave { height: .77cm; }
        .ident .l-id { height: .67cm; }
        .c { padding: .05cm .1cm 0; overflow: hidden; white-space: nowrap; line-height: 1.2; }
        .c.s2 { grid-column: span 2; } .c.s3 { grid-column: span 3; } .c.s4 { grid-column: span 4; }
        .r, .ri, .rb { display: block; font-family: Arial, sans-serif; font-weight: bold; }
        .r { font-size: 6pt; }
        .ri, .rb { font-size: 7pt; }
        .v { display: block; font-size: 7pt; overflow: hidden; text-overflow: ellipsis; }
        .v.forte { font-weight: bold; }
        .titulo-bloco { font: bold 7pt Arial, sans-serif; padding: .08cm .1cm; }
        .linha .titulo-bloco { display: flex; align-items: flex-start; }
        .aviso .titulo-bloco { min-height: .32cm; }
        .qr { position: absolute; left: 17.18cm; top: .19cm; width: 1.52cm; height: 1.52cm; }
        .qr svg { width: 100%; height: 100%; display: block; }
        .qrtexto { position: absolute; left: 15.5cm; top: 1.88cm; width: 4.72cm; height: .68cm; font: 6pt "Microsoft Sans Serif", Arial, sans-serif; line-height: 1.15; }
        .servico { display: flex; flex-direction: column; flex: 3 1 0; min-height: 1.69cm; }
        .l-desccod { height: .38cm; color: #555; }
        .descricao { flex: 1; padding: .05cm .1cm; overflow: hidden; }
        .pre { white-space: pre-wrap; word-break: break-word; }
        .info { flex: 2 1 0; min-height: 1.5cm; display: flex; flex-direction: column; overflow: hidden; }
        .info .v { padding: .05cm .1cm; }
        .info .totais { margin-top: auto; padding-bottom: .1cm; }
        .canhoto { border-top: .5pt dashed #000; height: .67cm; flex: none; }
        .marca { position: absolute; left: 0; right: 0; top: 40%; text-align: center; transform: rotate(-35deg);
                 font: 64pt Arial, sans-serif; color: #a6a6a6; opacity: .55; pointer-events: none; letter-spacing: .1em; }
        </style></head><body>
        """;
}
