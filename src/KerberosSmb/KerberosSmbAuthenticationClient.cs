using System;
using System.IO;
using Kerberos.NET;
using Kerberos.NET.Client;
using Kerberos.NET.Configuration;
using Microsoft.Extensions.Logging;
using SMBLibrary.Client.Authentication;

namespace KerberosSmb;

/// <summary>
/// Authenticates SMB2 sessions using Kerberos, backed by the ticket cache the kerberos-buildpack
/// sidecar maintains (KRB5_CONFIG/KRB5CCNAME). No password or KDC round trip happens here - the
/// existing TGT already in the cache is reused to request a service ticket for the target SPN.
/// </summary>
public sealed class KerberosSmbAuthenticationClient : IAuthenticationClient
{
    private readonly KerberosClient _kerberosClient;
    private string _spn;
    private byte[]? _sessionKey;

    public KerberosSmbAuthenticationClient(string spn, ILoggerFactory loggerFactory)
    {
        _spn = spn;

        var krb5ConfigPath = Environment.GetEnvironmentVariable("KRB5_CONFIG")
            ?? throw new InvalidOperationException("KRB5_CONFIG environment variable is not set");
        var ticketCachePath = Environment.GetEnvironmentVariable("KRB5CCNAME")
            ?? throw new InvalidOperationException("KRB5CCNAME environment variable is not set");

        var config = Krb5Config.Parse(File.ReadAllText(krb5ConfigPath));
        _kerberosClient = new KerberosClient(config, loggerFactory)
        {
            CacheInMemory = false,
            Cache = new Krb5TicketCache(ticketCachePath, loggerFactory)
        };
    }

    public byte[] InitializeSecurityContext(byte[] securityBlob)
    {
        // Reuse the cached TGT to get a service ticket for the target SPN and return the GSS-wrapped
        // AP-REQ. We don't request mutual auth, so no authenticator subkey is generated and the
        // server signs with the same ticket session key we hold.
        var session = _kerberosClient.GetServiceTicket(new RequestServiceTicket
        {
            ServicePrincipalName = _spn
        }).GetAwaiter().GetResult();

        // [MS-SMB2] 3.1.4.2 requires the first 16 bytes of the GSS session key. A Kerberos AES256
        // key is 32 bytes and SMBLibrary derives its signing key from whatever we return without
        // truncating (its built-in NTLM path always yields 16 bytes), so we truncate here.
        var fullKey = session.SessionKey.KeyValue.Span;
        _sessionKey = fullKey[..Math.Min(16, fullKey.Length)].ToArray();

        return session.ApReq.EncodeGssApi().ToArray();
    }

    public byte[]? GetSessionKey() => _sessionKey;

    public void ResetSecurityContext(string spn)
    {
        _spn = spn;
        _sessionKey = null;
    }
}
