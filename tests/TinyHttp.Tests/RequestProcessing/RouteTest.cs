namespace TinyHttp.Tests.RequestProcessing
{
    using System;
    using FluentAssertions;
    using NUnit.Framework;

    public class RouteTest
    {
        [Test]
        public void RootRoute()
        {
            var response = new Response();
            var route = new Route("GET", "/", d => response);
            route.Method.Should().Be("GET");
            route.Path.Should().Be("/");
            route.Action.Invoke(null).Should().Be(response);
            route.Regex.ToString().Should().Be("^/$");
        }

        [Test]
        public void ParameterizedRoute()
        {
            var parameters = new DynamicDictionary();
            var response = new Response();
            var route = new Route("GET", "/{name}", d => { parameters = d; return response; });
            route.Method.Should().Be("GET");
            route.Path.Should().Be("/{name}");
            route.Action.Invoke(null).Should().Be(response);
            route.Regex.ToString().Should().Be(@"^/(?<name>\S+)$");
            route.Invoke(new Request("GET", new Uri("http://localhost/bob"), 0, null, null));
            ((string) parameters["name"].ToString()).Should().Be("bob");
        }
    }
}