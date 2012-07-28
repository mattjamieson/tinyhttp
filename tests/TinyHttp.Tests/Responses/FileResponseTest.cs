namespace TinyHttp.Tests.Responses
{
    using System;
    using System.IO;
    using System.Net;
    using FluentAssertions;
    using NUnit.Framework;

    public class FileResponseTest
    {
        [Test]
        public void NullFilePath()
        {
            var response = new FileResponse(null);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void EmptyFilePath()
        {
            var response = new FileResponse(String.Empty);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void NoExtension()
        {
            var response = new FileResponse("NoExtension");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void NotInBaseDirectory()
        {
            var response = new FileResponse(@"C:\Temp\Test.txt");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void DoesNotExist()
        {
            FileResponse.BaseDirectory = Path.GetTempPath();
            var file = Path.Combine(Path.GetTempPath(), "Test.txt");
            var response = new FileResponse(file);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void DefaultBaseDirectory()
        {
            FileResponse.BaseDirectory.Should().Be(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "html"));
        }

        [Test]
        public void FileExists()
        {
            FileResponse.BaseDirectory = Path.GetTempPath();
            var filePath = Path.GetTempFileName();
            File.WriteAllText(filePath, "Test");
            var fileInfo = new FileInfo(filePath);
            var response = new FileResponse(filePath);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.ContentType.Should().Be(MimeTypes.GetMimeType(filePath));
            response.Headers.Should().Contain("ETag", fileInfo.LastWriteTimeUtc.Ticks.ToString("x"));
            response.Headers.Should().Contain("Last-Modified", fileInfo.LastWriteTimeUtc.ToString("R"));
            response.Body.AsString().Should().Be("Test");
            File.Delete(filePath);
        }
    }
}