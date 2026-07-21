using System.Security.Cryptography.X509Certificates;
using SamsungSwitchWatch.Agent.Configuration;

namespace SamsungSwitchWatch.Agent.Security;

public static class AgentCertificateLoader
{
    public static X509Certificate2 Load(HttpsOptions options, string? contentRoot = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.CertificateStoreThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint, options.CertificateStoreThumbprint, validOnly: false);
            if (matches.Count != 1)
            {
                foreach (var match in matches)
                {
                    match.Dispose();
                }
                throw new InvalidOperationException("CERTIFICATE_UNAVAILABLE");
            }

            var certificate = matches[0];
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException("CERTIFICATE_PRIVATE_KEY_UNAVAILABLE");
            }
            return certificate;
        }

        var path = Path.IsPathRooted(options.CertificatePath) || string.IsNullOrWhiteSpace(contentRoot)
            ? options.CertificatePath
            : Path.GetFullPath(Path.Combine(contentRoot, options.CertificatePath));
        var password = Environment.GetEnvironmentVariable(options.CertificatePasswordEnvironmentVariable);
        var loaded = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!loaded.HasPrivateKey)
        {
            loaded.Dispose();
            throw new InvalidOperationException("CERTIFICATE_PRIVATE_KEY_UNAVAILABLE");
        }
        return loaded;
    }
}
