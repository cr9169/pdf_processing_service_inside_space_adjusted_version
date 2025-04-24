﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PdfProcessingService.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Runtime.InteropServices;
using PdfProcessingService.Helpers;

namespace PdfProcessingService.Services
{
    /// <summary>
    /// Service for processing TXT files and indexing their content in Elasticsearch.
    /// </summary>
    public class TxtService // : IExtractService
    {
        // private readonly IElasticsearchService _elasticsearchService;
        private readonly ILogger<TxtService> _logger;
        private readonly ProcessingSettings _settings;
        private readonly VespaSettings _vespaSettings;
        private readonly SystemMemoryInfo _memoryInfo;

        // Default chunk size of 1MB if not configured
        private const int DefaultChunkSizeInBytes = 9 * 1024 * 1024;

        /// <summary>
        /// Initializes a new instance of the TxtService class.
        /// </summary>
        public TxtService(
            //IElasticsearchService elasticsearchService,
            IOptions<ProcessingSettings> settings,
            ILogger<TxtService> logger,
            IOptions<VespaSettings> vespaOptions,
            SystemMemoryInfo memoryInfo)

        {
            //_elasticsearchService = elasticsearchService;
            _logger = logger;
            _settings = settings.Value;
            _vespaSettings = vespaOptions.Value;
            _memoryInfo = memoryInfo;
        }

        /*
        /// <summary>
        /// Processes a TXT file and indexes its content in Elasticsearch.
        /// </summary>
        public async Task<ProcessingResponse> ProcessFileAsync(string filePath)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = new ProcessingResponse
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = filePath,
                Success = false,
                Benchmarks = new Dictionary<string, double>()
            };

            try
            {
                _logger.LogInformation("Starting TXT processing for file: {FilePath}", filePath);

                // Validate the file exists
                if (!File.Exists(filePath))
                {
                    response.ErrorMessage = $"File does not exist: {filePath}";
                    return response;
                }

                // Validate file extension
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension != ".txt")
                {
                    response.ErrorMessage = $"Invalid file extension: {extension}. Expected .txt";
                    return response;
                }

                // Get file info
                var fileInfo = new FileInfo(filePath);
                response.FileSizeInBytes = fileInfo.Length;

                // Generate a unique identifier for the file
                var fileId = $"{Path.GetFileNameWithoutExtension(filePath).Replace(" ", "_")}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";

                // Get chunk size from settings or use default
                int chunkSizeInBytes = _settings.ChunkSizeInBytes > 0
                    ? _settings.ChunkSizeInBytes
                    : DefaultChunkSizeInBytes;

                // Process the file in chunks
                List<DocumentChunk> chunks = await ChunkTextFileAsync(filePath, fileId, chunkSizeInBytes);
                response.ChunkCount = chunks.Count;

                // Index chunks in batches
                var maxBatchSize = 10; // Limit batch size for better performance
                for (int i = 0; i < chunks.Count; i += maxBatchSize)
                {
                    var batch = chunks.Skip(i).Take(maxBatchSize).ToList();
                    _logger.LogInformation("Indexing batch {CurrentBatch}/{TotalBatches} with {ChunkCount} chunks",
                        (i / maxBatchSize) + 1, (chunks.Count / maxBatchSize) + 1, batch.Count);

                    var indexResult = await _elasticsearchService.BulkIndexChunksAsync(batch);
                    if (!indexResult)
                    {
                        response.ErrorMessage = $"Failed to index batch {(i / maxBatchSize) + 1}/{(chunks.Count / maxBatchSize) + 1}";
                        return response;
                    }
                }

                // Update response with success
                response.Success = true;
                response.PageCount = 1; // TXT files don't have pages

                stopwatch.Stop();
                response.ProcessingTimeInSeconds = stopwatch.Elapsed.TotalSeconds;

                _logger.LogInformation("TXT processing completed successfully: {FilePath}, Chunks: {ChunkCount}",
                    filePath, chunks.Count);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                response.ProcessingTimeInSeconds = stopwatch.Elapsed.TotalSeconds;
                response.ErrorMessage = $"Error processing TXT: {ex.Message}";

                _logger.LogError(ex, "Error processing TXT file: {FilePath}", filePath);
                return response;
            }
        }*/

        /// <summary>
        /// Chunks a text file into smaller pieces for Elasticsearch indexing.
        /// </summary>
        /// <param name="filePath">Path to the text file</param>
        /// <param name="fileId">Unique identifier for the file</param>
        /// <param name="chunkSizeInBytes">Maximum size of each chunk in bytes</param>
        /// <returns>List of document chunks</returns>
        private async Task<List<DocumentChunk>> ChunkTextFileAsync(string filePath, string fileId, int chunkSizeInBytes)
        {
            var chunks = new List<DocumentChunk>();
            var fileInfo = new FileInfo(filePath);

            // For small files, just use a single chunk
            if (fileInfo.Length <= chunkSizeInBytes)
            {
                string content = await File.ReadAllTextAsync(filePath);
                chunks.Add(new DocumentChunk
                {
                    Id = Guid.NewGuid().ToString(),
                    OriginalFilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    FileIdentifier = fileId,
                    SequenceNumber = 1,
                    StartPage = 1,
                    EndPage = 1,
                    TotalPages = 1,
                    Content = content,
                    ProcessedAt = DateTime.UtcNow,
                    FileSizeInBytes = fileInfo.Length,
                    TotalChunks = 1
                });
                return chunks;
            }

            // For large files, read in chunks
            _logger.LogInformation("File size {Size} MB exceeds chunk size {ChunkSize} MB. Splitting into chunks.",
                fileInfo.Length / (1024 * 1024), chunkSizeInBytes / (1024 * 1024));

            // Use a StreamReader for efficient reading of large files
            using (var reader = new StreamReader(filePath, Encoding.UTF8))
            {
                var buffer = new char[chunkSizeInBytes / 2]; // Use half the chunk size for the buffer
                var contentBuilder = new StringBuilder();
                int sequenceNumber = 1;

                int bytesRead;
                while ((bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    contentBuilder.Append(buffer, 0, bytesRead);

                    // Check if we have enough data for a chunk
                    string currentContent = contentBuilder.ToString();
                    int estimatedByteSize = Encoding.UTF8.GetByteCount(currentContent);

                    if (estimatedByteSize >= chunkSizeInBytes)
                    {
                        // Find a good break point (ideally at paragraph or sentence)
                        int breakPoint = FindBreakPoint(currentContent);

                        // Create a chunk with content up to the break point
                        string chunkContent = currentContent.Substring(0, breakPoint);

                        chunks.Add(new DocumentChunk
                        {
                            Id = Guid.NewGuid().ToString(),
                            OriginalFilePath = filePath,
                            FileName = Path.GetFileName(filePath),
                            FileIdentifier = fileId,
                            SequenceNumber = sequenceNumber++,
                            StartPage = 1, // TXT doesn't have pages
                            EndPage = 1,
                            TotalPages = 1,
                            Content = chunkContent,
                            ProcessedAt = DateTime.UtcNow,
                            FileSizeInBytes = fileInfo.Length,
                            TotalChunks = -1 // We'll update this later
                        });

                        // Keep the remainder for the next chunk
                        contentBuilder.Clear();
                        contentBuilder.Append(currentContent.Substring(breakPoint));
                    }
                }

                // Don't forget any remaining content
                if (contentBuilder.Length > 0)
                {
                    chunks.Add(new DocumentChunk
                    {
                        Id = Guid.NewGuid().ToString(),
                        OriginalFilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        FileIdentifier = fileId,
                        SequenceNumber = sequenceNumber,
                        StartPage = 1,
                        EndPage = 1,
                        TotalPages = 1,
                        Content = contentBuilder.ToString(),
                        ProcessedAt = DateTime.UtcNow,
                        FileSizeInBytes = fileInfo.Length,
                        TotalChunks = -1
                    });
                }
            }

            // Update the total chunks count
            int totalChunks = chunks.Count;
            foreach (var chunk in chunks)
            {
                chunk.TotalChunks = totalChunks;
            }

            return chunks;
        }

        /// <summary>
        /// Finds a good break point in the text (paragraph, sentence, or word boundary).
        /// </summary>
        /// <param name="text">The text to analyze</param>
        /// <returns>Index where the text should be split</returns>
        private int FindBreakPoint(string text)
        {
            // If text is short, just return the end
            if (text.Length < 1000)
                return text.Length;

            // Try to find paragraph break near the middle
            int midPoint = text.Length / 2;
            int searchRange = Math.Min(text.Length / 4, 5000); // Look within 25% of the middle or 5000 chars, whichever is less

            // Search for double newline (paragraph break)
            for (int i = midPoint; i < midPoint + searchRange && i < text.Length - 1; i++)
            {
                if (text[i] == '\n' && text[i + 1] == '\n')
                    return i + 2; // After the paragraph break
            }

            for (int i = midPoint; i > midPoint - searchRange && i > 1; i--)
            {
                if (text[i] == '\n' && text[i - 1] == '\n')
                    return i + 1; // After the paragraph break
            }

            // If no paragraph break, look for single newline
            for (int i = midPoint; i < midPoint + searchRange && i < text.Length; i++)
            {
                if (text[i] == '\n')
                    return i + 1; // After the newline
            }

            for (int i = midPoint; i > midPoint - searchRange && i > 0; i--)
            {
                if (text[i] == '\n')
                    return i + 1; // After the newline
            }

            // If no newline, look for sentence end (period, exclamation, question mark)
            for (int i = midPoint; i < midPoint + searchRange && i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == '!' || text[i] == '?') &&
                    (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                    return i + 1; // After the sentence end
            }

            for (int i = midPoint; i > midPoint - searchRange && i > 0; i--)
            {
                if ((text[i] == '.' || text[i] == '!' || text[i] == '?') &&
                    (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                    return i + 1; // After the sentence end
            }

            // If no sentence break, look for space
            for (int i = midPoint; i < midPoint + searchRange && i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                    return i + 1; // After the space
            }

            for (int i = midPoint; i > midPoint - searchRange && i > 0; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                    return i + 1; // After the space
            }

            // If all else fails, just split at midpoint
            return midPoint;
        }


        ///////////////////////////////////////////////////////////////////////////////////////////////////


        /// <summary>
        /// During the processing process, the system first checks whether the file exists
        /// and matches the required type. If the file is large, it is divided into chunks,
        /// with each chunk represented as a DocumentChunk – a unit that groups all the relevant
        /// information for a fixed-size section of the file. Memory is used using Memory Mapped,
        /// which allows direct access to the file areas in memory without creating a separate
        /// FileStream for each chunk, thus improving performance. Each DocumentChunk is processed
        /// by a separate thread in parallel processing, which allows for simultaneous reading and
        /// processing of different parts of the file. After processing each chunk, the system performs
        /// a fine adjustment of the text boundaries to ensure that the cut is not made in the middle
        /// of a sentence or word. Finally, the sorted DocumentChunks are grouped into small groups
        /// (batches), with each batch being transferred for further processing – for example, to index
        /// in a search system – also carried out in parallel while limiting the number of threads
        /// active at the same time. This process, which combines partitioning for parallel processing,
        /// using DocumentChunk, and working with batches, makes optimal use of memory and dramatically
        /// improves the processing speed of large files.
        /// </summary>
        /*public async Task<ProcessingResponse> ProcessFileAsyncVersion2(string filePath)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var response = new ProcessingResponse
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = filePath,
                Success = false,
                Benchmarks = new Dictionary<string, double>()
            };

            try
            {
                _logger.LogInformation("Starting TXT processing for file: {FilePath}", filePath);

                // בדיקת קיום הקובץ וסיומת תקינה
                if (!File.Exists(filePath))
                {
                    response.ErrorMessage = $"File does not exist: {filePath}";
                    return response;
                }
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension != ".txt")
                {
                    response.ErrorMessage = $"Invalid file extension: {extension}. Expected .txt";
                    return response;
                }

                // קבלת מידע על הקובץ וזיהוי ייחודי
                var fileInfo = new FileInfo(filePath);
                response.FileSizeInBytes = fileInfo.Length;
                var fileId = $"{Path.GetFileNameWithoutExtension(filePath).Replace(" ", "_")}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";

                // קביעת גודל צ'אנק מההגדרות או ברירת מחדל
                int chunkSizeInBytes = _settings.ChunkSizeInBytes > 0 ? _settings.ChunkSizeInBytes : DefaultChunkSizeInBytes;

                // STEP 1: קריאה ועיבוד – חלוקת הקובץ לצ'אנקים
                var readStopwatch = Stopwatch.StartNew();
                List<DocumentChunk> chunks = await ChunkTextFileAsync(filePath, fileId, chunkSizeInBytes);
                readStopwatch.Stop();
                response.Benchmarks["FileReadTime"] = readStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Finished reading file. Time taken: {ReadTime} seconds", readStopwatch.Elapsed.TotalSeconds);
                response.ChunkCount = chunks.Count;

                // STEP 2: אינדוקס – שליחת הצ'אנקים ל-Elasticsearch כולל רענון ואימות זמינות
                var indexingStopwatch = Stopwatch.StartNew();
                var maxBatchSize = 10; // הגבלת גודל באצ'
                for (int i = 0; i < chunks.Count; i += maxBatchSize)
                {
                    var batch = chunks.Skip(i).Take(maxBatchSize).ToList();
                    _logger.LogInformation("Indexing batch {CurrentBatch}/{TotalBatches} with {ChunkCount} chunks",
                        (i / maxBatchSize) + 1, (chunks.Count / maxBatchSize) + 1, batch.Count);

                    var indexResult = await _elasticsearchService.BulkIndexChunksAsync(batch);
                    if (!indexResult)
                    {
                        response.ErrorMessage = $"Failed to index batch {(i / maxBatchSize) + 1}/{(chunks.Count / maxBatchSize) + 1}";
                        return response;
                    }
                }

                // ביצוע רענון לאינדקס כדי לוודא שהמסמכים זמינים לחיפוש
                try
                {
                    // נניח שהגדרות _settings.Url מכילות את כתובת ה-Elasticsearch
                    string url = _settings.Url;
                    if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                    {
                        url = "http://" + url;
                    }
                    using var httpClient = new HttpClient();
                    _logger.LogInformation("Requesting Elasticsearch refresh to ensure all documents are searchable");
                    var refreshUrl = $"{url}/target_index/_refresh";
                    await httpClient.PostAsync(refreshUrl, null);

                    // בדיקת ספירת המסמכים באינדקס
                    var countUrl = $"{url}/target_index/_count";
                    var countResponse = await httpClient.GetAsync(countUrl);
                    if (countResponse.IsSuccessStatusCode)
                    {
                        string countContent = await countResponse.Content.ReadAsStringAsync();
                        using JsonDocument jsonDoc = JsonDocument.Parse(countContent);
                        if (jsonDoc.RootElement.TryGetProperty("count", out JsonElement countElement))
                        {
                            int documentCount = countElement.GetInt32();
                            if (documentCount == chunks.Count)
                            {
                                _logger.LogInformation("ELASTICSEARCH INDEXING COMPLETE: All {ChunkCount} chunks were successfully indexed and verified", chunks.Count);
                                response.Benchmarks["IndexedDocumentCount"] = documentCount;
                            }
                            else
                            {
                                _logger.LogWarning("ELASTICSEARCH INDEXING INCOMPLETE: Expected {ChunkCount} chunks but found {DocumentCount} documents", chunks.Count, documentCount);
                                response.Benchmarks["ExpectedCount"] = chunks.Count;
                                response.Benchmarks["ActualCount"] = documentCount;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Unable to verify final document count in Elasticsearch: {ErrorMessage}", ex.Message);
                }

                indexingStopwatch.Stop();
                response.Benchmarks["CompleteIndexingTime"] = indexingStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Complete indexing time (including bulk, refresh & availability check): {IndexTime} seconds", indexingStopwatch.Elapsed.TotalSeconds);

                // סיום התהליך
                response.Success = true;
                response.PageCount = 1; // TXT files don't have pages
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("TXT processing completed successfully: {FilePath}, Chunks: {ChunkCount}", filePath, chunks.Count);

                return response;
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                response.ErrorMessage = $"Error processing TXT: {ex.Message}";
                _logger.LogError(ex, "Error processing TXT file: {FilePath}", filePath);
                return response;
            }
        }*/



        /// <summary>
        /// Chunks a text file in parallel for faster processing.
        /// </summary>
        private async Task<List<DocumentChunk>> ChunkTextFileParallelAsyncVersion2(string filePath, string fileId, int chunkSizeInBytes)
        {
            var fileInfo = new FileInfo(filePath);

            // For small files, use the original method
            if (fileInfo.Length <= chunkSizeInBytes)
            {
                return await ChunkTextFileAsync(filePath, fileId, chunkSizeInBytes);
            }

            _logger.LogInformation("Using parallel processing for large file: {FilePath}", filePath);

            int chunkCount = (int)Math.Ceiling((double)fileInfo.Length / chunkSizeInBytes);
            var chunkTasks = new List<Task<DocumentChunk>>();

            // Create a memory-mapped file once for the entire file.
            using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0L, MemoryMappedFileAccess.Read))
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    int index = i; // Capture the loop variable
                                   // Calculate the start position and view size for this chunk.
                    long startPosition = (long)index * chunkSizeInBytes;
                    long viewSize = Math.Min(chunkSizeInBytes, fileInfo.Length - startPosition);
                    int sequenceNumber = index + 1;

                    // Use Task.Run to process each chunk concurrently.
                    chunkTasks.Add(Task.Run(() =>
                    {

                        _logger.LogInformation("Processing chunk {ChunkIndex} on thread {ThreadId}", index, Thread.CurrentThread.ManagedThreadId);

                        // Create a view stream for this chunk.
                        using (var viewStream = mmf.CreateViewStream(startPosition, viewSize, MemoryMappedFileAccess.Read))
                        {
                            using (var reader = new StreamReader(viewStream, Encoding.UTF8))
                            {
                                // Read the entire chunk synchronously (MemoryMappedViewStream reading is fast).
                                string content = reader.ReadToEnd();

                                // Adjust boundaries to find clean breaks if needed.
                                if (index > 0)
                                {
                                    int breakPoint = FindFirstBreakPointVersion2(content);
                                    content = content.Substring(breakPoint);
                                }
                                if (index < chunkCount - 1)
                                {
                                    int breakPoint = FindLastBreakPointVersion2(content);
                                    content = content.Substring(0, breakPoint);
                                }

                                return new DocumentChunk
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    OriginalFilePath = filePath,
                                    FileName = Path.GetFileName(filePath),
                                    FileIdentifier = fileId,
                                    SequenceNumber = sequenceNumber,
                                    StartPage = 1,
                                    EndPage = 1,
                                    TotalPages = 1,
                                    Content = content,
                                    ProcessedAt = DateTime.UtcNow,
                                    FileSizeInBytes = fileInfo.Length,
                                    TotalChunks = chunkCount
                                };
                            }
                        }
                    }));
                }

                var results = await Task.WhenAll(chunkTasks);
                return results.OrderBy(c => c.SequenceNumber).ToList();
            }
        }



        // Helper methods to find clean break points
        private int FindFirstBreakPointVersion2(string text)
        {
            // Simple implementation - find first line break or space
            for (int i = 0; i < Math.Min(text.Length, 500); i++)
            {
                if (text[i] == '\n' || text[i] == ' ')
                    return i + 1;
            }

            return 0; // No good break point found
        }

        private int FindLastBreakPointVersion2(string text)
        {
            // Simple implementation - find last line break or sentence end
            for (int i = text.Length - 1; i > Math.Max(0, text.Length - 500); i--)
            {
                if (text[i] == '\n' ||
                    ((text[i] == '.' || text[i] == '!' || text[i] == '?') && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))))
                    return i + 1;
            }

            return text.Length; // No good break point found
        }


        //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        public async Task<ProcessingResponse> ProcessFileAsyncVersion3(string filePath)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var response = new ProcessingResponse
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = filePath,
                Success = false,
                Benchmarks = new Dictionary<string, double>()
            };

            try
            {
                _logger.LogInformation("Starting TXT processing (v3) for file: {FilePath}", filePath);

                // בדיקת קיום הקובץ וסיומת תקינה
                if (!File.Exists(filePath))
                {
                    response.ErrorMessage = $"File does not exist: {filePath}";
                    return response;
                }
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension != ".txt")
                {
                    response.ErrorMessage = $"Invalid file extension: {extension}. Expected .txt";
                    return response;
                }

                // מידע על הקובץ וזיהוי ייחודי
                var fileInfo = new FileInfo(filePath);
                response.FileSizeInBytes = fileInfo.Length;
                var fileId = $"{Path.GetFileNameWithoutExtension(filePath).Replace(" ", "_")}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";

                // קביעת גודל צ'אנק – מההגדרות או ברירת מחדל
                int chunkSizeInBytes = _settings.ChunkSizeInBytes > 0 ? _settings.ChunkSizeInBytes : DefaultChunkSizeInBytes;

                // STEP 1: קריאה ועיבוד (חלוקת הקובץ לצ'אנקים)
                var readStopwatch = Stopwatch.StartNew();
                List<DocumentChunk> chunks = await ChunkTextFileParallelAsyncVersion2(filePath, fileId, chunkSizeInBytes);
                readStopwatch.Stop();
                response.Benchmarks["FileReadTime"] = readStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Finished reading file. Time taken: {ReadTime} seconds", readStopwatch.Elapsed.TotalSeconds);
                response.ChunkCount = chunks.Count;

                // STEP 2: אינדוקס – מדידה של הזמן הכולל לשליחת הצ'אנקים ל-plugin ואימות זמינותם
                var indexingStopwatch = Stopwatch.StartNew();

                // חלוקת הצ'אנקים לבאטצ'ים ושליחתם ל-plugin
                var maxBatchSize = 10;
                var batchCount = (int)Math.Ceiling(chunks.Count / (double)maxBatchSize);
                var indexTasks = new List<Task<bool>>();
                for (int i = 0; i < chunks.Count; i += maxBatchSize)
                {
                    var batch = chunks.Skip(i).Take(maxBatchSize).ToList();
                    _logger.LogInformation("Indexing batch {CurrentBatch}/{TotalBatches} with {ChunkCount} chunks (v3)",
                        (i / maxBatchSize) + 1, batchCount, batch.Count);

                    var tasksForThisBatch = batch.Select(chunk => SendChunkToCustomPluginAsync(chunk)).ToList();
                    indexTasks.AddRange(tasksForThisBatch);

                    // הגבלת מקביליות – לדוגמה, 4 משימות במקביל
                    var maxParallel = _settings.MaxParallelism > 0 ? _settings.MaxParallelism : 4;
                    while (indexTasks.Count >= maxParallel)
                    {
                        var completedTask = await Task.WhenAny(indexTasks);
                        indexTasks.Remove(completedTask);
                        if (!await completedTask)
                        {
                            response.ErrorMessage = "Failed to index one of the chunks in the plugin.";
                            return response;
                        }
                    }
                }

                var results = await Task.WhenAll(indexTasks);
                if (results.Any(r => !r))
                {
                    response.ErrorMessage = "Failed to index one or more chunks via the plugin.";
                    return response;
                }

                // אחרי שהצ'אנקים נשלחו, נבצע רענון לאינדקס ואימות זמינות המסמכים
                try
                {
                    string url = _settings.Url;
                    if (!url.StartsWith("http://"))
                    {
                        url = "http://" + url;
                    }
                    using var httpClient = new HttpClient();
                    _logger.LogInformation("Requesting Elasticsearch refresh to ensure all documents are searchable");
                    var refreshUrl = $"{url}/target_index/_refresh";
                    await httpClient.PostAsync(refreshUrl, null);

                    // בדיקת ספירת המסמכים
                    var countUrl = $"{url}/target_index/_count";
                    var countResponse = await httpClient.GetAsync(countUrl);
                    if (countResponse.IsSuccessStatusCode)
                    {
                        string countContent = await countResponse.Content.ReadAsStringAsync();
                        using JsonDocument jsonDoc = JsonDocument.Parse(countContent);
                        if (jsonDoc.RootElement.TryGetProperty("count", out JsonElement countElement))
                        {
                            int documentCount = countElement.GetInt32();
                            if (documentCount == chunks.Count)
                            {
                                _logger.LogInformation("ELASTICSEARCH INDEXING COMPLETE: All {ChunkCount} chunks were successfully indexed and verified", chunks.Count);
                                response.Benchmarks["IndexedDocumentCount"] = documentCount;
                            }
                            else
                            {
                                _logger.LogWarning("ELASTICSEARCH INDEXING INCOMPLETE: Expected {ChunkCount} chunks but found {DocumentCount} documents", chunks.Count, documentCount);
                                response.Benchmarks["ExpectedCount"] = chunks.Count;
                                response.Benchmarks["ActualCount"] = documentCount;
                            }
                        }
                    }

                    // בדיקת בריאות הקלאסטר (optional)
                    try
                    {
                        string healthUrl = $"{url}/_cluster/health/target_index?wait_for_status=green&timeout=30s";
                        var healthResponse = await httpClient.GetAsync(healthUrl);
                        if (healthResponse.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Cluster health is green. Index is ready.");
                        }
                        else
                        {
                            _logger.LogWarning("Cluster health check returned non-green status.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error checking cluster health: {ErrorMessage}", ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Unable to verify final document count in Elasticsearch: {ErrorMessage}", ex.Message);
                }

                indexingStopwatch.Stop();
                response.Benchmarks["CompleteIndexingTime"] = indexingStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Complete indexing time (including bulk, refresh & availability check): {IndexTime} seconds", indexingStopwatch.Elapsed.TotalSeconds);

                // סיום התהליך
                response.Success = true;
                response.PageCount = 1; // TXT files don't have pages
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("TXT processing (v3) completed successfully: {FilePath}, Chunks: {ChunkCount}", filePath, chunks.Count);

                return response;
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                response.ErrorMessage = $"Error processing TXT (v3): {ex.Message}";
                _logger.LogError(ex, "Error processing TXT file (v3): {FilePath}", filePath);
                return response;
            }
        }

        /// <summary>
        /// Handles processing of a TXT file located on a NAS (Network Attached Storage) server.
        /// </summary>
        /// <remarks>
        /// This method receives a UNC path to a text file stored on a NAS share (e.g., "\\NAS-SERVER\shared\file.txt").
        /// It performs the following operations:
        /// 1. Validates that the file exists and has a .txt extension.
        /// 2. Retrieves file metadata (size, name, last modified time) and constructs a unique file identifier.
        /// 3. Reads and processes the file in parallel using memory-mapped file access for optimal performance,
        ///    splitting the content into fixed-size <see cref="DocumentChunk"/> instances.
        /// 4. Returns all the extracted chunks as part of the HTTP response, without indexing or pushing them elsewhere.
        /// 
        /// The file is accessed using the SMB (Server Message Block) protocol over TCP/IP (port 445),
        /// which is the standard Windows file sharing protocol. SMB handles authentication, file I/O, and transport over the network.
        /// 
        /// The function is intended to serve plugin clients that request text extraction directly from NAS files
        /// and expect the full content to be streamed back in chunked form (rather than pushed to Elasticsearch or Vespa).
        /// 
        /// Note: The SMB protocol performance is subject to network conditions, authentication delays, and server load.
        /// For high-throughput scenarios, consider exposing the NAS via HTTP or copying files to a local temporary folder.
        /// </remarks>
        /// <param name="filePath">The full UNC path to the target TXT file on the NAS server.</param>
        /// <returns>
        /// A <see cref="ProcessingResponseWithChunks"/> object containing metadata and a list of extracted text chunks.
        /// </returns>
        public async Task<ProcessingResponseWithChunks> handlePluginNasProcessingRequest(string filePath)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var response = new ProcessingResponseWithChunks
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = filePath,
                Success = false,
                Benchmarks = new Dictionary<string, double>(),
                Chunks = new List<DocumentChunk>() // מחזיר את כל הצ'אנקים עצמם
            };
            _logger.LogDebug("[DEBUG] Entering handlePluginNasProcessingRequest with filePath = {FilePath}", filePath);

            // Get initial CPU and heap measurements
            double cpuOverallStart = GetCurrentCpuUsagePercent();
            double heapOverallStart = GetCurrentHeapUsagePercent();

            _logger.LogDebug("[DEBUG] CPU start: {Cpu:F2}%, Heap start: {Heap:F2}%", cpuOverallStart, heapOverallStart);

            _logger.LogInformation($"[CPU] Overall | Phase: Start | Process CPU Load: {cpuOverallStart:F2}%");
            _logger.LogInformation($"[HEAP] Overall | Phase: Start | Heap Used: {heapOverallStart:F2}%");

            try
            {
                _logger.LogInformation("Starting NAS TXT processing for file: {FilePath}", filePath);

                // Validation step
                double cpuValidationStart = GetCurrentCpuUsagePercent();
                double heapValidationStart = GetCurrentHeapUsagePercent();
                long validationStartTime = Environment.TickCount;

                _logger.LogInformation($"[CPU] Step: Validation | Phase: Start | Process CPU Load: {cpuValidationStart:F2}%");
                _logger.LogInformation($"[HEAP] Step: Validation | Phase: Start | Heap Used: {heapValidationStart:F2}%");

                if (!File.Exists(filePath))
                {
                    response.ErrorMessage = $"File does not exist: {filePath}";
                    return response;
                }

                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension != ".txt")
                {
                    response.ErrorMessage = $"Invalid file extension: {extension}. Expected .txt";
                    return response;
                }

                var fileInfo = new FileInfo(filePath);
                response.FileSizeInBytes = fileInfo.Length;
                var fileId = $"{Path.GetFileNameWithoutExtension(filePath).Replace(" ", "_")}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
                int chunkSizeInBytes = _settings.ChunkSizeInBytes > 0 ? _settings.ChunkSizeInBytes : DefaultChunkSizeInBytes;

                double cpuValidationEnd = GetCurrentCpuUsagePercent();
                double heapValidationEnd = GetCurrentHeapUsagePercent();
                long validationDuration = Environment.TickCount - validationStartTime;
                double cpuValidationAvg = (cpuValidationStart + cpuValidationEnd) / 2;
                double heapValidationAvg = (heapValidationStart + heapValidationEnd) / 2;

                _logger.LogInformation($"[CPU] Step: Validation | Phase: End | Process CPU Load: {cpuValidationEnd:F2}%");
                _logger.LogInformation($"[HEAP] Step: Validation | Phase: End | Heap Used: {heapValidationEnd:F2}%");
                _logger.LogInformation($"[CPU] Step: Validation | Phase: Avg | Duration: {validationDuration / 1000.0:F2}s | Avg CPU: {cpuValidationAvg:F2}%");
                _logger.LogInformation($"[HEAP] Validation | Start: {heapValidationStart:F2}% | End: {heapValidationEnd:F2}% | Avg: {heapValidationAvg:F2}%");

                response.Benchmarks["ValidationCpuPercent"] = cpuValidationAvg;
                response.Benchmarks["ValidationHeapPercent"] = heapValidationAvg;
                response.Benchmarks["ValidationTimeSec"] = validationDuration / 1000.0;

                // File reading step
                double cpuReadStart = GetCurrentCpuUsagePercent();
                double heapReadStart = GetCurrentHeapUsagePercent();
                long readStartTime = Environment.TickCount;

                _logger.LogInformation($"[CPU] Step: FileRead | Phase: Start | Process CPU Load: {cpuReadStart:F2}%");
                _logger.LogInformation($"[HEAP] Step: FileRead | Phase: Start | Heap Used: {heapReadStart:F2}%");

                var chunks = await ChunkTextFileParallelAsyncVersion2(filePath, fileId, chunkSizeInBytes);

                double cpuReadEnd = GetCurrentCpuUsagePercent();
                double heapReadEnd = GetCurrentHeapUsagePercent();
                long readDuration = Environment.TickCount - readStartTime;
                double cpuReadAvg = (cpuReadStart + cpuReadEnd) / 2;
                double heapReadAvg = (heapReadStart + heapReadEnd) / 2;

                _logger.LogInformation($"[CPU] Step: FileRead | Phase: End | Process CPU Load: {cpuReadEnd:F2}%");
                _logger.LogInformation($"[HEAP] Step: FileRead | Phase: End | Heap Used: {heapReadEnd:F2}%");
                _logger.LogInformation($"[CPU] Step: FileRead | Phase: Avg | Duration: {readDuration / 1000.0:F2}s | Avg CPU: {cpuReadAvg:F2}%");
                _logger.LogInformation($"[HEAP] FileRead | Start: {heapReadStart:F2}% | End: {heapReadEnd:F2}% | Avg: {heapReadAvg:F2}%");

                response.Benchmarks["FileReadCpuPercent"] = cpuReadAvg;
                response.Benchmarks["FileReadHeapPercent"] = heapReadAvg;
                response.Benchmarks["FileReadTimeSec"] = readDuration / 1000.0;

                response.ChunkCount = chunks.Count;
                response.Chunks = chunks;

                // Prepare response step
                double cpuPrepareResponseStart = GetCurrentCpuUsagePercent();
                double heapPrepareResponseStart = GetCurrentHeapUsagePercent();
                long prepareResponseStartTime = Environment.TickCount;

                _logger.LogInformation($"[CPU] Step: PrepareResponse | Phase: Start | Process CPU Load: {cpuPrepareResponseStart:F2}%");
                _logger.LogInformation($"[HEAP] Step: PrepareResponse | Phase: Start | Heap Used: {heapPrepareResponseStart:F2}%");

                response.Success = true;
                response.PageCount = 1;

                double cpuPrepareResponseEnd = GetCurrentCpuUsagePercent();
                double heapPrepareResponseEnd = GetCurrentHeapUsagePercent();
                long prepareResponseDuration = Environment.TickCount - prepareResponseStartTime;
                double cpuPrepareResponseAvg = (cpuPrepareResponseStart + cpuPrepareResponseEnd) / 2;
                double heapPrepareResponseAvg = (heapPrepareResponseStart + heapPrepareResponseEnd) / 2;

                _logger.LogInformation($"[CPU] Step: PrepareResponse | Phase: End | Process CPU Load: {cpuPrepareResponseEnd:F2}%");
                _logger.LogInformation($"[HEAP] Step: PrepareResponse | Phase: End | Heap Used: {heapPrepareResponseEnd:F2}%");
                _logger.LogInformation($"[CPU] Step: PrepareResponse | Phase: Avg | Duration: {prepareResponseDuration / 1000.0:F2}s | Avg CPU: {cpuPrepareResponseAvg:F2}%");
                _logger.LogInformation($"[HEAP] PrepareResponse | Start: {heapPrepareResponseStart:F2}% | End: {heapPrepareResponseEnd:F2}% | Avg: {heapPrepareResponseAvg:F2}%");

                response.Benchmarks["PrepareResponseCpuPercent"] = cpuPrepareResponseAvg;
                response.Benchmarks["PrepareResponseHeapPercent"] = heapPrepareResponseAvg;
                response.Benchmarks["PrepareResponseTimeSec"] = prepareResponseDuration / 1000.0;

                // Overall metrics
                overallStopwatch.Stop();
                double cpuOverallEnd = GetCurrentCpuUsagePercent();
                double heapOverallEnd = GetCurrentHeapUsagePercent();
                double cpuOverallAvg = (cpuOverallStart + cpuOverallEnd) / 2;
                double heapOverallAvg = (heapOverallStart + heapOverallEnd) / 2;

                _logger.LogInformation($"[CPU] Overall | Phase: End | Process CPU Load: {cpuOverallEnd:F2}%");
                _logger.LogInformation($"[HEAP] Overall | Phase: End | Heap Used: {heapOverallEnd:F2}%");
                _logger.LogInformation($"[CPU] Overall | Phase: Avg | Avg CPU: {cpuOverallAvg:F2}%");
                _logger.LogInformation($"[HEAP] Overall | Start: {heapOverallStart:F2}% | End: {heapOverallEnd:F2}% | Avg: {heapOverallAvg:F2}%");

                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                response.Benchmarks["TotalCpuAvgPercent"] = cpuOverallAvg;
                response.Benchmarks["TotalHeapAvgPercent"] = heapOverallAvg;

                _logger.LogInformation("TXT processing from NAS completed successfully. Chunks: {ChunkCount}", chunks.Count);
                return response;
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                response.ErrorMessage = $"Error processing TXT file from NAS: {ex.Message}";
                _logger.LogError(ex, "Error processing TXT file from NAS: {FilePath}", filePath);
                return response;
            }
        }

        /// <summary>
        /// Gets the current CPU usage percentage for the .NET process
        /// </summary>
        private double GetCurrentCpuUsagePercent()
        {
            try
            {
                using var currentProcess = Process.GetCurrentProcess();

                // This approach gives an approximate measure - for more accuracy,
                // take readings over time and calculate the delta
                TimeSpan totalProcessorTime = currentProcess.TotalProcessorTime;

                // Get number of logical processors
                int processorCount = Environment.ProcessorCount;

                // Calculate CPU usage as a percentage of available processing power
                // across all cores (max is 100% per core * number of cores)
                double cpuUsage = totalProcessorTime.TotalSeconds /
                                 (Environment.TickCount / 1000.0) /
                                 processorCount * 100;

                // Ensure we don't return more than 100%
                return Math.Min(100, Math.Max(0, cpuUsage));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get CPU usage: {Message}", ex.Message);
                return -1; // Return -1 to indicate error
            }
        }

        /// <summary>
        /// Gets the current heap memory usage percentage
        /// </summary>
        private double GetCurrentHeapUsagePercent()
        {
            try
            {
                return _memoryInfo.GetUsedHeapPercent();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get heap usage: {Message}", ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Sends a single chunk as JSON to the /_custom_index endpoint.
        /// </summary>
        private async Task<bool> SendChunkToCustomPluginAsync(DocumentChunk chunk)
        {
            try
            {
                // Prepare JSON payload with relevant chunk data
                var bodyObject = new
                {
                    content = chunk.Content,
                    fileIdentifier = chunk.FileIdentifier,
                    fileName = chunk.FileName,
                    sequenceNumber = chunk.SequenceNumber,
                    totalChunks = chunk.TotalChunks
                };
                var json = JsonSerializer.Serialize(bodyObject);

                // Create simple HTTP client
                using var httpClient = new HttpClient();

                // Format URL correctly
                string url = _settings.Url;
                if (!url.StartsWith("http://"))
                {
                    url = "http://" + url;
                }
                var pluginUrl = $"{url}/_custom_index";

                // Log before sending request
                _logger.LogDebug("Sending chunk {SequenceNumber}/{TotalChunks} to Elasticsearch plugin",
                    chunk.SequenceNumber, chunk.TotalChunks);

                // Send the request
                using var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
                var stopwatch = Stopwatch.StartNew();
                var response = await httpClient.PostAsync(pluginUrl, requestContent);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to POST chunk {SequenceNumber}/{TotalChunks} to {PluginUrl}. Status: {StatusCode}, Response: {Response}",
                        chunk.SequenceNumber, chunk.TotalChunks, pluginUrl, response.StatusCode, responseBody);
                    return false;
                }

                // Parse response to extract indexing details
                string responseContent = await response.Content.ReadAsStringAsync();
                using JsonDocument jsonDoc = JsonDocument.Parse(responseContent);

                // Check if indexing was successful directly from our custom field
                if (jsonDoc.RootElement.TryGetProperty("indexing_success", out JsonElement successElement) &&
                    successElement.GetBoolean())
                {
                    // Extract document number if provided by plugin
                    string docNumberInfo = "";
                    if (jsonDoc.RootElement.TryGetProperty("document_number", out JsonElement docNumElement) &&
                        jsonDoc.RootElement.TryGetProperty("total_processed", out JsonElement totalElement))
                    {
                        docNumberInfo = $"(Document #{docNumElement.GetInt32()} of {totalElement.GetInt32()} indexed)";
                    }

                    // Extract processing time if provided by plugin
                    string processingTimeInfo = "";
                    if (jsonDoc.RootElement.TryGetProperty("processing_time_ms", out JsonElement timeElement))
                    {
                        processingTimeInfo = $"in {timeElement.GetInt64()}ms";
                    }
                    else
                    {
                        processingTimeInfo = $"in {stopwatch.ElapsedMilliseconds}ms";
                    }

                    // Log success with detailed information
                    _logger.LogInformation("Chunk {SequenceNumber}/{TotalChunks} indexed successfully {ProcessingTime} {DocInfo}",
                        chunk.SequenceNumber, chunk.TotalChunks, processingTimeInfo, docNumberInfo);

                    return true;
                }
                else
                {
                    _logger.LogWarning("Chunk {SequenceNumber}/{TotalChunks} may not be indexed properly. Response: {Response}",
                        chunk.SequenceNumber, chunk.TotalChunks, responseContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chunk {SequenceNumber}/{TotalChunks} to plugin",
                    chunk.SequenceNumber, chunk.TotalChunks);
                return false;
            }
        }

        /// <summary>
        /// Processes a TXT file and sends its chunks to the Vespa plugin endpoint.
        /// This method is identical in logic to ProcessFileAsyncVersion3 but targets Vespa.
        /// </summary>
        public async Task<ProcessingResponse> ProcessFileAsyncVespa(string filePath)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var response = new ProcessingResponse
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = filePath,
                Success = false,
                Benchmarks = new Dictionary<string, double>()
            };

            try
            {
                _logger.LogInformation("Starting TXT processing via Vespa plugin for file: {FilePath}", filePath);

                // Validate that the file exists
                if (!File.Exists(filePath))
                {
                    response.ErrorMessage = $"File does not exist: {filePath}";
                    return response;
                }

                // Validate file extension
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension != ".txt")
                {
                    response.ErrorMessage = $"Invalid file extension: {extension}. Expected .txt";
                    return response;
                }

                // Get file info
                var fileInfo = new FileInfo(filePath);
                response.FileSizeInBytes = fileInfo.Length;

                // Generate a unique file identifier
                var fileId = $"{Path.GetFileNameWithoutExtension(filePath).Replace(" ", "_")}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";

                // Determine chunk size from VespaSettings (5MB per chunk)
                int chunkSizeInBytes = _vespaSettings.ChunkSizeInBytes;

                // STEP 1: Split the file into chunks (using the parallel method if applicable)
                var readStopwatch = Stopwatch.StartNew();
                List<DocumentChunk> chunks = await ChunkTextFileParallelAsyncVersion2(filePath, fileId, chunkSizeInBytes);
                readStopwatch.Stop();
                response.Benchmarks["FileReadTime"] = readStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Finished reading file. Time taken: {ReadTime} seconds", readStopwatch.Elapsed.TotalSeconds);

                response.ChunkCount = chunks.Count;

                // STEP 2: Send the chunks to the Vespa plugin in batches using BulkBatchSize
                var indexStopwatch = Stopwatch.StartNew();
                int maxBatchSize = _vespaSettings.BulkBatchSize;
                int batchCount = (int)Math.Ceiling(chunks.Count / (double)maxBatchSize);
                var indexTasks = new List<Task<bool>>();

                for (int i = 0; i < chunks.Count; i += maxBatchSize)
                {
                    var batch = chunks.Skip(i).Take(maxBatchSize).ToList();
                    _logger.LogInformation("Indexing batch {CurrentBatch}/{TotalBatches} with {ChunkCount} chunks via Vespa plugin",
                        (i / maxBatchSize) + 1, batchCount, batch.Count);

                    var tasksForThisBatch = batch.Select(chunk => SendChunkToVespaPluginAsync(chunk)).ToList();
                    indexTasks.AddRange(tasksForThisBatch);

                    // Limit parallel tasks according to configuration or default value of 4
                    int maxParallel = _settings.MaxParallelism > 0 ? _settings.MaxParallelism : 4;
                    while (indexTasks.Count >= maxParallel)
                    {
                        var completedTask = await Task.WhenAny(indexTasks);
                        indexTasks.Remove(completedTask);

                        if (!await completedTask)
                        {
                            response.ErrorMessage = "Failed to index one of the chunks via Vespa plugin.";
                            return response;
                        }
                    }
                }

                // Wait for any remaining tasks to complete
                var results = await Task.WhenAll(indexTasks);
                if (results.Any(r => !r))
                {
                    response.ErrorMessage = "Failed to index one or more chunks via Vespa plugin.";
                    return response;
                }

                indexStopwatch.Stop();
                response.Benchmarks["IndexingTime"] = indexStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("Finished sending data to Vespa plugin. Time taken: {IndexTime} seconds", indexStopwatch.Elapsed.TotalSeconds);

                // Finalize response
                response.Success = true;
                response.PageCount = 1; // TXT files do not have pages

                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                _logger.LogInformation("TXT processing via Vespa plugin completed successfully: {FilePath}, Chunks: {ChunkCount}", filePath, chunks.Count);

                return response;
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                response.ProcessingTimeInSeconds = overallStopwatch.Elapsed.TotalSeconds;
                response.ErrorMessage = $"Error processing TXT via Vespa plugin: {ex.Message}";
                _logger.LogError(ex, "Error processing TXT file via Vespa plugin: {FilePath}", filePath);
                return response;
            }
        }

        /// <summary>
        /// Sends a single chunk as JSON to the Vespa plugin endpoint.
        /// Assumes that Vespa is listening on the endpoint: {VespaSettings.Url}/{VespaSettings.IndexName}/_vespa_index.
        /// </summary>
        private async Task<bool> SendChunkToVespaPluginAsync(DocumentChunk chunk)
        {
            try
            {
                // Prepare JSON payload with chunk data
                var bodyObject = new
                {
                    content = chunk.Content,
                    fileIdentifier = chunk.FileIdentifier,
                    fileName = chunk.FileName,
                    sequenceNumber = chunk.SequenceNumber,
                    totalChunks = chunk.TotalChunks
                };
                var json = JsonSerializer.Serialize(bodyObject);

                // Create an HttpClient with the specified connection timeout
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(_vespaSettings.ConnectionTimeout)
                };

                string url = _vespaSettings.Url;
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                {
                    url = "http://" + url;
                }
                // Build the Vespa endpoint using the IndexName from settings
                var vespaUrl = $"{url}/custom-indexing";

                _logger.LogDebug("Sending chunk {SequenceNumber}/{TotalChunks} to Vespa plugin", chunk.SequenceNumber, chunk.TotalChunks);

                using var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
                var stopwatch = Stopwatch.StartNew();
                var response = await httpClient.PostAsync(vespaUrl, requestContent);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to POST chunk {SequenceNumber}/{TotalChunks} to {VespaUrl}. Status: {StatusCode}, Response: {Response}",
                        chunk.SequenceNumber, chunk.TotalChunks, vespaUrl, response.StatusCode, responseBody);
                    return false;
                }

                string responseContent = await response.Content.ReadAsStringAsync();
                using JsonDocument jsonDoc = JsonDocument.Parse(responseContent);

                if (jsonDoc.RootElement.TryGetProperty("indexing_success", out JsonElement successElement) &&
                    successElement.GetBoolean())
                {
                    string docNumberInfo = "";
                    if (jsonDoc.RootElement.TryGetProperty("document_number", out JsonElement docNumElement) &&
                        jsonDoc.RootElement.TryGetProperty("total_processed", out JsonElement totalElement))
                    {
                        docNumberInfo = $"(Document #{docNumElement.GetInt32()} of {totalElement.GetInt32()} indexed)";
                    }
                    string processingTimeInfo = "";
                    if (jsonDoc.RootElement.TryGetProperty("processing_time_ms", out JsonElement timeElement))
                    {
                        processingTimeInfo = $"in {timeElement.GetInt64()}ms";
                    }
                    else
                    {
                        processingTimeInfo = $"in {stopwatch.ElapsedMilliseconds}ms";
                    }
                    _logger.LogInformation("Chunk {SequenceNumber}/{TotalChunks} indexed successfully {ProcessingTime} {DocInfo}",
                        chunk.SequenceNumber, chunk.TotalChunks, processingTimeInfo, docNumberInfo);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Chunk {SequenceNumber}/{TotalChunks} may not be indexed properly by Vespa. Response: {Response}",
                        chunk.SequenceNumber, chunk.TotalChunks, responseContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending chunk {SequenceNumber}/{TotalChunks} to Vespa plugin",
                    chunk.SequenceNumber, chunk.TotalChunks);
                return false;
            }
        }
    }
}