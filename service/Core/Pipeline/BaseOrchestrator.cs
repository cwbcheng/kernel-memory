// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Context;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.DocumentStorage;
using Microsoft.KernelMemory.FileSystem.DevTools;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Models;

namespace Microsoft.KernelMemory.Pipeline;

[Experimental("KMEXP04")]
public abstract class BaseOrchestrator : IPipelineOrchestrator, IDisposable
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions s_notIndentedJsonOptions = new() { WriteIndented = false };

    private readonly List<IMemoryDb> _memoryDbs;
    private readonly List<ITextEmbeddingGenerator> _embeddingGenerators;
    private readonly ITextGenerator _textGenerator;
    private readonly List<string> _defaultIngestionSteps;
    private readonly IDocumentStorage _documentStorage;
    private readonly IMimeTypeDetection _mimeTypeDetection;
    private readonly string? _defaultIndexName;

    protected ILogger<BaseOrchestrator> Log { get; private set; }
    protected CancellationTokenSource CancellationTokenSource { get; private set; }

    protected BaseOrchestrator(
        IDocumentStorage documentStorage,
        List<ITextEmbeddingGenerator> embeddingGenerators,
        List<IMemoryDb> memoryDbs,
        ITextGenerator textGenerator,
        IMimeTypeDetection? mimeTypeDetection = null,
        KernelMemoryConfig? config = null,
        ILogger<BaseOrchestrator>? log = null)
    {
        config ??= new KernelMemoryConfig();

        this.Log = log ?? DefaultLogger<BaseOrchestrator>.Instance;
        this._defaultIngestionSteps = config.DataIngestion.GetDefaultStepsOrDefaults();
        this.EmbeddingGenerationEnabled = config.DataIngestion.EmbeddingGenerationEnabled;
        this._documentStorage = documentStorage;
        this._embeddingGenerators = embeddingGenerators;
        this._memoryDbs = memoryDbs;
        this._textGenerator = textGenerator;
        this._defaultIndexName = config?.DefaultIndexName;

        this._mimeTypeDetection = mimeTypeDetection ?? new MimeTypesDetection();
        this.CancellationTokenSource = new CancellationTokenSource();

        if (this.EmbeddingGenerationEnabled && embeddingGenerators.Count == 0)
        {
            this.Log.LogWarning("No embedding generators available");
        }

        if (memoryDbs.Count == 0)
        {
            this.Log.LogWarning("No vector DBs available");
        }
    }

    ///<inheritdoc />
    public abstract List<string> HandlerNames { get; }

    ///<inheritdoc />
    public abstract Task AddHandlerAsync(IPipelineStepHandler handler, CancellationToken cancellationToken = default);

    ///<inheritdoc />
    public abstract Task TryAddHandlerAsync(IPipelineStepHandler handler, CancellationToken cancellationToken = default);

    ///<inheritdoc />
    public abstract Task RunPipelineAsync(DataPipeline pipeline, CancellationToken cancellationToken = default);

    ///<inheritdoc />
    public async Task<string> ImportDocumentAsync(
        string index,
        DocumentUploadRequest uploadRequest,
        IContext? context = null,
        CancellationToken cancellationToken = default)
    {
        this.Log.LogInformation("Queueing upload of {0} files for further processing [request {1}]", uploadRequest.Files.Count, uploadRequest.DocumentId);

        index = IndexName.CleanName(index, this._defaultIndexName);

        var pipeline = this.PrepareNewDocumentUpload(
            index: index,
            documentId: uploadRequest.DocumentId,
            tags: uploadRequest.Tags,
            filesToUpload: uploadRequest.Files,
            contextArgs: context?.Arguments);

        if (uploadRequest.Steps.Count > 0)
        {
            foreach (var step in uploadRequest.Steps)
            {
                pipeline.Then(step);
            }
        }
        else
        {
            foreach (var step in this._defaultIngestionSteps)
            {
                pipeline.Then(step);
            }
        }

        pipeline.Build();

        try
        {
            await this.RunPipelineAsync(pipeline, cancellationToken).ConfigureAwait(false);
            return pipeline.DocumentId;
        }
        catch (Exception e)
        {
            this.Log.LogError(e, "Pipeline start failed.");
            throw;
        }
    }

    ///<inheritdoc />
    public DataPipeline PrepareNewDocumentUpload(
        string index,
        string documentId,
        TagCollection tags,
        IEnumerable<DocumentUploadRequest.UploadedFile>? filesToUpload = null,
        IDictionary<string, object?>? contextArgs = null)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);

        filesToUpload ??= [];

        var pipeline = new DataPipeline
        {
            Index = index,
            DocumentId = documentId,
            Tags = tags,
            ContextArguments = contextArgs ?? new Dictionary<string, object?>(),
            FilesToUpload = filesToUpload.ToList(),
        };

        pipeline.Validate();

        return pipeline;
    }

    ///<inheritdoc />
    public async Task<DataPipeline?> ReadPipelineStatusAsync(string index, string documentId, CancellationToken cancellationToken = default)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);

        try
        {
            using StreamableFileContent? streamableContent = await this._documentStorage.ReadFileAsync(index, documentId, Constants.PipelineStatusFilename, false, cancellationToken)
                .ConfigureAwait(false);

            if (streamableContent == null)
            {
                throw new InvalidPipelineDataException("The pipeline data is not found");
            }

            using (var stream = await streamableContent.GetStreamAsync().ConfigureAwait(false)) {
                // 检查流是否真的可读或有内容，避免 BinaryData.FromStreamAsync 可能因空流或不可读流抛异常
                if (stream == null || !stream.CanRead)
                {
                    throw new InvalidPipelineDataException($"Stream from pipeline status file is null or not readable for index '{index}', documentId '{documentId}'");
                }
                // 对于某些流类型，特别是网络流，检查长度可能不可靠或导致问题。
                // 但对于 FileStream，如果长度为0，可以提前判断。
                if (stream.CanSeek && stream.Length == 0)
                {
                    // 如果文件为空，Deserialize 可能会失败或返回 null，根据业务逻辑处理
                    throw new InvalidPipelineDataException($"\"Pipeline status file stream is empty for index '{index}', documentId '{documentId}'\"");
                }
                BinaryData? content = await BinaryData.FromStreamAsync(stream, cancellationToken)
                .ConfigureAwait(false);
                // BinaryData.FromStreamAsync 在流结束时返回的 content 不会是 null，但其内部的字节数组可能是空的。
                // content.ToMemory().Length == 0 可以用来检查是否为空内容
                if (content == null || content.ToMemory().Length == 0) // 检查 content 是否为 null 或空
                {
                    this.Log.LogWarning("Pipeline data content is null or empty after reading stream for index '{0}', documentId '{1}'", index, documentId);
                    throw new InvalidPipelineDataException("The pipeline data is null or empty");
                }

                var jsonString = content.ToString().RemoveBOM().Trim();
                if (string.IsNullOrWhiteSpace(jsonString))
                {
                    this.Log.LogWarning("Pipeline JSON string is null or whitespace for index '{0}', documentId '{1}'", index, documentId);
                    throw new InvalidPipelineDataException("The pipeline JSON data is null or whitespace");
                }


                var result = JsonSerializer.Deserialize<DataPipeline>(jsonString);

                if (result == null)
                {
                    this.Log.LogWarning("Deserialized pipeline data is null for index '{0}', documentId '{1}'. JSON: {2}", index, documentId, jsonString);
                    throw new InvalidPipelineDataException("The pipeline data deserializes to a null value");
                }

                return result;
            }
        }
        catch (DocumentStorageFileNotFoundException)
        {
            this.Log.LogWarning("Pipeline status file not found for index '{0}', documentId '{1}'", index, documentId);
            throw new PipelineNotFoundException("Pipeline/Document not found");
        }
        catch (JsonException ex) // 捕获 JSON 反序列化异常
        {
            this.Log.LogError(ex, "Failed to deserialize pipeline status for index '{0}', documentId '{1}'", index, documentId);
            throw new InvalidPipelineDataException("Failed to deserialize pipeline data.", ex);
        }
    }

    ///<inheritdoc />
    public async Task<DataPipelineStatus?> ReadPipelineSummaryAsync(string index, string documentId, CancellationToken cancellationToken = default)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);

        try
        {
            DataPipeline? pipeline = await this.ReadPipelineStatusAsync(index: index, documentId: documentId, cancellationToken).ConfigureAwait(false);
            return pipeline?.ToDataPipelineStatus();
        }
        catch (PipelineNotFoundException)
        {
            return null;
        }
    }

    ///<inheritdoc />
    public async Task<bool> IsDocumentReadyAsync(string index, string documentId, CancellationToken cancellationToken = default)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);

        try
        {
            this.Log.LogDebug("Checking if document {Id} on index {Index} is ready", documentId, index);
            DataPipeline? pipeline = await this.ReadPipelineStatusAsync(index: index, documentId, cancellationToken).ConfigureAwait(false);

            if (pipeline == null)
            {
                this.Log.LogWarning("Document {Id} on index {Index} is not ready, pipeline is NULL", documentId, index);
                return false;
            }

            this.Log.LogDebug("Document {Id} on index {Index}, Complete = {Complete}, Files Count = {Count}", documentId, index, pipeline.Complete, pipeline.Files.Count);
            return pipeline.Complete && pipeline.Files.Count > 0;
        }
        catch (PipelineNotFoundException)
        {
            this.Log.LogWarning("Document {Id} on index {Index} not found", documentId, index);
            return false;
        }
    }

    ///<inheritdoc />
    public Task StopAllPipelinesAsync()
    {
        return this.CancellationTokenSource.CancelAsync();
    }

    ///<inheritdoc />
    public async Task<StreamableFileContent> ReadFileAsStreamAsync(DataPipeline pipeline, string fileName, CancellationToken cancellationToken = default)
    {
        pipeline.Index = IndexName.CleanName(pipeline.Index, this._defaultIndexName);
        return await this._documentStorage.ReadFileAsync(pipeline.Index, pipeline.DocumentId, fileName, true, cancellationToken)
            .ConfigureAwait(false);
    }

    ///<inheritdoc />
    public async Task<string> ReadTextFileAsync(DataPipeline pipeline, string fileName, CancellationToken cancellationToken = default)
    {
        pipeline.Index = IndexName.CleanName(pipeline.Index, this._defaultIndexName);
        return (await this.ReadFileAsync(pipeline, fileName, cancellationToken).ConfigureAwait(false)).ToString();
    }

    ///<inheritdoc />
    public async Task<BinaryData> ReadFileAsync(DataPipeline pipeline, string fileName, CancellationToken cancellationToken = default)
    {
        using StreamableFileContent streamableContent = await this.ReadFileAsStreamAsync(pipeline, fileName, cancellationToken).ConfigureAwait(false);
        return await BinaryData.FromStreamAsync(await streamableContent.GetStreamAsync().ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    ///<inheritdoc />
    public Task WriteTextFileAsync(DataPipeline pipeline, string fileName, string fileContent, CancellationToken cancellationToken = default)
    {
        pipeline.Index = IndexName.CleanName(pipeline.Index, this._defaultIndexName);
        return this.WriteFileAsync(pipeline, fileName, new BinaryData(fileContent), cancellationToken);
    }

    ///<inheritdoc />
    public Task WriteFileAsync(DataPipeline pipeline, string fileName, BinaryData fileContent, CancellationToken cancellationToken = default)
    {
        pipeline.Index = IndexName.CleanName(pipeline.Index, this._defaultIndexName);
        return this._documentStorage.WriteFileAsync(pipeline.Index, pipeline.DocumentId, fileName, fileContent.ToStream(), cancellationToken);
    }

    ///<inheritdoc />
    public bool EmbeddingGenerationEnabled { get; }

    ///<inheritdoc />
    public List<ITextEmbeddingGenerator> GetEmbeddingGenerators()
    {
        return this._embeddingGenerators;
    }

    ///<inheritdoc />
    public List<IMemoryDb> GetMemoryDbs()
    {
        return this._memoryDbs;
    }

    ///<inheritdoc />
    public ITextGenerator GetTextGenerator()
    {
        return this._textGenerator;
    }

    ///<inheritdoc />
    public Task StartIndexDeletionAsync(string? index = null, CancellationToken cancellationToken = default)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);
        DataPipeline pipeline = PrepareIndexDeletion(index: index);
        return this.RunPipelineAsync(pipeline, cancellationToken);
    }

    ///<inheritdoc />
    public Task StartDocumentDeletionAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
    {
        index = IndexName.CleanName(index, this._defaultIndexName);
        DataPipeline pipeline = PrepareDocumentDeletion(index: index, documentId: documentId);
        return this.RunPipelineAsync(pipeline, cancellationToken);
    }

    ///<inheritdoc />
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// If the pipeline asked to delete a document or an index, there might be some files
    /// left over in the storage, such as the status file that we wish to delete to keep
    /// the storage clean. We try to delete what is left, ignoring exceptions.
    /// </summary>
    protected async Task CleanUpAfterCompletionAsync(DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
#pragma warning disable CA1031 // catch all by design
        if (pipeline.IsDocumentDeletionPipeline())
        {
            try
            {
                await this._documentStorage.DeleteDocumentDirectoryAsync(index: pipeline.Index, documentId: pipeline.DocumentId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                this.Log.LogError(e, "Error while trying to delete the document directory.");
            }
        }

        if (pipeline.IsIndexDeletionPipeline())
        {
            try
            {
                await this._documentStorage.DeleteIndexDirectoryAsync(pipeline.Index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                this.Log.LogError(e, "Error while trying to delete the index directory.");
            }
        }
#pragma warning restore CA1031
    }

    protected static DataPipeline PrepareIndexDeletion(string? index)
    {
        var pipeline = new DataPipeline
        {
            Index = index!,
            DocumentId = string.Empty,
        };

        return pipeline.Then(Constants.PipelineStepsDeleteIndex).Build();
    }

    protected static DataPipeline PrepareDocumentDeletion(string? index, string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new KernelMemoryException("The document ID is empty");
        }

        var pipeline = new DataPipeline
        {
            Index = index!,
            DocumentId = documentId,
        };

        return pipeline.Then(Constants.PipelineStepsDeleteDocument).Build();
    }

    protected async Task UploadFilesAsync(DataPipeline currentPipeline, CancellationToken cancellationToken = default)
    {
        if (currentPipeline.UploadComplete)
        {
            this.Log.LogDebug("Upload complete");
            return;
        }

        // If the folder contains the status of a previous execution,
        // capture it to run consolidation later, e.g. purging deprecated memory records.
        // Note: although not required, the list of executions to purge is ordered from oldest to most recent
        DataPipeline? previousPipeline;
        try
        {
            previousPipeline = await this.ReadPipelineStatusAsync(currentPipeline.Index, currentPipeline.DocumentId, cancellationToken).ConfigureAwait(false);
        }
        catch (PipelineNotFoundException)
        {
            previousPipeline = null;
        }

        if (previousPipeline != null && previousPipeline.ExecutionId != currentPipeline.ExecutionId)
        {
            var dedupe = new HashSet<string>();
            foreach (var oldExecution in currentPipeline.PreviousExecutionsToPurge)
            {
                dedupe.Add(oldExecution.ExecutionId);
            }

            foreach (var oldExecution in previousPipeline.PreviousExecutionsToPurge)
            {
                if (dedupe.Contains(oldExecution.ExecutionId)) { continue; }

                // Reset the list to avoid wasting space with nested trees
                oldExecution.PreviousExecutionsToPurge = [];

                currentPipeline.PreviousExecutionsToPurge.Add(oldExecution);
                dedupe.Add(oldExecution.ExecutionId);
            }

            // Reset the list to avoid wasting space with nested trees
            previousPipeline.PreviousExecutionsToPurge = [];

            currentPipeline.PreviousExecutionsToPurge.Add(previousPipeline);
        }

        await this.UploadFormFilesAsync(currentPipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Update the status file, throwing an exception if the write fails.
    /// </summary>
    /// <param name="pipeline">Pipeline data</param>
    /// <param name="cancellationToken">Task cancellation token</param>
    protected async Task UpdatePipelineStatusAsync(DataPipeline pipeline, CancellationToken cancellationToken)
    {
        this.Log.LogDebug("Saving pipeline status to '{0}/{1}/{2}'", pipeline.Index, pipeline.DocumentId, Constants.PipelineStatusFilename);
        try
        {
            await this._documentStorage.WriteFileAsync(
                    pipeline.Index,
                    pipeline.DocumentId,
                    Constants.PipelineStatusFilename,
                    new BinaryData(ToJson(pipeline, true)).ToStream(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.Log.LogWarning(e, "Unable to save pipeline status");
            throw;
        }
    }

    protected static string ToJson(object data, bool indented = false)
    {
        return JsonSerializer.Serialize(data, indented ? s_indentedJsonOptions : s_notIndentedJsonOptions);
    }

    private async Task UploadFormFilesAsync(DataPipeline pipeline, CancellationToken cancellationToken)
    {
        this.Log.LogDebug("Uploading {0} files, pipeline '{1}/{2}'", pipeline.FilesToUpload.Count, pipeline.Index, pipeline.DocumentId);

        await this._documentStorage.CreateIndexDirectoryAsync(pipeline.Index, cancellationToken).ConfigureAwait(false);
        await this._documentStorage.CreateDocumentDirectoryAsync(pipeline.Index, pipeline.DocumentId, cancellationToken).ConfigureAwait(false);

        foreach (DocumentUploadRequest.UploadedFile file in pipeline.FilesToUpload)
        {
            if (string.Equals(file.FileName, Constants.PipelineStatusFilename, StringComparison.OrdinalIgnoreCase))
            {
                this.Log.LogError("Invalid file name, upload not supported: {0}", file.FileName);
                continue;
            }

            // Read the value before the stream is closed (would throw an exception otherwise)
            var fileSize = file.FileContent.Length;

            this.Log.LogDebug("Uploading file '{0}', size {1} bytes", file.FileName, fileSize);
            await this._documentStorage.WriteFileAsync(pipeline.Index, pipeline.DocumentId, file.FileName, file.FileContent, cancellationToken).ConfigureAwait(false);

            string mimeType = string.Empty;
            try
            {
                mimeType = this._mimeTypeDetection.GetFileType(file.FileName);
            }
            catch (MimeTypeException)
            {
                this.Log.LogWarning("File type not supported, the ingestion pipeline might skip it");
            }

            pipeline.Files.Add(new DataPipeline.FileDetails
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = file.FileName,
                Size = fileSize,
                MimeType = mimeType,
                Tags = pipeline.Tags,
            });

            this.Log.LogInformation("File uploaded: {0}, {1} bytes", file.FileName, fileSize);
            pipeline.LastUpdate = DateTimeOffset.UtcNow;
        }

        await this.UpdatePipelineStatusAsync(pipeline, cancellationToken).ConfigureAwait(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.CancellationTokenSource.Dispose();
        }
    }
}
