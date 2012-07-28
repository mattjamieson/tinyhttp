namespace TinyHttp.Tests
{
    using System.Net;
    using FluentAssertions;
    using NUnit.Framework;

    public class ResponseTest
    {
        [Test] 
        public void Constructor()
        {
            var response = new Response();
            response.Body.Should().BeNull();
            response.ContentType.Should().BeNull();
            response.Headers.Should().BeEmpty();
            response.StatusCode.Should().Be(default(HttpStatusCode));
        }
    }
}