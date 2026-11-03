using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;

namespace NfseApp;

public record DadosNovaNota(string DCompet, decimal VServ, decimal VDescIncond, string Descricao, decimal? PTotTribSN);

/// <summary>
/// Monta uma DPS nova a partir da DPS de uma NFS-e já autorizada ("copiar a última nota"),
/// já no leiaute exigido pelo SefinNacional 1.6.0 (produção e produção restrita em 11/2026).
/// </summary>
public static class Dps
{
    private static readonly XNamespace N = Notas.N;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (string IdDps, XElement Dps) Montar(string nfseBaseXml, DadosNovaNota dados, Ambiente amb, long nDps)
    {
        var baseInf = XDocument.Parse(nfseBaseXml).Root!.Element(N + "infNFSe")!.Element(N + "DPS")!.Element(N + "infDPS")!;
        var inf = new XElement(baseInf);

        inf.Element(N + "tpAmb")!.Value = amb.TpAmb.ToString();
        inf.Element(N + "dhEmi")!.Value = DateTimeOffset.Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv);
        inf.Element(N + "verAplic")!.Value = "NfseApp-0.1";
        inf.Element(N + "nDPS")!.Value = nDps.ToString(Inv);
        inf.Element(N + "dCompet")!.Value = dados.DCompet;
        if (inf.Element(N + "finNFSe") == null)
            inf.Element(N + "tpEmit")!.AddAfterSelf(new XElement(N + "finNFSe", "0")); // E1235: obrigatório no leiaute novo
        inf.Element(N + "subst")?.Remove(); // uma cópia nunca substitui a nota de origem

        var prest = inf.Element(N + "prest")!;
        var opSimpNac = prest.Element(N + "regTrib")?.Element(N + "opSimpNac")?.Value;
        var simples = opSimpNac is "2" or "3";

        // Homologação não deve mandar e-mail de teste para o cliente de verdade.
        if (amb.TpAmb == 2) inf.Element(N + "toma")?.Element(N + "email")?.Remove();

        inf.Element(N + "serv")!.Element(N + "cServ")!.Element(N + "xDescServ")!.Value = dados.Descricao;

        var valores = inf.Element(N + "valores")!;
        valores.Element(N + "vServPrest")!.Element(N + "vServ")!.Value = Dinheiro(dados.VServ);
        AjustarDesconto(valores, dados.VDescIncond);

        var trib = valores.Element(N + "trib")!;
        var tribMun = trib.Element(N + "tribMun");
        // E0625: ME/EPP sem retenção do ISS não pode informar alíquota.
        if (opSimpNac == "3" && tribMun?.Element(N + "tpRetISSQN")?.Value == "1")
            tribMun.Element(N + "pAliq")?.Remove();

        RecalcularPisCofins(trib, dados.VServ - dados.VDescIncond);

        // E0712: no Simples o total de tributos é o percentual do Simples (pTotTribSN), não o indicador.
        var totTrib = trib.Element(N + "totTrib");
        if (simples && totTrib != null)
        {
            if (dados.PTotTribSN is not { } pct)
                throw new InvalidOperationException("Informe o % de tributos do Simples Nacional (pergunte ao contador).");
            totTrib.ReplaceNodes(new XElement(N + "pTotTribSN", Dinheiro(pct)));
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
