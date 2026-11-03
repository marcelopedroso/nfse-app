namespace NfseApp;

/// <summary>
/// Duas edições do mesmo código, escolhidas na compilação:
/// - dia a dia (-p:SoProducao=true): só produção, sem seletor de ambiente;
/// - teste (padrão): produção + homologação, em outra porta e outra janela, para rodar junto com a do dia a dia.
/// </summary>
public static class Edicao
{
#if SO_PRODUCAO
    public static readonly bool TemHomologacao = false;
    public static readonly int Porta = 5199;
    public static readonly string Nome = "NFS-e";
    public static readonly string PerfilJanela = "janela";
#else
    public static readonly bool TemHomologacao = true;
    public static readonly int Porta = 5198;
    public static readonly string Nome = "NFS-e · Teste";
    public static readonly string PerfilJanela = "janela-teste";
#endif

    public static string Versao =>
        typeof(Edicao).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
}
