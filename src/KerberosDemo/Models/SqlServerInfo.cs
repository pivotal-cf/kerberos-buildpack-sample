namespace KerberosDemo.Models;

public class SqlServerInfo
{
    public string ConnectionString { get; set; } = null!;
    public string Server { get; set; } = null!;
    public string Database { get; set; } = null!;
    public string Version { get; set; } = null!;
}