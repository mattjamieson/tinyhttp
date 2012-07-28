namespace TinyHttp.Tests
{
    using System.Net;
    using FluentAssertions;
    using NUnit.Framework;
    
    public class NotFoundResponseTest
    {
        [Test]
        public void NotFoundResponse()
        {
            var response = new NotFoundResponse("application/json");
            response.ContentType.Should().Be("application/json");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void DefaultNotFoundResponse()
        {
            var response = new NotFoundResponse();
            response.ContentType.Should().Be("text/html");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}