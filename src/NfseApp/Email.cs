using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NfseApp;

public record EmailNota(string Para, string Assunto, string CorpoHtml, string CorpoTexto);

/// <summary>
/// E-mail da nota para o tomador: o app só deixa a mensagem pronta no Outlook (com o PDF anexado) e quem envia é a pessoa.
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

    /// <summary>Pasta temporária dos anexos (o Outlook copia os arquivos para a mensagem). Limpa as de envios anteriores.</summary>
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
    /// Abre um e-mail novo no Outlook clássico com os anexos, por automação COM via PowerShell (o .exe é publicado com
    /// trimming, que desliga o COM embutido do .NET). A assinatura padrão da pessoa entra no fim. Null = deu certo.
    /// </summary>
    public static async Task<string?> AbrirNoOutlook(EmailNota email, IEnumerable<string> anexos, string pasta)
    {
        var dados = Path.Combine(pasta, "email.json");
        var script = Path.Combine(pasta, "email.ps1");
        await File.WriteAllTextAsync(dados, JsonSerializer.Serialize(new { para = email.Para, assunto = email.Assunto, corpo = email.CorpoHtml, anexos }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(script, ScriptOutlook, new UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, dados }) psi.ArgumentList.Add(a);
        try
        {
            using var processo = Process.Start(psi)!;
            var erro = processo.StandardError.ReadToEndAsync();
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await processo.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException) { processo.Kill(true); return "o Outlook demorou demais para responder"; }
            return processo.ExitCode == 0 ? null : (await erro).Trim() is { Length: > 0 } e ? e : $"código {processo.ExitCode}";
        }
        catch (Exception e) { return e.Message; }
        finally
        {
            try { File.Delete(script); File.Delete(dados); } catch { /* temporário */ }
        }
    }

    /// <summary>Sem Outlook clássico: abre a pasta com os anexos e um e-mail novo no programa padrão (mailto não anexa).</summary>
    public static void AbrirAlternativa(EmailNota email, string anexoPrincipal)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + anexoPrincipal } }); } catch { /* sem Explorer */ }
        var mailto = $"mailto:{Uri.EscapeDataString(email.Para)}?subject={Uri.EscapeDataString(email.Assunto)}&body={Uri.EscapeDataString(email.CorpoTexto)}";
        Janela.AbrirNavegador(mailto);
    }

    // Display() antes do texto: é ele que põe a assinatura padrão no corpo; o texto entra logo depois do <body>.
    private const string ScriptOutlook = """
        param([string]$arquivo)
        $ErrorActionPreference = 'Stop'
        $d = Get-Content -LiteralPath $arquivo -Raw -Encoding UTF8 | ConvertFrom-Json
        $outlook = New-Object -ComObject Outlook.Application
        $m = $outlook.CreateItem(0)
        $m.To = $d.para
        $m.Subject = $d.assunto
        foreach ($a in $d.anexos) { [void]$m.Attachments.Add([string]$a) }
        $m.Display()
        $html = [string]$m.HTMLBody
        $b = $html.IndexOf('<body', [StringComparison]::OrdinalIgnoreCase)
        if ($b -ge 0) {
            $i = $html.IndexOf('>', $b) + 1
            $m.HTMLBody = $html.Substring(0, $i) + $d.corpo + $html.Substring($i)
        } else {
            $m.HTMLBody = $d.corpo + $html
        }
        """;
}
