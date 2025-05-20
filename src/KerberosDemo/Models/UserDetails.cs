using System.Collections.Generic;

namespace KerberosDemo.Models;

public class UserDetails
{
    public string Name { get; set; } = null!;
    public List<ClaimSummary> Claims { get; set; } = null!;
}