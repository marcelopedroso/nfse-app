namespace NfseApp;

/// <param name="Portal">Emissor Nacional (portal web), onde fica o PDF oficial da nota.</param>
public record Ambiente(string Id, string Nome, int TpAmb, string Sefin, string Adn, string Portal)
{
    public static readonly Ambiente Producao = new("prod", "Produção", 1,
        "https://sefin.nfse.gov.br/SefinNacional", "https://adn.nfse.gov.br", "https://www.nfse.gov.br/EmissorNacional");

    public static readonly Ambiente Homologacao = new("hom", "Homologação", 2,
        "https://sefin.producaorestrita.nfse.gov.br/SefinNacional", "https://adn.producaorestrita.nfse.gov.br",
        "https://www.producaorestrita.nfse.gov.br/EmissorNacional");

    // A edição do dia a dia não conhece homologação: qualquer pedido cai em produção.
    public static Ambiente De(string? id) => id == "prod" || !Edicao.TemHomologacao ? Producao : Homologacao;
}
