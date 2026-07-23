using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KerberosSmb;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KerberosSmbDemo.Controllers;

[ApiController]
[Route("[controller]")]
public class HomeController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<HomeController> _logger;
    private readonly HttpClient _sidecarClient;
    private readonly KerberosSmbOptions _smbOptions;

    public HomeController(ILogger<HomeController> logger, IConfiguration configuration, IOptions<KerberosSmbOptions> smbOptions)
    {
        _logger = logger;
        _configuration = configuration;
        _smbOptions = smbOptions.Value;
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
    /// Get the contents of a file on disk inside the app container
    /// </summary>
    [HttpGet("/getfile")]
    public ActionResult<byte[]> ReadFile(string filePath)
    {
        return !System.IO.File.Exists(filePath)
            ? NotFound($"{filePath} not found")
            : File(System.IO.File.OpenRead(filePath), "application/octet-stream", Path.GetFileName(filePath));
    }

    /// <summary>
    /// Test the connection to the Kerberos domain controller
    /// </summary>
    [HttpGet("/testkdc")]
    public async Task<string> TestKDC(string? kdc)
    {
        if (string.IsNullOrEmpty(kdc))
        {
            kdc = Environment.GetEnvironmentVariable("KRB_KDC");
            if (string.IsNullOrEmpty(kdc))
            {
                return "KRB_KDC env var is not configured";
            }
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

    /// <summary>
    /// Test the connection to the configured (or supplied) SMB file server on port 445
    /// </summary>
    [HttpGet("/testsmb")]
    public async Task<string> TestSmb(string? server)
    {
        if (string.IsNullOrEmpty(server))
        {
            server = _smbOptions.Server;
            if (string.IsNullOrEmpty(server))
            {
                return "SMB_SERVER env var is not configured";
            }
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(server, 445);
            return $"Successfully connected to {server} on port 445";
        }
        catch (Exception e)
        {
            return $"Failed connection test to {server} on port 445\n{e}";
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

    /// <summary>
    /// Verify the existence of key Kerberos files, dump their contents, and probe SMB connectivity
    /// </summary>
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
        if (!string.IsNullOrEmpty(krb5Conf) && System.IO.File.Exists(krb5Conf))
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

        builder.AppendLine("==== SMB configuration ====");
        builder.AppendLine($"SMB_SERVER={_smbOptions.Server}");
        builder.AppendLine($"SMB_SHARE={_smbOptions.Share}");
        builder.AppendLine($"SMB_SPN={_smbOptions.Spn}");
        builder.AppendLine("");

        builder.AppendLine("==== SMB connectivity (port 445) ====");
        builder.AppendLine(await TestSmb(_smbOptions.Server));

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
    /// Get all environment variables available to the demo app
    /// </summary>
    [HttpGet("/env")]
    public Dictionary<string, string?> EnvVars()
    {
        return Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(x => x.Key.ToString()!, x => x.Value?.ToString());
    }

    /// <summary>
    /// Run a command inside the app container
    /// </summary>
    [HttpGet("/run")]
    public async Task<string> Run(string command, string? input = null)
    {
        var commandSegments = command.Split(" ");
        var processName = commandSegments[0];
        var args = commandSegments[1..];
        try
        {
            var process = new Process();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.FileName = processName;
            process.StartInfo.Arguments = string.Join(" ", args);
            foreach (var (key, value) in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                         .Select(x => ((string)x.Key, (string?)x.Value)))
            {
                process.StartInfo.EnvironmentVariables[key] = value;
            }

            process.Start();

            if (input != null)
            {
                await process.StandardInput.WriteLineAsync(Environment.ExpandEnvironmentVariables(input));
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
            if (!process.HasExited)
            {
                process.Kill();
            }

            return $"{output}\n{error}";
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    /// <summary>
    /// Use the sidecar to get a ticket for an arbitrary SPN
    /// </summary>
    [HttpGet("/ticket")]
    public async Task<ActionResult<string>> GetTicket(string? spn)
    {
        return await _sidecarClient.GetStringAsync($"ticket?spn={spn}");
    }
}
