using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace NfseApp;

public record CertInfo(string Thumbprint, string Nome, string Documento, string TipoDocumento, DateTime Validade, string Emissor);

/// <summary>Certificados ICP-Brasil instalados no Windows do usuário (CurrentUser\My).</summary>
public static class Certificados
{
    public static List<CertInfo> Listar()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.Subject.Contains("ICP-Brasil") && c.NotAfter > DateTime.Now)
            .Select(Descrever)
            .OfType<CertInfo>()
            .OrderBy(c => c.Nome)
            .ToList();
    }

    public static X509Certificate2? Abrir(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var achados = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        return achados.Count > 0 ? achados[0] : null;
    }

    /// <summary>CN de e-CNPJ/e-CPF vem como "NOME:DOCUMENTO".</summary>
    public static CertInfo? Descrever(X509Certificate2 c)
    {
        var cn = c.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        var m = Regex.Match(cn, @"^(.*):(\d{11}|\d{14})$");
        if (!m.Success) return null;
        var doc = m.Groups[2].Value;
        return new CertInfo(c.Thumbprint, m.Groups[1].Value, doc, doc.Length == 14 ? "CNPJ" : "CPF",
            c.NotAfter, c.GetNameInfo(X509NameType.SimpleName, true) ?? "");
    }

    /// <summary>Prova que a chave privada está acessível (assina um desafio local, nada sai da máquina).</summary>
    public static void TestarChave(X509Certificate2 c)
    {
        using var rsa = c.GetRSAPrivateKey() ?? throw new InvalidOperationException("Certificado sem chave RSA.");
        var desafio = RandomNumberGenerator.GetBytes(32);
        var assinatura = rsa.SignData(desafio, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var pub = c.GetRSAPublicKey()!;
        if (!pub.VerifyData(desafio, assinatura, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("A chave privada não confere com o certificado.");
    }
}
