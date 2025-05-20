﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using KerberosDemo.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KerberosDemo.Controllers;

[ApiController]
[Route("[controller]")]
public class HomeController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<HomeController> _logger;
    private readonly HttpClient _sidecarClient;

    public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _sidecarClient = new HttpClient
        {
            BaseAddress = new Uri(_configuration.GetValue<string>("SidecarUrl") ??
                                  throw new InvalidOperationException("Sidecar URL not set"))
        };
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("/")]
    public void RedirectToSwagger()
    {
        Response.Redirect("/swagger/index.html");
    }

    /// <summary>
    ///     Test IWA connection to SQL Server
    /// </summary>
    /// <remarks>
    ///     Things needed to get to work on Linux:
    ///     1. Package krb5-user installed (MIT Kerberos)
    ///     2. SQL Server is running under AD principal
    ///     3. SQL server principal account has SPN assigned in form of MSSQLSvc/<FQDN /> where FQDN is the result of reverse
    ///     DNS lookup of server address. THIS MAY DIFFER FROM THE HOST NAME. Use the following to obtain real FQDN:
    ///     a. obtain IP from server address: nslookup <serveraddress />
    ///     b. obtain FQDN from IP: nslookup <serverip />
    ///     4. MIT kerberos configuration filePath is present that at minimum looks similar to this:
    ///     <code>
    /// [libdefaults]
    /// default_realm = ALMIREX.DC
    /// 
    /// [realms]
    /// ALMIREX.DC = {
    ///     kdc = AD.ALMIREX.COM
    /// }
    /// </code>
    ///     5. Kerberos credential cache is populated with TGT (session ticket needed to obtain authentication tickets). Use
    ///     kinit to obtain TGT and populate ticket cache
    ///     6. Configure MIT kerberos environment variables
    ///     a) KRB5_CONFIG - path to config filePath from step 4
    ///     b) KRB5CCNAME - path to credential cache if different from default
    ///     7. SQL Server is configured to use SSL (required by Kerberos authentication)
    ///     a) If using a certificate that is not trusted on client, append TrustServerCertificate=True to connection string
    ///     Additional valuable Kerberos issue diagnostics can be acquired by setting KRB5_TRACE=/dev/stdout env var before
    ///     running this app
    /// </remarks>
    [HttpGet("/sql")]
    public ActionResult<SqlServerInfo> SqlTest(string? connectionString)
    {
        connectionString ??= _configuration.GetConnectionString("SqlServer");
        if (connectionString == null)
            return StatusCode(500,
                "Connection string not set. Set 'ConnectionStrings__SqlServer' environment variable");

        var sqlClient = new SqlConnection(connectionString);
        try
        {
            var serverInfo =
                sqlClient.QuerySingle<SqlServerInfo>(
                    "SELECT @@servername AS Server, @@version as Version, DB_NAME() as [Database]");
            serverInfo.ConnectionString = connectionString;
            return Ok(serverInfo);
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.ToString());
        }
    }

    /// <summary>
    ///     Get the contents of a file on disk
    /// </summary>
    [HttpGet("/getfile")]
    public ActionResult<byte[]> ReadFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return NotFound($"{filePath} not found");

        return File(System.IO.File.OpenRead(filePath), "application/octet-stream", Path.GetFileName(filePath));
    }

    /// <summary>
    ///     Test the connection to the Kerberos domain controller
    /// </summary>
    [HttpGet("/testkdc")]
    public async Task<string> TestKDC(string? kdc)
    {
        if (string.IsNullOrEmpty(kdc))
        {
            kdc = Environment.GetEnvironmentVariable("KRB5_KDC");
            if (string.IsNullOrEmpty(kdc))
                return "KRB5_KDC env var is not configured";
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(kdc, 88);
            return $"Successfully connected to {kdc} on port 88";
        }
        catch (Exception e)
        {
            return $"Failed connection test to {kdc} on port 88\n{e}";
        }
    }

    [HttpGet("/sidecarhealth")]
    public async Task<ActionResult<string>> SidecarHealth()
    {
        var response = await _sidecarClient.GetAsync("health/ready");
        var content = await response.Content.ReadAsStringAsync();
        return new ObjectResult(content)
        {
            StatusCode = (int)response.StatusCode
        };
    }

    [HttpGet("/diag")]
    public async Task<string> Diagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== TimeStamps ====");
        builder.AppendLine($"DateTime.Now: {DateTime.Now}");
        builder.AppendLine($"DateTime.UtcNow: {DateTime.UtcNow}");
        builder.AppendLine("");

        builder.AppendLine("==== KRB5 files ====");
        VerifyEnvVar("KRB5_CONFIG");
        VerifyEnvVar("KRB5CCNAME");
        VerifyEnvVar("KRB5_KTNAME");
        builder.AppendLine("");

        var krb5Conf = Environment.GetEnvironmentVariable("KRB5_CONFIG");
        if (!string.IsNullOrEmpty(krb5Conf))
        {
            builder.AppendLine($"==== {krb5Conf} content ====");
            builder.AppendLine(await System.IO.File.ReadAllTextAsync(krb5Conf));
        }

        builder.AppendLine("=== klist ===");
        builder.AppendLine(await Run("klist"));

        builder.AppendLine("==== KRB5_CLIENT_KTNAME keytab contents ===");
        const string readKtScript = """
                                    read_kt %KRB5_CLIENT_KTNAME%
                                    list
                                    q
                                    """;
        builder.AppendLine(await Run("ktutil", Environment.ExpandEnvironmentVariables(readKtScript)));

        return builder.ToString();

        void VerifyEnvVar(string var)
        {
            var varValue = Environment.GetEnvironmentVariable(var);
            builder.AppendLine($"{var}={varValue}");
            if (!string.IsNullOrEmpty(varValue))
            {
                var fileExists = System.IO.File.Exists(varValue);
                builder.AppendLine($"{varValue} = {(fileExists ? "exists" : "missing")}");
            }
        }
    }

    /// <summary>
    ///     Get all environment variables available to the demo app
    /// </summary>
    [HttpGet("/env")]
    public Dictionary<string, string?> EnvVars()
    {
        return Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(x => x.Key.ToString()!, x => x.Value?.ToString());
    }

    /// <summary>
    ///     Run a command inside the app container
    /// </summary>
    [HttpGet("/run")]
    public async Task<string> Run(string command, string? input = null)
    {
        var commandSegments = command.Split(" ");
        var processName = commandSegments[0];
        var args = commandSegments[1..];
        // Start the child process.
        try
        {
            var process = new Process();
            // Redirect the output stream of the child process.
            process.StartInfo.UseShellExecute = false;

            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.FileName = processName;
            process.StartInfo.Arguments = string.Join(" ", args);
            foreach (var (key, value) in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                         .Select(x => ((string)x.Key, (string?)x.Value)))
                process.StartInfo.EnvironmentVariables[key] = value;

            process.Start();

            // Do not wait for the child process to exit before reading to the end of its redirected stream.
            // Read the output stream first and then wait.
            if (input != null)
                await process.StandardInput.WriteLineAsync(Environment.ExpandEnvironmentVariables(input));

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
            if (!process.HasExited) process.Kill();

            return $"{output}\n{error}";
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    /// <summary>
    ///     Use the sidecar to get a ticket for an arbitrary SPN
    /// </summary>
    [HttpGet("/ticket")]
    public async Task<ActionResult<string>> GetTicket(string? spn)
    {
        return await _sidecarClient.GetStringAsync($"ticket?spn={spn}");
    }

    /// <summary>
    ///     Authenticates incoming caller via SPNEGO (Kerberos ticket via HTTP header)
    /// </summary>
    /// <param name="forceAuth">Force the user to authenticate</param>
    [HttpGet("/user")]
    public ActionResult<UserDetails> AuthenticateUser(bool forceAuth)
    {
        var identity = (ClaimsIdentity)User.Identity!;
        if (!identity.IsAuthenticated)
        {
            if (forceAuth) return Challenge();

            if (!Request.Headers.TryGetValue("Authorization", out _))
                return base.Unauthorized(
                    "Authorization header not included. Call with '?forceAuth=true' to force SPNEGO exchange by the browser");

            return Unauthorized("Not logged in.");
        }

        var user = new UserDetails
        {
            Name = identity.Name!,
            Claims = identity.Claims.Select(x => new ClaimSummary { Type = x.Type, Value = x.Value }).ToList()
        };
        return user;
    }
}