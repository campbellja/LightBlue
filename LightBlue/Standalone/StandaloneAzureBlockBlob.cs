﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using LightBlue.Infrastructure;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

using Newtonsoft.Json;

namespace LightBlue.Standalone
{
    public class StandaloneAzureBlockBlob : IAzureBlockBlob
    {
        private const int BufferSize = 4096;
        private const int MaxFileLockRetryAttempts = 5;

        private readonly string _blobName;
        private readonly string _blobPath;
        private readonly string _metadataPath;
        private readonly StandaloneAzureBlobProperties _properties;
        private Dictionary<string, string> _metadata;
        private readonly TimeSpan _waitTimeBetweenRetries = TimeSpan.FromSeconds(5);

        public StandaloneAzureBlockBlob(string containerDirectory, string blobName)
        {
            _blobName = blobName;
            _blobPath = Path.Combine(containerDirectory, blobName);
            _metadataPath = Path.Combine(containerDirectory, ".meta", blobName);
            _metadata = new Dictionary<string, string>();
            _properties = new StandaloneAzureBlobProperties {Length = -1, ContentType = null};
        }

        public Uri Uri
        {
            get { return new Uri(_blobPath); }
        }

        public string Name
        {
            get { return _blobName; }
        }

        public IAzureBlobProperties Properties
        {
            get { return _properties; }
        }

        public IAzureCopyState CopyState { get; private set; }

        public IDictionary<string, string> Metadata
        {
            get { return _metadata; }
        }

        public void Delete()
        {
            File.Delete(_blobPath);
            File.Delete(_metadataPath);
        }

        public Task DeleteAsync()
        {
            Delete();
            return Task.FromResult(new object());
        }

        public bool Exists()
        {
            return File.Exists(_blobPath);
        }

        public Task<bool> ExistsAsync()
        {
            return Task.FromResult(File.Exists(_blobPath));
        }

        public void FetchAttributes()
        {
            var fileInfo = new FileInfo(_blobPath);
            if (!fileInfo.Exists)
            {
                throw new StorageException("The specified blob does not exist");
            }
            
            var metadataStore = LoadMetadataStore();
            _properties.ContentType = metadataStore.ContentType;
            _properties.Length = fileInfo.Length;
            _metadata = metadataStore.Metadata;
        }

        public void SetMetadata()
        {
            var fileInfo = new FileInfo(_blobPath);
            if (!fileInfo.Exists)
            {
                throw new StorageException("The specified blob does not exist");
            }

            StandaloneMetadataStore metadataStore = null;

            if (!File.Exists(_metadataPath))
            {
                metadataStore = new StandaloneMetadataStore
                {
                    ContentType = File.Exists(_blobPath) ? "application/octet-stream" : null,
                    Metadata = new Dictionary<string, string>()
                };
            }
            
            FileLockExtensions.WaitAndRetryOnFileLock(()=> SetMetadata(metadataStore), _waitTimeBetweenRetries, MaxFileLockRetryAttempts, WhenSetMetadataFileHasSharingViolation);
        }

        private void WhenSetMetadataFileHasSharingViolation(int retriesRemaining)
        {
            if (retriesRemaining <= 0)
            {
                throw new StorageException(String.Format("Tried {0} times to write to locked metadata file {1}", MaxFileLockRetryAttempts, _metadataPath));
            }
        }

        private void SetMetadata(StandaloneMetadataStore currentMetadataStore)
        {
            using (var fileStream = new FileStream(_metadataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, true))
            {
                if (currentMetadataStore == null)
                {
                    currentMetadataStore = ReadMetadataStore(fileStream);
                }

                foreach (var key in _metadata.Keys)
                {
                    currentMetadataStore.Metadata[key] = _metadata[key];
                }
                
                fileStream.SetLength(0);
                WriteMetadataStore(currentMetadataStore, fileStream);
            }
        }

        public Task SetMetadataAsync()
        {
            SetMetadata();
            return Task.FromResult(new object());
        }

        public void SetProperties()
        {
            var fileInfo = new FileInfo(_blobPath);
            if (!fileInfo.Exists)
            {
                throw new StorageException("The specified blob does not exist");
            }
            var metadataStore = LoadMetadataStore();

            metadataStore.ContentType = _properties.ContentType;

            WriteMetadataStore(metadataStore);
        }

        public Task SetPropertiesAsync()
        {
            SetProperties();
            return Task.FromResult(new object());
        }

        public string GetSharedAccessSignature(SharedAccessBlobPolicy policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException("policy");
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "?sv={0:yyyy-MM-dd}&sr=b&sig=s&sp={1}",
                DateTime.Today,
                policy.Permissions.DeterminePermissionsString());
        }

        public void DownloadToStream(Stream target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            try
            {
                using (var fileStream = new FileStream(_blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    do
                    {
                        bytesRead = fileStream.Read(buffer, 0, buffer.Length);

                        target.Write(buffer, 0, bytesRead);
                    } while (bytesRead == BufferSize);
                }
            }
            catch (FileNotFoundException ex)
            {
                throw new StorageException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The blob file was not found. Expected location '{0}'.",
                        _blobPath),
                    ex);
            }
        }

        public async Task DownloadToStreamAsync(Stream target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            try
            {
                using (var fileStream = new FileStream(_blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    do
                    {
                        bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                        await target.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    } while (bytesRead == BufferSize);
                }
            }
            catch (FileNotFoundException ex)
            {
                throw new StorageException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The blob file was not found. Expected location '{0}'.",
                        _blobPath),
                    ex);
            }
        }

        public async Task UploadFromStreamAsync(Stream source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            using (var fileStream = new FileStream(_blobPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buffer = new byte[BufferSize];
                int bytesRead;
                do
                {
                    bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);

                    await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                } while (bytesRead == BufferSize);
            }
        }

        public Task UploadFromFileAsync(string path)
        {
            File.Copy(path, _blobPath, true);

            return Task.FromResult(new object());
        }

        public Task UploadFromByteArrayAsync(byte[] buffer)
        {
            return UploadFromByteArrayAsync(buffer, 0, buffer.Length);
        }

        public async Task UploadFromByteArrayAsync(byte[] buffer, int index, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            using (var fileStream = new FileStream(_blobPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                await fileStream.WriteAsync(buffer, index, count).ConfigureAwait(false);
            }
        }

        public string StartCopyFromBlob(IAzureBlockBlob source)
        {
            var standaloneAzureBlockBlob = source as StandaloneAzureBlockBlob;
            if (standaloneAzureBlockBlob == null)
            {
                throw new ArgumentException("Can only copy between blobs in the same hosting environment");
            }

            try
            {
                RetryFileOperation(() => File.Copy(standaloneAzureBlockBlob._blobPath, _blobPath, true));
                if ( File.Exists(standaloneAzureBlockBlob._metadataPath))
                {
                    RetryFileOperation(() => File.Copy(standaloneAzureBlockBlob._metadataPath, _metadataPath, true));
                }
                else
                {
                    RetryFileOperation(() => File.Delete(_metadataPath));
                }

                CopyState = new StandaloneAzureCopyState(CopyStatus.Success, null);
            }
            catch (IOException ex)
            {
                CopyState = new StandaloneAzureCopyState(CopyStatus.Failed, ex.ToTraceMessage());
            }
            return Guid.NewGuid().ToString();
        }

        private static void RetryFileOperation(Action fileOperation)
        {
            var retryCount = 0;
            while (true)
            {
                try
                {
                    fileOperation();
                    break;
                }
                catch (IOException)
                {
                    if (retryCount++ >= 4)
                    {
                        throw;
                    }

                    Thread.Sleep(retryCount * 100);
                }
            }
        }

        private StandaloneMetadataStore LoadMetadataStore()
        {
            if (!File.Exists(_metadataPath))
            {
                return new StandaloneMetadataStore
                {
                    ContentType = File.Exists(_blobPath) ? "application/octet-stream" : null,
                    Metadata = new Dictionary<string, string>()
                };
            }

            using (var file = File.OpenText(_metadataPath))
            {
                var serializer = new JsonSerializer();
                return (StandaloneMetadataStore)serializer.Deserialize(file, typeof(StandaloneMetadataStore));
            }
        }

        private void WriteMetadataStore(StandaloneMetadataStore metadataStore)
        {
            using (var file = File.CreateText(_metadataPath))
            {
                var serializer = new JsonSerializer();
                serializer.Serialize(file, metadataStore);
            }
        }

        private StandaloneMetadataStore ReadMetadataStore(Stream fileStream)
        {
            var serializer = new JsonSerializer();
            return (StandaloneMetadataStore)serializer.Deserialize(new StreamReader(fileStream), typeof(StandaloneMetadataStore));
        }

        private void WriteMetadataStore(StandaloneMetadataStore metadataStore, Stream fileStream)
        {
            using (var streamWriter = new StreamWriter(fileStream))
            {
                new JsonSerializer().Serialize(streamWriter, metadataStore);
            }
        }
    }
}