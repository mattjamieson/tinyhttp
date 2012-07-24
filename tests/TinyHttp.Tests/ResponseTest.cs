namespace TinyHttp.Tests
{
    using System.Net;
    using NUnit.Framework;

    public class ResponseTest
    {
        [Test] 
        public void Constructor()
        {
            var response = new Response();
            Assert.IsNull(response.Body);
            Assert.IsNull(response.ContentType);
            Assert.IsNotNull(response.Headers);
            Assert.AreEqual(0, response.Headers.Count);
            Assert.AreEqual(default(HttpStatusCode), response.StatusCode);
        }
    }
}