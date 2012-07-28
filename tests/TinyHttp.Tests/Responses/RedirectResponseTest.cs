namespace TinyHttp.Tests.Responses
{
    using System.Net;
    using FluentAssertions;
    using NUnit.Framework;

    public class RedirectResponseTest
    {
        [Test]
        public void PermanentRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.Permanent);
            response.Headers.Should().Contain("Location", "/");
            response.Body.AsString().Should().BeEmpty();
            response.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        }

        [Test]
        public void TemporaryRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.Temporary);
            response.Headers.Should().Contain("Location", "/");
            response.Body.AsString().Should().BeEmpty();
            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        }

        [Test]
        public void SeeOtherRedirect()
        {
            var response = new RedirectResponse("/", RedirectResponse.RedirectType.SeeOther);
            response.Headers.Should().Contain("Location", "/");
            response.Body.AsString().Should().BeEmpty();
            response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        }

        [Test]
        public void DefaultRedirect()
        {
            var response = new RedirectResponse("/");
            response.Headers.Should().Contain("Location", "/");
            response.Body.AsString().Should().BeEmpty();
            response.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        }
    }
}