using System.Globalization;
using System.Xml.Linq;

namespace NfseApp;

public record Nota(
    string Chave, long Numero, string DhProc, string DhEmi, string Competencia,
    string PrestDoc, string PrestNome, string TomaDoc, string TomaNome, string TomaEmail,
    decimal VServ, decimal VDesc, decimal VLiq, string Descricao, string CTribNac, string XTribNac,
    int Serie, long NDps, bool Cancelada, string? MotivoCancelamento);

public record Tomador(string Documento, string Nome, string Email, int Notas, decimal Total, string UltimaData, long UltimoNumero, decimal UltimoValor);

/// <summary>O que a nota nova herda da nota-base e a tela precisa mostrar antes de emitir.</summary>
public record Tributacao(string Regime, bool IssRetido, decimal? PAliqPis, decimal? PAliqCofins, string EmailTomador, string FoneTomador, string LocalPrestacao);

public static class Notas
{
    public static readonly XNamespace N = "http://www.sped.fazenda.gov.br/nfse";

    public static Nota Ler(string xml, IReadOnlyDictionary<string, string> cancelamentos)
    {
        var inf = XDocument.Parse(xml).Root!.Element(N + "infNFSe")!;
        var dps = inf.Element(N + "DPS")!.Element(N + "infDPS")!;
        var toma = dps.Element(N + "toma");
        var chave = inf.Attribute("Id")!.Value[3..];
        cancelamentos.TryGetValue(chave, out var motivo);
        return new Nota(
            chave,
            Num(inf.Element(N + "nNFSe")),
            Txt(inf.Element(N + "dhProc")),
            Txt(dps.Element(N + "dhEmi")),
            Txt(dps.Element(N + "dCompet")),
            Documento(dps.Element(N + "prest")),
            Txt(inf.Element(N + "emit")?.Element(N + "xNome")),
            Documento(toma),
            Txt(toma?.Element(N + "xNome")),
            Txt(toma?.Element(N + "email")),
            Dec(dps.Element(N + "valores")?.Element(N + "vServPrest")?.Element(N + "vServ")),
            Dec(dps.Element(N + "valores")?.Element(N + "vDescCondIncond")?.Element(N + "vDescIncond")),
            Dec(inf.Element(N + "valores")?.Element(N + "vLiq")),
            Txt(dps.Element(N + "serv")?.Element(N + "cServ")?.Element(N + "xDescServ")),
            Txt(dps.Element(N + "serv")?.Element(N + "cServ")?.Element(N + "cTribNac")),
            Txt(inf.Element(N + "xTribNac")),
            (int)Num(dps.Element(N + "serie")),
            Num(dps.Element(N + "nDPS")),
            motivo != null,
            motivo);
    }

    /// <summary>Evento de cancelamento (e101101) ou cancelamento por substituição (e105102): devolve chave → motivo.</summary>
    public static (string Chave, string Motivo)? LerCancelamento(string xml)
    {
        var raiz = XDocument.Parse(xml).Root!;
        var ped = raiz.Descendants(N + "infPedReg").FirstOrDefault();
        var ch = ped?.Element(N + "chNFSe")?.Value;
        if (ped == null || ch == null) return null;
        var evt = ped.Elements().FirstOrDefault(e => e.Name.LocalName is "e101101" or "e105102");
        if (evt == null) return null;
        var motivo = evt.Element(N + "xMotivo")?.Value ?? evt.Element(N + "xDesc")?.Value ?? "Cancelada";
        return (ch, motivo);
    }

    public static List<Tomador> Tomadores(IEnumerable<Nota> emitidas) =>
        emitidas.Where(n => !n.Cancelada && n.TomaDoc != "")
            .GroupBy(n => n.TomaDoc)
            .Select(g =>
            {
                var ultima = g.OrderByDescending(n => n.DhProc).First();
                return new Tomador(g.Key, ultima.TomaNome, ultima.TomaEmail, g.Count(), g.Sum(n => n.VServ), ultima.DhProc, ultima.Numero, ultima.VServ);
            })
            .OrderByDescending(t => t.UltimaData)
            .ToList();

    public static Tributacao LerTributacao(string nfseXml)
    {
        var inf = XDocument.Parse(nfseXml).Root!.Element(N + "infNFSe")!;
        var dps = inf.Element(N + "DPS")!.Element(N + "infDPS")!;
        var trib = dps.Element(N + "valores")?.Element(N + "trib");
        var pc = trib?.Element(N + "tribFed")?.Element(N + "piscofins");
        var regime = dps.Element(N + "prest")?.Element(N + "regTrib")?.Element(N + "opSimpNac")?.Value switch
        {
            "2" => "MEI (Simples Nacional)",
            "3" => "ME/EPP (Simples Nacional)",
            _ => "Não optante do Simples",
        };
        decimal? Pct(string nome) => pc?.Element(N + nome) is { } e ? Dec(e) : null;
        return new Tributacao(
            regime,
            trib?.Element(N + "tribMun")?.Element(N + "tpRetISSQN")?.Value is "2" or "3",
            Pct("pAliqPis"),
            Pct("pAliqCofins"),
            Txt(dps.Element(N + "toma")?.Element(N + "email")),
            Txt(dps.Element(N + "toma")?.Element(N + "fone")),
            Txt(inf.Element(N + "xLocPrestacao")));
    }

    public static string Documento(XElement? pessoa) =>
        pessoa?.Element(N + "CNPJ")?.Value ?? pessoa?.Element(N + "CPF")?.Value ?? pessoa?.Element(N + "NIF")?.Value ?? "";

    private static string Txt(XElement? e) => e?.Value ?? "";
    private static long Num(XElement? e) => long.TryParse(e?.Value, out var v) ? v : 0;
    private static decimal Dec(XElement? e) => decimal.TryParse(e?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
