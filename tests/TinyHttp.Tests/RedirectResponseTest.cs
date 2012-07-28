namespace TinyHttp.Tests
{
    using System;
    using System.IO;
    using System.Net;
    using NUnit.Framework;

    public class RedirectResponseTest
    {
        [Test]
        public void PermanentRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.Permanent);
            Assert.AreEqual("/", response.Headers["Location"]);
            Assert.AreEqual(String.Empty, BodyAsString(response));
            Assert.AreEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
        }

        [Test]
        public void TemporaryRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.Temporary);
            Assert.AreEqual("/", response.Headers["Location"]);
            Assert.AreEqual(String.Empty, BodyAsString(response));
            Assert.AreEqual(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        }

        [Test]
        public void SeeOtherRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.SeeOther);
            Assert.AreEqual("/", response.Headers["Location"]);
            Assert.AreEqual(String.Empty, BodyAsString(response));
            Assert.AreEqual(HttpStatusCode.SeeOther, response.StatusCode);
        }

        [Test]
        public void DefaultRedirect()
        {
            var response = new RedirectResponse("/");
            Assert.AreEqual("/", response.Headers["Location"]);
            Assert.AreEqual(String.Empty, BodyAsString(response));
            Assert.AreEqual(HttpStatusCode.SeeOther, response.StatusCode);
        }

        private static string BodyAsString(Response response)
        {
            string body;
            using (var stream = new MemoryStream())
            {
                response.Body.Invoke(stream);
                stream.Seek(0, SeekOrigin.Begin);
                body = new StreamReader(stream).ReadToEnd();
            }
            return body;
        }
    }
}