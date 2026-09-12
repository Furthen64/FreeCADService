using System.Text.Json;
using System.Text.Json.Serialization;

namespace FcadServe.Api;

public sealed record SubmitJobRequest(string? StlPath, string? OutputDir);

/// <summary>
/// Accepts both the documented snake_case request keys (stl_path, output_dir)
/// and the camelCase equivalents (stlPath, outputDir) for job submission.
/// Providing the same key twice, or both spellings of the same key, is
/// rejected as a malformed body.
/// </summary>
public sealed class SubmitJobRequestConverter : JsonConverter<SubmitJobRequest>
{
    public override SubmitJobRequest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("request body must be a JSON object.");

        string? stlPath = null;
        string? outputDir = null;
        var seenStl = false;
        var seenOutput = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new SubmitJobRequest(stlPath, outputDir);
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("invalid request body.");

            var name = reader.GetString() ?? "";
            var normalized = name.ToLowerInvariant().Replace("_", "");
            reader.Read();

            string? value;
            if (reader.TokenType == JsonTokenType.String)
                value = reader.GetString();
            else if (reader.TokenType == JsonTokenType.Null)
                value = null;
            else
                throw new JsonException($"property '{name}' must be a string.");

            switch (normalized)
            {
                case "stlpath":
                    if (seenStl) throw new JsonException("duplicate stl_path property.");
                    stlPath = value;
                    seenStl = true;
                    break;
                case "outputdir":
                    if (seenOutput) throw new JsonException("duplicate output_dir property.");
                    outputDir = value;
                    seenOutput = true;
                    break;
            }
        }

        throw new JsonException("unterminated request body.");
    }

    public override void Write(Utf8JsonWriter writer, SubmitJobRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("stlPath", value.StlPath);
        writer.WriteString("outputDir", value.OutputDir);
        writer.WriteEndObject();
    }
}

public static class ApiModels
{
    public static object SubmitResponse(string jobId, string basePath) => new
    {
        job_id = jobId,
        status = "queued",
        status_url = $"{basePath}/v1/jobs/{jobId}",
    };

    public static object Error(string code, string error) => new { code, error };

    public static object JobToDto(Jobs.JobRecord job, string basePath)
    {
        var artifacts = Jobs.ArtifactFile.Deserialize(job.ArtifactJson).Select(a => new
        {
            name = a.Name,
            kind = a.Kind,
            path = a.Path,
            size_bytes = a.SizeBytes,
        }).ToArray();

        return new
        {
            job_id = job.Id,
            status = job.Status.ToString().ToLowerInvariant(),
            phase = job.Phase.ToString().ToLowerInvariant(),
            created_at = job.CreatedAtUtc.ToString("O"),
            started_at = job.StartedAtUtc?.ToString("O"),
            finished_at = job.FinishedAtUtc?.ToString("O"),
            updated_at = job.UpdatedAtUtc.ToString("O"),
            stl_path = job.StlPath,
            output_dir = job.RequestedOutputDir,
            effective_output_dir = job.EffectiveOutputDir,
            input_size_bytes = job.InputSizeBytes,
            input_sha256 = job.InputSha256,
            exit_code = job.ExitCode,
            error_code = job.ErrorCode,
            error = job.Error,
            report_path = job.ReportPath,
            artifacts,
            status_url = $"{basePath}/v1/jobs/{job.Id}",
        };
    }

    public static object ArtifactsToDto(Jobs.JobRecord job)
    {
        var files = Jobs.ArtifactFile.Deserialize(job.ArtifactJson).Select(a => new
        {
            name = a.Name,
            kind = a.Kind,
            path = a.Path,
            size_bytes = a.SizeBytes,
        }).ToArray();

        return new
        {
            job_id = job.Id,
            status = job.Status.ToString().ToLowerInvariant(),
            output_dir = job.EffectiveOutputDir,
            files,
        };
    }
}