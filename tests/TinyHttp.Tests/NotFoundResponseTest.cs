namespace TinyHttp.Tests
{
    using System.Net;
    using NUnit.Framework;

    public class NotFoundResponseTest
    {
        [Test]
        public void NotFoundResponse()
        {
            var response = new NotFoundResponse("application/json");
            Assert.AreEqual("application/json", response.ContentType);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Test]
        public void DefaultNotFoundResponse()
        {
            var response = new NotFoundResponse();
            Assert.AreEqual("text/html", response.ContentType);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}