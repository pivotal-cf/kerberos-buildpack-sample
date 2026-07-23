using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace KerberosSmb;

public class KerberosSmbOptions
{
    /// <summary>
    /// Gets or sets the file server hostname or FQDN
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMB share name
    /// </summary>
    public string Share { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target SPN for the file server, in the form cifs/&lt;fqdn&gt;
    /// </summary>
    public string Spn { get; set; } = string.Empty;

    public class Validator : IValidateOptions<KerberosSmbOptions>
    {
        public ValidateOptionsResult Validate(string? name, KerberosSmbOptions options)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(options.Server))
            {
                errors.Add("SMB server is not configured. Use SMB_SERVER environment variable to configure");
            }

            if (string.IsNullOrEmpty(options.Share))
            {
                errors.Add("SMB share is not configured. Use SMB_SHARE environment variable to configure");
            }

            if (string.IsNullOrEmpty(options.Spn))
            {
                errors.Add("SMB target SPN is not configured. Use SMB_SPN environment variable to configure");
            }

            return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
        }
    }
}
