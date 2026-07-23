# Kerberos Buildpack Samples

Sample applications demonstrating usage of the [Kerberos Buildpack](https://techdocs.broadcom.com/us/en/vmware-tanzu/platform/elastic-application-runtime/10-4/eart/kerberos-overview.html)
on Tanzu Platform for Cloud Foundry.

## Samples

| Sample | Description |
| --- | --- |
| [KerberosDemo](src/KerberosDemo/README.md) | SPNEGO web authentication and SQL Server Integrated Authentication over Kerberos |
| [KerberosSmbDemo](src/KerberosSmbDemo/README.md) | Kerberos-authenticated SMB/CIFS file share access |

Each sample has its own `manifest.yml` and README with prerequisites, deployment, and endpoint
documentation - see the links above.

## General setup

Both samples require the [Kerberos Buildpack](https://techdocs.broadcom.com/us/en/vmware-tanzu/platform/elastic-application-runtime/10-4/eart/kerberos-overview.html)
to be available to your Cloud Foundry foundation (either as a registered buildpack name or a
download link), listed ahead of `dotnet_core_buildpack` in each app's `manifest.yml`. Each sample
is deployed independently via `cf push` from inside its own project directory.
