using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;

namespace NfseApp;

public record DadosNovaNota(string DCompet, decimal VServ, decimal VDescIncond, string Descricao, decimal? PTotTribSN,
    string? FoneTomador = null, string? EmailTomador = null);

/// <summary>
/// Diferenças de leiaute/regras entre os ambientes da Sefin. Em 11/2026 a produção restrita já roda as regras novas
/// (RTC) e a produção ainda não: produção RECUSA finNFSe, que a produção restrita EXIGE.
/// </summary>
/// <param name="FinNFSe">Incluir finNFSe depois de tpEmit (E1235 diz qual lado o ambiente quer).</param>
/// <param name="SemAliquotaMeEpp">Tirar pAliq de ME/EPP sem retenção de ISS (E0625).</param>
/// <param name="TotalTributosSimples">Exigir pTotTribSN no lugar de indTotTrib para o Simples (E0712).</param>
public record Leiaute(bool FinNFSe, bool SemAliquotaMeEpp, bool TotalTributosSimples)
{
    /// <summary>Produção: a estrutura da nota anterior, que a própria produção já aceitou. Homologação: regras novas.</summary>
    public static Leiaute Padrao(Ambiente amb) => amb.TpAmb == 2 ? new(true, true, true) : new(false, false, false);

    /// <summary>
    /// Ajuste pedido por uma recusa da Sefin, ou null se a recusa não é de leiaute. Recusa não cria nota,
    /// então reenviar a mesma DPS ajustada (mesmo número) é seguro.
    /// </summary>
    public Leiaute? AjustarPara(IEnumerable<string> erros, List<string> explicacoes)
    {
        var texto = string.Join(" ", erros);
        var novo = this;
        if (texto.Contains("E1235") && texto.Contains("invalid child element 'finNFSe'") && FinNFSe)
        { novo = novo with { FinNFSe = false }; explicacoes.Add("este ambiente não aceita o campo finNFSe; tirei"); }
        else if (texto.Contains("E1235") && texto.Contains("finNFSe") && !FinNFSe)
        { novo = novo with { FinNFSe = true }; explicacoes.Add("este ambiente exige o campo finNFSe; incluí"); }
        if (texto.Contains("E0625") && !SemAliquotaMeEpp)
        { novo = novo with { SemAliquotaMeEpp = true }; explicacoes.Add("ME/EPP sem retenção não pode informar alíquota de ISS; tirei"); }
        if (texto.Contains("E0712") && !TotalTributosSimples)
        { novo = novo with { TotalTributosSimples = true }; explicacoes.Add("o total de tributos do Simples tem que ir como percentual; usei o % informado"); }
        return novo == this ? null : novo;
    }
}

/// <summary>
/// Monta uma DPS nova a partir da DPS de uma NFS-e já autorizada ("copiar a última nota"),
/// no leiaute do ambiente de destino (ver <see cref="Leiaute"/>).
/// </summary>
public static class Dps
{
    private static readonly XNamespace N = Notas.N;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (string IdDps, XElement Dps) Montar(string nfseBaseXml, DadosNovaNota dados, Ambiente amb, long nDps, Leiaute? leiaute = null)
    {
        leiaute ??= Leiaute.Padrao(amb);
        var baseInf = XDocument.Parse(nfseBaseXml).Root!.Element(N + "infNFSe")!.Element(N + "DPS")!.Element(N + "infDPS")!;
        var inf = new XElement(baseInf);

        inf.Element(N + "tpAmb")!.Value = amb.TpAmb.ToString();
        inf.Element(N + "dhEmi")!.Value = DateTimeOffset.Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv);
        inf.Element(N + "verAplic")!.Value = "NfseApp-0.1";
        inf.Element(N + "nDPS")!.Value = nDps.ToString(Inv);
        inf.Element(N + "dCompet")!.Value = dados.DCompet;
        if (leiaute.FinNFSe && inf.Element(N + "finNFSe") == null)
            inf.Element(N + "tpEmit")!.AddAfterSelf(new XElement(N + "finNFSe", "0"));
        else if (!leiaute.FinNFSe)
            inf.Element(N + "finNFSe")?.Remove();
        inf.Element(N + "subst")?.Remove(); // uma cópia nunca substitui a nota de origem

        var prest = inf.Element(N + "prest")!;
        var opSimpNac = prest.Element(N + "regTrib")?.Element(N + "opSimpNac")?.Value;
        var simples = opSimpNac is "2" or "3";

        // Contato do tomador vem da tela (o cliente pode ter mudado). No leiaute, fone e email são os dois últimos campos.
        if (inf.Element(N + "toma") is { } toma)
        {
            toma.Element(N + "fone")?.Remove();
            toma.Element(N + "email")?.Remove();
            if (!string.IsNullOrEmpty(dados.FoneTomador)) toma.Add(new XElement(N + "fone", dados.FoneTomador));
            if (!string.IsNullOrEmpty(dados.EmailTomador)) toma.Add(new XElement(N + "email", dados.EmailTomador));
        }

        inf.Element(N + "serv")!.Element(N + "cServ")!.Element(N + "xDescServ")!.Value = dados.Descricao;

        var valores = inf.Element(N + "valores")!;
        valores.Element(N + "vServPrest")!.Element(N + "vServ")!.Value = Dinheiro(dados.VServ);
        AjustarDesconto(valores, dados.VDescIncond);

        var trib = valores.Element(N + "trib")!;
        var tribMun = trib.Element(N + "tribMun");
        // E0625 (regras novas): ME/EPP sem retenção do ISS não pode informar alíquota. Fora disso, fica a da nota anterior.
        if (leiaute.SemAliquotaMeEpp && opSimpNac == "3" && tribMun?.Element(N + "tpRetISSQN")?.Value == "1")
            tribMun.Element(N + "pAliq")?.Remove();

        RecalcularPisCofins(trib, dados.VServ - dados.VDescIncond);

        // Total aproximado de tributos (Lei 12.741): no Simples vai o % informado (pTotTribSN). Sem %, fica o da nota
        // anterior, a não ser que o ambiente exija o % (E0712, regras novas).
        var totTrib = trib.Element(N + "totTrib");
        if (simples && totTrib != null)
        {
            if (dados.PTotTribSN is { } pct)
                totTrib.ReplaceNodes(new XElement(N + "pTotTribSN", Dinheiro(pct)));
            else if (leiaute.TotalTributosSimples)
                throw new InvalidOperationException("Informe o % de tributos do Simples Nacional (pergunte ao contador).");
        }

        var cLocEmi = inf.Element(N + "cLocEmi")!.Value;
        var doc = Notas.Documento(prest);
        var serie = inf.Element(N + "serie")!.Value;
        var id = "DPS" + cLocEmi + (doc.Length == 14 ? "2" : "1") + doc.PadLeft(14, '0') + serie.PadLeft(5, '0') + nDps.ToString(Inv).PadLeft(15, '0');
        inf.SetAttributeValue("Id", id);

        return (id, new XElement(N + "DPS", new XAttribute("versao", "1.01"), inf));
    }

    /// <summary>Assinatura XMLDSig enveloped do infDPS (RSA-SHA1 + C14N, igual ao Emissor Nacional).</summary>
    public static string Assinar(XElement dps, string idDps, X509Certificate2 cert)
    {
        var xml = new XmlDocument { PreserveWhitespace = true };
        xml.LoadXml(dps.ToString(SaveOptions.DisableFormatting));

        var signed = new SignedXml(xml) { SigningKey = cert.GetRSAPrivateKey() };
        signed.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
        var referencia = new Reference("#" + idDps) { DigestMethod = SignedXml.XmlDsigSHA1Url };
        referencia.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        referencia.AddTransform(new XmlDsigC14NTransform());
        signed.AddReference(referencia);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();

        xml.DocumentElement!.AppendChild(xml.ImportNode(signed.GetXml(), true));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml.DocumentElement.OuterXml;
    }

    private static void AjustarDesconto(XElement valores, decimal desconto)
    {
        var grupo = valores.Element(N + "vDescCondIncond");
        if (desconto <= 0)
        {
            grupo?.Element(N + "vDescIncond")?.Remove();
            if (grupo != null && !grupo.HasElements) grupo.Remove();
            return;
        }
        if (grupo == null)
        {
            grupo = new XElement(N + "vDescCondIncond");
            valores.Element(N + "vServPrest")!.AddAfterSelf(grupo);
        }
        var incond = grupo.Element(N + "vDescIncond");
        if (incond == null) grupo.AddFirst(incond = new XElement(N + "vDescIncond"));
        incond.Value = Dinheiro(desconto);
    }

    private static void RecalcularPisCofins(XElement trib, decimal baseCalculo)
    {
        var pc = trib.Element(N + "tribFed")?.Element(N + "piscofins");
        if (pc?.Element(N + "vBCPisCofins") is not { } vbc) return;
        vbc.Value = Dinheiro(baseCalculo);
        Aplicar(pc, "pAliqPis", "vPis", baseCalculo);
        Aplicar(pc, "pAliqCofins", "vCofins", baseCalculo);
    }

    private static void Aplicar(XElement pc, string aliquota, string valor, decimal baseCalculo)
    {
        if (pc.Element(N + aliquota) is not { } a || pc.Element(N + valor) is not { } v) return;
        v.Value = Dinheiro(Math.Round(baseCalculo * decimal.Parse(a.Value, Inv) / 100m, 2, MidpointRounding.AwayFromZero));
    }

    private static string Dinheiro(decimal v) => v.ToString("0.00", Inv);
}
