namespace NfseApp;

public record Ambiente(string Id, string Nome, int TpAmb, string Sefin, string Adn)
{
    public static readonly Ambiente Producao = new("prod", "Produção", 1,
        "https://sefin.nfse.gov.br/SefinNacional", "https://adn.nfse.gov.br");

    public static readonly Ambiente Homologacao = new("hom", "Homologação", 2,
        "https://sefin.producaorestrita.nfse.gov.br/SefinNacional", "https://adn.producaorestrita.nfse.gov.br");

    // A edição do dia a dia não conhece homologação: qualquer pedido cai em produção.
    public static Ambiente De(string? id) => id == "prod" || !Edicao.TemHomologacao ? Producao : Homologacao;
}
