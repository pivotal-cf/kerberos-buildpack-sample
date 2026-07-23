# KerberosSmbDemo

This application demonstrates using the [Kerberos Buildpack](https://techdocs.broadcom.com/us/en/vmware-tanzu/platform/tanzu-platform-for-cloud-foundry/10-0/tpcf/kerberos.html)
to access a Kerberos-authenticated SMB/CIFS file share from a Linux Cloud Foundry container.

File share authentication is performed entirely via Kerberos, using the ticket cache the buildpack's sidecar already maintains.
The Kerberos/SMB integration (`KerberosSmb` project) is pure managed code, built on [Kerberos.NET](https://github.com/dotnet/Kerberos.NET)
(the same library the buildpack's own sidecar uses) and [SMBLibrary](https://github.com/TalAloni/SMBLibrary),
wired together via SMBLibrary's `IAuthenticationClient` extension point.

## Prerequisites

- Create an SPN for the file server's service account in the form `cifs/<FQDN>`, where the FQDN
  matches what's resolved **from inside the Cloud Foundry container**, not from a domain-joined
  machine. This may differ from the FQDN on domain-joined machines - to find the correct FQDN:
  - SSH into any running app on Cloud Foundry: `cf ssh <appname>`
  - Find the IP address of the file server host: `nslookup <fileserver-host>`
  - Do a reverse DNS lookup on that IP to find the FQDN: `nslookup <fileserver-ip>`
  - Create the SPN for the account under which the file server runs: `cifs/<FQDN>`
- Ensure the service account configured via `KRB_SERVICE_ACCOUNT` has the appropriate share and
  NTFS permissions on the target share (read for `/smb/list` and `/smb/download`, write for
  `/smb/upload`, delete for `/smb/delete`).
- Ensure port 445 (SMB) is reachable from the Cloud Foundry container's network. Use `/testsmb`
  to confirm.

## Deploying

This sample is split across two projects - `KerberosSmbDemo` (the web app) and `KerberosSmb` (the
Kerberos/SMB library it references). Because of that, `manifest.yml` sets `path: ..` so the pushed
package includes the sibling `KerberosSmb` project, and the `.deployment` file in `src/` tells the
.NET Core buildpack which project to build and run. The buildpack restores and publishes from
source during staging, so no local `dotnet publish` step is required.

1. Edit the included `manifest.yml` with your own settings.

2. From inside the `KerberosSmbDemo` project, run the following:

   ```shell
   cf push
   ```

## Configuration

| Variable | Description |
| --- | --- |
| `KRB_KDC` | Kerberos domain controller (inherited from the buildpack contract) |
| `KRB_SERVICE_ACCOUNT` | Service account used to authenticate (inherited from the buildpack contract) |
| `KRB_PASSWORD` | Service account password (inherited from the buildpack contract) |
| `SMB_SERVER` | File server hostname or FQDN |
| `SMB_SHARE` | Share name |
| `SMB_SPN` | Target SPN for the file server, in the form `cifs/<FQDN>` |

## Endpoints

- `/diag` - verify the existence of key Kerberos files, return their contents, and probe SMB connectivity
- `/env` - return all environment variables (and respective values) available to the app
- `/getFile?filePath=` - return the contents of an arbitrary file inside the app container
- `/run?command=&input=` - run a command inside the app
- `/sidecarHealth` - check the health of the sidecar
- `/testKDC?kdc=` - test the connection to the configured domain controller (or another host via the kdc parameter) on port 88
- `/testSmb?server=` - test the connection to the configured (or supplied) SMB file server on port 445
- `/ticket?spn=` - use the sidecar to get an authentication ticket (optionally using an arbitrary SPN)
- `/smb/list` - list files at the root of the configured SMB share
- `/smb/download?fileName=` - download a file from the configured SMB share
- `/smb/upload` - upload a file to the configured SMB share (`multipart/form-data`)
- `/smb/delete?fileName=` - delete a file from the configured SMB share
