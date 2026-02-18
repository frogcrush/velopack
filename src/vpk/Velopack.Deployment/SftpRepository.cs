using Microsoft.Extensions.Logging;
using Velopack.Core;

namespace Velopack.Deployment;

public class SftpDownloadOptions : RepositoryOptions, IObjectDownloadOptions
{
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string Username { get; set; }
    public string Password { get; set; }
    public string PrivateKeyPath { get; set; }
    public string RemotePath { get; set; }
}

public class SftpUploadOptions : SftpDownloadOptions, IObjectUploadOptions
{
    public int KeepMaxReleases { get; set; }
}

public class SftpClient : IDisposable
{
    private readonly Renci.SshNet.SftpClient sftpClient;
    private readonly SftpDownloadOptions downloadOptions;

    public SftpClient(Renci.SshNet.SftpClient client, SftpDownloadOptions options)
    {
        sftpClient = client;
        downloadOptions = options;
    }

    public async Task<Renci.SshNet.SftpClient> GetClient()
    {
        if (!sftpClient.IsConnected) {
            await sftpClient.ConnectAsync(CancellationToken.None);
            await sftpClient.ChangeDirectoryAsync(downloadOptions.RemotePath);
        }

        return sftpClient;
    }

    public void Dispose()
    {
        if (sftpClient != null) {
            if (sftpClient.IsConnected) {
                sftpClient.Disconnect();
            }
            sftpClient.Dispose();
        }
    }
}

public class SftpRepository(ILogger logger) : ObjectRepository<SftpDownloadOptions, SftpUploadOptions, SftpClient>(logger)
{
    protected override SftpClient CreateClient(SftpDownloadOptions options)
    {
        var authMethods = new List<Renci.SshNet.AuthenticationMethod>();
        if (!string.IsNullOrEmpty(options.Password)) {
            authMethods.Add(new Renci.SshNet.PasswordAuthenticationMethod(options.Username, options.Password));
        }
        if (!string.IsNullOrEmpty(options.PrivateKeyPath)) {
            authMethods.Add(new Renci.SshNet.PrivateKeyAuthenticationMethod(options.Username, new Renci.SshNet.PrivateKeyFile(options.PrivateKeyPath)));
        }
        if (authMethods.Count == 0) {
            throw new ArgumentException("At least one authentication method must be provided (password or private key).");
        }
        var connectionInfo = new Renci.SshNet.ConnectionInfo(options.Host, options.Port, options.Username, authMethods.ToArray());         
        return new SftpClient(new Renci.SshNet.SftpClient(connectionInfo), options);
    }

    protected override async Task DeleteObject(SftpClient client, string key)
    {
        await RetryAsync(async () => {
            var sftp = await client.GetClient();
            await sftp.DeleteAsync(key);
        }, "Deleting " + key);
    }

    protected override async Task<byte[]> GetObjectBytes(SftpClient client, string key)
    {
        return await RetryAsyncRet(
            async () => {
                try {
                    var sftp = await client.GetClient();
                    using var file = await sftp.OpenAsync(key, FileMode.Open, FileAccess.Read, CancellationToken.None);
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, CancellationToken.None);
                    return ms.ToArray();
                } catch (Exception ex) when (ex.Message.Contains("not found") || ex.GetType().Name.Contains("NotFound")) {
                    return null;
                }
            },
            $"Downloading {key}...");
    }

    protected override async Task SaveEntryToFileAsync(SftpDownloadOptions options, VelopackAsset entry, string filePath)
    {
        var client = CreateClient(options);
        try {
            await RetryAsync(
                async () => {
                    var sftp = await client.GetClient();
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await sftp.DownloadFileAsync(entry.FileName, fileStream);
                },
                $"Downloading {entry.FileName}...");
        } finally {
            client.Dispose();
        }
    }

    protected override async Task UploadObject(SftpClient client, string key, FileInfo f, bool overwriteRemote, bool noCache)
    {
        try {
            var sftp = await client.GetClient();
            bool remoteExists = await sftp.ExistsAsync(key);
            
            if (remoteExists && !overwriteRemote) {
                Log.Info($"Upload file '{key}' skipped (already exists in remote)");
                return;
            }

            if (remoteExists && overwriteRemote) {
                Log.Info($"File '{key}' exists in remote, replacing...");
            }
        } catch {
            // don't care if this check fails. worst case, we end up re-uploading a file that
            // already exists. storage providers should prefer the newer file of the same name.
        }

        await RetryAsync(async () => {
            var sftp = await client.GetClient();
            if (await sftp.ExistsAsync(key)) {
                await sftp.DeleteAsync(key);
            }
            using var fileStream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            await sftp.UploadFileAsync(fileStream, key);
        }, "Uploading " + key);
    }
}
