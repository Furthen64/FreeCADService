using System.Security.Cryptography;
using FcadServe.Options;
using Microsoft.Extensions.Logging;

namespace FcadServe.Api;

/// <summary>Thrown when an upload is missing a usable filename or the name is not an .stl.</summary>
public sealed class UploadFilenameException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Thrown when a streamed upload exceeds the configured input size limit.</summary>
public sealed class UploadTooLargeException(long limit)
    : Exception($"uploaded file exceeds the maximum of {limit} bytes.")
{
    public long Limit { get; } = limit;
}

public sealed record UploadResult(string Id, string Name, string Path, long SizeBytes, string Sha256);

/// <summary>
/// Accepts raw STL uploads into a staging directory and returns the absolute
/// on-disk path a client can submit. Stored names are unique (uploads never
/// overwrite), and files are streamed with a running SHA-256 so the size limit
/// and checksum are computed without buffering the whole body in memory.
/// </summary>
public sealed class UploadStore(ServiceOptions options, ILogger<UploadStore> logger)
{
    private static readonly string[] AllowedExtensions = [".stl"];

    private readonly string _root = options.UploadRoot;

    /// <summary>
    /// Streams the request body into the upload root under a unique name derived
    /// from the sanitized requested filename. Throws <see cref="UploadFilenameException"/>
    /// or <see cref="UploadTooLargeException"/> on invalid/max-size input.
    /// </summary>
    public async Task<UploadResult> StoreAsync(string? requestedName, Stream body, CancellationToken ct)
    {
        var name = SanitizeName(requestedName);
        var target = UniquePath(name);

        Directory.CreateDirectory(_root);
        var tmp = Path.Combine(_root, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            long size = 0;
            byte[] digest;
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > options.Jobs.MaxInputSizeBytes)
                        throw new UploadTooLargeException(options.Jobs.MaxInputSizeBytes);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                digest = sha.Hash!;
            }

            if (size == 0)
                throw new UploadFilenameException("empty_upload", "uploaded file is empty.");

            File.Move(tmp, target);
            logger.LogInformation("Upload {Id}: stored {Bytes} bytes as {Path}", name, size, target);

            var id = $"u_{Guid.NewGuid():N}"[..11];
            return new UploadResult(id, Path.GetFileName(target), target, size, Convert.ToHexStringLower(digest));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Reduces any client-supplied name to a bare "stem.ext", validates the extension.</summary>
    public static string SanitizeName(string? requested)
    {
        var name = (requested ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = name.Trim().Trim('"').Trim('.', ' ');
        if (string.IsNullOrEmpty(name))
            throw new UploadFilenameException("missing_filename", "a filename is required (use ?name=part.stl or a Content-Disposition header).");

        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new UploadFilenameException("unsupported_extension",
                $"unsupported extension '{ext}' (allowed: .stl).");
        return name;
    }

    /// <summary>Picks a target in the upload root that does not exist yet.</summary>
    private string UniquePath(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = Path.Combine(_root, name);
        for (var i = 0; i < 8 && File.Exists(candidate); i++)
            candidate = Path.Combine(_root, $"{stem}_{Random.Shared.NextInt64():x8}{ext}");
        for (var i = 0; File.Exists(candidate); i++)
            candidate = Path.Combine(_root, $"{stem}_{Random.Shared.NextInt64():x8}_{i:x}{ext}");
        return Path.GetFullPath(candidate);
    }
}