using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KerberosSmb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KerberosSmbDemo.Controllers;

/// <summary>
/// Demonstrates file access against a Kerberos-authenticated SMB share, using the ticket
/// cache the kerberos-buildpack sidecar maintains.
/// </summary>
[ApiController]
[Route("smb")]
public class SmbFilesController(KerberosSmbClient client) : ControllerBase
{
    [HttpGet("list")]
    public ActionResult<IReadOnlyList<SmbFileEntry>> List()
    {
        try
        {
            client.Connect();
            return Ok(client.ListFiles());
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.ToString());
        }
    }

    [HttpGet("download")]
    public ActionResult Download(string fileName)
    {
        try
        {
            client.Connect();
            var content = client.DownloadFile(fileName);
            return File(content, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.ToString());
        }
    }

    [HttpPost("upload")]
    public async Task<ActionResult> Upload(IFormFile file)
    {
        try
        {
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            client.Connect();
            client.UploadFile(file.FileName, stream.ToArray());
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.ToString());
        }
    }

    [HttpDelete("delete")]
    public ActionResult Delete(string fileName)
    {
        try
        {
            client.Connect();
            client.DeleteFile(fileName);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.ToString());
        }
    }
}
