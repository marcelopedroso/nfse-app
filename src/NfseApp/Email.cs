using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace NfseApp;

public record EmailNota(string Para, string Assunto, string CorpoHtml, string CorpoTexto);

/// <summary>
/// E-mail da nota para o tomador: o app só deixa a mensagem pronta no Outlook (com o PDF e o XML anexados) e quem envia
/// é a pessoa. Regra do app: não roda scripts nem shells de comando; a mensagem é um arquivo .eml.
/// </summary>
public static class Email
{
    public static EmailNota Montar(Nota n, string urlConsulta)
    {
        var competencia = DateTime.TryParseExact(n.Competencia, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)
            ? c.ToString("MM/yyyy") : n.Competencia;
        var assunto = $"NFS-e nº {n.Numero} - {n.PrestNome}";
        var frase = $"Segue em anexo a NFS-e nº {n.Numero}, referente a {competencia}.";
        var texto = $"Prezados,\n\n{frase}\nChave de acesso: {n.Chave}\nConsulta pública: {urlConsulta}\n";
        var url = WebUtility.HtmlEncode(urlConsulta);
        var html = "<div style=\"font-family: Aptos, Calibri, Arial, sans-serif; font-size: 11pt\"><p>Prezados,</p>" +
            $"<p>{WebUtility.HtmlEncode(frase)}<br>Chave de acesso: {n.Chave}<br>Consulta pública: <a href=\"{url}\">{url}</a></p></div>";
        return new EmailNota(n.TomaEmail, assunto, html, texto);
    }

    /// <summary>Nome de arquivo dos anexos: "NFS-e 1 - MARCELO DIAS PEDROSO".</summary>
    public static string NomeArquivo(Nota n) =>
        string.Concat($"NFS-e {n.Numero} - {n.PrestNome}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

    /// <summary>Pasta temporária do .eml de cada envio. Limpa as de envios anteriores.</summary>
    public static string NovaPasta()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "NfseApp-email");
        if (Directory.Exists(raiz))
            foreach (var antiga in Directory.EnumerateDirectories(raiz).Where(d => Directory.GetCreationTime(d) < DateTime.Now.AddDays(-1)))
                try { Directory.Delete(antiga, true); } catch { /* ainda em uso: fica para a próxima */ }
        var pasta = Path.Combine(raiz, DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(pasta);
        return pasta;
    }

    /// <summary>
    /// Monta a mensagem como um arquivo .eml (MIME) marcado com "X-Unsent: 1": o Outlook abre como mensagem nova,
    /// pronta para conferir e enviar, com os anexos dentro do próprio arquivo. Sem "From": vale a conta do Outlook.
    /// A assinatura não entra sozinha (no Outlook: Inserir > Assinatura).
    /// </summary>
    public static string CriarEml(EmailNota email, string pasta, string nome, IEnumerable<(string Nome, string Tipo, byte[] Dados)> anexos)
    {
        var limite = "NfseApp-" + Guid.NewGuid().ToString("N");
        var eml = new StringBuilder();
        void Linha(string texto = "") => eml.Append(texto).Append("\r\n");
        void Base64(byte[] dados)
        {
            var b64 = Convert.ToBase64String(dados);
            for (var i = 0; i < b64.Length; i += 76) Linha(b64.Substring(i, Math.Min(76, b64.Length - i)));
        }

        if (email.Para != "") Linha($"To: {email.Para}");
        Linha($"Subject: {Cabecalho(email.Assunto)}");
        Linha("X-Unsent: 1");
        Linha("MIME-Version: 1.0");
        Linha($"Content-Type: multipart/mixed; boundary=\"{limite}\"");
        Linha();
        Linha($"--{limite}");
        Linha("Content-Type: text/html; charset=\"utf-8\"");
        Linha("Content-Transfer-Encoding: base64");
        Linha();
        Base64(Encoding.UTF8.GetBytes(email.CorpoHtml));
        foreach (var (arquivo, tipo, dados) in anexos)
        {
            Linha($"--{limite}");
            Linha($"Content-Type: {tipo}; name=\"{Ascii(arquivo)}\"");
            Linha("Content-Transfer-Encoding: base64");
            Linha($"Content-Disposition: attachment; filename=\"{Ascii(arquivo)}\"; filename*=UTF-8''{Uri.EscapeDataString(arquivo)}");
            Linha();
            Base64(dados);
        }
        Linha($"--{limite}--");

        var caminho = Path.Combine(pasta, nome + ".eml");
        File.WriteAllText(caminho, eml.ToString(), Encoding.ASCII);
        return caminho;
    }

    /// <summary>
    /// Abre o .eml no Outlook clássico ("OUTLOOK.EXE /eml") ou, sem ele, no programa de e-mail padrão do Windows.
    /// Null = deu certo.
    /// </summary>
    public static string? Abrir(string eml)
    {
        try
        {
            if (CaminhoOutlook() is { } outlook)
            {
                var psi = new ProcessStartInfo(outlook) { UseShellExecute = false };
                psi.ArgumentList.Add("/eml");
                psi.ArgumentList.Add(eml);
                Process.Start(psi);
            }
            else Process.Start(new ProcessStartInfo(eml) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Quando não deu para abrir o e-mail: mostra o .eml na pasta, para a pessoa abrir à mão.</summary>
    public static void MostrarNaPasta(string eml)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + eml } }); } catch { /* sem Explorer */ }
    }

    // Caminho do Outlook clássico pelo registro do Windows (App Paths), o mesmo que o "Executar" usa.
    private static string? CaminhoOutlook()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var raiz in new[] { Registry.CurrentUser, Registry.LocalMachine })
            if (raiz.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE")?.GetValue(null) is string caminho
                && File.Exists(caminho.Trim('"')))
                return caminho.Trim('"');
        return null;
    }

    // Cabeçalho com acento vai codificado (RFC 2047); o nome do anexo tem também a versão ASCII.
    private static string Cabecalho(string texto) =>
        texto.All(c => c < 128) ? texto : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(texto))}?=";

    private static string Ascii(string texto) => new(texto.Select(c => c < 128 && c != '"' ? c : '_').ToArray());
}
