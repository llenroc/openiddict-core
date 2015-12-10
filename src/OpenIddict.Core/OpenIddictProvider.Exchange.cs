﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Server;
using Microsoft.AspNet.Authentication;
using Microsoft.AspNet.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace OpenIddict {
    public partial class OpenIddictProvider<TUser, TApplication> : OpenIdConnectServerProvider where TUser : class where TApplication : class {
        public override async Task ValidateTokenRequest([NotNull] ValidateTokenRequestContext context) {
            var manager = context.HttpContext.RequestServices.GetRequiredService<OpenIddictManager<TUser, TApplication>>();

            // Note: OpenIdConnectServerHandler supports authorization code, refresh token,
            // client credentials, resource owner password credentials and custom grants
            // but this authorization server uses a stricter policy rejecting custom grant types.
            if (!context.Request.IsAuthorizationCodeGrantType() && !context.Request.IsRefreshTokenGrantType() &&
                !context.Request.IsPasswordGrantType() && !context.Request.IsClientCredentialsGrantType()) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.UnsupportedGrantType,
                    description: "Only authorization code, refresh token, client credentials " +
                                 "and password grants are accepted by this authorization server.");

                return;
            }

            // Note: though required by the OpenID Connect specification for the refresh token grant,
            // client authentication is not mandatory for non-confidential client applications in OAuth2.
            // To avoid breaking OAuth2 scenarios, OpenIddict uses a relaxed policy that allows
            // public applications to use the refresh token grant without having to authenticate.
            // See http://openid.net/specs/openid-connect-core-1_0.html#RefreshingAccessToken
            // and https://tools.ietf.org/html/rfc6749#section-6 for more information.

            // Skip client authentication if the client identifier is missing.
            // Note: ASOS will automatically ensure that the calling application
            // cannot use an authorization code or a refresh token if it's not
            // the intended audience, even if client authentication was skipped.
            if (string.IsNullOrEmpty(context.ClientId)) {
                context.Skip();

                return;
            }

            // Retrieve the application details corresponding to the requested client_id.
            var application = await manager.FindApplicationByIdAsync(context.ClientId);
            if (application == null) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidClient,
                    description: "Application not found in the database: ensure that your client_id is correct.");

                return;
            }

            // Reject tokens requests containing a client_secret if the client application is not confidential.
            if (await manager.IsPublicApplicationAsync(application) && !string.IsNullOrEmpty(context.ClientSecret)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "Public clients are not allowed to send a client_secret.");

                return;
            }

            // Confidential applications MUST authenticate
            // to protect them from impersonation attacks.
            else if (await manager.IsConfidentialApplicationAsync(application)) {
                if (string.IsNullOrEmpty(context.ClientSecret)) {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "Missing credentials: ensure that you specified a client_secret.");

                    return;
                }

                if (!await manager.ValidateSecretAsync(application, context.ClientSecret)) {
                    context.Reject(
                        error: OpenIdConnectConstants.Errors.InvalidClient,
                        description: "Invalid credentials: ensure that you specified a correct client_secret.");

                    return;
                }
            }

            context.Validate();
        }

        public override async Task GrantClientCredentials([NotNull] GrantClientCredentialsContext context) {
            var manager = context.HttpContext.RequestServices.GetRequiredService<OpenIddictManager<TUser, TApplication>>();

            // Retrieve the application details corresponding to the requested client_id.
            var application = await manager.FindApplicationByIdAsync(context.ClientId);
            Debug.Assert(application != null);

            var identity = new ClaimsIdentity(context.Options.AuthenticationScheme);
            identity.AddClaim(ClaimTypes.NameIdentifier, context.ClientId, destination: "id_token token");
            identity.AddClaim(ClaimTypes.Name, await manager.GetDisplayNameAsync(application), destination: "id_token token");

            // Create a new authentication ticket
            // holding the application identity.
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                null, context.Options.AuthenticationScheme);

            ticket.SetResources(context.Request.GetResources());
            ticket.SetScopes(context.Request.GetScopes());

            context.Validate(ticket);
        }

        public override async Task GrantRefreshToken([NotNull] GrantRefreshTokenContext context) {
            var manager = context.HttpContext.RequestServices.GetRequiredService<OpenIddictManager<TUser, TApplication>>();
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>();

            // If the user manager doesn't support security
            // stamps, skip the default validation logic.
            if (!manager.SupportsUserSecurityStamp) {
                return;
            }

            var principal = context.AuthenticationTicket?.Principal;
            Debug.Assert(principal != null);

            var user = await manager.FindByIdAsync(principal.GetUserId());
            if (user == null) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidGrant,
                    description: "The refresh token is no longer valid.");

                return;
            }

            var identifier = principal.GetClaim(options.Value.ClaimsIdentity.SecurityStampClaimType);
            if (!string.IsNullOrEmpty(identifier) &&
                !string.Equals(identifier, await manager.GetSecurityStampAsync(user), StringComparison.Ordinal)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidGrant,
                    description: "The refresh token is no longer valid.");

                return;
            }
        }

        public override async Task GrantResourceOwnerCredentials([NotNull] GrantResourceOwnerCredentialsContext context) {
            var manager = context.HttpContext.RequestServices.GetRequiredService<OpenIddictManager<TUser, TApplication>>();

            var user = await manager.FindByNameAsync(context.UserName);
            if (user == null) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidGrant,
                    description: "Invalid credentials.");

                return;
            }

            // Ensure the user is not already locked out.
            if (manager.SupportsUserLockout && await manager.IsLockedOutAsync(user)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidGrant,
                    description: "Account locked out.");

                return;
            }
            
            // Ensure the password is valid.
            if (!await manager.CheckPasswordAsync(user, context.Password)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidGrant,
                    description: "Invalid credentials.");

                if (manager.SupportsUserLockout) {
                    await manager.AccessFailedAsync(user);

                    // Ensure the user is not locked out.
                    if (await manager.IsLockedOutAsync(user)) {
                        context.Reject(
                            error: OpenIdConnectConstants.Errors.InvalidGrant,
                            description: "Account locked out.");
                    }
                }

                return;
            }

            if (manager.SupportsUserLockout) { 
                await manager.ResetAccessFailedCountAsync(user);
            }

            // Return an error if the username corresponds to the registered
            // email address and if the "email" scope has not been requested.
            if (context.Request.HasScope(OpenIdConnectConstants.Scopes.Profile) &&
               !context.Request.HasScope(OpenIdConnectConstants.Scopes.Email) &&
                string.Equals(await manager.GetUserNameAsync(user),
                              await manager.GetEmailAsync(user),
                              StringComparison.OrdinalIgnoreCase)) {
                context.Reject(
                    error: OpenIdConnectConstants.Errors.InvalidRequest,
                    description: "The 'email' scope is required.");

                return;
            }

            var identity = await manager.CreateIdentityAsync(user, context.Request.GetScopes());
            Debug.Assert(identity != null);

            // Create a new authentication ticket holding the user identity.
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                null, context.Options.AuthenticationScheme);

            ticket.SetResources(context.Request.GetResources());
            ticket.SetScopes(context.Request.GetScopes());

            context.Validate(ticket);
        }
    }
}