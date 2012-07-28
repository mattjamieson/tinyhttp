namespace TinyHttp.Tests.Responses
{
    using System.Net;
    using System.Text;
    using FluentAssertions;
    using NUnit.Framework;

    public class HtmlResponseTest
    {
        [Test]
        public void Defaults()
        {
            var response = new HtmlResponse("Test");
            response.ContentType.Should().Be("text/html");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void SetEncoding()
        {
            var response = new HtmlResponse("Test", encoding: Encoding.ASCII);
            response.ContentType.Should().Be("text/html");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void SetContentType()
        {
            var response = new HtmlResponse("Test", contentType: "text/javascript");
            response.ContentType.Should().Be("text/javascript");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().Be("Test");
        }

        [Test]
        public void NullBody()
        {
            var response = new HtmlResponse(null);
            response.ContentType.Should().Be("text/html");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Body.AsString().Should().BeNull();
        }
    }
}