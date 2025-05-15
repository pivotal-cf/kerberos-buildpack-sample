# Sample application for Kerberos Buildpack

This sample application can be used to demonstrate different IWA usage modes and validate or troubleshoot the configuration of an environment.

## Prerequisites

- For the end-user authentication endpoint (`/user`), create SPN for the account under which current service runs (same as `KRB_SERVICE_ACCOUNT`) to `http/<Cloud Foundry route>`. For example, if this sample app is assigned the route <https://kerberos-demo.apps.pcfone.io> then the SPN would be `http//kerberos-demo.apps.pcfone.io`.
- For the SQL Server endpoint (`/sql`), ensure the following:
  - SQL Server is running under an Active Directory account
  - SQL Server is routable from Tanzu Platform for Cloud Foundry
  - SQL Server is assigned correct SPN that matches it's FQDN as resolved by apps running on cloud foundry. **This may be different from FQDN as understood by machines on domain joined machines**. To ensure correct SPN, use the following procedure:
    - SSH into any running app on the cloud foundry: `cf ssh <appname>`
    - Find the IP Address of the sql server host: `nslookup <sqlserver-host>`
    - Do reverse DNS lookup on IP to find FQDN: `nslookup <sqlserver-ip>`
    - Create SPN for the account under which SQL server runs in this format: `MSSQLSvc/<FQDN>`
  - Configure SQL Server to use SSL and install the necessary certificate. See [this article](https://www.mssqltips.com/sqlservertip/3299/how-to-configure-ssl-encryption-in-sql-server/) on the procedure.
    - **Note**: The certificate must match the routable address from the platform. Since SQL server SNI is not supported for SQL, the certificate must have all server URLs that will be used to access it. Alternatively, disable SSL validation on the client by including `TrustServerCertificate=True` in the connection string.

## Deploying

1. Edit sample `manifest.yaml` with your own settings

2. From inside `KerberosDemo` project, run the following:

   ```text
   cf push
   ```

## Endpoints

This application includes endpoints for demonstrating, testing and debugging various aspects of Kerberos integrations.

- `/diag` - verify the existence of key Kerberos files and return their contents
- `/env` - return all environment variables (and respective values) that are available to the app
- `/getFile?filePath=README.md` - return the contents of an arbitrary file
- `/run?command=&input=` - run a command inside the app
- `/sidecarHealth` - check the health of the sidecar
- `/sql?connectionString=` - connects to a SQL Server using Integrated authentication
- `/testKDC?kdc=` - test the connection to the configured domain controller (or another host via the kdc parameter) on port 88
- `/ticket?spn=` - use the sidecar to get an authentication ticket (optionally using an arbitrary SPN)
- `/user?forceAuth=` - authenticates incoming caller via SPNEGO (Kerberos ticket via HTTP header)
