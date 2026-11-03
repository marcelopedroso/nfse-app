using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NfseApp;

/// <summary>Escolha da porta e detecção de uma instância desta mesma edição já aberta.</summary>
public static class Instancia
{
    public const string Assinatura = "NfseApp";

    /// <summary>A porta responde como esta mesma edição do app? (Outro programa na porta não conta.)</summary>
    public static async Task<bool> JaAberta(int porta)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{porta}/api/info"));
            var raiz = json.RootElement;
            return raiz.TryGetProperty("app", out var app) && app.GetString() == Assinatura
                && raiz.TryGetProperty("nome", out var nome) && nome.GetString() == Edicao.Nome;
        }
        catch { return false; }
    }

    public static bool PortaLivre(int porta)
    {
        try
        {
            var teste = new TcpListener(IPAddress.Loopback, porta);
            teste.Start();
            teste.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
