using System.Security.Cryptography.Xml;
using System.Xml;

namespace NfseApp;

/// <summary>
/// Autoteste sem rede: monta e assina uma DPS a partir da última nota já baixada e confere a assinatura.
/// Existe para exercitar a assinatura também na edição só-produção, que nunca emite em teste,
/// e para pegar o que o trimming possa ter removido. Uso: NfseApp.exe --autoteste saida-hom.xml saida-prod.xml
/// (a DPS de produção nunca é enviada: só é gravada para a bateria validar contra o schema de produção).
/// </summary>
public static class Autoteste
{
    public static int Rodar(string saida, string? saidaProducao)
    {
        try
        {
            var cert = Certificados.Listar().Where(c => c.TipoDocumento == "CNPJ")
                .Select(c => Certificados.Abrir(c.Thumbprint)).FirstOrDefault(c => c != null)
                ?? throw new InvalidOperationException("Nenhum certificado e-CNPJ instalado.");
            var doc = Certificados.Descrever(cert)!.Documento;
            var repo = new Repositorio(doc, Ambiente.Producao);
            var baseNota = repo.Todas().Where(n => n.PrestDoc == doc && !n.Cancelada).MaxBy(n => n.DhProc)
                ?? throw new InvalidOperationException("Nenhuma nota baixada para usar de base (sincronize antes).");

            var dados = new DadosNovaNota(DateTime.Today.ToString("yyyy-MM-dd"), 1234.56m, 100m, "Autoteste", 10.94m,
                "42999990000", "teste.nfseapp@example.com");
            var (id, dps) = Dps.Montar(repo.Xml(baseNota.Chave)!, dados, Ambiente.Homologacao, 999999);
            var assinada = Dps.Assinar(dps, id, cert);
            if (saidaProducao != null)
            {
                var (idProd, dpsProd) = Dps.Montar(repo.Xml(baseNota.Chave)!, dados, Ambiente.Producao, 999999);
                File.WriteAllText(saidaProducao, Dps.Assinar(dpsProd, idProd, cert));
            }

            // Confere com a mesma biblioteca que assinou: é ela que o trimming pode ter quebrado.
            var xml = new XmlDocument { PreserveWhitespace = true };
            xml.LoadXml(assinada);
            var verificador = new SignedXml(xml);
            verificador.LoadXml((XmlElement)xml.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
            if (!verificador.CheckSignature()) throw new InvalidOperationException("A assinatura gerada não confere.");
            if (ApiNacional.Descompactar(ApiNacional.Compactar(assinada)) != assinada)
                throw new InvalidOperationException("Compactação de ida e volta não bate.");

            File.WriteAllText(saida, assinada);
            Console.WriteLine($"AUTOTESTE OK {id}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"AUTOTESTE FALHOU {e}");
            return 1;
        }
    }
}
