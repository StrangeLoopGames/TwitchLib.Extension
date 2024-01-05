﻿using JWT;
using JWT.Algorithms;
using JWT.Serializers;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using TwitchLib.Extension.Exceptions;
using TwitchLib.Extension.Models;

namespace TwitchLib.Extension
{
    public abstract class ExtensionBase
    {
        private const string _extensionUrl = "https://api.twitch.tv/helix/extensions/{0}";
        private readonly ExtensionConfiguration _config;
        protected IEnumerable<Secret> Secrets { get; set; }

        public string CurrentSecret { get => Secrets.ToList().OrderByDescending(x => x.Expires).First().Content; }

        public ExtensionBase(ExtensionConfiguration config)
        {
            _config = config;
            Secrets = new List<Secret> { new Secret(config.StartingSecret, DateTime.Now, DateTime.Now.AddYears(100)) };
        }
        
        /// <summary>
        /// Creates a new secret for a specified extension. Also rotates any current secrets out of service, with enough 
        /// time for extension clients to gracefully switch over to the new secret. The delay period, 
        /// between the generation of the new secret and its use by Twitch, is specified by a required parameter, activation_delay_secs. 
        /// The default delay is 300 (5 minutes); if a value less than this is specified, Twitch uses 300.
        /// 
        /// Use this function only when you are ready to install the new secret it returns.
        /// </summary>
        /// <param name="activationDelaySeconds">How long Twitch should wait before using your new secret and rolling it out to users</param>
        /// <returns>Data object containing all of current extension secrets that are valid and haven't expired</returns>
        public virtual async Task<ExtensionSecretsData> CreateExtensionSecretAsync(int activationDelaySeconds = 300) => 
            await CreateExtensionSecretAsync(CurrentSecret, _config.Id, _config.OwnerId, activationDelaySeconds);

        /// <summary>
        /// Retrieves a specified extension’s secret data: a version and an array of secret objects. 
        /// Each secret object returned contains a base64-encoded secret, a UTC timestamp when the secret becomes active, 
        /// and a timestamp when the secret expires.
        /// </summary>
        /// <returns>Data object containing all of current extension secrets that are valid and haven't expired</returns>
        public virtual async Task<ExtensionSecretsData> GetExtensionSecretAsync() => 
            await GetExtensionSecretAsync(CurrentSecret, _config.Id, _config.OwnerId);

        /// <summary>
        /// Deletes all secrets associated with a specified extension.
        /// 
        /// This immediately breaks all clients until both a new Create Extension Secret is executed 
        /// and the clients manually refresh themselves.
        /// 
        /// Use this only if a secret is compromised and must be removed immediately from circulation.
        /// </summary>
        /// <returns>true if secrets were successfully revoked</returns>
        public virtual async Task<bool> RevokeExtensionSecretAsync()
        {
            return await RevokeExtensionSecretAsync(
                CurrentSecret,
                _config.Id,
                _config.OwnerId);
        }

        /// <summary>
        /// Returns one page of live channels that have installed and activated a specified extension. 
        /// 
        /// A channel that just went live may take a few minutes to appear in this list, and a channel may continue to 
        /// appear on this list for a few minutes after it stops broadcasting.
        /// </summary>
        /// <param name="cursor"></param>
        /// <returns>List of channels that are live with the extension installed</returns>
        public virtual async Task<Models.LiveChannels> GetLiveChannelsWithExtensionActivatedAsync(string cursor = null)
        {
            return await GetLiveChannelsWithExtensionActivatedAsync(
               CurrentSecret,
                _config.Id,
                _config.OwnerId,
                cursor);
        }

        /// <summary>
        /// Enable activation of a specified extension, after any required broadcaster configuration is correct. 
        /// This is for extensions that require broadcaster configuration before activation.
        /// </summary>
        /// <param name="channelId">The Twitch channel ID we are setting the specified value for</param>
        /// <param name="requiredConfiguration"></param>
        /// <returns>true if requiredConfiguration was set successfully</returns>
        public virtual async Task<bool> SetExtensionRequiredConfigurationAsync(string channelId, string requiredConfiguration)
        {
            return await SetExtensionRequiredConfigurationAsync(
                CurrentSecret,
                _config.Id,
                _config.VersionNumber,
                _config.OwnerId,
                channelId,
                requiredConfiguration);
        }

        /// <summary>
        /// Indicates whether the broadcaster allowed the permissions your extension requested, 
        /// through a required permissions_received parameter. The endpoint URL includes the channel ID 
        /// of the page where the extension is iframe embedded.
        /// </summary>
        /// <param name="channelId">The Twitch channel ID we are setting the specified value for</param>
        /// <param name="permissionsReceived"></param>
        /// <returns>true if permissionsReceived was set successfully</returns>
        public virtual async Task<bool> SetExtensionBroadcasterOAuthReceiptAsync(string channelId, bool permissionsReceived)
        {
            return await SetExtensionBroadcasterOAuthReceiptAsync(
                CurrentSecret,
                _config.Id,
                _config.VersionNumber,
                _config.OwnerId,
                channelId,
                permissionsReceived);
        }

        /// <summary>
        /// Twitch provides a publish-subscribe system for your EBS (Extension Back-end Service) to communicate 
        /// with both the broadcaster and viewers. Calling this endpoint forwards your message using the same
        /// mechanism as the send() function in the JavaScript helper API.
        /// </summary>
        /// <param name="channelId">The Twitch channel ID we are sending the message for</param>
        /// <param name="message"></param>
        /// <param name="jwt">Optional JWT of user, this JWT should only be those passed by twitch in the x-extension-jwt header</param>
        /// <returns>true if PubSub message successfully sent</returns>
        public virtual async Task<bool> SendExtensionPubSubMessageAsync(string channelId, Models.ExtensionPubSubRequest message, string jwt = null)
        {
            return await SendExtensionPubSubMessageAsync(
                CurrentSecret,
                _config.Id,
                _config.OwnerId,
                channelId,
                message,
                jwt);
        }
        
        protected async Task<Models.ExtensionSecretsData> CreateExtensionSecretAsync(string extensionSecret, string extensionId, string extensionOwnerId, int activationDelaySeconds = 300)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId))  throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (activationDelaySeconds < 300) throw new BadParameterException("The activation delay in seconds is not allowed to be less than 300");

            var url = $"jwt/secrets?extension_id={extensionId}";
            var request = new CreateSecretRequest { Activation_Delay_Secs = activationDelaySeconds };
            return ExtensionSecretsData.FromJson((await RequestAsync(extensionSecret, url, "POST", extensionOwnerId, extensionId, request.ToJson()).ConfigureAwait(false)).Value);
        }

        protected async Task<Models.ExtensionSecretsData> GetExtensionSecretAsync(string extensionSecret, string extensionId, string extensionOwnerId)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");

            var url = $"jwt/secrets?extension_id={extensionId}";
            return ExtensionSecretsData.FromJson((await RequestAsync(extensionSecret, url, "GET", extensionOwnerId, extensionId).ConfigureAwait(false)).Value);
        }

        protected async Task<bool> RevokeExtensionSecretAsync(string extensionSecret, string extensionId, string extensionOwnerId)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");

            var url = $"jwt/secrets?extension_id={extensionId}";
            return (await RequestAsync(extensionSecret, url, "DELETE", extensionOwnerId, extensionId).ConfigureAwait(false)).Key == 204;
        }

        protected async Task<Models.LiveChannels> GetLiveChannelsWithExtensionActivatedAsync(string extensionSecret, string extensionId,string extensionOwnerId, string cursor = null)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");

            var url = $"/live?extension_id={extensionId}";
            if (!string.IsNullOrWhiteSpace(cursor))
                url += $"?after={cursor}";
            return LiveChannels.FromJson((await RequestAsync(extensionSecret, url, "GET", extensionOwnerId, extensionId).ConfigureAwait(false)).Value);
        }

        protected async Task<bool> SetExtensionRequiredConfigurationAsync(string extensionSecret, string extensionId, string extensionVersion, string extensionOwnerId, string channelId, string requiredConfiguration)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionVersion)) throw new BadParameterException("The extension version is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(channelId)) throw new BadParameterException("The channel id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrEmpty(requiredConfiguration)) throw new BadParameterException("The required configuration is not valid. It is not allowed to be null or empty.");

            var url = $"required_configuration?broadcaster_id={channelId}";
            var request = new SetExtensionRequiredConfigurationRequest 
            { 
                RequiredConfiguration = requiredConfiguration,
                ExtensionId = extensionId,
                ExtensionVersion = extensionVersion,
            };

            return (await RequestAsync(extensionSecret, url, "PUT", extensionOwnerId, extensionId, request.ToJson()).ConfigureAwait(false)).Key == 204;
        }

        protected async Task<bool> SetExtensionBroadcasterOAuthReceiptAsync(string extensionSecret, string extensionId, string extensionVersion, string extensionOwnerId, string channelId, bool permissionsReceived)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionVersion)) throw new BadParameterException("The extension version is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(channelId)) throw new BadParameterException("The channel id is not valid. It is not allowed to be null, empty or filled with whitespaces.");

            var url = $"{extensionId}/{extensionVersion}/oauth_receipt?channel_id={channelId}";
            var request = new SetExtensionBroadcasterOAuthReceiptRequest { Permissions_Received = permissionsReceived };
            return (await RequestAsync(extensionSecret, url, "PUT", extensionOwnerId, extensionId, request.ToJson()).ConfigureAwait(false)).Key == 204;
        }

        protected async Task<bool> SendExtensionPubSubMessageAsync(string extensionSecret, string extensionId, string extensionOwnerId, string channelId, Models.ExtensionPubSubRequest message, string jwt =null)
        {
            if (string.IsNullOrWhiteSpace(extensionSecret)) throw new BadParameterException("The extension secret is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionId)) throw new BadParameterException("The extension id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(extensionOwnerId)) throw new BadParameterException("The extension owner id is not valid. It is not allowed to be null, empty or filled with whitespaces.");
            if (string.IsNullOrWhiteSpace(channelId)) throw new BadParameterException("The channel id is not valid. It is not allowed to be null, empty or filled with whitespaces.");

            if (string.IsNullOrEmpty(jwt)) jwt = Sign(extensionSecret, extensionOwnerId, 10, channelId);
            return (await RequestAsync(extensionSecret, "pubsub", "POST", extensionOwnerId, extensionId, message.ToJson(), jwt).ConfigureAwait(false)).Key == 204;
        }

        private async Task<KeyValuePair<int, string>> RequestAsync(string secret, string url, string method, string userId, string clientId, object payload=null, string jwt = null)
        {
            var request = WebRequest.CreateHttp(string.Format(_extensionUrl, url));

            request.Headers["Client-Id"] = clientId;
            request.Method = method;
            request.ContentType = "application/json";
            var token = jwt ?? Sign(secret, userId, 10);
            request.Headers["Authorization"] = $"Bearer {token}";

            if (payload != null)
                using (var writer = new StreamWriter(await request.GetRequestStreamAsync()))
                    writer.Write(payload);
            try
            {
                var response = (HttpWebResponse)request.GetResponse();
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string data = reader.ReadToEnd();
                    return new KeyValuePair<int, string>((int)response.StatusCode, data);
                }
            }
            catch (WebException ex) { HandleWebException(ex); }

            return new KeyValuePair<int, string>(0, null);
        }
       
      
        private void HandleWebException(WebException e)
        {
            HttpWebResponse errorResp = e.Response as HttpWebResponse;
            if (errorResp == null)
                throw e;
            switch (errorResp.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    throw new BadRequestException("Your request failed because your ClientID was invalid/not set.");
                case HttpStatusCode.Unauthorized:
                    throw new BadScopeException("Your request was blocked due to bad credentials (do you have the right scope for your access token?).");
                case HttpStatusCode.NotFound:
                    throw new BadResourceException("The resource you tried to access was not valid.");
                default:
                    throw e;
            }
        }


        #region JWTSignAndVerify
        public ClaimsPrincipal Verify(string jwt, out SecurityToken validTokenOverlay)
        {
            ClaimsPrincipal user = null;
            validTokenOverlay = null;
            foreach (var secret in Secrets.ToList().OrderByDescending(x => x.Expires).Where(x => x.Expires > DateTime.Now))
            {
                user = VerifyWithSecret(jwt, secret.Content, out validTokenOverlay);
                if (user != null)
                {
                    ((ClaimsIdentity)user.Identity).AddClaim(new Claim("extension_id", _config.Id, ClaimValueTypes.String));
                    break;
                }
            }
            return user;
        }

        private ClaimsPrincipal VerifyWithSecret(string jwt, string secret, out SecurityToken validTokenOverlay)
        {
            var validationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(secret)),
                ValidateAudience = false,
                ValidateLifetime = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true
            };

            var handler = new JwtSecurityTokenHandler();

            try
            {
                return handler.ValidateToken(jwt, validationParameters, out validTokenOverlay);
            }
            catch
            {
                validTokenOverlay = null;
                return null;
            }
        }

        private string Sign(string secret, string userId, int expirySeconds)
        {
            var payload = new Dictionary<string, object>
                {
                    { "exp", (GetEpoch() + expirySeconds) },
                    { "user_id", userId },
                    { "role", "external" }
                };

            IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
            IJsonSerializer serializer = new JsonNetSerializer();
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

            var token = encoder.Encode(payload, Convert.FromBase64String(secret));
            return token;
        }

        class perms
        {
            public string[] send;
        }
        private string Sign(string secret, string userId, int expirySeconds, string channelId)
        {
            var perms = new perms { send = new string[] { "*" } };

            var payload = new Dictionary<string, object>
                {
                    { "exp", (GetEpoch() + expirySeconds) },
                    { "user_id", userId },
                    { "role", "external" },
                    { "channel_id", channelId },
                    { "pubsub_perms", perms }
                };

            IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
            IJsonSerializer serializer = new JsonNetSerializer();
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtEncoder encoder = new JwtEncoder(algorithm, serializer, urlEncoder);

            var token = encoder.Encode(payload, Convert.FromBase64String(secret));
            return token;
        }
        
        private int GetEpoch()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            int secondsSinceEpoch = (int)t.TotalSeconds;
            return secondsSinceEpoch;
        }

        #endregion
    }
}
