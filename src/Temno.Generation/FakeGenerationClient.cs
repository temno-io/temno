using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Generation;

public class FakeGenerationClient : IGenerationClient
{
    private readonly Dictionary<string, (BackendGenerationJob Job, int QueryCount)> _jobs = new();
    
    public List<CatalogEntry> Models { get; set; } = new()
    {
        new CatalogEntry
        {
            Id = "video-gen-v1",
            Kind = GenerationKind.Video,
            DisplayName = "Temno Video V1",
            AllowedEndpoints = new List<string> { "generate" },
            ResponseShape = "video",
            PaidOnly = false
        },
        new CatalogEntry
        {
            Id = "image-gen-v1",
            Kind = GenerationKind.Image,
            DisplayName = "Temno Image V1",
            AllowedEndpoints = new List<string> { "generate" },
            ResponseShape = "images",
            PaidOnly = false
        },
        new CatalogEntry
        {
            Id = "upscale-v1",
            Kind = GenerationKind.Upscale,
            DisplayName = "Temno Upscale V1",
            AllowedEndpoints = new List<string> { "upscale" },
            ResponseShape = "video",
            PaidOnly = false
        }
    };

    public Task<List<CatalogEntry>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Models);
    }

    public Task<string> SubmitGenerationAsync(
        string model,
        BackendGenerationParams parameters,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var jobId = $"job-{Guid.NewGuid():N}";
        var job = new BackendGenerationJob
        {
            Id = jobId,
            Status = BackendGenerationStatus.Queued,
            ErrorMessage = null,
            CostCredits = 10,
            CompletedAt = null,
            ResultUrls = null
        };
        _jobs[jobId] = (job, 0);
        return Task.FromResult(jobId);
    }

    public Task<BackendGenerationJob?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return Task.FromResult<BackendGenerationJob?>(null);
        }

        var (job, queryCount) = state;
        queryCount++;

        // Simulate state machine: Queued -> Running -> Succeeded
        if (job.Status == BackendGenerationStatus.Queued && queryCount >= 2)
        {
            job.Status = BackendGenerationStatus.Running;
        }
        else if (job.Status == BackendGenerationStatus.Running && queryCount >= 3)
        {
            job.Status = BackendGenerationStatus.Succeeded;
            job.ResultUrls = new List<string> { "https://fake.temno.io/assets/output.mp4" };
            job.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        _jobs[jobId] = (job, queryCount);
        return Task.FromResult<BackendGenerationJob?>(job);
    }

    public Task<string> UploadReferenceAsync(
        string filePath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(filePath);
        var fakeUrl = $"https://fake.temno.io/uploads/{Guid.NewGuid():N}_{fileName}";
        return Task.FromResult(fakeUrl);
    }
}
