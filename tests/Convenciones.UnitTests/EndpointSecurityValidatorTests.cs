using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Convenciones.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>
/// Proves the startup gate works — including that it cannot pass vacuously.
/// </summary>
/// <remarks>
/// This is the check that stops an endpoint from being added without a security decision. If it ever
/// silently validates nothing, every future endpoint is protected by authentication alone, so these
/// tests assert the failures as carefully as the success.
/// </remarks>
public sealed class EndpointSecurityValidatorTests
{
    private static Endpoint Route(string pattern, params object[] metadata) =>
        new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(metadata),
            displayName: pattern);

    [Fact]
    public void Endpoint_without_a_security_declaration_fails_startup()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            EndpointSecurityValidator.Validate([Route("/undeclared")]));

        exception.Message.ShouldContain("/undeclared");
        exception.Message.ShouldContain("RequiresCapability");
    }

    [Fact]
    public void Endpoint_with_a_capability_passes()
    {
        Should.NotThrow(() => EndpointSecurityValidator.Validate(
            [Route("/agreements", new RequiresCapabilityAttribute("agreement.read"))]));
    }

    [Fact]
    public void Endpoint_requiring_only_an_active_session_passes()
    {
        Should.NotThrow(() => EndpointSecurityValidator.Validate(
            [Route("/me", new RequiresActiveSessionAttribute())]));
    }

    [Fact]
    public void Anonymous_endpoint_passes()
    {
        Should.NotThrow(() => EndpointSecurityValidator.Validate(
            [Route("/health/live", new AllowAnonymousAttribute())]));
    }

    [Fact]
    public void Every_undeclared_endpoint_is_named_in_one_failure()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            EndpointSecurityValidator.Validate(
            [
                Route("/declared", new RequiresCapabilityAttribute("document.read")),
                Route("/one"),
                Route("/two"),
            ]));

        exception.Message.ShouldContain("/one");
        exception.Message.ShouldContain("/two");
        exception.Message.ShouldNotContain("/declared,");
    }

    [Fact]
    public void An_empty_route_set_is_refused_rather_than_silently_accepted()
    {
        // A validator that iterates zero endpoints reports success for a pipeline it never inspected.
        // That is what "vacuous pass" means, and it must be impossible.
        var exception = Should.Throw<InvalidOperationException>(() =>
            EndpointSecurityValidator.Validate([]));

        exception.Message.ShouldContain("no routed endpoint", Case.Insensitive);
    }

    [Fact]
    public void Non_route_endpoints_are_ignored()
    {
        var metadataOnly = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "not a route");

        Should.NotThrow(() => EndpointSecurityValidator.Validate(
            [metadataOnly, Route("/declared", new RequiresActiveSessionAttribute())]));
    }
}
