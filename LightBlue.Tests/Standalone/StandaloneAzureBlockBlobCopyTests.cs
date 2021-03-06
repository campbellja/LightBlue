using System.Collections.Generic;
using System.IO;
using System.Text;

using ExpectedObjects;
using ExpectedObjects.Comparisons;

using LightBlue.Standalone;

using Microsoft.WindowsAzure.Storage.Blob;

using Xunit;

namespace LightBlue.Tests.Standalone
{
    public class StandaloneAzureBlockBlobCopyTests : StandaloneAzureTestsBase
    {
        private const string SourceBlobName = "source";
        private const string DestinationBlobName = "destination";

        public StandaloneAzureBlockBlobCopyTests()
            : base(DirectoryType.Container)
        {
        }

        [Fact]
        public void CanCopyBlob()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.StartCopyFromBlob(sourceBlob);

            Assert.Equal("File content", File.ReadAllText(destinationBlob.Uri.LocalPath));
        }

        [Fact]
        public void CanOverwriteBlob()
        {
            var sourceBuffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(sourceBuffer, 0, sourceBuffer.Length).Wait();

            var originalContentBuffer = Encoding.UTF8.GetBytes("Original content");
            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.UploadFromByteArrayAsync(originalContentBuffer, 0, originalContentBuffer.Length).Wait();
            destinationBlob.StartCopyFromBlob(sourceBlob);

            Assert.Equal("File content", File.ReadAllText(destinationBlob.Uri.LocalPath));
        }

        [Fact]
        public void WillNotCopyMetadataWhereItDoesNotExist()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.StartCopyFromBlob(sourceBlob);

            Assert.False(File.Exists(Path.Combine(BasePath, ".meta", DestinationBlobName)));
        }

        [Fact]
        public void WillCopyMetadataFromSourceWherePresent()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();
            sourceBlob.Metadata["thing"] = "something";
            sourceBlob.Properties.ContentType = "whatever";
            sourceBlob.SetMetadata();
            sourceBlob.SetProperties();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.StartCopyFromBlob(sourceBlob);
            destinationBlob.FetchAttributes();

            new
            {
                Metadata = new Dictionary<string, string>
                {
                    {"thing", "something"}
                },
                Properties = new
                {
                    ContentType = "whatever",
                    Length = (long) 12
                }
            }.ToExpectedObject().ShouldMatch(destinationBlob);
        }

        [Fact]
        public void WillRemoveExistingMetadataWhereSourceDoesNotHaveMetadata()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var originalContentBuffer = Encoding.UTF8.GetBytes("Original content");
            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.UploadFromByteArrayAsync(originalContentBuffer, 0, originalContentBuffer.Length).Wait();
            destinationBlob.Metadata["thing"] = "other thing";
            destinationBlob.SetMetadata();
            destinationBlob.StartCopyFromBlob(sourceBlob);

            Assert.False(File.Exists(Path.Combine(BasePath, ".meta", DestinationBlobName)));
        }

        [Fact]
        public void CopyStateIsNullBeforeCopy()
        {
            var blob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);

            Assert.Null(blob.CopyState);
        }

        [Fact]
        public void CopyStateIsSuccessAfterCopy()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
            destinationBlob.StartCopyFromBlob(sourceBlob);

            Assert.Equal(CopyStatus.Success, destinationBlob.CopyState.Status);
        }

        [Fact]
        public void CopyStateIsFailedIfBlobLocked()
        {
            var buffer = Encoding.UTF8.GetBytes("File content");
            var sourceBlob = new StandaloneAzureBlockBlob(BasePath, SourceBlobName);
            sourceBlob.UploadFromByteArrayAsync(buffer, 0, buffer.Length).Wait();

            using (new FileStream(sourceBlob.Uri.LocalPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var destinationBlob = new StandaloneAzureBlockBlob(BasePath, DestinationBlobName);
                destinationBlob.StartCopyFromBlob(sourceBlob);

                new
                {
                    Status = CopyStatus.Failed,
                    StatusDescription = new NotNullComparison()
                }.ToExpectedObject().ShouldMatch(destinationBlob.CopyState);
            }
        }
    }
}