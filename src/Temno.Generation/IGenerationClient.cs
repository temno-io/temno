using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Generation;

public interface IGenerationClient
{
    Task<List<CatalogEntry>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<string> SubmitGenerationAsync(
        string model,
        BackendGenerationParams parameters,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    Task<BackendGenerationJob?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

    Task<string> UploadReferenceAsync(
        string filePath,
        string contentType,
        CancellationToken cancellationToken = default);
}
