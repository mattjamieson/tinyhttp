namespace TinyHttp.Tests.Responses
{
    using System.Net;
    using System.Text;
    using FluentAssertions;
    using NUnit.Framework;

    public class TextResponseTest
    {
        [Test]
        public void Defaults()
        {
            var response = new TextResponse("Test");
            response.ContentType.Should().Be("text/plain");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void SetEncoding()
        {
            var response = new TextResponse("Test", encoding: Encoding.ASCII);
            response.ContentType.Should().Be("text/plain");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void SetContentType()
        {
            var response = new TextResponse("Test", contentType: "text/javascript");
            response.ContentType.Should().Be("text/javascript");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void NullBody()
        {
            var response = new TextResponse(null);
            response.ContentType.Should().Be("text/plain");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().BeNull();
        }
    }
}